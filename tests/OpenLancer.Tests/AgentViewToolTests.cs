using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor;
using OpenLancer.Editor.Agent;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The view tools of section 7.7 of the MCP1 design with the views of a test editor: the check of the arguments, the
/// references that resolve on the session, the limits, the results with their images, and the hosts that have the
/// tools. The render bench of the editor renders the images (section 13.4).
/// </summary>
public sealed class AgentViewToolTests
{
    private static (TestWorkspace Ws, TestAgentHost Host, FakeAgentViews Views) Editor(AgentView? view = null)
    {
        var ws = SceneWorkspace.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(ws.Session("act"), AgentHostKind.Editor)
        {
            TestViews = views,
            TestView = view ?? new AgentView("system:Li01", null, null),
        };
        return (ws, host, views);
    }

    private static readonly string[] ViewTools =
        ["open_view", "select_items", "render_scene", "render_cutscene", "capture_panel"];

    /// <summary>The server without a window has no views, so each view tool tells the agent to use the editor
    /// (decision D721).</summary>
    [Fact]
    public void TheServerWithoutAWindowGivesNotAvailable()
    {
        using var ws = SceneWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));

        foreach (var tool in ViewTools)
        {
            var error = Error(host, tool);
            Assert.Equal(("not_available", "EditorOnly"), (error.Code, error.Detail));
            Assert.Equal($"Only the editor host has the tool {tool}. Start the agent server in the editor.", error.Message);
        }
    }

    [Fact]
    public void OpenViewOpensOneSystemMissionOrCutscene()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var system = Ok(host, "open_view", """{ "system": "li01" }""");
        var byRef = Ok(host, "open_view", """{ "system": "system:Li02" }""");
        var none = Error(host, "open_view");
        var two = Error(host, "open_view", """{ "system": "Li01", "cutscene": "thn:DATA\\x.thn" }""");
        var missing = Error(host, "open_view", """{ "system": "Li0x" }""");

        Assert.Equal(("system", "system:Li01"), ((string?)system["kind"], (string?)system["ref"]));
        Assert.NotNull(system["rev"]);
        Assert.Equal("system:Li02", (string?)byRef["ref"]);
        Assert.Equal([(ViewKind.System, "Li01"), (ViewKind.System, "Li02")], views.Opened);
        Assert.Equal(("invalid_argument", "BadArgument"), (none.Code, none.Detail));
        Assert.Equal("Give exactly one of the arguments system, mission and cutscene.", none.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (two.Code, two.Detail));
        Assert.Equal(("not_found", ItemRefs.NotFound), (missing.Code, missing.Detail));
    }

    [Fact]
    public void OpenViewResolvesTheMissionAndTheCutscene()
    {
        using var m = MissionEdits.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(m.Session, AgentHostKind.Editor) { TestViews = views };
        using var thn = ThnSessions.Workspace();
        var cutsceneViews = new FakeAgentViews();
        var cutsceneHost = new TestAgentHost(thn.Session("act"), AgentHostKind.Editor) { TestViews = cutsceneViews };

        var mission = Ok(host, "open_view", $$"""{ "mission": {{Json("msn:" + MissionEdits.Script.ToLowerInvariant())}} }""");
        var noScript = Error(host, "open_view", """{ "mission": "msn:DATA\\MISSIONS\\none.ini" }""");
        var cutscene = Ok(cutsceneHost, "open_view",
            $$"""{ "cutscene": {{Json($"thn:{ThnSessions.ScriptPath}#entity/Cam_A")}} }""");

        Assert.Equal("mission", (string?)mission["kind"]);
        Assert.Equal(ViewKind.Mission, views.Opened.Single().Kind);
        Assert.Equal(MissionEdits.Script, views.Opened.Single().Target, ignoreCase: true);
        Assert.Equal("not_found", noScript.Code);
        Assert.Equal("cutscene", (string?)cutscene["kind"]);
        Assert.Equal($"thn:{ThnSessions.ScriptPath}", (string?)cutscene["ref"], ignoreCase: true);
        Assert.Equal(ViewKind.Cutscene, cutsceneViews.Opened.Single().Kind);
    }

    /// <summary>The editor has one selection until milestone M3b. Each reference becomes the item that the editor
    /// selects, with its section, its system and the row of a line.</summary>
    [Fact]
    public void SelectItemsSelectsOneItem()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var station = Ok(host, "select_items", """{ "refs": ["obj:li01_01"] }""");
        var unframed = Ok(host, "select_items", """{ "refs": ["zone:Zone_ring"], "frame": false }""");
        var two = Error(host, "select_items", """{ "refs": ["obj:Li01_01", "zone:Zone_ring"] }""");
        var group = Error(host, "select_items", """{ "refs": ["group:my_group"] }""");
        var empty = Error(host, "select_items", """{ "refs": [] }""");

        Assert.Equal("obj:Li01_01", (string?)station["ref"]);
        Assert.Equal("inspector", (string?)station["place"]);
        Assert.True((bool)station["shown"]!);
        Assert.True((bool)station["framed"]!);
        Assert.False((bool)unframed["framed"]!);
        var (target, frame) = views.Selected[0];
        Assert.Equal("Li01", target.System, ignoreCase: true);
        Assert.Equal("Li01_01", target.Section!.Id.Key);
        Assert.True(frame);
        Assert.False(views.Selected[1].Frame);
        Assert.Equal(("refused", "OneItem"), (two.Code, two.Detail));
        Assert.Equal("The editor selects one item at a time. Give one reference in refs.", two.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (group.Code, group.Detail));
        Assert.Equal(("invalid_argument", "BadArgument"), (empty.Code, empty.Detail));
        Assert.Equal(2, views.Selected.Count);
    }

    [Fact]
    public void SelectItemsResolvesMissionAndCutsceneReferences()
    {
        using var m = MissionEdits.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(m.Session, AgentHostKind.Editor) { TestViews = views };
        using var thn = ThnSessions.Workspace();
        var session = thn.Session("act");
        var cutsceneViews = new FakeAgentViews();
        var cutsceneHost = new TestAgentHost(session, AgentHostKind.Editor) { TestViews = cutsceneViews };

        Ok(host, "select_items", $$"""{ "refs": [{{Json($"trg:{MissionEdits.Script}#ol_wave")}}] }""");
        Ok(host, "select_items", $$"""{ "refs": [{{Json("msn:" + MissionEdits.Script)}}] }""");
        Ok(cutsceneHost, "select_items", $$"""{ "refs": [{{Json($"thn:{ThnSessions.ScriptPath}#entity/Cam_A")}}] }""");
        Ok(cutsceneHost, "select_items", $$"""{ "refs": [{{Json($"thn:{ThnSessions.ScriptPath}")}}] }""");

        Assert.Equal("ol_wave", views.Selected[0].Target.Section!.Id.Key);
        Assert.Null(views.Selected[0].Target.System);
        Assert.Equal(MissionEdits.Script, views.Selected[1].Target.Script, ignoreCase: true);
        Assert.Equal(ThnSessions.Entity(session, "Cam_A"), cutsceneViews.Selected[0].Target.Element);
        Assert.Equal(ThnSessions.ScriptPath, cutsceneViews.Selected[1].Target.Cutscene, ignoreCase: true);
    }

    /// <summary>An image of the open system has the size 1024 by 640 and the zones by default. Without a camera, the image
    /// frames the whole system. The result has the image and the structured content of its schema.</summary>
    [Fact]
    public void RenderSceneRendersTheOpenSystemWithTheDefaults()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var result = Call(host, "render_scene");

        Assert.False(result.IsError, result.Text);
        Assert.Empty(SchemaCheck.Problems(AgentTools.Find("render_scene")!.OutputSchema, result.Content));
        Assert.Equal([FakeAgentViews.Image], result.Images);
        var request = views.Scene!;
        Assert.Equal("Li01", request.System);
        Assert.Null(request.Camera);
        Assert.Empty(request.Frame);
        Assert.Equal((1024, 640, true), (request.Width, request.Height, request.Zones));
        Assert.Equal(200, request.MaxVisible);
        Assert.Equal("obj:Li01_01", (string?)result.Content["visible"]![0]!["ref"]);
        Assert.NotNull(result.Content["rev"]);
    }

    [Fact]
    public void RenderSceneTakesACameraOrItemsToFrame()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        Ok(host, "render_scene", """{ "camera": { "position": [0, 50000, 0], "look_at": [0, 0, 0] }, "width": 800, "height": 600, "zones": false }""");
        var first = views.Scene!;
        Ok(host, "render_scene", """{ "system": "Li01", "camera": { "position": [10, 20, 30], "look_at": [0, 0, 0], "fov": 40 } }""");
        var fov = views.Scene!.Camera!;
        Ok(host, "render_scene", """{ "frame": ["obj:Li01_01", "zone:Zone_ring", "system:Li01"] }""");
        var frame = views.Scene!.Frame;

        Assert.Equal(new Vector3(0, 50000, 0), first.Camera!.Position);
        Assert.Equal(ShotCamera.SceneFov(800f / 600), first.Camera.Fov);
        Assert.Equal((800, 600, false), (first.Width, first.Height, first.Zones));
        Assert.Equal(40, fov.Fov);
        Assert.Equal(["Li01_01", "Zone_ring", null], frame.Select(f => f.Section?.Id.Key));
        Assert.Equal("obj:Li01_01", frame[0].Ref);
    }

    [Fact]
    public void RenderSceneRefusesWrongArguments()
    {
        var (ws, host, _) = Editor();
        using var __ = ws;

        var both = Error(host, "render_scene", """{ "camera": { "position": [1, 2, 3], "look_at": [0, 0, 0] }, "frame": ["obj:Li01_01"] }""");
        var self = Error(host, "render_scene", """{ "camera": { "position": [1, 2, 3], "look_at": [1, 2, 3] } }""");
        var wide = Error(host, "render_scene", """{ "width": 2049 }""");
        var small = Error(host, "render_scene", """{ "height": 15 }""");
        var fov = Error(host, "render_scene", """{ "camera": { "position": [1, 2, 3], "look_at": [0, 0, 0], "fov": 170 } }""");
        var kind = Error(host, "render_scene", $$"""{ "frame": [{{Json("msn:" + MissionEdits.Script)}}] }""");
        var missing = Error(host, "render_scene", """{ "frame": ["obj:none"] }""");
        var noView = Error(new TestAgentHost(host.Session, AgentHostKind.Editor) { TestViews = new FakeAgentViews() },
            "render_scene");

        Assert.Equal(("invalid_argument", "BadArgument", "Give camera or frame, not both."), both);
        Assert.Equal(("invalid_argument", "BadArgument"), (self.Code, self.Detail));
        Assert.Equal(("limit", "RenderSize"), (wide.Code, wide.Detail));
        Assert.Equal("One image can have at most 2048 pixels on each side. Give a smaller width or height.", wide.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (small.Code, small.Detail));
        Assert.Equal("The argument 'camera.fov' needs a number from 5 to 150.", fov.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (kind.Code, kind.Detail));
        Assert.Equal(("not_found", ItemRefs.NotFound), (missing.Code, missing.Detail));
        Assert.Equal(("refused", "NoSystem"), (noView.Code, noView.Detail));
    }

    /// <summary>The render limit is a setting (decision D724). A hidden group gives not_available (decision
    /// D725).</summary>
    [Fact]
    public void TheRenderLimitAndTheHiddenGroupsApply()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;
        host.TestLimits = new AgentLimits { RenderPixels = 512 };

        var limited = Error(host, "render_scene", """{ "width": 513 }""");
        host.TestLimits = new AgentLimits();
        host.Hidden.Add(ToolGroup.Scene);
        var hidden = Error(host, "render_scene");

        Assert.Equal(("limit", "RenderSize"), (limited.Code, limited.Detail));
        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Null(views.Scene);
        Assert.Equal(256, new AgentLimits { RenderPixels = 1 }.Clamped().RenderPixels);
        // Decision D722: at most 2048 pixels on each side, also as a setting.
        Assert.Equal(2048, new AgentLimits { RenderPixels = 100_000 }.Clamped().RenderPixels);
    }

    [Fact]
    public void RenderCutsceneTakesATimeOfTheScriptAndACamera()
    {
        using var thn = ThnSessions.Workspace();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(thn.Session("act"), AgentHostKind.Editor) { TestViews = views };
        var cutscene = Json($"thn:{ThnSessions.ScriptPath}");

        var start = Call(host, "render_cutscene", $$"""{ "cutscene": {{cutscene}} }""");
        var request = views.Cutscene!;
        var later = Ok(host, "render_cutscene", $$"""{ "cutscene": {{cutscene}}, "time": 6.5, "camera": "Cam_A", "width": 400, "height": 300 }""");
        var after = Error(host, "render_cutscene", $$"""{ "cutscene": {{cutscene}}, "time": 10.5 }""");
        var none = Error(host, "render_cutscene", """{ "cutscene": "thn:DATA\\none.thn" }""");

        Assert.False(start.IsError, start.Text);
        Assert.Equal([FakeAgentViews.Image], start.Images);
        Assert.Equal((0d, null, 1024, 640), (request.Time, request.Camera, request.Width, request.Height));
        Assert.Equal(ThnSessions.ScriptPath, request.Path, ignoreCase: true);
        Assert.Equal((6.5, "Cam_A", 400, 300), (views.Cutscene!.Time, views.Cutscene.Camera, views.Cutscene.Width,
            views.Cutscene.Height));
        Assert.Equal("Cam_A", (string?)later["camera"]);
        Assert.Equal(("invalid_argument", "BadArgument"), (after.Code, after.Detail));
        Assert.Equal("The cutscene ends at 10 s. Give a time from 0 to 10.", after.Message);
        Assert.Equal("not_found", none.Code);
    }

    [Fact]
    public void CapturePanelTakesOneOfTheSixPanels()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var console = Call(host, "capture_panel", """{ "panel": "console" }""");
        var other = Error(host, "capture_panel", """{ "panel": "assets" }""");

        Assert.False(console.IsError, console.Text);
        Assert.Equal(AgentPanel.Console, views.Panel);
        Assert.Equal(new AgentLimits().RenderPixels, views.PanelSide);
        Assert.Equal("console", (string?)console.Content["panel"]);
        Assert.Equal([FakeAgentViews.Image], console.Images);
        Assert.Equal(("invalid_argument", "BadArgument"), (other.Code, other.Detail));
    }

    /// <summary>The name of each panel of the tool is the id of its panel in the layout, so the error of a hidden panel
    /// names the panel as the agent named it.</summary>
    [Fact]
    public void EachPanelOfTheToolIsAPanelOfTheLayout()
    {
        foreach (var panel in Enum.GetValues<AgentPanel>())
            Assert.Equal(AgentTools.Snake(panel.ToString()), EditorAgentViews.PanelIdOf(panel));
    }

    /// <summary>The binding gives the JSON as a text block and each image as an image block after it (section 5.7 and
    /// 7.7 of the MCP1 design). The images do not count for the limit of the text, and the revision keeps them.</summary>
    [Fact]
    public void TheImageOfAResultFollowsItsText()
    {
        var image = new ToolImage([1, 2, 3, 4], 2, 1);
        var result = ToolResult.Of(new PanelShotView(AgentPanel.Scene, 2, 1), image).WithRevision(7);

        var protocol = McpBinding.Protocol(result);
        var limited = result.Limited(new AgentLimits { ResultKilobytes = 16 });

        Assert.Equal(2, protocol.Content.Count);
        Assert.Equal(result.Text, Assert.IsType<TextContentBlock>(protocol.Content[0]).Text);
        var block = Assert.IsType<ImageContentBlock>(protocol.Content[1]);
        Assert.Equal("image/png", block.MimeType);
        Assert.Equal(image.Png, block.DecodedData.ToArray());
        Assert.Equal("7", (string?)result.Content["rev"]);
        Assert.Same(result, limited);
        Assert.Equal([image], result.Images);
        Assert.Single(McpBinding.Protocol(ToolResult.Of(new PanelShotView(AgentPanel.Scene, 2, 1))).Content);
    }

    /// <summary>An image can show a system that the Scene does not show. The tool does not open it (section 7.7 of the
    /// MCP1 design). So the agent checks a layout without a change to the view of the user.</summary>
    [Fact]
    public void RenderSceneRendersAnotherSystemWithoutAnOpen()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        Ok(host, "render_scene", """{ "system": "li02", "frame": ["obj:Li02_01", "system:Li02"] }""");

        Assert.Equal("Li02", views.Scene!.System);
        Assert.Equal(["Li02_01", null], views.Scene.Frame.Select(f => f.Section?.Id.Key));
        Assert.Empty(views.Opened);
        Assert.Empty(views.Selected);
    }

    /// <summary>Each item of a frame is in the system of the image. So a frame never shows another system than the
    /// system of the result.</summary>
    [Fact]
    public void EachItemOfAFrameIsInTheSystemOfTheImage()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var unknown = Error(host, "render_scene", """{ "frame": ["system:NoSuchSystem"] }""");
        var otherSystem = Call(host, "render_scene", """{ "frame": ["system:Li02"] }""");
        var otherItem = Error(host, "render_scene", """{ "frame": ["obj:Li01_01", "obj:Li02_01"] }""");
        var named = Error(host, "render_scene", """{ "system": "Li02", "frame": ["zone:Zone_ring"] }""");
        var notAnItem = Error(host, "render_scene", """{ "frame": ["base:Li01_01_Base"] }""");

        Assert.Equal("not_found", unknown.Code);
        Assert.True(otherSystem.IsError, otherSystem.Text);
        Assert.Equal(("invalid_argument", "OtherSystem", "frame", "system:Li02"),
            ((string?)otherSystem.Content["code"], (string?)otherSystem.Content["detail"],
                (string?)otherSystem.Content["field"], (string?)otherSystem.Content["target"]));
        Assert.Equal("The item system:Li02 is in the system Li02, not in Li01. Give system Li02, or frame only items of Li01.",
            (string?)otherSystem.Content["message"]);
        Assert.Equal(("invalid_argument", "OtherSystem"), (otherItem.Code, otherItem.Detail));
        Assert.Equal("The item zone:Zone_ring is in the system Li01, not in Li02. Give system Li01, or frame only items of Li02.",
            named.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (notAnItem.Code, notAnItem.Detail));
        Assert.Null(views.Scene);
    }

    /// <summary>One image frames at most the largest page limit of items (section 8.4 of the MCP1 design, decision
    /// D724).</summary>
    [Fact]
    public void OneImageFramesAtMostThePageLimitOfItems()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;
        host.TestLimits = new AgentLimits { ListRows = 2, MaxListRows = 2 };

        Ok(host, "render_scene", """{ "frame": ["obj:Li01_01", "obj:Li01_dock"] }""");
        var limited = Call(host, "render_scene", """{ "frame": ["obj:Li01_01", "obj:Li01_dock", "obj:Li01_child"] }""");

        Assert.Equal(2, views.Scene!.Frame.Count);
        Assert.Equal(2, views.Scene.MaxVisible);
        Assert.True(limited.IsError, limited.Text);
        Assert.Equal(("limit", "FrameItems", "frame"), ((string?)limited.Content["code"],
            (string?)limited.Content["detail"], (string?)limited.Content["field"]));
        Assert.Equal("One image can frame at most 2 items. Give fewer references in frame.",
            (string?)limited.Content["message"]);
    }

    /// <summary>A camera has finite coordinates up to the far plane of the Scene. So the GPU never renders an image from
    /// a camera that the result cannot give back.</summary>
    [Theory]
    [InlineData("[1e20, 0, 0]", "[0, 0, 0]")]
    [InlineData("[1e39, 0, 0]", "[0, 0, 0]")]
    [InlineData("[3e38, 0, 0]", "[-3e38, 0, 0]")]
    [InlineData("[0, 0, 0]", "[0, -10000001, 0]")]
    public void ACameraHasCoordinatesUpToTheFarPlaneOfTheScene(string position, string lookAt)
    {
        var (ws, host, views) = Editor();
        using var _ = ws;

        var error = Error(host, "render_scene", $$"""{ "camera": { "position": {{position}}, "look_at": {{lookAt}} } }""");
        Ok(host, "render_scene", """{ "camera": { "position": [10000000, 0, -10000000], "look_at": [0, 0, 0] } }""");

        Assert.Equal(("invalid_argument", "BadArgument"), (error.Code, error.Detail));
        Assert.EndsWith("needs three numbers from -10000000 to 10000000.", error.Message);
        Assert.Equal(new Vector3(1e7f, 0, -1e7f), views.Scene!.Camera!.Position);
    }

    /// <summary>Each path inside a reference of a view tool goes through the install path rule (decision D763 of the
    /// MCP1 design). The views of the editor never see such a call.</summary>
    [Fact]
    public void EachPathOfAViewToolFollowsTheInstallPathRule()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;
        string[] bad =
        [
            @"C:\Windows\win.ini", @"..\x.thn", @"..\..\x.thn", @"DATA\..\..\x.thn", @"\\server\share\x.thn",
            @"\\?\C:\Windows\win.ini", @"\\.\CON", @"C:x.thn", @"\Windows\win.ini", "/etc/x.thn", @"DATA\x.thn:stream",
            @"DATA\x.thn::$DATA", "CON", @"DATA\CON", @"DATA\NUL.thn", @"DATA\COM1.ini", @"DATA\LPT1", @"DATA\x.thn.",
            @"DATA\x.thn ", "DATA\\x\u0000.thn",
        ];
        var failures = new System.Collections.Generic.List<string>();

        foreach (var path in bad)
        {
            foreach (var (tool, arguments) in new[]
                     {
                         ("open_view", $$"""{ "mission": {{Json("msn:" + path)}} }"""),
                         ("open_view", $$"""{ "cutscene": {{Json("thn:" + path)}} }"""),
                         ("select_items", $$"""{ "refs": [{{Json("msn:" + path)}}] }"""),
                         ("select_items", $$"""{ "refs": [{{Json("thn:" + path)}}] }"""),
                         ("select_items", $$"""{ "refs": [{{Json("thn:" + path + "#entity/x")}}] }"""),
                         ("select_items", $$"""{ "refs": [{{Json("thn:" + path + "#event/0")}}] }"""),
                         ("select_items", $$"""{ "refs": [{{Json("sec:" + path + "#Object/x")}}] }"""),
                         ("select_items", $$"""{ "refs": [{{Json("line:" + path + "#Trigger/t/0")}}] }"""),
                         ("select_items", $$"""{ "refs": [{{Json("trg:" + path + "#t")}}] }"""),
                         ("render_scene", $$"""{ "frame": [{{Json("sec:" + path + "#Object/x")}}] }"""),
                         ("render_cutscene", $$"""{ "cutscene": {{Json("thn:" + path)}} }"""),
                     })
            {
                var result = Call(host, tool, arguments);
                if ((string?)result.Content["code"] != "invalid_argument")
                    failures.Add($"{tool} {arguments}: {result.Text}");
            }
        }

        Assert.Empty(failures);
        Assert.Empty(views.Opened);
        Assert.Empty(views.Selected);
        Assert.Null(views.Scene);
        Assert.Null(views.Cutscene);
    }

    /// <summary>A view of an item of a hidden tool group gives not_available, as the tools of that group do (decision
    /// D725 of the MCP1 design). The panels without a group stay.</summary>
    [Fact]
    public void AViewOfAHiddenGroupGivesNotAvailable()
    {
        using var m = MissionEdits.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(m.Session, AgentHostKind.Editor) { TestViews = views };
        using var thn = ThnSessions.Workspace();
        var cutsceneViews = new FakeAgentViews();
        var cutsceneHost = new TestAgentHost(thn.Session("act"), AgentHostKind.Editor) { TestViews = cutsceneViews };
        var (ws, sceneHost, sceneViews) = Editor();
        using var _ = ws;
        foreach (var each in new[] { host, cutsceneHost, sceneHost })
            each.Hidden.UnionWith([ToolGroup.Scene, ToolGroup.Missions, ToolGroup.Cutscenes]);
        var cutscene = Json($"thn:{ThnSessions.ScriptPath}");

        var hidden = new[]
        {
            Error(host, "open_view", $$"""{ "mission": {{Json("msn:" + MissionEdits.Script)}} }"""),
            Error(host, "select_items", $$"""{ "refs": [{{Json($"trg:{MissionEdits.Script}#ol_wave")}}] }"""),
            Error(host, "capture_panel", """{ "panel": "mission" }"""),
            Error(cutsceneHost, "open_view", $$"""{ "cutscene": {{cutscene}} }"""),
            Error(cutsceneHost, "select_items", $$"""{ "refs": [{{cutscene}}] }"""),
            Error(cutsceneHost, "capture_panel", """{ "panel": "timeline" }"""),
            Error(sceneHost, "open_view", """{ "system": "Li01" }"""),
            Error(sceneHost, "select_items", """{ "refs": ["obj:Li01_01"] }"""),
            Error(sceneHost, "capture_panel", """{ "panel": "scene" }"""),
        };
        Ok(sceneHost, "capture_panel", """{ "panel": "console" }""");

        Assert.All(hidden, e => Assert.Equal(("not_available", "GroupHidden"), (e.Code, e.Detail)));
        Assert.Equal("The user hid the tool group 'missions'. Ask the user to show it in the agent settings.",
            hidden[0].Message);
        Assert.Empty(views.Opened.Concat(cutsceneViews.Opened).Concat(sceneViews.Opened));
        Assert.Empty(views.Selected.Concat(cutsceneViews.Selected).Concat(sceneViews.Selected));
        Assert.Equal(AgentPanel.Console, sceneViews.Panel);
    }

    /// <summary>A result gives each reference in the canonical form (section 5.6 of the MCP1 design). The path and the
    /// names are as the session stores them.</summary>
    [Fact]
    public void TheResultsGiveTheCanonicalReferences()
    {
        using var m = MissionEdits.Create();
        var views = new FakeAgentViews();
        var host = new TestAgentHost(m.Session, AgentHostKind.Editor) { TestViews = views };
        var lower = MissionEdits.Script.ToLowerInvariant().Replace('\\', '/');

        var opened = Ok(host, "open_view", $$"""{ "mission": {{Json("msn:" + lower)}} }""");
        var script = Ok(host, "select_items", $$"""{ "refs": [{{Json("msn:" + lower)}}] }""");
        var trigger = Ok(host, "select_items", $$"""{ "refs": [{{Json($"trg:{lower}#OL_WAVE")}}] }""");
        var line = Ok(host, "select_items", $$"""{ "refs": [{{Json($"line:{lower}#trigger/OL_WAVE/1")}}] }""");

        Assert.Equal("msn:" + MissionEdits.Script, (string?)opened["ref"]);
        Assert.Equal("msn:" + MissionEdits.Script, (string?)script["ref"]);
        Assert.Equal($"trg:{MissionEdits.Script}#ol_wave", (string?)trigger["ref"]);
        Assert.Equal($"line:{MissionEdits.Script}#Trigger/ol_wave/1", (string?)line["ref"]);
        Assert.Equal(1, views.Selected[2].Target.Row);
        Assert.True(views.Selected[2].Target.InMission);
        Assert.Equal(MissionEdits.Script, views.Opened.Single().Target);
    }

    /// <summary>A new system of the active mod, as one edit. The edit makes the file of the system in the folder of the
    /// mod, the text of its name and its <c>[system]</c> in <c>universe.ini</c>. With <paramref name="dock"/>, it also
    /// makes a new base in the system.</summary>
    private static EditCommand NewSystem(EditSession session, bool dock = false) => EditCommand.Group("New system",
    [
        EditCommand.NewFile(NewModFileTests.NewPath, ContentPathsTests.Sections(NewModFileTests.SystemText)),
        EditCommand.SetText(OpenLancer.Data.Strings.StringKind.String, "ol01_sun_name", "Ol01 Sun"),
        EditCommand.AddSection(ListWorkspace.UniversePath, "system",
        [
            ListWorkspace.Line("nickname", "Ol01"),
            ListWorkspace.Line("file", ContentPaths.UniverseValue(session, NewModFileTests.NewPath)!),
        ]),
        .. dock
            ? new[]
            {
                EditCommand.AddSection(ListWorkspace.UniversePath, "Base",
                [
                    ListWorkspace.Line("nickname", "Ol01_01_Base"),
                    ListWorkspace.Line("system", "Ol01"),
                    ListWorkspace.Line("file", @"MODS\act\BASES\Ol01_01_Base.ini"),
                ]),
            }
            : [],
    ]);

    private static void AssertSaved(EditSession session)
    {
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
    }

    /// <summary>
    /// While the editor loads the game data again after a save, the dispatcher of the editor runs only studio_status, and
    /// it gives data_load reloading. A view of a system that the save added and a play at its base
    /// wait for the load. After the load, they run in their order with the game data of the save. Without the wait,
    /// the view would refuse the system because the loaded data does not have it yet.
    /// </summary>
    [Fact]
    public async Task DuringALoadOnlyStudioStatusRunsAndAViewOfTheSaveWaitsForTheLoad()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;
        var editor = new AgentPlayToolTests.FakePlayEditor();
        host.TestPlay = new EditorAgentPlay(editor);
        var session = host.Session!;
        session.Execute(NewSystem(session, dock: true));
        AssertSaved(session);
        views.Unloaded.Add("Ol01");
        var ui = new QueuedUIThread();
        var loads = new DataLoads(ui);
        using var reload = new ManualResetEventSlim();
        // As the window does: the views read the state of the loads, and a call waits while a load runs.
        using var dispatcher = new AgentDispatcher(host, () =>
        {
            views.DataLoad = EditorAgentViews.DataLoadOf(loads);
            return new EditorActivity(false, false, loads.IsBusy, false);
        }, null, new FakeClock());
        // The end of the reload gives the game data of the save to the views.
        loads.Reload(() => () =>
        {
            Assert.True(reload.Wait(TimeSpan.FromSeconds(30)));
            return views.Unloaded.Clear;
        });

        var open = Queue(dispatcher, "open_view", """{ "system": "Ol01" }""");
        var play = Queue(dispatcher, "play", """{ "start": "base", "base": "Ol01_01_Base" }""");
        var during = Queue(dispatcher, "studio_status");
        var ranDuring = new[] { dispatcher.Frame(), dispatcher.Frame() };
        var duringStatus = await Done(during);
        var waited = (open.IsCompleted, play.IsCompleted, views.Opened.Count, editor.Requests.Count);
        reload.Set();
        await ui.RunUntilAsync(() => !loads.IsBusy, "the end of the reload");
        var ranAfter = new[] { dispatcher.Frame(), dispatcher.Frame() };
        var opened = await Done(open);
        var played = await Done(play);
        var after = Queue(dispatcher, "studio_status");
        dispatcher.Frame();

        Assert.Equal([true, false], ranDuring);
        Assert.Equal("reloading", (string?)duringStatus.Content["data_load"]);
        Assert.Equal((false, false, 0, 0), waited);
        Assert.Equal([true, true], ranAfter);
        Assert.False(opened.IsError, opened.Text);
        Assert.False(played.IsError, played.Text);
        Assert.Equal([(ViewKind.System, "Ol01")], views.Opened);
        Assert.Single(editor.Requests);
        Assert.Equal("ready", (string?)(await Done(after)).Content["data_load"]);
    }

    /// <summary>A call that waits for a load longer than the wait limit gives busy with the reason of the load. Another
    /// reason to wait, for example a field edit of the user, holds studio_status too.</summary>
    [Fact]
    public async Task ACallThatWaitsTooLongForALoadGivesBusyWithTheReasonOfTheLoad()
    {
        var (ws, host, _) = Editor();
        using var __ = ws;
        var clock = new FakeClock();
        var editing = true;
        using var dispatcher = new AgentDispatcher(host, () => new EditorActivity(editing, false, true, false), null,
            clock);

        var open = Queue(dispatcher, "open_view", """{ "system": "Li01" }""");
        var status = Queue(dispatcher, "studio_status");
        var ranWhileEditing = dispatcher.Frame();
        var statusWaited = !status.IsCompleted;
        editing = false;
        var ranStatus = dispatcher.Frame();
        clock.Advance(TimeSpan.FromSeconds(30));
        var ranAtLimit = dispatcher.Frame();
        var busy = await Done(open);

        Assert.False(ranWhileEditing);
        Assert.True(statusWaited);
        Assert.True(ranStatus);
        Assert.False((await Done(status)).IsError);
        Assert.False(ranAtLimit);
        Assert.Equal(("busy", "Waited"), ((string?)busy.Content["code"], (string?)busy.Content["detail"]));
        Assert.Equal("The editor loads the game data now. The call waited 30 seconds. Try again in a few seconds.",
            (string?)busy.Content["message"]);
    }

    /// <summary>A new object of a system that the loaded game data has shows at once. The Scene applies the edits of W,
    /// so the object needs no save and no load.</summary>
    [Fact]
    public void ANewObjectOfALoadedSystemShowsAtOnceWithoutASave()
    {
        var (ws, host, views) = Editor();
        using var _ = ws;
        host.Session!.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "Object",
        [
            ListWorkspace.Line("nickname", "ol_new_station"),
            ListWorkspace.Line("pos", "0, 0, 5000"),
            ListWorkspace.Line("archetype", "station1"),
        ]));

        Ok(host, "open_view", """{ "system": "Li01" }""");
        Ok(host, "render_scene", """{ "frame": ["obj:ol_new_station"] }""");
        Ok(host, "select_items", """{ "refs": ["obj:ol_new_station"] }""");

        Assert.Equal([(ViewKind.System, "Li01")], views.Opened);
        Assert.Equal("ol_new_station", Assert.Single(views.Scene!.Frame).Section!.Id.Key);
        Assert.Equal("ol_new_station", Assert.Single(views.Selected).Target.Section!.Id.Key);
    }

    /// <summary>Puts a call into the queue of the dispatcher.</summary>
    private static Task<ToolResult> Queue(AgentDispatcher dispatcher, string tool, string arguments = "{}") =>
        dispatcher.Queue.CallAsync(tool, JsonNode.Parse(arguments)!.AsObject(), null, TestContext.Current.CancellationToken);

    /// <summary>Waits for a call that must end soon.</summary>
    private static Task<ToolResult> Done(Task<ToolResult> call) =>
        call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
}
