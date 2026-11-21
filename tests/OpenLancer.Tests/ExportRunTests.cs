using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Export;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The tasks of one export of the Export dialog (milestone M11c). A close of the dialog with Escape while the
/// export runs must not leave an export that never ends, and a write that ends after the close still goes into the
/// history.</summary>
public sealed class ExportRunTests
{
    private const string Patch = "[Good]\nnickname = lf_hull\nprice = 5600\n";

    private static HistoryLocation Location(ExportGame game, Mod mod) =>
        new(PublishHistory.FolderOf(game.Game.Folder, mod.Id), mod.Id, mod.Folder, [game.Root]);

    private static GuardedExport Prepare(ExportGame game, Mod mod) =>
        GuardedExport.Prepare(game.Request(Mod.Load(mod.Folder)) with
        {
            OutputFolder = Path.Combine(PublishHistory.FolderOf(game.Game.Folder, mod.Id), "1.2.0"),
            Forms = ExportForms.DropIn,
        }, Location(game, mod), DateTime.UtcNow);

    /// <summary>Calls <see cref="ExportRun.Update"/> as the frames of the editor do, until the run ends.</summary>
    private static async Task FinishAsync(ExportRun run)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (run.Running)
        {
            Assert.True(DateTime.UtcNow < end, "The export did not end in 60 s.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
            run.Update();
        }
    }

    /// <summary>A close during the prepare drops its result: no guard question and no write. The run then ends, so the
    /// editor can export again.</summary>
    [Fact]
    public async Task ACloseDuringThePrepareDropsItsResult()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var run = new ExportRun();
        var prepared = new List<GuardedExport>();
        run.Prepared += prepared.Add;
        using var gate = new ManualResetEventSlim();

        run.Prepare(() =>
        {
            gate.Wait();
            return Prepare(game, mod);
        });
        run.Close();
        gate.Set();
        await FinishAsync(run);

        Assert.True(run.Closed);
        Assert.Empty(prepared);
        Assert.Empty(PublishHistory.Load(Location(game, mod).Folder, mod.Id).History.Exports);
        Assert.False(Directory.Exists(Path.Combine(Location(game, mod).Folder, "1.2.0")));
    }

    /// <summary>A write that ends after a close of the dialog still records the export in the history, and the result
    /// tells that the dialog closed, so that Export and play does not start the game.</summary>
    [Fact]
    public async Task AWriteAfterACloseStillRecordsTheExport()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var run = new ExportRun();
        GuardedExport? prepared = null;
        ExportWritten? written = null;
        run.Prepared += export => prepared = export;
        run.Written += result => written = result;

        run.Prepare(() => Prepare(game, mod));
        await FinishAsync(run);
        Assert.NotNull(prepared);
        run.Write(prepared, replace: false);
        Assert.True(run.Running);
        run.Close();
        await FinishAsync(run);

        Assert.NotNull(written);
        Assert.True(written.Closed);
        Assert.True(written.Result.Succeeded, string.Join(" | ", written.Result.Problems));
        Assert.Equal(RecordKind.Added, written.Record!.Kind);
        var entry = Assert.Single(PublishHistory.Load(Location(game, mod).Folder, mod.Id).History.Exports);
        Assert.Equal(written.Result.Packages[0].Sha256, Assert.Single(entry.Packages).Sha256);
    }

    /// <summary>A run takes one export at a time. A prepare that throws gives its reason, and the run ends.</summary>
    [Fact]
    public async Task AFailedPrepareGivesItsReason()
    {
        var run = new ExportRun();
        var failures = new List<string>();
        run.Failed += failures.Add;
        using var gate = new ManualResetEventSlim();

        run.Prepare(() =>
        {
            gate.Wait();
            throw new IOException("The disk is full.");
        });
        Assert.Throws<InvalidOperationException>(() => run.Prepare(() => null!));
        gate.Set();
        await FinishAsync(run);

        Assert.Equal(["The disk is full."], failures);
        Assert.False(run.Running);
    }

    /// <summary>The save gate of a mod runs on the thread of the prepare, before it (#87). Errors stop the run: the
    /// dialog gets them, and the prepare does not run. A gate without errors lets the prepare run.</summary>
    [Fact]
    public async Task TheErrorsOfTheSaveGateStopThePrepare()
    {
        using var game = new ExportGame();
        var mod = game.GoodsMod(Patch);
        var run = new ExportRun();
        var gated = new List<IReadOnlyList<OpenLancer.Data.Diagnostic>>();
        var prepared = new List<GuardedExport>();
        run.Gated += gated.Add;
        run.Prepared += prepared.Add;
        var error = OpenLancer.Data.Diagnostic.Error(ExportGame.GoodsPath, "The game crashes.");

        run.Prepare(() => [error], () => throw new InvalidOperationException("The prepare must not run."));
        await FinishAsync(run);

        Assert.Equal([error], Assert.Single(gated));
        Assert.Empty(prepared);
        Assert.False(Directory.Exists(Path.Combine(Location(game, mod).Folder, "1.2.0")));
        run.Prepare(() => [], () => Prepare(game, mod));
        await FinishAsync(run);
        Assert.Single(gated);
        Assert.Single(prepared);
    }
}
