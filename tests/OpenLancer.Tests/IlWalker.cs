using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace OpenLancer.Tests;

/// <summary>
/// Reads the IL of the methods of a type. It gives the calls (<c>call</c>, <c>callvirt</c>, <c>newobj</c>,
/// <c>ldftn</c> and <c>ldvirtftn</c>), the string literals (<c>ldstr</c>), the field loads (<c>ldfld</c>,
/// <c>ldsfld</c> and their address forms) and each instruction of a method. The reader resolves each member with the
/// generic arguments of the type and of the method. A compiler-generated nested type, for example the class of a lambda
/// or a state machine, counts as the type that holds it. A local function is a method of its type.
/// </summary>
internal static class IlWalker
{
    /// <summary>A call: the type that holds it, the method that makes it, the member that it calls and its
    /// opcode.</summary>
    public sealed record Call(Type Owner, MethodBase Caller, MemberInfo Callee, OpCode Op);

    /// <summary>A string literal: the type that holds it, the method that loads it and the text. Each part of an
    /// interpolated string is a literal of its own.</summary>
    public sealed record Literal(Type Owner, MethodBase Method, string Text);

    /// <summary>A load of a field or of its address: the type that holds it, the method that loads it and the
    /// field.</summary>
    public sealed record FieldLoad(Type Owner, MethodBase Method, FieldInfo Field);

    /// <summary>An instruction: its opcode and its operand as a number. The operand of an instruction without an
    /// operand, of a switch and of a floating-point constant is 0. A token is the operand of a member or a
    /// string.</summary>
    public readonly record struct Instruction(OpCode Op, long Operand);

    /// <summary>A call whose first argument is a constant integer: the type that holds it, the method that makes it,
    /// the method that it calls and the constant. An enum value, for example a key, is such a constant.</summary>
    public sealed record ConstantArgument(Type Owner, MethodBase Caller, MethodBase Callee, long Value);

    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly;

    static IlWalker()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var code = (OpCode)field.GetValue(null)!;
            var value = (ushort)code.Value;
            if (value < 0x100)
                OneByte[value] = code;
            else if ((value & 0xff00) == 0xfe00)
                TwoByte[value & 0xff] = code;
        }
    }

    /// <summary>The calls of each method and constructor of a type, without its nested types.</summary>
    public static IEnumerable<Call> Calls(Type type)
    {
        var owner = OwnerOf(type);
        foreach (var method in MethodsOf(type))
        {
            var typeArguments = method.DeclaringType is { IsGenericType: true } generic
                ? generic.GetGenericArguments()
                : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            foreach (var (op, token) in Tokens(method))
            {
                if (op != OpCodes.Call && op != OpCodes.Callvirt && op != OpCodes.Newobj && op != OpCodes.Ldftn &&
                    op != OpCodes.Ldvirtftn)
                    continue;
                var callee = method.Module.ResolveMember(token, typeArguments, methodArguments) ??
                             throw new InvalidOperationException($"{method.Name}: token {token:x8} does not resolve.");
                yield return new Call(owner, method, callee, op);
            }
        }
    }

    /// <summary>The string literals of each method and constructor of a type, without its nested types. The field
    /// initializers are in the constructors.</summary>
    public static IEnumerable<Literal> Literals(Type type)
    {
        var owner = OwnerOf(type);
        foreach (var method in MethodsOf(type))
        {
            foreach (var (op, token) in Tokens(method))
            {
                if (op == OpCodes.Ldstr)
                    yield return new Literal(owner, method, method.Module.ResolveString(token));
            }
        }
    }

    /// <summary>The field loads of each method and constructor of a type, without its nested types.</summary>
    public static IEnumerable<FieldLoad> FieldLoads(Type type)
    {
        var owner = OwnerOf(type);
        foreach (var method in MethodsOf(type))
        {
            var typeArguments = method.DeclaringType is { IsGenericType: true } generic
                ? generic.GetGenericArguments()
                : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            foreach (var (op, token) in Tokens(method))
            {
                if (op != OpCodes.Ldfld && op != OpCodes.Ldsfld && op != OpCodes.Ldflda && op != OpCodes.Ldsflda)
                    continue;
                var field = method.Module.ResolveField(token, typeArguments, methodArguments) ??
                            throw new InvalidOperationException($"{method.Name}: token {token:x8} does not resolve.");
                yield return new FieldLoad(owner, method, field);
            }
        }
    }

    /// <summary>
    /// The calls of each method and constructor of a type, without its nested types, whose first argument is a constant
    /// integer. The reader finds the first argument only when each later argument is one instruction that loads one
    /// value, for example a constant, a local or a parameter. It skips the other calls.
    /// </summary>
    public static IEnumerable<ConstantArgument> ConstantFirstArguments(Type type)
    {
        var owner = OwnerOf(type);
        foreach (var method in MethodsOf(type))
        {
            var typeArguments = method.DeclaringType is { IsGenericType: true } generic
                ? generic.GetGenericArguments()
                : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            var code = Instructions(method).ToList();
            for (var at = 0; at < code.Count; at++)
            {
                if (code[at].Op != OpCodes.Call && code[at].Op != OpCodes.Callvirt ||
                    method.Module.ResolveMember((int)code[at].Operand, typeArguments, methodArguments) is not MethodBase
                        callee)
                    continue;
                var count = callee.GetParameters().Length;
                var first = at - count;
                if (count == 0 || first < 0 || !code.Skip(first + 1).Take(count - 1).All(i => LoadsOneValue(i.Op)))
                    continue;
                if (ConstantOf(code[first]) is { } value)
                    yield return new ConstantArgument(owner, method, callee, value);
            }
        }
    }

    /// <summary>True for an instruction that loads one value and takes none from the stack.</summary>
    private static bool LoadsOneValue(OpCode op) =>
        op.Name is { } name && (name.StartsWith("ldc.", StringComparison.Ordinal) ||
                                name.StartsWith("ldloc", StringComparison.Ordinal) ||
                                name.StartsWith("ldarg", StringComparison.Ordinal) ||
                                name is "ldnull" or "ldstr" or "ldsfld" or "ldsflda");

    /// <summary>The integer that an instruction loads, or null when it loads no integer constant.</summary>
    private static long? ConstantOf(Instruction instruction) => instruction.Op.Name switch
    {
        "ldc.i4.m1" => -1,
        "ldc.i4.s" or "ldc.i4" or "ldc.i8" => instruction.Operand,
        { } name when name.StartsWith("ldc.i4.", StringComparison.Ordinal) => name[^1] - '0',
        _ => null,
    };

    /// <summary>The type that a type counts as: the first declaring type that the compiler did not make.</summary>
    public static Type OwnerOf(Type type)
    {
        while (type.DeclaringType is { } outer && (type.IsDefined(typeof(CompilerGeneratedAttribute)) ||
                                                   type.Name.StartsWith('<')))
            type = outer;
        return type;
    }

    private static IEnumerable<MethodBase> MethodsOf(Type type) =>
        type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));

    /// <summary>The instructions of the IL of a method, in their order. A method without IL has none.</summary>
    public static IEnumerable<Instruction> Instructions(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            yield break;
        var at = 0;
        while (at < il.Length)
        {
            var op = il[at] == 0xfe ? TwoByte[il[++at]] : OneByte[il[at]];
            at++;
            if (op.Size == 0)
                throw new InvalidOperationException($"{method.DeclaringType}.{method.Name}: unknown opcode at {at - 1}.");
            long operand = op.OperandType switch
            {
                OperandType.InlineNone or OperandType.InlineSwitch or OperandType.InlineR or OperandType.ShortInlineR => 0,
                OperandType.ShortInlineI => (sbyte)il[at],
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineVar => il[at],
                OperandType.InlineVar => BitConverter.ToUInt16(il, at),
                OperandType.InlineI8 => BitConverter.ToInt64(il, at),
                _ => BitConverter.ToInt32(il, at),
            };
            yield return new Instruction(op, operand);
            at += OperandSize(op, il, at);
        }
    }

    /// <summary>Each opcode of the IL of a method whose operand is the token of a method, a field or a string, with the
    /// token.</summary>
    private static IEnumerable<(OpCode Op, int Token)> Tokens(MethodBase method) =>
        Instructions(method)
            .Where(i => i.Op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineString)
            .Select(i => (i.Op, (int)i.Operand));

    /// <summary>The size of the operand of an opcode at a place of the IL.</summary>
    private static int OperandSize(OpCode op, byte[] il, int at) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, at),
        _ => 4,
    };
}
