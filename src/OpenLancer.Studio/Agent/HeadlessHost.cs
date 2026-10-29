using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibreLancer.Data;
using LibreLancer.Data.Schema;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Missions;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;

namespace OpenLancer.Studio.Agent;

/// <summary>What <c>oltool mcp serve</c> does with a recovery file of an earlier session (section 6.2 of the MCP1
/// design).</summary>
public enum RecoveryChoice
{
    /// <summary>The recovery file stays, and the session takes no edits until the user decides.</summary>
    Keep,

    /// <summary>The session restores the unsaved edits of the file (<c>--restore-recovery</c>).</summary>
    Restore,

    /// <summary>The session deletes the file (<c>--discard-recovery</c>).</summary>
    Discard,
}

/// <summary>The options of a host without a window that takes edits (<c>oltool mcp serve</c> and <c>oltool mcp
/// replay</c>).</summary>
public sealed record HeadlessOptions
{
    /// <summary>True to leave out <c>save</c> and <c>export_mod</c> (<c>--no-save</c>).</summary>
    public bool NoSave { get; init; }

    /// <summary>What the host does with a recovery file of an earlier session.</summary>
    public RecoveryChoice Recovery { get; init; }

    /// <summary>True to keep the unsaved edits in the recovery file of the mod, as the editor does (decision D727). The
    /// server writes it, and a replay does not.</summary>
    public bool WritesRecovery { get; init; } = true;

    /// <summary>The clock of the write of the recovery file. The tests give a fake clock.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Gets the messages of the host for the user, for example the standard error of oltool, or null.</summary>
    public TextWriter? Messages { get; init; }
}

/// <summary>
/// The host of <c>oltool mcp serve</c> and <c>oltool mcp replay</c> (section 6.2 of the MCP1 design). It has its own
/// session on a profile, which it opens on the session thread, and no window. It has no Console and no view, so the
/// editor tools give <c>not_available</c>.
/// </summary>
/// <remarks>
/// <para>The host takes the lock of the active mod, as the editor does. When another editor has the mod open, the
/// session is read-only, and the edit tools give <c>read_only</c>. A waiting recovery file of an earlier session also
/// makes the session read-only, until the user restores or discards it. A mod in the OpenLancer repo always opens
/// read-only and without the lock, because the host cannot ask the user (decision D738). A profile or an active mod in
/// a game folder opens read-only too (the write guard of the session).</para>
/// <para>The host writes the recovery file 2 seconds after the last change of the edit view, as the editor does, and
/// again when it closes. A client can end the process at any time, so the write at the close is not enough alone. Review
/// finding RF7 of the MCP1 design asks for the write after each edit. At the close, the host releases the lock.</para>
/// </remarks>
public sealed class HeadlessHost : AgentHost, IDisposable
{
    /// <summary>The time without a change before the host writes the recovery file.</summary>
    internal static readonly TimeSpan RecoveryDelay = TimeSpan.FromSeconds(2);

    /// <summary>The tools that write the saved files of the mod or its packages. <c>--no-save</c> leaves them
    /// out.</summary>
    internal static readonly IReadOnlySet<string> SaveTools = new HashSet<string> { "save", "export_mod" };

    private readonly Lazy<InfocardManager?> texts;
    private readonly HeadlessOptions options;

    /// <summary>True when the lock of the active mod is held by another program, for example an editor.</summary>
    private readonly bool lockedByOther;

    /// <summary>The time stamp of the clock at the last change that the recovery file does not have yet, or
    /// null.</summary>
    private long? outdatedSince;

    private HeadlessHost(EditSession session, IReadOnlyList<StudioTool> tools, HeadlessOptions options)
    {
        Session = session;
        Tools = tools;
        this.options = options;
        texts = new Lazy<InfocardManager?>(() => Infocards(session));
        if (session.ActiveMod is { } mod)
        {
            RepoReason = RepoText(mod);
            lockedByOther = !session.HoldsLock && ModLock.HeldBy(mod.Folder, mod.Id) != null;
        }
        if (options.WritesRecovery && session.HoldsLock)
            session.Changed += _ => outdatedSince = options.Clock.GetTimestamp();
    }

    public override AgentHostKind Kind => AgentHostKind.Headless;

    public override EditSession Session { get; }

    public override AgentLimits Limits { get; } = new();

    /// <summary>The reason of the session, with the step for the user. A waiting recovery file names the flags of
    /// <c>oltool mcp serve</c>, and a lock of another editor names the agent server of that editor.</summary>
    public override string? NoEditReason =>
        Session.HasRecovery ? AgentTexts.Error("headless_recovery", ("mod", Session.ActiveMod!.Id))
        : Session.ReadOnlyReason is { } reason ? lockedByOther ? $"{reason} {AgentTexts.Error("headless_locked")}" : reason
        : RepoReason;

    /// <summary>The tools of the host: the read-only tools when it opens the profile only to read, else each tool. With
    /// <c>--no-save</c>, the host has no <c>save</c> and no <c>export_mod</c> (section 6.2 of the MCP1 design).</summary>
    public override IReadOnlyList<StudioTool> Tools { get; }

    /// <summary>Why the host never edits the active mod, or null: the mod is in the OpenLancer repo.</summary>
    public string? RepoReason { get; }

    /// <summary>
    /// Opens a profile only to read: the edit view of the active mod, without the lock, so the session writes no
    /// file. Call it on the session thread.
    /// </summary>
    /// <exception cref="ModLoadException">The profile or a mod does not load.</exception>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    public static HeadlessHost OpenReadOnly(string profilePath, string modId)
    {
        var profile = ModProfile.Load(Path.GetFullPath(profilePath));
        var session = EditSession.OpenReadOnly(profile, modId, DllStringResourceWriter.Instance, ThornEvaluator.Instance,
            AgentTexts.Error("headless_read_only", ("mod", modId)));
        session.Missions.Coverage = EngineCoverage.Instance;
        return new HeadlessHost(session, AgentTools.ReadOnly, new HeadlessOptions { WritesRecovery = false });
    }

    /// <summary>
    /// Opens a profile to edit the active mod, with the lock of the mod (section 6.2 of the MCP1 design). A mod in the
    /// OpenLancer repo opens only to read and without the lock. Then the host handles a recovery file of an earlier
    /// session as the options ask. Call it on the session thread.
    /// </summary>
    /// <exception cref="ModLoadException">The profile or a mod does not load.</exception>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    /// <exception cref="IOException">The recovery file could not be deleted.</exception>
    public static HeadlessHost Open(string profilePath, string modId, HeadlessOptions options)
    {
        var profile = ModProfile.Load(Path.GetFullPath(profilePath));
        var index = profile.IndexOf(modId);
        var session = (index >= 0 ? RepoText(profile.Mods[index]) : null) is { } repo
            // The host never edits a repo mod, so it takes no lock, and the editor can still edit the mod.
            ? EditSession.OpenReadOnly(profile, modId, DllStringResourceWriter.Instance, ThornEvaluator.Instance, repo)
            : EditSession.Open(profile, modId, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
        try
        {
            session.Missions.Coverage = EngineCoverage.Instance;
            IReadOnlyList<StudioTool> tools = options.NoSave
                ? [.. AgentTools.All.Where(t => !SaveTools.Contains(t.Name))]
                : AgentTools.All;
            var host = new HeadlessHost(session, tools, options);
            host.Recover(options.Recovery);
            return host;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>The reason of <see cref="RepoMods.Reason"/> with the step of this host, or null for a mod outside the
    /// OpenLancer repo (decision D738). The editor host adds its own step to the same reason.</summary>
    private static string? RepoText(Mod mod) =>
        RepoMods.Reason(mod) is { } reason ? $"{reason} {AgentTexts.Error("headless_repo_mod")}" : null;

    /// <summary>Restores or discards a recovery file of an earlier session, as the options ask. The restored edits are
    /// unsaved edits of this session, so the host writes them to a new recovery file.</summary>
    private void Recover(RecoveryChoice choice)
    {
        if (!Session.HasRecovery || choice == RecoveryChoice.Keep)
            return;
        if (choice == RecoveryChoice.Discard)
        {
            Session.DiscardRecovery();
            options.Messages?.WriteLine(AgentTexts.Error("headless_recovery_discarded"));
            return;
        }
        var problems = Session.RestoreRecovery();
        foreach (var problem in problems)
            options.Messages?.WriteLine(problem);
        options.Messages?.WriteLine(AgentTexts.Error(problems.Count == 0 ? "headless_recovery_restored"
            : "headless_recovery_not_applied"));
    }

    /// <summary>Writes the recovery file when the last change is 2 seconds old, on the session thread.</summary>
    /// <returns>The time until the next write, or null when the recovery file has each change.</returns>
    public override TimeSpan? Idle()
    {
        if (outdatedSince is not { } since)
            return null;
        var wait = RecoveryDelay - options.Clock.GetElapsedTime(since);
        if (wait > TimeSpan.Zero)
            return wait;
        WriteRecovery();
        return null;
    }

    /// <summary>Writes the unsaved edits to the recovery file of the mod, or deletes the file when there are
    /// none.</summary>
    private void WriteRecovery()
    {
        outdatedSince = null;
        foreach (var problem in Session.WriteRecovery())
            options.Messages?.WriteLine(AgentTexts.Error("headless_recovery_lost", ("problem", problem)));
    }

    public override string NameText(int ids) =>
        ids > 0 && texts.Value is { } infocards && infocards.HasStringResource(ids) ? infocards.GetStringResource(ids) : "";

    public override string InfoText(int ids) =>
        ids > 0 && texts.Value?.GetXmlResource(ids) is { } xml ? Commands.ItemTexts.PlainText(xml) : "";

    /// <summary>The target checks in the process of the host, on the session thread: the host has no frame that a
    /// long check could stop. The tail gives the result.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.NotAvailable"/> with the detail
    /// <see cref="CheckQueries.TargetCheckFailed"/>: the profile or the files of the target do not load.</exception>
    public override Func<CancellationToken, Task<TargetCheckResult>> CheckTarget(string target)
    {
        var found = ExportTarget.Find(target)!;
        IReadOnlyList<OpenLancer.Data.Diagnostic> problems;
        IReadOnlyList<string> notes;
        try
        {
            (problems, notes) = TargetValidation.Run(Session.Profile, found, gameFolder: null);
        }
        catch (Exception e) when (e is ModLoadException or IOException or UnauthorizedAccessException)
        {
            throw new EditException(CheckQueries.TargetCheckError(e.Message));
        }
        var result = new TargetCheckResult(problems.Select(p => Problem.From(p, null)).ToList(), notes);
        return _ => Task.FromResult(result);
    }

    /// <summary>
    /// Closes the host at the end of the server. An export of the agent stops before its write, and a write that started
    /// ends first. The host writes the unsaved edits to the recovery file, then releases the lock of the mod.
    /// </summary>
    public void Dispose()
    {
        Exports.Stop();
        Exports.Wait();
        if (outdatedSince != null)
            WriteRecovery();
        Session.Dispose();
    }

    /// <summary>The text DLLs that the <c>freelancer.ini</c> of the edit view names. A DLL that does not load gives no
    /// names, as an item without a name shows its nickname.</summary>
    /// <remarks>The engine reader of <c>freelancer.ini</c> and of the DLLs throws the base exception type for some
    /// problems of the data, for example a repeated entry. The names are only a help of the results, so each problem
    /// of the read gives no names.</remarks>
    private static InfocardManager? Infocards(EditSession session)
    {
        try
        {
            return new InfocardManager(new FreelancerIni(session.EditView.CreateFileSystem()).Resources);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }
}

/// <summary>
/// The session thread of a host without a window (section 5.5, item 7 of the MCP1 design). It opens the host, then runs
/// the session part of one call after the other. The SDK runs its handlers at the same time on other threads, and each
/// handler puts its call into <see cref="Queue"/>.
/// </summary>
public sealed class SessionThread : IDisposable
{
    private readonly Thread thread;
    private readonly CancellationTokenSource stop = new();
    private readonly ManualResetEventSlim ready = new();
    private readonly Func<AgentHost> open;
    private readonly TimeProvider? clock;
    private readonly AuditLog? audit;
    private Exception? failure;

    private SessionThread(Func<AgentHost> open, TimeProvider? clock, AuditLog? audit)
    {
        this.open = open;
        this.clock = clock;
        this.audit = audit;
        thread = new Thread(Run) { IsBackground = true, Name = "OpenLancer agent session" };
    }

    /// <summary>The host. It exists after <see cref="Start"/> returns.</summary>
    public AgentHost Host { get; private set; } = null!;

    /// <summary>The queue of the calls. It exists after <see cref="Start"/> returns.</summary>
    public ToolQueue Queue { get; private set; } = null!;

    /// <summary>Starts the thread, opens the host on it and waits for the open.</summary>
    /// <exception cref="Exception">The exception of the open of the host.</exception>
    public static SessionThread Start(Func<AgentHost> open, TimeProvider? clock = null, AuditLog? audit = null)
    {
        var started = new SessionThread(open, clock, audit);
        started.thread.Start();
        started.ready.Wait();
        if (started.failure is { } e)
        {
            started.thread.Join();
            throw e;
        }
        return started;
    }

    /// <summary>Stops the thread after the session part that runs now, and closes the host.</summary>
    public void Dispose()
    {
        stop.Cancel();
        thread.Join();
        stop.Dispose();
        ready.Dispose();
    }

    private void Run()
    {
        try
        {
            Host = open();
            Queue = new ToolQueue(Host, clock, audit);
        }
        catch (Exception e)
        {
            failure = e;
            ready.Set();
            return;
        }
        ready.Set();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // The host writes the recovery file between the calls, so the wait has its time limit.
                Queue.WaitForCall(Host.Idle(), stop.Token);
                while (Queue.RunNext())
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The host stops.
        }
        finally
        {
            Queue.Dispose();
            (Host as IDisposable)?.Dispose();
        }
    }
}
