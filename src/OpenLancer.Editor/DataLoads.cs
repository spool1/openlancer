using System;
using LibreLancer;
using OpenLancer.Studio;

namespace OpenLancer.Editor;

/// <summary>
/// One load of the engine data. It runs in the background and gives the action that ends the load on the UI thread. It
/// must not throw: it gives a failed load to the action that ends it. A load that throws anyway still ends, without an
/// action (see <see cref="DataLoads"/>).
/// </summary>
internal delegate Action DataLoad();

/// <summary>
/// Runs the loads of the engine data of one session in the background: the first load when the profile opens, and a
/// reload after each save that changed files. Each load fills the resources of the session, so only one load runs at a
/// time.
/// </summary>
/// <remarks>
/// <para>A save can come while a load runs, for example a save of the restored edits of an earlier session during the
/// first load. The save writes its files at once, and its reload waits until the running load ends, as Unity imports
/// the assets of a save after the running import. More saves during one load give only one more reload, because a
/// reload reads the saved files when it starts.</para>
/// <para>A close of the editor window waits for the running load, as Unity ends a running import before it quits (see
/// <see cref="Close"/>).</para>
/// <para>A load reads many files for seconds, so it runs on a thread of its own (<see cref="OwnThread"/>), not on a
/// thread of the thread pool.</para>
/// </remarks>
internal sealed class DataLoads(IUIThread ui)
{
    private Func<DataLoad>? waitingReload;
    private Action? waitingClose;

    /// <summary>True while the first load runs.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>True while a reload runs.</summary>
    public bool IsReloading { get; private set; }

    /// <summary>True while the first load or a reload runs.</summary>
    public bool IsBusy => IsLoading || IsReloading;

    /// <summary>True when a reload waits for the end of the running load.</summary>
    public bool ReloadWaits => waitingReload != null;

    /// <summary>True after <see cref="Close"/>. Then no load starts.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>True when a close waits for the end of the running load.</summary>
    public bool CloseWaits => waitingClose != null;

    /// <summary>Starts the first load. Call it on the UI thread.</summary>
    /// <exception cref="InvalidOperationException">A load runs, or the loads are closed.</exception>
    public void Load(DataLoad load)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("The game data loads already.");
        }

        if (IsClosed)
        {
            throw new InvalidOperationException("The loads of the session are closed.");
        }

        IsLoading = true;
        Run(load);
    }

    /// <summary>Starts a reload now, or after the running load. After <see cref="Close"/>, it does nothing, because the
    /// session closes. Call it on the UI thread.</summary>
    /// <param name="start">Starts the reload on the UI thread and gives its load. It reads the saved files when the
    /// reload starts, so a reload that waits reads the files of the last save.</param>
    /// <returns>True when the reload started. False when it waits for the running load, or when the loads are
    /// closed.</returns>
    public bool Reload(Func<DataLoad> start)
    {
        if (IsClosed)
        {
            return false;
        }

        if (IsBusy)
        {
            waitingReload = start;
            return false;
        }

        var load = start();
        IsReloading = true;
        Run(load);
        return true;
    }

    /// <summary>
    /// Closes the loads, then runs <paramref name="closed"/>: now when no load runs, else on the UI thread when the
    /// running load ends. Each load fills the resources of the session, so the session must not be disposed while a load
    /// runs (finding 9 of the S1 review). A reload that waits does not start, and no load starts after this call. A
    /// second close while the first one waits changes nothing. Call it on the UI thread.
    /// </summary>
    /// <returns>True when <paramref name="closed"/> ran now. False when it waits for the running load.</returns>
    public bool Close(Action closed)
    {
        IsClosed = true;
        waitingReload = null;
        if (IsBusy)
        {
            waitingClose ??= closed;
            return false;
        }

        closed();
        return true;
    }

    private void Run(DataLoad load) => OwnThread.Run(() =>
    {
        Action? end = null;
        try
        {
            end = load();
        }
        finally
        {
            // A load that throws against its rule must also end. If not, the session stays busy, and the profile
            // cannot close.
            ui.QueueUIThread(() => Ended(end));
        }
    });

    /// <summary>Ends a load on the UI thread, then runs the close or starts the reload that waits for it.</summary>
    /// <param name="end">The action that ends the load, or null when the load threw.</param>
    private void Ended(Action? end)
    {
        IsLoading = false;
        IsReloading = false;
        end?.Invoke();
        if (waitingClose is { } closed)
        {
            waitingClose = null;
            closed();
        }
        else if (waitingReload is { } start)
        {
            waitingReload = null;
            Reload(start);
        }
    }
}
