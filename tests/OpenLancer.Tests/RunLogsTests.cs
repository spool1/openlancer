using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Editor;
using OpenLancer.Runtime;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The log of the last run of a program stays in its previous log when the program starts again.</summary>
public sealed class RunLogsTests : IDisposable
{
    private const string Program = "OpenLancer.Play";

    private readonly TestWorkspace ws = new();
    private readonly string folder;

    public RunLogsTests()
    {
        folder = Path.Combine(ws.Root, "logs");
        Directory.CreateDirectory(folder);
    }

    public void Dispose() => ws.Dispose();

    private string Log => Path.Combine(folder, "OpenLancer.Play.log.txt");

    private string Old => Path.Combine(folder, "OpenLancer.Play.log.old.txt");

    private string Previous => Path.Combine(folder, "OpenLancer.Play.log.previous.txt");

    private string PreviousOld => Path.Combine(folder, "OpenLancer.Play.log.previous.old.txt");

    [Fact]
    public void TheLogsAreInTheLogFolderOfOpenLancer()
    {
        var logs = AppHandler.LogFolder(Product.Name);

        Assert.Equal(logs, RunLogs.Folder);
        Assert.Equal(Path.Combine(logs, "OpenLancer.Editor.log.txt"), RunLogs.PathOf("OpenLancer.Editor"));
        Assert.Equal(Path.Combine(logs, "OpenLancer.Editor.log.previous.txt"), RunLogs.PreviousPathOf("OpenLancer.Editor"));
        Assert.Equal(Path.Combine(logs, "OpenLancer.Play.log.previous.txt"), ProgramPaths.PreviousPlayLog);
    }

    [Fact]
    public void TheLogMovesToThePreviousLog()
    {
        File.WriteAllText(Log, "run 1");

        RunLogs.KeepLastRun(Program, folder);

        Assert.False(File.Exists(Log));
        Assert.Equal("run 1", File.ReadAllText(Previous));
        Assert.False(File.Exists(PreviousOld));
    }

    [Fact]
    public void TheOldFileMovesWithTheLog()
    {
        File.WriteAllText(Log, "run 1 new lines");
        File.WriteAllText(Old, "run 1 old lines");

        RunLogs.KeepLastRun(Program, folder);

        Assert.False(File.Exists(Log));
        Assert.False(File.Exists(Old));
        Assert.Equal("run 1 new lines", File.ReadAllText(Previous));
        Assert.Equal("run 1 old lines", File.ReadAllText(PreviousOld));
    }

    [Fact]
    public void ASecondKeepReplacesThePreviousLog()
    {
        File.WriteAllText(Log, "run 1");
        RunLogs.KeepLastRun(Program, folder);
        File.WriteAllText(Log, "run 2");

        RunLogs.KeepLastRun(Program, folder);

        Assert.Equal("run 2", File.ReadAllText(Previous));
        Assert.False(File.Exists(Log));
    }

    [Fact]
    public void AKeepWithoutAnOldFileDeletesAStalePreviousOldFile()
    {
        File.WriteAllText(Previous, "run 1 new lines");
        File.WriteAllText(PreviousOld, "run 1 old lines");
        File.WriteAllText(Log, "run 2");

        RunLogs.KeepLastRun(Program, folder);

        // The two previous files come from one run.
        Assert.Equal("run 2", File.ReadAllText(Previous));
        Assert.False(File.Exists(PreviousOld));
    }

    [Fact]
    public void AMissingFolderOrLogDoesNothing()
    {
        var missing = Path.Combine(ws.Root, "no logs");
        RunLogs.KeepLastRun(Program, missing);
        Assert.False(Directory.Exists(missing));

        File.WriteAllText(Previous, "run 1 new lines");
        File.WriteAllText(PreviousOld, "run 1 old lines");
        File.WriteAllText(Old, "an old file without its log");

        RunLogs.KeepLastRun(Program, folder);

        Assert.Equal("run 1 new lines", File.ReadAllText(Previous));
        Assert.Equal("run 1 old lines", File.ReadAllText(PreviousOld));
        Assert.Equal("an old file without its log", File.ReadAllText(Old));
    }

    [Fact]
    public void ALogThatAnotherProgramWritesStaysWithItsOldFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to move a file that another program has open.");
        File.WriteAllText(Previous, "run 1");
        File.WriteAllText(Old, "run 2 old lines");
        // The engine opens the log as this stream does: other programs can read it, but not move or delete it. For
        // example a second editor starts while the first one runs.
        using (var held = new FileStream(Log, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            held.Write("run 2"u8);
            held.Flush();

            RunLogs.KeepLastRun(Program, folder);

            Assert.True(File.Exists(Log));
        }

        Assert.Equal("run 2", File.ReadAllText(Log));
        Assert.Equal("run 2 old lines", File.ReadAllText(Old));
        Assert.Equal("run 1", File.ReadAllText(Previous));
        Assert.False(File.Exists(PreviousOld));
    }

    /// <summary>The editor and the game keep the last log before the engine starts the new log in
    /// <c>AppHandler.Run</c>.</summary>
    [Theory]
    [InlineData("OpenLancer.Editor")]
    [InlineData("OpenLancer.Play")]
    public void EachProgramKeepsTheLastLogBeforeTheEngineStartsANewLog(string program)
    {
        var main = Assembly.Load(program).GetType(program + ".Program", true)!;
        var calls = IlWalker.Calls(main).Where(c => c.Caller.Name == "Main").Select(c => c.Callee).ToList();

        var keep = calls.FindIndex(c => c.DeclaringType == typeof(RunLogs) && c.Name == nameof(RunLogs.KeepLastRun));
        var run = calls.FindIndex(c => c.DeclaringType == typeof(AppHandler) && c.Name == nameof(AppHandler.Run));

        Assert.True(keep >= 0, $"{program} does not keep the last log.");
        Assert.True(run > keep, $"{program} keeps the last log after the engine starts the new log.");
    }

    /// <summary>Main gives the call its own assembly, <c>typeof(Program).Assembly</c>, and no text. The engine names
    /// the log with the name of the assembly that calls <c>AppHandler.Run</c>, and Main makes that call. So the kept log
    /// is the log that the engine writes.</summary>
    [Theory]
    [InlineData("OpenLancer.Editor")]
    [InlineData("OpenLancer.Play")]
    public void EachProgramKeepsTheLogOfItsOwnAssembly(string program)
    {
        var type = Assembly.Load(program).GetType(program + ".Program", true)!;
        var main = type.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
        var il = IlWalker.Instructions(main).ToList();
        MethodBase? Callee(int index) => il[index].Op.OperandType == OperandType.InlineMethod
            ? main.Module.ResolveMethod((int)il[index].Operand)
            : null;

        var at = Enumerable.Range(0, il.Count)
            .Single(i => Callee(i) is { Name: nameof(RunLogs.KeepLastRun) } m && m.DeclaringType == typeof(RunLogs));

        Assert.Equal([typeof(Assembly)], Callee(at)!.GetParameters().Select(p => p.ParameterType));
        Assert.True(at >= 3, $"{program} gives the call no assembly of a type.");
        Assert.Equal(OpCodes.Ldtoken, il[at - 3].Op);
        Assert.Equal(type, main.Module.ResolveType((int)il[at - 3].Operand));
        Assert.Equal(nameof(Type.GetTypeFromHandle), Callee(at - 2)?.Name);
        Assert.Equal("get_" + nameof(Type.Assembly), Callee(at - 1)?.Name);
    }
}
