using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>A host of the agent tools for the tests: a session, and the state of the host that each test sets.</summary>
internal sealed class TestAgentHost(EditSession? session, AgentHostKind kind = AgentHostKind.Headless) : AgentHost
{
    public override AgentHostKind Kind { get; } = kind;

    public override EditSession? Session { get; } = session;

    public override string? NoEditReason => Session?.ReadOnlyReason ?? Reason;

    /// <summary>Why the host takes no edits beside the reason of the session, for example the rule of the mods in the
    /// OpenLancer repo, or null.</summary>
    public string? Reason { get; set; }

    public bool TestPaused { get; set; }

    public override bool Paused => TestPaused;

    /// <summary>Why an export cannot start beside an export of the agent, for example an export of the Export dialog,
    /// or null.</summary>
    public string? TestExportBusy { get; set; }

    public override string? ExportBusy => TestExportBusy ?? base.ExportBusy;

    /// <summary>Each edit that the host learned, with its tool.</summary>
    public List<(string Tool, OpenLancer.Data.Editing.Commands.EditResult Result)> Edits { get; } = [];

    /// <summary>Each refusal that the host learned, with its tool.</summary>
    public List<(string Tool, OpenLancer.Data.Editing.Commands.EditError Error)> Refusals { get; } = [];

    public override void Edited(string tool, OpenLancer.Data.Editing.Commands.EditResult result) => Edits.Add((tool, result));

    public override void Refused(string tool, OpenLancer.Data.Editing.Commands.EditError error) => Refusals.Add((tool, error));

    public AgentLimits TestLimits { get; set; } = new();

    /// <summary>The linked games of the host, or null for the base folder of the profile, as the host without a window
    /// has it.</summary>
    public AgentGames? TestGames { get; set; }

    public override AgentGames Games(Data.Mods.ModManifest manifest) => TestGames ?? base.Games(manifest);

    public override AgentLimits Limits => TestLimits;

    public HashSet<ToolGroup> Hidden { get; } = [];

    public override IReadOnlySet<ToolGroup> HiddenGroups => Hidden;

    public Dictionary<int, string> Names { get; } = [];

    public List<AgentConsoleEntry> ConsoleEntries { get; } = [];

    public AgentView? TestView { get; set; }

    /// <summary>The views of the editor, or null for a host without a window.</summary>
    public IAgentViews? TestViews { get; set; }

    public override IAgentViews? Views => TestViews;

    /// <summary>The Play of the editor, or null for a host without a window.</summary>
    public IAgentPlay? TestPlay { get; set; }

    public override IAgentPlay? Play => TestPlay;

    /// <summary>Runs in each read of <see cref="View"/>. studio_status reads the view, so a test sees each session part
    /// of the tool with it.</summary>
    public Action? Probe { get; set; }

    public override AgentView? View
    {
        get
        {
            Probe?.Invoke();
            return TestView;
        }
    }

    /// <summary>The tail of <see cref="CheckTarget"/>, or null for a host without target checks.</summary>
    public Func<string, Func<CancellationToken, Task<TargetCheckResult>>>? Target { get; set; }

    public override string NameText(int ids) => Names.GetValueOrDefault(ids, "");

    public override string InfoText(int ids) => "";

    public override IReadOnlyList<AgentConsoleEntry>? Console(ConsoleFilter filter) =>
        Kind == AgentHostKind.Editor ? ConsoleEntries.Where(e => filter.Since == null || e.Sequence > filter.Since).ToList()
            : null;

    public override Func<CancellationToken, Task<TargetCheckResult>> CheckTarget(string target) => Target?.Invoke(target) ??
        throw new InvalidOperationException("The test host has no target checks.");
}

/// <summary>The views of a test editor: they keep each request and give a fixed image.</summary>
internal sealed class FakeAgentViews : IAgentViews
{
    /// <summary>The image of each image call: the start of a PNG file.</summary>
    public static readonly ToolImage Image = new([0x89, 0x50, 0x4E, 0x47], 2, 1);

    public List<(ViewKind Kind, string Target)> Opened { get; } = [];

    public List<(SelectTarget Target, bool Frame)> Selected { get; } = [];

    public SceneShotRequest? Scene { get; private set; }

    public CutsceneShotRequest? Cutscene { get; private set; }

    public AgentPanel? Panel { get; private set; }

    /// <summary>The largest side of the last capture.</summary>
    public int? PanelSide { get; private set; }

    public void Open(ViewKind kind, string target) => Opened.Add((kind, target));

    public SelectionView Select(SelectTarget target, bool frame)
    {
        Selected.Add((target, frame));
        return new SelectionView(target.Ref, target.Section != null ? SelectionPlace.Inspector : SelectionPlace.Mission,
            true, frame);
    }

    public Func<CancellationToken, Task<(SceneShotView View, ToolImage Image)>> RenderScene(SceneShotRequest request)
    {
        Scene = request;
        var camera = request.Camera ?? ShotCamera.Frame([], request.Width / (float)request.Height);
        var view = new SceneShotView("system:" + request.System, request.Width, request.Height, ShotView.Of(camera),
            request.Zones, [new VisibleRow("obj:Li01_01", 512, 320, 12.5f, 1000)], 1);
        return _ => Task.FromResult((view, Image));
    }

    public Func<CancellationToken, Task<(CutsceneShotView View, ToolImage Image)>> RenderCutscene(
        CutsceneShotRequest request)
    {
        Cutscene = request;
        var view = new CutsceneShotView("thn:" + request.Path, (float)request.Time, request.Camera ?? "Cam_A",
            request.Width, request.Height, "Script only", false);
        return _ => Task.FromResult((view, Image));
    }

    public Func<CancellationToken, Task<(PanelShotView View, ToolImage Image)>> CapturePanel(AgentPanel panel,
        int maxSide)
    {
        Panel = panel;
        PanelSide = maxSide;
        return _ => Task.FromResult((new PanelShotView(panel, 2, 1), Image));
    }
}

/// <summary>The calls of the agent tools for the tests, without a host thread.</summary>
internal static class AgentCalls
{
    /// <summary>Runs a call: the session part, then the tail when the call has one.</summary>
    public static ToolResult Call(AgentHost host, string tool, string arguments = "{}")
    {
        var step = AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject());
        return step.Result ?? step.Tail!(CancellationToken.None, ToolProgress.None).GetAwaiter().GetResult();
    }

    /// <summary>Runs a call that must give a result, not an error, and gives its structured content.</summary>
    public static JsonObject Ok(AgentHost host, string tool, string arguments = "{}")
    {
        var result = Call(host, tool, arguments);
        Assert.False(result.IsError, result.Text);
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find(tool)!.OutputSchema, result.Content));
        return result.Content;
    }

    /// <summary>Runs a call that must give a tool error, and gives its code and its detail. The structured content of
    /// a tool error also fits the output schema of the tool (section 5.7 of the MCP1 design).</summary>
    public static (string? Code, string? Detail, string? Message) Error(AgentHost host, string tool, string arguments = "{}")
    {
        var result = Call(host, tool, arguments);
        Assert.True(result.IsError, result.Text);
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find(tool)!.OutputSchema, result.Content));
        return ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["message"]);
    }

    /// <summary>A target check that gives no problems and one note.</summary>
    public static Func<CancellationToken, Task<TargetCheckResult>> TargetNote(string note) =>
        _ => Task.FromResult(new TargetCheckResult([], [note]));

    /// <summary>A text as a JSON string, for the arguments of a call.</summary>
    public static string Json(string text) => JsonSerializer.Serialize(text);
}
