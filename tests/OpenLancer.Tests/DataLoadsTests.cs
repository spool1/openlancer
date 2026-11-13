using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Editor;
using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The loads of the engine data of a session, without a window (see <see cref="DataLoads"/>). Each load fills the same
/// resources of the session, so two loads must never run at the same time. The fake loads count the loads that use the
/// resources at the same time, and the queued UI thread runs the ends of the loads as the frames of the editor do.
/// </summary>
public sealed class DataLoadsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The resources of a session. They count the loads that use them at the same time.</summary>
    private sealed class Resources
    {
        private int users;
        private int most;

        /// <summary>The most loads that used the resources at the same time.</summary>
        public int Most => Volatile.Read(ref most);

        /// <summary>True while a load uses the resources.</summary>
        public bool InUse => Volatile.Read(ref users) > 0;

        /// <summary>Uses the resources until <paramref name="until"/> is set, or for a moment when it is null.</summary>
        public void Use(ManualResetEventSlim? until)
        {
            var now = Interlocked.Increment(ref users);
            for (var seen = Volatile.Read(ref most); now > seen; seen = Volatile.Read(ref most))
            {
                Interlocked.CompareExchange(ref most, now, seen);
            }

            if (until == null)
            {
                Thread.Sleep(20);
            }
            else
            {
                Assert.True(until.Wait(Timeout), "The test did not end the load.");
            }

            Interlocked.Decrement(ref users);
        }
    }

    /// <summary>
    /// The race of fix S1: the user restores the unsaved edits of an earlier session at the open of the profile, then
    /// presses Ctrl+S before the first load of the game data ends. The save asks for a reload. The reload must wait for
    /// the first load, because both fill the same resources. A second save during the load gives no second reload.
    /// </summary>
    [Fact]
    public async Task ASaveDuringTheFirstLoadReloadsAfterTheLoadEnds()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        var resources = new Resources();
        var events = new List<string>();
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        loads.Load(() =>
        {
            running.Set();
            resources.Use(release);
            return () => events.Add("loaded");
        });
        Assert.True(running.Wait(Timeout, TestContext.Current.CancellationToken));

        var starts = 0;
        DataLoad StartReload()
        {
            var number = ++starts;
            events.Add($"reload {number} started");
            return () =>
            {
                resources.Use(null);
                return () => events.Add($"reload {number} loaded");
            };
        }

        // Two saves during the first load.
        bool[] started = [loads.Reload(StartReload), loads.Reload(StartReload)];
        var waiting = (loads.IsLoading, loads.IsReloading, loads.ReloadWaits, starts);

        release.Set();
        await ui.RunUntilAsync(() => events.Contains("reload 1 loaded"), "the reload after the first load");

        // The race comes first: a reload that starts at once uses the resources with the first load.
        Assert.Equal(1, resources.Most);
        Assert.Equal([false, false], started);
        Assert.Equal((true, false, true, 0), waiting);
        Assert.Equal(["loaded", "reload 1 started", "reload 1 loaded"], events);
        Assert.False(loads.IsBusy);
        Assert.False(loads.ReloadWaits);
    }

    /// <summary>A save during a reload gives one more reload after it, which reads the files of the last save.</summary>
    [Fact]
    public async Task ASaveDuringAReloadReloadsAgainAfterIt()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        var resources = new Resources();
        var events = new List<string>();
        using var release = new ManualResetEventSlim();
        loads.Load(() => () => events.Add("loaded"));
        await ui.RunUntilAsync(() => !loads.IsBusy, "the first load");

        Assert.True(loads.Reload(() =>
        {
            events.Add("reload 1 started");
            return () =>
            {
                resources.Use(release);
                return () => events.Add("reload 1 loaded");
            };
        }));
        Assert.False(loads.Reload(() =>
        {
            events.Add("reload 2 started");
            return () =>
            {
                resources.Use(null);
                return () => events.Add("reload 2 loaded");
            };
        }));
        Assert.True(loads.IsReloading);

        release.Set();
        await ui.RunUntilAsync(() => events.Contains("reload 2 loaded"), "the second reload");

        Assert.Equal(1, resources.Most);
        Assert.Equal(["loaded", "reload 1 started", "reload 1 loaded", "reload 2 started", "reload 2 loaded"], events);
        Assert.False(loads.IsBusy);
    }

    /// <summary>
    /// A load must not throw. A load that throws anyway still ends, so that the session does not stay busy and the
    /// profile can close. The reload that waits for it then starts.
    /// </summary>
    [Fact]
    public async Task ALoadThatThrowsStillEnds()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        var events = new List<string>();
        using var release = new ManualResetEventSlim();
        loads.Load(() =>
        {
            Assert.True(release.Wait(Timeout), "The test did not end the load.");
            throw new InvalidOperationException("A load that breaks its rule.");
        });
        Assert.False(loads.Reload(() => () => () => events.Add("reloaded")));

        release.Set();
        await ui.RunUntilAsync(() => events.Contains("reloaded"), "the reload after the load that threw");

        Assert.False(loads.IsBusy);
    }

    /// <summary>
    /// The Console tells one time that the reload of a save waits for the running load, also after more saves. A save
    /// without a running load gives no line, because its reload starts at once.
    /// </summary>
    [Fact]
    public async Task TheConsoleTellsOneTimeThatAReloadWaits()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        using var release = new ManualResetEventSlim();
        loads.Load(() =>
        {
            Assert.True(release.Wait(Timeout), "The test did not end the load.");
            return () => { };
        });
        DataLoad Start() => () => () => { };

        Assert.Equal("The game data loads now. It loads again with the saved files after this load.",
            EditController.RequestReload(loads, Start));
        Assert.Null(EditController.RequestReload(loads, Start));
        release.Set();
        await ui.RunUntilAsync(() => !loads.IsBusy, "the reload after the first load");

        Assert.Null(EditController.RequestReload(loads, Start));
        Assert.True(loads.IsReloading);
        await ui.RunUntilAsync(() => !loads.IsBusy, "the reload of the last save");
    }

    /// <summary>
    /// Finding 9 of the S1 review: the user closes the editor window while the first load runs. The close disposes the
    /// session, so it must wait for the end of the load, as Unity ends a running import before it quits. The reload of a
    /// save before the close does not start, and no load starts after the close. A second close changes nothing.
    /// </summary>
    [Fact]
    public async Task ACloseDuringALoadWaitsForItsEndAndStartsNoReload()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        var resources = new Resources();
        var events = new List<string>();
        using var running = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        loads.Load(() =>
        {
            running.Set();
            resources.Use(release);
            return () => events.Add("loaded");
        });
        Assert.True(running.Wait(Timeout, TestContext.Current.CancellationToken));
        DataLoad StartReload()
        {
            events.Add("reload started");
            return () => () => events.Add("reload loaded");
        }

        Assert.NotNull(EditController.RequestReload(loads, StartReload));
        Assert.False(loads.Close(() => events.Add(resources.InUse ? "disposed during the load" : "disposed")));
        Assert.False(loads.Close(() => events.Add("second close")));
        var closing = (loads.IsBusy, loads.IsClosed, loads.CloseWaits, loads.ReloadWaits);
        // A save while the close waits writes its files, but it gives no reload and no Console line.
        Assert.Null(EditController.RequestReload(loads, StartReload));
        Assert.False(loads.ReloadWaits);

        release.Set();
        await ui.RunUntilAsync(() => events.Count == 2, "the close after the load");
        ui.RunQueued();

        Assert.Equal((true, true, true, false), closing);
        Assert.Equal(["loaded", "disposed"], events);
        Assert.False(loads.IsBusy);
        Assert.False(loads.CloseWaits);
        Assert.False(loads.Reload(StartReload));
        Assert.Throws<InvalidOperationException>(() => loads.Load(() => () => { }));
        Assert.Equal(["loaded", "disposed"], events);
    }

    /// <summary>A close without a running load runs at once. Then no load starts.</summary>
    [Fact]
    public async Task ACloseWithoutALoadRunsAtOnce()
    {
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        loads.Load(() => () => { });
        await ui.RunUntilAsync(() => !loads.IsBusy, "the first load");
        var closed = 0;

        Assert.True(loads.Close(() => closed++));

        Assert.Equal(1, closed);
        Assert.True(loads.IsClosed);
        Assert.False(loads.CloseWaits);
        Assert.False(loads.Reload(() => () => () => { }));
        Assert.False(loads.IsBusy);
    }

    /// <summary>The first load starts one time. A second start while it runs is a bug of the caller.</summary>
    [Fact]
    public void TheFirstLoadDoesNotStartWhileALoadRuns()
    {
        var loads = new DataLoads(new QueuedUIThread());
        using var release = new ManualResetEventSlim();
        loads.Load(() =>
        {
            release.Wait(Timeout);
            return () => { };
        });

        Assert.Throws<InvalidOperationException>(() => loads.Load(() => () => { }));
        release.Set();
    }
}
