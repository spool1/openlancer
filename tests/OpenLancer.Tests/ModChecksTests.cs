using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The checks of the Mods window (decision D572 of the M11c design): a prepare of each target without a write,
/// on a worker thread, one at a time.</summary>
public sealed class ModChecksTests
{
    private const string Patch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    /// <summary>A check records the result of each target in the publish history, writes no package, and gives the
    /// texts of the Preview tab.</summary>
    [Fact]
    public void ACheckRecordsEachTargetAndGivesThePreviews()
    {
        using var game = new ExportGame();
        var mod = game.Mod("m", new() { [@"patches\" + ExportGame.GoodsPath] = Patch },
            """, "targets": ["vanilla-1.1", "openlancer"]""");
        var report = game.Report();
        var history = PublishHistory.FolderOf(game.Game.Folder, "m");

        var result = ModChecks.Run(new ModCheckRequest(mod.Folder, game.Game.Folder, [report], [game.Root]),
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

        Assert.Equal([ModTargetsOf("vanilla-1.1"), ModTargetsOf("openlancer")], result.Targets.Select(t => t.Target));
        Assert.All(result.Targets, t => Assert.Equal(0, t.Errors));
        Assert.Equal(0, result.Errors);
        Assert.Equal(
            ["README: drop-in zip, Freelancer 1.1", "README: FLMM package, Freelancer 1.1", "README: mod folder zip, OpenLancer",
             "FLMM script header: Freelancer 1.1", ModExport.ChecklistFile],
            result.Previews.Select(p => p.Name));
        Assert.StartsWith("Mod m\r\nVersion 1.2.0 for Freelancer 1.1. Mod id: m.", result.Previews[0].Text);
        Assert.StartsWith("<header name=\"Mod m\">\r\n<scriptversion>\r\n1.31\r\n</scriptversion>\r\n", result.Previews[3].Text);
        Assert.Contains("- Good 'lf_hull': price 6600 -> 5600.", result.Previews[4].Text);
        var recorded = PublishHistory.Load(history, "m").History;
        Assert.Equal(result.Content, recorded.CheckOf("vanilla-1.1")!.Content);
        Assert.Equal(TargetCheck.BaseKey(report), recorded.CheckOf("vanilla-1.1")!.Base);
        Assert.Equal(result.Content, recorded.CheckOf("openlancer")!.Content);
        Assert.Empty(recorded.Exports);
        Assert.Equal([PublishHistory.FileName], Directory.EnumerateFileSystemEntries(history).Select(Path.GetFileName));
    }

    /// <summary>A mod whose mod.json does not load gives one error, and the history does not change.</summary>
    [Fact]
    public void AModThatDoesNotLoadGivesItsError()
    {
        using var workspace = new PublishWorkspace();
        var folder = Path.Combine(workspace.Mods, "broken");
        PublishWorkspace.Write(Path.Combine(folder, "mod.json"), "{ not json");

        var result = ModChecks.Run(new ModCheckRequest(folder, workspace.Root, [], []),
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

        Assert.Equal(1, result.Errors);
        Assert.Empty(result.Targets);
        Assert.Empty(result.Previews);
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "exports")));
    }

    /// <summary>The checks run one at a time. A second request of a waiting mod adds no check, and a cancel drops the
    /// result of the running check and the checks that wait.</summary>
    [Fact]
    public async Task ChecksRunOneAtATimeAndCancelDropsTheResults()
    {
        var started = new List<string>();
        using var gate = new SemaphoreSlim(0);
        var checks = new ModChecks(request =>
        {
            lock (started)
                started.Add(Path.GetFileName(request.Folder));
            gate.Wait();
            return new ModCheckResult(request.Folder, DateTime.UtcNow, "c", "p", [], [], [], []);
        });
        var results = new List<string>();
        checks.Checked += r => results.Add(Path.GetFileName(r.Folder));

        checks.Request(Request("a"));
        checks.Request(Request("b"));
        checks.Request(Request("a"));
        checks.Update();

        Assert.True(checks.IsChecking(Folder("a")));
        Assert.Equal(Folder("a"), checks.Running);
        Assert.Equal(1, checks.Waiting);
        gate.Release();
        await RunAsync(checks, () => results.Count == 1);
        Assert.Equal(["a"], results);
        Assert.NotNull(checks.ResultOf(Folder("a")));
        await RunAsync(checks, () =>
        {
            lock (started)
                return started.Count == 2;
        });
        Assert.Equal(Folder("b"), checks.Running);
        lock (started)
            Assert.Equal(["a", "b"], started);

        checks.Request(Request("c"));
        checks.CancelAll();
        Assert.Null(checks.Running);
        Assert.Equal(0, checks.Waiting);
        checks.Request(Request("d"));
        gate.Release(2);
        await RunAsync(checks, () => results.Count == 2);

        Assert.Equal(["a", "d"], results);
        Assert.Null(checks.ResultOf(Folder("b")));
        lock (started)
            Assert.Equal(["a", "b", "d"], started);
    }

    /// <summary>A cancel drops the result of a running check, but its worker still writes the publish history. So a
    /// rename of the mod waits until the worker ends (review of M11c Build 2).</summary>
    [Fact]
    public async Task ACancelledCheckStillMakesTheModBusy()
    {
        using var gate = new SemaphoreSlim(0);
        var checks = new ModChecks(request =>
        {
            gate.Wait();
            return new ModCheckResult(request.Folder, DateTime.UtcNow, "c", "p", [], [], [], []);
        });

        checks.Request(Request("a"));
        checks.Request(Request("b"));
        Assert.True(checks.IsBusyWith(Folder("b")));
        checks.Update();
        checks.CancelAll();

        Assert.Null(checks.Running);
        Assert.False(checks.IsChecking(Folder("a")));
        Assert.True(checks.IsBusyWith(Folder("a")));
        Assert.False(checks.IsBusyWith(Folder("b")));
        gate.Release();
        await RunAsync(checks, () => !checks.IsBusyWith(Folder("a")));
        Assert.Null(checks.ResultOf(Folder("a")));
    }

    /// <summary>A check of a mod with a save that is not complete gives the text of a check (section 16 of the M11c
    /// design), and the history does not change.</summary>
    [Fact]
    public void ACheckOfAPendingSaveGivesTheTextOfACheck()
    {
        using var workspace = new PublishWorkspace();
        var mod = workspace.Mod("m");
        PublishWorkspace.Write(Path.Combine(mod, SaveTransaction.FolderName, "journal"), "olsave journal 1\n");

        var result = ModChecks.Run(new ModCheckRequest(mod, workspace.Root, [], []),
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

        Assert.Equal(PublishMessages.CheckPendingSave, Assert.Single(result.Problems).Message);
        Assert.Equal("The last save of this mod is not complete. Open the profile once. Then check the mod.",
            PublishMessages.CheckPendingSave);
        Assert.False(Directory.Exists(workspace.Exports("m")));
    }

    private static string Folder(string name) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ol-checks", name));

    private static ModCheckRequest Request(string name) => new(Folder(name), Path.GetTempPath(), [], []);

    private static ExportTarget ModTargetsOf(string id) => ExportTarget.Find(id)!;

    /// <summary>Runs the frames of the window until the condition is true.</summary>
    private static async Task RunAsync(ModChecks checks, Func<bool> done)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < end, "The checks did not end in 30 s.");
            checks.Update();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
