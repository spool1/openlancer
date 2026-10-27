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

/// <summary>
/// The host of <c>oltool mcp serve</c> (section 6.2 of the MCP1 design): its own session on a profile, opened on the
/// session thread, and no window. It has no Console and no view, so the editor tools give <c>not_available</c>.
/// </summary>
/// <remarks>In this build, the server only reads (<c>--read-only</c>): the session takes no lock and writes no file, and
/// the host has only the read-only tools. A mod in the OpenLancer repo stays read-only also with the edit tools, because
/// the host cannot ask the user (decision D738).</remarks>
public sealed class HeadlessHost : AgentHost, IDisposable
{
    private readonly Lazy<InfocardManager?> texts;

    private HeadlessHost(EditSession session)
    {
        Session = session;
        texts = new Lazy<InfocardManager?>(() => Infocards(session));
        if (session.ActiveMod is { } mod && RepoMods.Reason(mod) != null)
            RepoReason = AgentTexts.Error("headless_repo_mod", ("mod", mod.Id));
    }

    public override AgentHostKind Kind => AgentHostKind.Headless;

    public override EditSession Session { get; }

    public override AgentLimits Limits { get; } = new();

    public override string? NoEditReason => Session.ReadOnlyReason ?? RepoReason;

    /// <summary>The read-only tools: the host opens the profile only to read (section 6.2 of the MCP1 design).</summary>
    public override IReadOnlyList<StudioTool> Tools => AgentTools.ReadOnly;

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
        return new HeadlessHost(session);
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

    public void Dispose() => Session.Dispose();

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
                Queue.WaitForCall(stop.Token);
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
