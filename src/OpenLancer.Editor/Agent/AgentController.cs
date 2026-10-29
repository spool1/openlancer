using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>The state of the agent server of the editor.</summary>
internal enum AgentServerState
{
    Off,
    Starting,
    On,

    /// <summary>The server did not start, because another program uses the port.</summary>
    PortInUse,
}

/// <summary>
/// The start and the stop of the agent server of the editor (section 6.1 of the MCP1 design): the dispatcher, the HTTP
/// host, the discovery file, the audit log and the Console messages with the source "Agent". It runs on the UI thread.
/// The server is off until the user starts it (decision D704).
/// </summary>
internal sealed class AgentController : IDisposable
{
    /// <summary>The Console source of the messages of the agent server (decision D718).</summary>
    public const string Source = "Agent";

    /// <summary>How long the status bar shows the client after its last call.</summary>
    private static readonly TimeSpan ClientShown = TimeSpan.FromSeconds(10);

    /// <summary>The client of the status bar when no call gave the name of its client. A stateless server gets the name
    /// only in a request that gives it, and a client of an earlier protocol revision gives it only once.</summary>
    internal const string UnnamedClient = "unnamed client";

    private readonly IAgentEditor editor;
    private readonly IUIThread ui;
    private readonly Func<EditorActivity> activity;
    private readonly string discoveryPath;
    private readonly string logFolder;
    private AgentDispatcher? dispatcher;
    private AgentServer? server;
    private AuditLog? audit;
    private int generation;

    /// <summary>The export of the agent that runs now. It stays for all starts of the server, so the Export dialog
    /// waits also for an export of an earlier start.</summary>
    public AgentExports Exports { get; } = new();
    private (EditSession? Session, string? Mod) written;

    /// <param name="activity">What the editor does now, for the waits of the calls.</param>
    /// <param name="discoveryPath">The discovery file (see <see cref="AgentDiscovery"/>).</param>
    /// <param name="logFolder">The log folder of OpenLancer, for the audit log.</param>
    public AgentController(IAgentEditor editor, IUIThread ui, Func<EditorActivity> activity, string discoveryPath,
        string logFolder)
    {
        this.editor = editor;
        this.ui = ui;
        this.activity = activity;
        this.discoveryPath = discoveryPath;
        this.logFolder = logFolder;
    }

    public AgentServerState State { get; private set; }

    /// <summary>True while the user pauses the agent edits (decision D734). Each tool that changes data, writes files or
    /// starts a play then gives the error <c>paused</c>. The read tools and <c>play_stop</c> still run. The pause stays
    /// until the user resumes the edits, also across a stop and a start of the server.</summary>
    public bool Paused { get; set; }

    /// <summary>The port of the running server, or of the last start.</summary>
    public int Port { get; private set; } = AgentSettings.DefaultPort;

    /// <summary>The text of the status bar (section 10 of the MCP1 design). While a client calls, it gives the agent
    /// edits since the last save.</summary>
    public string StatusText => State switch
    {
        AgentServerState.On when Paused => PausedText,
        AgentServerState.On when dispatcher?.Queue is { LastCall: { } last } queue &&
                                  DateTimeOffset.UtcNow - last < ClientShown =>
            ClientText(queue.LastClient ?? UnnamedClient, editor.Edits?.AgentEdits ?? 0),
        AgentServerState.On => $"Agent server: on, port {Port}",
        AgentServerState.PortInUse => $"Agent server: port {Port} is in use",
        _ => "Agent server: off",
    };

    /// <summary>The text of the status bar while the user pauses the agent edits.</summary>
    internal const string PausedText = "Agent paused";

    /// <summary>The text of the status bar while a client calls: "Agent: {client}, 1 edit" or "Agent: {client}, {n}
    /// edits", with the agent edits since the last save.</summary>
    internal static string ClientText(string client, int edits) =>
        $"Agent: {client}, {(edits == 1 ? "1 edit" : $"{edits} edits")}";

    /// <summary>Starts the server on the port of the settings. The result comes in a later frame.</summary>
    public void Start()
    {
        if (State is AgentServerState.Starting or AgentServerState.On)
            return;
        State = AgentServerState.Starting;
        Port = editor.Settings.Port;
        var limits = editor.Settings.Limits;
        audit ??= OpenAudit();
        dispatcher = new AgentDispatcher(new EditorAgentHost(editor, Exports), activity, audit);
        var queue = dispatcher.Queue;
        var port = Port;
        var id = ++generation;
        Task.Run(async () =>
        {
            var start = await AgentServer.StartAsync(port, queue, limits).ConfigureAwait(false);
            ui.QueueUIThread(() => Started(id, start));
        });
    }

    /// <summary>Stops the server at once. The calls that wait end as cancelled. The discovery file goes.</summary>
    public void Stop() => Stop(wait: false);

    /// <summary>Runs the session part of at most one call. Call it once in each frame, before the panels draw. When the
    /// open profile changed, it writes the discovery file again.</summary>
    public void Frame()
    {
        if (State != AgentServerState.On || dispatcher == null || server == null)
            return;
        var session = editor.Edits;
        if (session != written.Session || session?.ActiveMod?.Id != written.Mod)
            WriteDiscovery(server);
        dispatcher.Frame();
    }

    /// <summary>Stops the server and waits a short time for its end, at the close of the editor.</summary>
    public void Dispose()
    {
        Stop(wait: true);
        audit?.Dispose();
    }

    /// <param name="wait">True to wait a short time for the end of the HTTP host. The UI thread waits only at the close
    /// of the editor, when no frame needs it.</param>
    private void Stop(bool wait)
    {
        if (State is AgentServerState.Off or AgentServerState.PortInUse)
            return;
        generation++;
        State = AgentServerState.Off;
        // An export of the agent stops before its write. A write that started goes on to its end.
        Exports.Stop();
        dispatcher?.Dispose();
        dispatcher = null;
        if (server is not { } running)
            return;
        server = null;
        AgentDiscovery.Delete(discoveryPath, running.Token);
        // A request that waits for the UI thread ends with the queue, so the stop does not wait for this thread.
        var stopped = Task.Run(async () => await running.DisposeAsync().ConfigureAwait(false));
        if (wait)
            stopped.Wait(TimeSpan.FromSeconds(5));
        editor.Log.Add(ConsoleSeverity.Info, Source, "The agent server stopped.");
    }

    private void Started(int id, AgentServerStart start)
    {
        if (id != generation)
        {
            // The user stopped the server during its start.
            if (start.Server is { } late)
                _ = Task.Run(async () => await late.DisposeAsync().ConfigureAwait(false));
            return;
        }
        if (start.Server is not { } started)
        {
            dispatcher?.Dispose();
            dispatcher = null;
            State = start.PortInUse ? AgentServerState.PortInUse : AgentServerState.Off;
            editor.Log.Add(ConsoleSeverity.Error, Source, start.PortInUse
                ? $"Port {Port} is in use, so the agent server did not start. Close the program that uses the port, or set " +
                  "another port in the settings and in .mcp.json."
                : start.Problem!);
            return;
        }
        server = started;
        State = AgentServerState.On;
        WriteDiscovery(started);
        editor.Log.Add(ConsoleSeverity.Info, Source, $"The agent server listens on port {Port}.");
    }

    private void WriteDiscovery(AgentServer running)
    {
        var session = editor.Edits;
        written = (session, session?.ActiveMod?.Id);
        try
        {
            using var process = Process.GetCurrentProcess();
            AgentDiscovery.Write(discoveryPath, new AgentDiscoveryInfo(running.Port, running.Token, Environment.ProcessId,
                process.StartTime.ToUniversalTime(), session?.Profile.Name, written.Mod));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            editor.Log.Add(ConsoleSeverity.Warning, Source,
                $"The editor could not write the discovery file of the agent server: {e.Message} oltool mcp token gives no " +
                "token until the next start.");
        }
    }

    /// <summary>The audit log of this run of the editor, in the log folder (decision D728), or null when it does not
    /// open.</summary>
    private AuditLog? OpenAudit()
    {
        try
        {
            var path = Path.Combine(logFolder, AuditLog.FileName(DateTime.UtcNow));
            // The queue writes on worker threads, so the message of a full log goes to the UI thread.
            return new AuditLog(path, AuditLog.DefaultMaxBytes, () => ui.QueueUIThread(() => editor.Log.Add(
                ConsoleSeverity.Warning, Source,
                $"The audit log {path} reached its size limit of {AuditLog.DefaultMaxBytes / (1024 * 1024)} MB. The " +
                "editor writes no more agent calls into it until the editor starts again.")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            editor.Log.Add(ConsoleSeverity.Warning, Source, $"The audit log of the agent server did not open: {e.Message}");
            return null;
        }
    }
}
