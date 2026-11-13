using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The base run of the Console out of process (decision D516 of the console filter design), with stub
/// programs in place of oltool.</summary>
public sealed class BaseRunRunnerTests
{
    private const string Json = """{"Warning|Ini|Unknown entry k|zone|data\\a.ini":2}""";

    [Fact]
    public void TheResultHasTheCountsOrTheFailure()
    {
        var counts = BaseRunRunner.Result(0, Json, "");
        Assert.Equal(2, counts.Counts!["Warning|Ini|Unknown entry k|zone|data\\a.ini"]);
        Assert.Null(counts.Failure);

        var failed = BaseRunRunner.Result(2, "", "The folder x does not exist.\n");
        Assert.Equal((null, "The folder x does not exist."), (failed.Counts, failed.Failure));
        Assert.Contains("exit code 3", BaseRunRunner.Result(3, "", "").Failure);
        Assert.NotNull(BaseRunRunner.Result(0, "garbage", "").Failure);
    }

    /// <summary>The counts go through JSON without a change.</summary>
    [Fact]
    public void TheCountsGoThroughJson()
    {
        var counts = new Dictionary<string, int> { ["b"] = 1, ["a|x"] = 3 };

        Assert.Equal(counts, BaseEngineLines.ReadJson(BaseEngineLines.ToJson(counts)));
        Assert.Throws<InvalidDataException>(() => BaseEngineLines.ReadJson("[]"));
    }

    [Fact]
    public void TheCommandLoadsTheBaseFolderAsJson()
    {
        var command = BaseRunRunner.OltoolCommand(@"C:\Games\Base");

        Assert.Equal(["engine-lines", @"C:\Games\Base", "--json"], command.ArgumentList);
        Assert.Equal(ValidationRunnerCommandName(), command.FileName);
    }

    /// <summary>A new run stops the run before it, and the result of the old run is dropped, for example the run of a
    /// profile that closed. The slow stub writes a file when it is not stopped.</summary>
    [Fact]
    public async Task ANewRunDropsTheResultOfTheOldRun()
    {
        using var ws = new TestWorkspace();
        var ended = Path.Combine(ws.Root, "slow-ended.txt");
        var ui = new QueuedUIThread();
        var results = new List<(string Folder, BaseRunResult Result)>();
        using var runner = new BaseRunRunner(folder => folder == "slow" ? Slow(ended) : Fast());

        runner.Start("slow", ui, r => results.Add(("slow", r)));
        Assert.True(runner.Running);
        runner.Start("fast", ui, r => results.Add(("fast", r)));
        // Each process queues its result when it ends: the first when the second start stopped it.
        await ui.RunAsync(2);

        var (folder, result) = Assert.Single(results);
        Assert.Equal("fast", folder);
        Assert.Equal(2, result.Counts!.Count);
        Assert.False(runner.Running);
        Assert.False(File.Exists(ended));
    }

    /// <summary>A stop drops the result of the run, for example when the profile closes.</summary>
    [Fact]
    public async Task AStopDropsTheResult()
    {
        var ui = new QueuedUIThread();
        var results = new List<BaseRunResult>();
        using var runner = new BaseRunRunner(_ => Fast());

        runner.Start("fast", ui, results.Add);
        runner.Stop();
        await ui.RunUntilQuietAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(results);
        Assert.False(runner.Running);
    }

    /// <summary>A program that does not start gives its failure at once.</summary>
    [Fact]
    public void AProgramThatDoesNotStartGivesItsFailureAtOnce()
    {
        using var ws = new TestWorkspace();
        var results = new List<BaseRunResult>();
        using var runner = new BaseRunRunner(_ => new ProcessStartInfo(Path.Combine(ws.Root, "missing.exe")));

        runner.Start("x", new QueuedUIThread(), results.Add);

        var result = Assert.Single(results);
        Assert.Null(result.Counts);
        Assert.Contains("did not start", result.Failure);
    }

    [Fact]
    public void TheFailureTextNamesTheCause()
    {
        Assert.Equal("OpenLancer cannot compare the engine messages with the base game. The Console shows all engine " +
                     "messages of this profile. Cause: oltool did not start.",
            BaseRunRunner.FailedText("oltool did not start."));
    }

    private static string ValidationRunnerCommandName() =>
        OpenLancer.Editor.Editing.ValidationRunner.OltoolCommand("p", null).FileName;

    /// <summary>A stub program that waits about three seconds, writes the file, then writes counts.</summary>
    private static ProcessStartInfo Slow(string ended) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & echo ended>\"{ended}\" & echo {{\"a\":1}}")
        : new ProcessStartInfo("/bin/sh", ["-c", $"sleep 3; echo ended > '{ended}'; echo '{{\"a\":1}}'"]);

    /// <summary>A stub program that writes two counts.</summary>
    private static ProcessStartInfo Fast() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c echo {\"a\":1,\"b\":2}")
        : new ProcessStartInfo("/bin/sh", ["-c", "echo '{\"a\":1,\"b\":2}'"]);
}
