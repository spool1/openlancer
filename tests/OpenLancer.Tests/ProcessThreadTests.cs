using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Editor.Play;
using OpenLancer.Studio;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A program of a play runs for a long time, and the work that waits for it blocks its thread. So
/// <see cref="PlayProcess"/> and <see cref="GameProcess"/> start that work only through <see cref="OwnThread"/>. The
/// test reads their IL, also the IL of their lambdas. It fails at each call that can put their work on the thread pool.
/// </summary>
/// <remarks>The process tests show that no action of a program comes to the UI queue from a thread of the pool. The
/// reader of the error output queues no action, so only this test covers it.</remarks>
public sealed class ProcessThreadTests
{
    private static readonly Type[] ProcessTypes = [typeof(PlayProcess), typeof(GameProcess)];

    [Fact]
    public void TheProcessTypesPutNoWorkOnTheThreadPool()
    {
        var found = PoolCalls(typeof(PlayProcess).Assembly, ProcessTypes.Contains)
            .Select(c => $"{c.Call.Owner.Name}.{c.Call.Caller.Name} {c.Reason}: {Name(c.Call.Callee)}")
            .Distinct()
            .ToList();

        Assert.True(found.Count == 0, "Calls that can put the work of a program on the thread pool:\n" +
                                      string.Join("\n", found));
    }

    /// <summary>Each process type starts its work through <see cref="OwnThread"/>. So the walker reads the IL of the
    /// types, and the test above does not pass with no calls.</summary>
    [Fact]
    public void EachProcessTypeStartsItsWorkThroughOwnThread()
    {
        var owners = typeof(PlayProcess).Assembly.GetTypes().SelectMany(IlWalker.Calls)
            .Where(c => c.Callee.DeclaringType == typeof(OwnThread))
            .Select(c => c.Owner)
            .ToHashSet();

        Assert.All(ProcessTypes, type => Assert.Contains(type, owners));
    }

    /// <summary>The rule finds each form of pool work of <see cref="PoolWorkSample"/>, also in a lambda. It does not
    /// find the work of <see cref="OwnThread"/>.</summary>
    [Fact]
    public void TheRuleFindsEachFormOfPoolWork()
    {
        var found = PoolCalls(typeof(ProcessThreadTests).Assembly, t => t == typeof(PoolWorkSample))
            .Select(c => Name(c.Call.Callee))
            .Distinct()
            .Order(StringComparer.Ordinal);

        string[] expected =
        [
            "Process.BeginOutputReadLine", "Process.add_Exited", "Task.ContinueWith", "Task.Run",
            "TaskFactory.StartNew", "TextReader.ReadLineAsync", "ThreadPool.QueueUserWorkItem", "Timer..ctor",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), found);
    }

    /// <summary>Why a process type cannot call a member, or null.</summary>
    /// <remarks>A call that gives a task is not always pool work. But a process type has no other reason to get a
    /// task, so the rule names each one.</remarks>
    private static string? PoolRule(MemberInfo member) => member switch
    {
        _ when member.DeclaringType == typeof(OwnThread) => null,
        MethodInfo m when IsTask(m.ReturnType) => "gets a task, and its work or its continuation can wait for the pool",
        MethodBase m when m.DeclaringType == typeof(ThreadPool) => "queues work on the thread pool",
        ConstructorInfo c when c.DeclaringType == typeof(Timer) => "starts a timer, and its callback runs on the pool",
        MethodInfo m when m.DeclaringType == typeof(Process) &&
                          m.Name is "add_Exited" or "add_OutputDataReceived" or "add_ErrorDataReceived" or
                              nameof(Process.BeginOutputReadLine) or nameof(Process.BeginErrorReadLine) =>
            "gets the events of the process on the thread pool",
        _ => null,
    };

    private static bool IsTask(Type type) =>
        typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask) ||
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>);

    /// <summary>Each call of the types that the filter selects that the rule names, with the reason.</summary>
    private static IEnumerable<(IlWalker.Call Call, string Reason)> PoolCalls(Assembly assembly, Func<Type, bool> owner) =>
        from type in assembly.GetTypes()
        where owner(IlWalker.OwnerOf(type))
        from call in IlWalker.Calls(type)
        let reason = PoolRule(call.Callee)
        where reason != null
        select (call, reason);

    private static string Name(MemberInfo member) => $"{member.DeclaringType?.Name}.{member.Name}";
}

/// <summary>
/// A type that puts work on the thread pool in each form that the rule of <see cref="ProcessThreadTests"/> names. It
/// also starts work through <see cref="OwnThread"/>, which the rule does not name. The test of the rule reads it.
/// </summary>
internal static class PoolWorkSample
{
    public static Task Run(Action work) => Task.Run(work);

    public static Action StartInALambda(Action work) => () => Task.Factory.StartNew(work);

    public static Task Continue(Task task, Action work) => task.ContinueWith(_ => work());

    public static Task<string?> Read(TextReader reader) => reader.ReadLineAsync();

    public static bool Queue(Action work) => ThreadPool.QueueUserWorkItem(_ => work());

    public static Timer StartTimer(Action work) => new(_ => work(), null, 0, Timeout.Infinite);

    public static void Events(Process process, Action work)
    {
        process.Exited += (_, _) => work();
        process.BeginOutputReadLine();
    }

    public static Task Own(Action work) => OwnThread.Run(work);
}
