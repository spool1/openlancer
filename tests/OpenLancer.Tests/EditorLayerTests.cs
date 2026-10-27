using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The editor UI changes data only through the command layer (section 5.2, item 5 and section 13.1, item 16 of the
/// MCP1 design, decision D766). The test reads the IL of each method of the editor assembly. Outside the command runner,
/// no editor type makes a session command, gets a delegate that makes one, or runs one.
/// </summary>
/// <remarks>
/// <para>The cutscene callers keep their session commands and <see cref="CommandRunner.Execute(EditCommand)"/> until
/// milestone MCP1-4 moves them to the cutscene commands. A fixed allow-list names them, and a test fails when an entry
/// of the list has no edit call any more.</para>
/// <para>A second rule reads the IL of the runtime assembly. No runtime method runs a session command, so the editor
/// cannot reach a raw edit through a runtime helper.</para>
/// </remarks>
public sealed class EditorLayerTests
{
    /// <summary>The cutscene callers that keep their edit calls until milestone MCP1-4. ThnCurveEditor is not on the
    /// list, because it starts its drag through CurvePointDrag of OpenLancer.Editor.Cutscene.</summary>
    private static readonly IReadOnlyList<Allowed> AllowList =
    [
        new("the types of OpenLancer.Editor.Cutscene", t => t.Namespace == "OpenLancer.Editor.Cutscene", _ => true),
        new(nameof(ThnPage), t => t == typeof(ThnPage), _ => true),
        new(nameof(ThnFieldGesture), t => t == typeof(ThnFieldGesture), _ => true),
        new($"{nameof(PropertyGrid)}, for calls of {nameof(ThnEdits)} and of {nameof(CommandRunner.Execute)}",
            t => t == typeof(PropertyGrid), m => m.DeclaringType == typeof(ThnEdits) || IsExecute(m)),
    ];

    [Fact]
    public void NoEditorTypeOutsideTheRunnerMakesOrRunsASessionCommand()
    {
        var found = EditCalls(typeof(EditorWindow).Assembly, EditRule)
            .Where(c => c.Owner != typeof(CommandRunner) && !AllowList.Any(a => a.Covers(c)))
            .Select(c => c.ToString())
            .Distinct()
            .ToList();

        Assert.True(found.Count == 0, "Edit calls outside the command runner:\n" + string.Join("\n", found));
    }

    [Fact]
    public void NoRuntimeMethodRunsASessionCommand()
    {
        var found = EditCalls(typeof(GizmoDrag).Assembly, RunRule).Select(c => c.ToString()).Distinct().ToList();

        Assert.True(found.Count == 0, "Runtime calls that run a session command:\n" + string.Join("\n", found));
    }

    /// <summary>Each entry of the allow-list has an edit call. When milestone MCP1-4 moves a caller, its entry
    /// goes.</summary>
    [Fact]
    public void EachEntryOfTheAllowListHasAnEditCall()
    {
        var calls = EditCalls(typeof(EditorWindow).Assembly, EditRule).ToList();

        foreach (var entry in AllowList)
            Assert.True(calls.Any(entry.Covers), $"The allow-list entry '{entry.Name}' has no edit call. Remove it.");
    }

    /// <summary>The walker finds a direct call, a call in a lambda, a method group, a call in a local function and a
    /// delegate that makes a session command. It gives each one to the type that holds it.</summary>
    [Fact]
    public void TheWalkerFindsEachFormOfAnEditCall()
    {
        var calls = EditCalls(typeof(EditorLayerTests).Assembly, EditRule)
            .Where(c => c.Owner == typeof(RawEditSample)).ToList();

        Assert.Contains(calls, c => c.Callee.Name == nameof(EditCommand.Group) && c.Op == OpCodes.Call);
        Assert.Contains(calls, c => c.Callee.Name == nameof(EditSession.Execute) && c.Op == OpCodes.Callvirt);
        Assert.Contains(calls, c => c.Callee.Name == nameof(EditSession.Begin) && c.Caller.Name.Contains("<Lambda>"));
        Assert.Contains(calls, c => c.Callee.DeclaringType == typeof(EditTransaction) && c.Caller.Name.Contains("<Lambda>"));
        Assert.Contains(calls, c => c.Callee.Name == nameof(EditCommand.Revert) && c.Op == OpCodes.Ldftn);
        Assert.Contains(calls, c => c.Callee.Name == nameof(CommandTransaction.Begin) && c.Caller.Name.Contains("g__Local"));
        Assert.Contains(calls, c => c.Callee.Name == "get_" + nameof(EditPlan.Command));
        Assert.DoesNotContain(calls, c => c.Callee.Name == "get_" + nameof(EditPlan.CanRun));
        var delegateCalls = calls.Where(c => c.Caller.Name == nameof(RawEditSample.Delegate)).ToList();
        Assert.Contains(delegateCalls, c => c.Callee.Name == "get_" + nameof(ThnFieldCommand.Make));
        Assert.Contains(delegateCalls, c => c.Callee.Name == nameof(Func<ThnValue, EditCommand>.Invoke));
        Assert.Contains(delegateCalls, c => IsExecute(c.Callee));
    }

    /// <summary>Why an editor type cannot call a member, or null. The rule of section 5.2, item 5 of the MCP1
    /// design.</summary>
    /// <remarks>The rule reads the members of each type, also of the editor and of .NET. So it also finds the
    /// <c>Invoke</c> of a delegate and the item of a list that give a session command.</remarks>
    private static string? EditRule(MemberInfo member) => member switch
    {
        ConstructorInfo c when typeof(EditCommand).IsAssignableFrom(c.DeclaringType) => "makes a session command",
        MethodInfo m when typeof(EditCommand).IsAssignableFrom(m.ReturnType) => "makes a session command",
        MethodInfo m when MakesCommands(m.ReturnType) => "gets a delegate that makes a session command",
        MethodBase m when m.DeclaringType == typeof(EditTransaction) => "uses a transaction of the session",
        MethodInfo m when m.DeclaringType == typeof(EditBatch) && m.Name == nameof(EditBatch.Run) => "runs a session command",
        MethodInfo m when m.DeclaringType == typeof(CommandTransaction) && m.Name == nameof(CommandTransaction.Begin) =>
            "opens a gesture",
        MethodInfo m when IsExecute(m) => "runs a session command without a plan",
        _ => RunRule(member),
    };

    /// <summary>Why a type cannot call a member because it runs a session command, or null.</summary>
    private static string? RunRule(MemberInfo member) =>
        member is MethodInfo m && m.DeclaringType == typeof(EditSession) &&
        m.Name is nameof(EditSession.Execute) or nameof(EditSession.Begin) or nameof(EditSession.BeginBatch)
            ? "runs a session command"
            : null;

    /// <summary>True for a delegate type that gives a session command, for example
    /// <c>Func&lt;ThnValue, EditCommand&gt;</c>.</summary>
    private static bool MakesCommands(Type type) =>
        typeof(Delegate).IsAssignableFrom(type) && type.GetMethod(nameof(Action.Invoke)) is { } invoke &&
        typeof(EditCommand).IsAssignableFrom(invoke.ReturnType);

    /// <summary>True for <see cref="CommandRunner.Execute(EditCommand)"/> and its overload. They run a session command
    /// without a plan. Only the cutscene callers use them until milestone MCP1-4.</summary>
    private static bool IsExecute(MemberInfo member) =>
        member.DeclaringType == typeof(CommandRunner) && member.Name == nameof(CommandRunner.Execute);

    /// <summary>Each call of an assembly that a rule names, with the type that holds it.</summary>
    /// <remarks>A call of a member of the same type, for example of its own lambda, does not count. The walker reads
    /// the body of that member as a part of the type.</remarks>
    private static IEnumerable<EditCall> EditCalls(Assembly assembly, Func<MemberInfo, string?> rule) =>
        assembly.GetTypes().SelectMany(IlCalls.Of).Select(c => (Call: c, Reason: rule(c.Callee)))
            .Where(c => c.Reason != null &&
                        !(c.Call.Callee.DeclaringType is { } declaring && IlCalls.OwnerOf(declaring) == c.Call.Owner))
            .Select(c => new EditCall(c.Call.Owner, c.Call.Caller, c.Call.Callee, c.Call.Op, c.Reason!));

    /// <summary>A call that a rule names.</summary>
    private sealed record EditCall(Type Owner, MethodBase Caller, MemberInfo Callee, OpCode Op, string Reason)
    {
        public override string ToString() =>
            $"{Owner.FullName}.{Caller.Name} {Reason}: {Callee.DeclaringType?.Name}.{Callee.Name}";
    }

    /// <summary>An entry of the allow-list: the types that it covers, and the members that they can call.</summary>
    private sealed record Allowed(string Name, Func<Type, bool> Owner, Func<MemberInfo, bool> Callee)
    {
        public bool Covers(EditCall call) => Owner(call.Owner) && Callee(call.Callee);
    }
}

/// <summary>
/// A type that makes the edit calls of the editor rule in each form. The forms are a direct call, a call in a lambda, a
/// method group, a call in a local function and a delegate that makes a session command. The test of the walker reads
/// it.
/// </summary>
internal static class RawEditSample
{
    public static void Direct(EditSession session) => session.Execute(EditCommand.Group("Direct", []));

    public static Action Lambda(EditSession session) => () => session.Begin("Lambda").Dispose();

    public static Func<SectionRef, string?, EditCommand> MethodGroup() => EditCommand.Revert;

    public static CommandTransaction LocalFunction(EditSession session)
    {
        return Local();

        CommandTransaction Local() => CommandTransaction.Begin(session, "Local");
    }

    public static bool Plan(EditPlan plan) => plan.CanRun && plan.Command != null;

    public static bool Delegate(CommandRunner runner, ThnFieldCommand command, ThnValue value) =>
        runner.Execute(command.Make(value));
}

/// <summary>
/// The calls in the IL of the methods of a type: <c>call</c>, <c>callvirt</c>, <c>newobj</c>, <c>ldftn</c> and
/// <c>ldvirtftn</c>. The reader resolves each member with the generic arguments of the type and of the method. A
/// compiler-generated nested type, for example the class of a lambda or a state machine, counts as the type that holds
/// it. A local function is a method of its type.
/// </summary>
internal static class IlCalls
{
    /// <summary>A call: the type that holds it, the method that makes it, the member that it calls and its
    /// opcode.</summary>
    public sealed record Call(Type Owner, MethodBase Caller, MemberInfo Callee, OpCode Op);

    private static readonly OpCode[] OneByte = new OpCode[0x100];
    private static readonly OpCode[] TwoByte = new OpCode[0x100];

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                     BindingFlags.Static | BindingFlags.DeclaredOnly;

    static IlCalls()
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
    public static IEnumerable<Call> Of(Type type)
    {
        var owner = OwnerOf(type);
        var methods = type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All));
        foreach (var method in methods)
        {
            foreach (var call in CallsOf(method))
                yield return call with { Owner = owner };
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

    private static IEnumerable<Call> CallsOf(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            yield break;
        var typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var at = 0;
        while (at < il.Length)
        {
            var op = il[at] == 0xfe ? TwoByte[il[++at]] : OneByte[il[at]];
            at++;
            if (op.Size == 0)
                throw new InvalidOperationException($"{method.DeclaringType}.{method.Name}: unknown opcode at {at - 1}.");
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj || op == OpCodes.Ldftn ||
                op == OpCodes.Ldvirtftn)
            {
                var token = BitConverter.ToInt32(il, at);
                var callee = method.Module.ResolveMember(token, typeArguments, methodArguments) ??
                             throw new InvalidOperationException($"{method.Name}: token {token:x8} does not resolve.");
                yield return new Call(method.DeclaringType!, method, callee, op);
            }

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
