using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Validation;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The validation after a save: the JSON list that <c>oltool validate --json</c> writes, the result that the editor
/// reads from the process, and the runner that starts one process at a time. The retail test
/// <c>OltoolValidateTests</c> runs the real oltool.
/// </summary>
public sealed class ValidationTests
{
    [Fact]
    public void TheJsonListKeepsEachProblem()
    {
        Diagnostic[] problems =
        [
            Diagnostic.Error(@"C:\mods\proof\patches\goods.ini", "a \"quoted\" error"),
            Diagnostic.Warning("Engine", "a warning"),
        ];

        var json = ValidationJson.Write(problems);

        Assert.Contains("\"severity\": \"error\"", json);
        Assert.Equal(problems, ValidationJson.Read(json));
        Assert.Empty(ValidationJson.Read(ValidationJson.Write([])));
    }

    /// <summary>A problem of a cutscene keeps its place: the cutscene, the element with its key and the value, or only
    /// the cutscene.</summary>
    [Fact]
    public void TheJsonListKeepsThePlaceOfAProblemOfACutscene()
    {
        Diagnostic[] problems =
        [
            Diagnostic.Error(@"DATA\x.thn", "entity \"A\", cameraprops.fovh: m") with
            {
                Cutscene = new ThnLocation(@"DATA\x.thn", ThnElementKind.Entity, 3, "cameraprops.fovh", "\"A\""),
            },
            Diagnostic.Error(@"DATA\x.thn", "event: m") with { Cutscene = new ThnLocation(@"DATA\x.thn", ThnElementKind.Event, 0) },
            Diagnostic.Error(@"DATA\x.thn", "event { 1, START_PSYS, { \"Fx\" } }, time: m") with
            {
                Cutscene = new ThnLocation(@"DATA\x.thn", ThnElementKind.Event, 2, "time", "{ 1, START_PSYS, { \"Fx\" } }"),
            },
            Diagnostic.Error(@"patches\DATA\x.thn", "m") with { Cutscene = new ThnLocation(@"DATA\x.thn") },
        ];

        var json = ValidationJson.Write(problems);

        Assert.Contains("\"kind\": \"entity\"", json);
        Assert.Contains("\"key\":", json);
        Assert.Equal(problems, ValidationJson.Read(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{\"severity\": \"fatal\", \"source\": \"s\", \"message\": \"m\"}]")]
    [InlineData("[{\"severity\": \"error\", \"message\": \"m\"}]")]
    [InlineData("[null]")]
    [InlineData("[{\"severity\": \"error\", \"source\": \"s\", \"message\": \"m\", \"cutscene\": {\"kind\": \"entity\", \"index\": 0}}]")]
    [InlineData("[{\"severity\": \"error\", \"source\": \"s\", \"message\": \"m\", \"cutscene\": {\"path\": \"p\", \"kind\": \"part\", \"index\": 0}}]")]
    [InlineData("[{\"severity\": \"error\", \"source\": \"s\", \"message\": \"m\", \"cutscene\": {\"path\": \"p\", \"kind\": \"event\", \"index\": -1}}]")]
    [InlineData("[{\"severity\": \"error\", \"source\": \"s\", \"message\": \"m\", \"cutscene\": {\"path\": \"p\", \"index\": 2}}]")]
    [InlineData("[{\"severity\": \"error\", \"source\": \"s\", \"message\": \"m\", \"cutscene\": {\"path\": \"p\", \"key\": \"k\"}}]")]
    public void TextThatIsNotAListOfProblemsIsRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => ValidationJson.Read(json));
    }

    [Fact]
    public void ExitCodesZeroAndOneHaveTheProblemsAndTwoHasTheFailure()
    {
        var json = ValidationJson.Write([Diagnostic.Error("s", "m")]);

        Assert.Equal([Diagnostic.Error("s", "m")], ValidationRunner.Result(4, 1, json, "")!.Problems);
        Assert.Empty(ValidationRunner.Result(4, 0, "[]", "")!.Problems!);
        var failed = ValidationRunner.Result(4, 2, "", "profile.json: File not found.\n");
        Assert.Null(failed.Problems);
        Assert.Equal("profile.json: File not found.", failed.Failure);
        Assert.Equal(4, failed.Save);
        Assert.Contains("exit code 3", ValidationRunner.Result(4, 3, "", "").Failure);
        Assert.NotNull(ValidationRunner.Result(4, 0, "garbage", "").Failure);
    }

    /// <summary>
    /// A new validation stops the running one: its process ends before the files change, and its result does not get
    /// to the editor (review finding F16). The first stub program writes a file when it is not stopped.
    /// </summary>
    [Fact]
    public async Task ANewValidationStopsTheRunningOneAndDropsItsResult()
    {
        using var ws = new TestWorkspace();
        var ended = Path.Combine(ws.Root, "slow-ended.txt");
        var ui = new QueuedUIThread();
        var results = new List<ValidationResult>();
        using var runner = new ValidationRunner((profile, _) => profile == "slow" ? Slow(ended) : Fast());

        runner.Start("slow", "act", 1, ui, results.Add);
        Assert.Equal(new ValidationState(1, true, null), runner.State);
        runner.Start("fast", "act", 2, ui, results.Add);
        Assert.Equal(new ValidationState(2, true, null), runner.State);
        // Each process queues its result when it ends: the first when the second start stopped it.
        await ui.RunAsync(2);

        var result = Assert.Single(results);
        Assert.Equal(2, result.Save);
        Assert.Empty(result.Problems!);
        Assert.Equal(new ValidationState(2, false, result), runner.State);
        Assert.False(File.Exists(ended));
    }

    /// <summary>
    /// The state that the gate of Play reads (section 9.3 of the M4 design). A stop keeps the save without a result,
    /// and its result does not come. <see cref="ValidationRunner.Validated"/> comes after the done action, so the
    /// Console has the result first. A reset forgets the state.
    /// </summary>
    [Fact]
    public async Task AStoppedValidationKeepsItsSaveWithoutAResult()
    {
        using var ws = new TestWorkspace();
        var ui = new QueuedUIThread();
        var calls = new List<string>();
        using var runner = new ValidationRunner((profile, _) =>
            profile == "slow" ? Slow(Path.Combine(ws.Root, "slow-ended.txt")) : Fast());
        runner.Validated += state => calls.Add($"validated {state.Save} {state.Running}");
        Assert.Null(runner.State);

        runner.Start("slow", "act", 1, ui, result => calls.Add($"done {result.Save}"));
        runner.Stop();

        Assert.Equal(new ValidationState(1, false, null), runner.State);
        // The stopped process queues its result when it ends, and the runner drops it.
        await ui.RunAsync(1);
        Assert.Empty(calls);
        Assert.Equal(new ValidationState(1, false, null), runner.State);

        runner.Start("fast", "act", 2, ui, result =>
        {
            calls.Add($"done {result.Save}");
            Assert.Equal(new ValidationState(2, false, result), runner.State);
        });
        await ui.RunAsync(1);
        Assert.Equal(["done 2", "validated 2 False"], calls);
        Assert.NotNull(runner.State!.Result);

        runner.Reset();
        Assert.Null(runner.State);
    }

    /// <summary>A program that does not start gives its result during the start. The state has the failure.</summary>
    [Fact]
    public void AProgramThatDoesNotStartGivesItsResultAtOnce()
    {
        using var ws = new TestWorkspace();
        var calls = new List<string>();
        using var runner = new ValidationRunner((_, _) => new ProcessStartInfo(Path.Combine(ws.Root, "missing.exe")));
        runner.Validated += state => calls.Add($"validated {state.Save} {state.Running}");

        runner.Start("p", null, 3, new QueuedUIThread(), result => calls.Add($"done {result.Save}"));

        Assert.Equal(["done 3", "validated 3 False"], calls);
        Assert.Null(runner.State!.Result!.Problems);
        Assert.Contains("did not start", runner.State.Result.Failure);
        Assert.Equal(3, runner.State.Save);
    }

    /// <summary>A reset during a validation drops its result, for example when the profile closes.</summary>
    [Fact]
    public async Task AResetDropsTheResultOfTheRunningValidation()
    {
        var ui = new QueuedUIThread();
        var calls = new List<string>();
        using var runner = new ValidationRunner((_, _) => Fast());
        runner.Validated += state => calls.Add($"validated {state.Save}");

        runner.Start("p", "act", 4, ui, result => calls.Add($"done {result.Save}"));
        runner.Reset();
        await ui.RunUntilQuietAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(calls);
        Assert.Null(runner.State);
    }

    /// <summary>
    /// The command validates the active mod against the mods before it. Without an active mod, it has no
    /// <c>--active</c>, so oltool reports what all mods add to vanilla (section 9.3 of the M4 design).
    /// </summary>
    [Fact]
    public void TheCommandHasActiveOnlyWithAnActiveMod()
    {
        var withMod = ValidationRunner.OltoolCommand(@"C:\profiles\p.json", "proof");
        var withoutMod = ValidationRunner.OltoolCommand(@"C:\profiles\p.json", null);

        Assert.Equal(["validate", @"C:\profiles\p.json", "--active", "proof", "--json"], withMod.ArgumentList);
        Assert.Equal(["validate", @"C:\profiles\p.json", "--json"], withoutMod.ArgumentList);
        Assert.Equal(withMod.FileName, withoutMod.FileName);
    }

    /// <summary>A stub program that waits about three seconds, writes the file, then writes an empty list.</summary>
    private static ProcessStartInfo Slow(string ended) => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & echo ended>\"{ended}\" & echo []")
        : new ProcessStartInfo("/bin/sh", ["-c", $"sleep 3; echo ended > '{ended}'; echo []"]);

    /// <summary>A stub program that writes an empty list.</summary>
    private static ProcessStartInfo Fast() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c echo []")
        : new ProcessStartInfo("/bin/sh", ["-c", "echo []"]);
}
