using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace OpenLancer.Tests;

/// <summary>
/// Reads the IL of the methods of a type. It gives the calls (<c>call</c>, <c>callvirt</c>, <c>newobj</c>,
/// <c>ldftn</c> and <c>ldvirtftn</c>) and the string literals (<c>ldstr</c>). The reader resolves each member with the
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

    /// <summary>Each opcode of the IL of a method whose operand is the token of a method or of a string, with the
    /// token.</summary>
    private static IEnumerable<(OpCode Op, int Token)> Tokens(MethodBase method)
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
            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineString)
                yield return (op, BitConverter.ToInt32(il, at));
            at += OperandSize(op, il, at);
        }
    }

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
