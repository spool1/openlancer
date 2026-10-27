using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The shared edit call of the agent tools and the tools <c>set_fields</c> and <c>set_text</c> (section 7.2 of the MCP1
/// design). The tests cover section 13.1, items 5, 6, 8, 10, 11 and 12: one entry with the edit origin Agent, the
/// preview, the guards, Pause, the write scope and the references.
/// </summary>
public sealed class AgentEditToolTests
{
    internal static readonly SectionRef Station = SceneWorkspace.Object("Li01_01");

    /// <summary>The scene workspace with an edit session of the mod "act", and a host of the session.</summary>
    /// <param name="li01">The text of the system file Li01, or null for the default text.</param>
    internal static (TestWorkspace Ws, EditSession Session, TestAgentHost Host) Scene(string? li01 = null)
    {
        var ws = SceneWorkspace.Create();
        if (li01 != null)
            ws.WriteBase(SceneWorkspace.Li01Path, li01);
        var session = ws.Session("act");
        return (ws, session, new TestAgentHost(session));
    }

    [Fact]
    public void SetFieldsIsOneAgentEntryAndOneUndoPutsBackW()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var before = session.WorkingSection(Station);

        var result = Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [10, 20, 30] } }""");

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal(("Set pos", "Agent: Set pos"), ((string?)result["label"], (string?)result["undo_label"]));
        Assert.Equal(["obj:Li01_01"], Texts(result["changes"]));
        Assert.Equal("1", (string?)result["rev"]);
        Assert.Equal("The editor applied the edit as one Undo step: Agent: Set pos.", (string?)result["summary"]);
        Assert.Equal((EditOrigin.Agent, "Agent: Set pos"), (session.UndoOrigin, session.UndoLabel));
        Assert.Equal("10, 20, 30", Line(session, Station, "pos"));
        Assert.Equal([("set_fields", "Agent: Set pos")], host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));

        session.Undo();
        Assert.False(session.CanUndo);
        Assert.Same(before, session.WorkingSection(Station));
    }

    /// <summary>A preview changes nothing: the revision, the history, the dirty state and the edited paths stay, and no
    /// <see cref="EditSession.Changed"/> fires. It gives the changes of the real call (section 8.3, item 4 of the MCP1
    /// design).</summary>
    [Fact]
    public void APreviewChangesNothingAndGivesTheResultOfTheRealCall()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var changed = 0;
        session.Changed += _ => changed++;
        var paths = session.EditedPaths;

        var preview = Ok(host, "set_fields",
            """{ "ref": "obj:Li01_01", "fields": { "pos": [10, 20, 30] }, "preview": true, "with_patch": true }""");

        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Null(preview["undo_label"]);
        Assert.Equal("0", (string?)preview["rev"]);
        Assert.Equal((0, false, false), (changed, session.IsDirty, session.CanUndo));
        Assert.Equal(paths, session.EditedPaths);
        Assert.Null(session.OpenBatch);
        Assert.Null(Line(session, Station, "pos"));
        var patch = Assert.Single(preview["patch"]!.AsObject());
        Assert.Equal(SceneWorkspace.Li01Path, patch.Key, ignoreCase: true);
        Assert.Contains("pos = 10, 20, 30", (string?)patch.Value);
        Assert.Empty(host.Edits);

        var real = Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [10, 20, 30] } }""");
        Assert.Equal(Texts(preview["changes"]), Texts(real["changes"]));
        Assert.Equal(Texts(preview["adds"]), Texts(real["adds"]));
        Assert.Empty(real["patch"]!.AsObject());
        Assert.Equal(1, changed);
    }

    [Fact]
    public void WithPatchNeedsAPreview()
    {
        var (ws, _, host) = Scene();
        using var __ = ws;

        var error = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] }, "with_patch": true }""");

        Assert.Equal(("invalid_argument", "BadArgument"), (error.Code, error.Detail));
        Assert.Equal("The argument 'with_patch' works only with preview: true. Add preview: true, or remove with_patch.",
            error.Message);
    }

    /// <summary>A call with no net change gives no_change and no entry (contract addition 10 of the MCP1
    /// design).</summary>
    [Fact]
    public void ACallWithNoNetChangeGivesNoChangeAndNoEntry()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var result = Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "archetype": "station1" } }""");

        Assert.Equal("no_change", (string?)result["status"]);
        Assert.Equal("0", (string?)result["rev"]);
        Assert.Equal("The data has these values already, so the edit changed nothing.", (string?)result["summary"]);
        Assert.False(session.CanUndo);
        Assert.Empty(host.Edits);
    }

    /// <summary>if_revision refuses an edit after a change of the edit view (decision D723 of the MCP1 design). A
    /// preview does not change the revision, so the guard of a later call still passes.</summary>
    [Fact]
    public void AStaleRevisionRefusesTheEdit()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] }, "preview": true }""");
        Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] }, "if_revision": "0" }""");
        var stale = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [4, 5, 6] }, "if_revision": "0" }""");
        var bad = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [4, 5, 6] }, "if_revision": "x" }""");

        Assert.Equal(("stale_revision", EditSession.RevisionChanged), (stale.Code, stale.Detail));
        Assert.Equal("The edit view changed after revision 0. Read the items again, then edit.", stale.Message);
        Assert.Equal(("invalid_argument", "BadArgument"), (bad.Code, bad.Detail));
        Assert.Equal("1, 2, 3", Line(session, Station, "pos"));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>Pause refuses an edit tool and lets a query run (decision D734 of the MCP1 design). The host learns the
    /// refusal for its Console.</summary>
    [Fact]
    public void PauseRefusesAnEditAndAQueryStillRuns()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        host.TestPaused = true;

        var paused = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");
        var status = Ok(host, "studio_status");

        Assert.Equal(("paused", "Paused"), (paused.Code, paused.Detail));
        Assert.Equal("The user paused the agent edits. Ask the user to resume them in the Agent menu.", paused.Message);
        Assert.True((bool)status["paused"]!);
        Assert.False(session.CanUndo);
        Assert.Equal([("set_fields", EditErrorKind.Paused)], host.Refusals.Select(r => (r.Tool, r.Error.Kind)));
    }

    /// <summary>An edit call while a gesture of the user is open gives busy. The editor host waits for the end of the
    /// gesture first, so the error comes only from a host without such a wait.</summary>
    [Fact]
    public void AnEditWhileAGestureOfTheUserIsOpenGivesBusy()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        using var gesture = session.Begin("Move Li01_01");

        var busy = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");

        Assert.Equal(("busy", "Busy"), (busy.Code, busy.Detail));
        Assert.True(session.InTransaction);
        Assert.Null(session.OpenBatch);
    }

    /// <summary>A refused preview and a query do not go to the Console of the host (section 8.5 of the MCP1
    /// design).</summary>
    [Fact]
    public void ARefusedPreviewAndAQueryDoNotGoToTheHost()
    {
        var (ws, _, host) = Scene();
        using var __ = ws;

        Error(host, "set_fields", """{ "ref": "obj:none", "fields": { "pos": [1, 2, 3] }, "preview": true }""");
        Error(host, "get_item", """{ "ref": "obj:none" }""");
        Error(host, "set_fields", """{ "ref": "obj:none", "fields": { "pos": [1, 2, 3] } }""");

        Assert.Equal([("set_fields", EditErrorKind.NotFound)], host.Refusals.Select(r => (r.Tool, r.Error.Kind)));
    }

    /// <summary>A session that only reads gives read_only with its reason. So do a mod in the OpenLancer repo and a
    /// profile in a game folder (section 8.2, items 6 and 7 of the MCP1 design).</summary>
    [Fact]
    public void AReadOnlySessionARepoModAndAProfileInAGameFolderGiveReadOnly()
    {
        using var ws = SceneWorkspace.Create();
        const string Call = """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""";

        using (var reading = EditSession.OpenReadOnly(ModProfile.Load(ws.ProfilePath), "act",
                   TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator, "Only to read."))
        {
            Assert.Equal(("read_only", "NotEditable", "Only to read."), Error(new TestAgentHost(reading), "set_fields", Call));
        }

        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "");
        var session = ws.Session("act");
        var repo = new TestAgentHost(session, AgentHostKind.Editor) { Reason = RepoMods.Reason(session.ActiveMod!) };
        Assert.Equal(("read_only", "NotEditable", "The mod 'act' is part of the OpenLancer repo, so its files are tracked files."),
            Error(repo, "set_fields", Call));

        var inside = Path.Combine(ws.Base, "inside.json");
        File.WriteAllText(inside, """{ "base": ".", "mods": [ "../mods/act" ] }""");
        using var game = EditSession.Open(ModProfile.Load(inside), "act", TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator);
        foreach (var kind in new[] { AgentHostKind.Editor, AgentHostKind.Headless })
        {
            Assert.Equal(("read_only", "NotEditable", GameTexts.ProfileInGame),
                Error(new TestAgentHost(game, kind), "set_fields", Call));
        }
        Assert.False(session.CanUndo);
    }

    /// <summary>An item of a tool group that the user hides gives not_available (decision D725 of the MCP1 design). An
    /// item of no group still takes edits.</summary>
    [Fact]
    public void AnItemOfAHiddenGroupGivesNotAvailable()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        host.Hidden.Add(ToolGroup.Scene);

        var hidden = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");
        var system = Error(host, "set_fields", """{ "ref": "system:Li01", "fields": { "visit": 1 } }""");
        var dataItem = Ok(host, "set_fields", """{ "ref": "base:Li01_01_Base", "fields": { "system": "Li02" } }""");

        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Equal("The user hid the tool group 'scene'. Ask the user to show it in the agent settings.", hidden.Message);
        Assert.Equal(("not_available", "GroupHidden"), (system.Code, system.Detail));
        Assert.Equal("applied", (string?)dataItem["status"]);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>The edit tools of MCP1-2 do not change mission scripts and cutscenes. Their tools come with MCP1-3 and
    /// MCP1-4.</summary>
    [Theory]
    [InlineData("msn:DATA\\MISSIONS\\M01A\\m01a.ini", EditTools.MissionEdits)]
    [InlineData("trg:DATA\\MISSIONS\\M01A\\m01a.ini#t1", EditTools.MissionEdits)]
    [InlineData("sec:DATA\\MISSIONS\\M01A\\m01a.ini#Trigger/t1", EditTools.MissionEdits)]
    [InlineData("line:DATA\\MISSIONS\\M01A\\m01a.ini#Trigger/t1/1", EditTools.MissionEdits)]
    [InlineData("prefab:builtin:patrol", EditTools.MissionEdits)]
    [InlineData("thn:DATA\\MODS\\act\\CUTSCENES\\x.thn", EditTools.CutsceneEdits)]
    [InlineData("thn:DATA\\MODS\\act\\CUTSCENES\\x.thn#entity/Camera_01", EditTools.CutsceneEdits)]
    public void AMissionOrACutsceneReferenceGivesNotAvailable(string reference, string detail)
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var error = Error(host, "set_fields", $$"""{ "ref": {{Json(reference)}}, "fields": { "x": 1 } }""");

        Assert.Equal(("not_available", detail), (error.Code, error.Detail));
        Assert.False(session.CanUndo);
    }

    /// <summary>A reference with a drive, a stream name or a ".." above the root is not an install path (section 8.2,
    /// item 4 of the MCP1 design).</summary>
    [Theory]
    [InlineData("sec:C:\\x.ini#Object/a")]
    [InlineData("sec:DATA\\x.ini:stream#Object/a")]
    [InlineData("sec:..\\x.ini#Object/a")]
    [InlineData("sec:DATA\\..\\..\\x.ini#Object/a")]
    [InlineData("sec:\\\\server\\share\\x.ini#Object/a")]
    public void ABadPathInAReferenceIsAnArgumentError(string reference)
    {
        var (ws, _, host) = Scene();
        using var __ = ws;

        var result = Call(host, "set_fields", $$"""{ "ref": {{Json(reference)}}, "fields": { "x": 1 } }""");

        Assert.True(result.IsError);
        Assert.Equal(("invalid_argument", "ref"), ((string?)result.Content["code"], (string?)result.Content["field"]));
    }

    /// <summary>A key with a space resolves, and the result gives the canonical form of a reference (section 13.1, item
    /// 11 of the MCP1 design).</summary>
    [Fact]
    public void AKeyWithASpaceResolvesAndTheResultGivesTheCanonicalForm()
    {
        var (ws, session, host) = Scene(SceneWorkspace.Li01Text + "\n[Object]\nnickname = Li01 spaced\narchetype = buoy\npos = 0, 0, 0\n");
        using var _ = ws;

        var spaced = Ok(host, "set_fields", """{ "ref": "obj:Li01 spaced", "fields": { "pos": [1, 2, 3] } }""");
        var section = Ok(host, "set_fields", $$"""
            { "ref": {{Json($"sec:{SceneWorkspace.Li01Path}#Object/Li01_01")}}, "fields": { "pos": [4, 5, 6] } }
            """);

        Assert.Equal(["obj:Li01 spaced"], Texts(spaced["changes"]));
        Assert.Equal(["obj:Li01_01"], Texts(section["changes"]));
        Assert.Equal("1, 2, 3", Line(session, SceneWorkspace.Object("Li01 spaced"), "pos"));
    }

    /// <summary>A value that a list check refuses goes into the edit view, as in the Inspector. The result gives the new
    /// problem (section 4.0.5 of the M6b design, item 4). A later edit that corrects it gives the fixed
    /// problem.</summary>
    [Fact]
    public void AValueThatAListCheckRefusesAppliesAndGivesTheNewProblem()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var broken = Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "loadout": "lo_missing" } }""");
        var fixedOne = Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "loadout": "lo_station" } }""");

        Assert.Equal("applied", (string?)broken["status"]);
        var problem = Assert.Single(broken["new_problems"]!.AsArray())!;
        Assert.Equal("obj:Li01_01", (string?)problem["ref"]);
        Assert.Contains("lo_missing", (string?)problem["message"]);
        Assert.Empty(broken["fixed_problems"]!.AsArray());
        Assert.Equal((string?)problem["message"], (string?)Assert.Single(fixedOne["fixed_problems"]!.AsArray())!["message"]);
        Assert.Empty(fixedOne["new_problems"]!.AsArray());
        Assert.Equal(2, session.Revision);
    }

    /// <summary>A set_fields value that the field cannot hold refuses with invalid_value. A key that the item does not
    /// have gives invalid_argument, and the key line refuses.</summary>
    [Fact]
    public void ABadValueOrAnUnknownKeyRefusesAndChangesNothing()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var comma = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "loadout": "a, b" } }""");
        var unknown = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "no_such_key": 1 } }""");
        var key = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "nickname": "x" } }""");
        var empty = Error(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { } }""");

        Assert.Equal(("invalid_value", "FieldValue"), (comma.Code, comma.Detail));
        Assert.Equal(("invalid_argument", "UnknownField"), (unknown.Code, unknown.Detail));
        Assert.Equal(("refused", "KeyLine"), (key.Code, key.Detail));
        Assert.Equal(("invalid_argument", "BadArgument"), (empty.Code, empty.Detail));
        Assert.False(session.CanUndo);
        Assert.Null(session.OpenBatch);
    }

    [Fact]
    public void SetTextSetsTheNameOfOneSectionAndWarnsAboutTheSectionsThatShareIt()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var gun = ItemWorkspace.Gun("gun1");
        var good = ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "gun1");
        var texts = new ItemTexts(session, _ => "", _ => "");

        var result = Ok(host, "set_text", $$"""{ "ref": {{Json(ItemRefs.Of(gun))}}, "field": "name", "text": "My Gun" }""");

        Assert.Equal("Agent: Set ids_name of gun1", (string?)result["undo_label"]);
        Assert.Equal("My Gun", texts.TextOf(gun, "ids_name"));
        Assert.Equal(["300"], ItemWorkspace.Firsts(session, good, "ids_name"));
        var warning = Assert.Single(result["warnings"]!.AsArray())!;
        Assert.Equal(ItemRefs.Canonical(session, good), (string?)warning["ref"]);
        Assert.StartsWith($"{ItemRefs.Canonical(session, good)} shows the same text and keeps the old text.",
            (string?)warning["message"]);

        session.Undo();
        Assert.Equal(["300"], ItemWorkspace.Firsts(session, gun, "ids_name"));
        Assert.Equal("", texts.TextOf(gun, "ids_name"));
    }

    [Fact]
    public void SetTextRefusesAKeyWithoutTextAndAnEmptyText()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var gun = Json(ItemRefs.Of(ItemWorkspace.Gun("gun1")));

        var key = Error(host, "set_text", $$"""{ "ref": {{gun}}, "field": "projectile_archetype", "text": "x" }""");
        var empty = Error(host, "set_text", $$"""{ "ref": {{gun}}, "field": "name", "text": " " }""");

        Assert.Equal(("invalid_argument", MissionErrorsNotTextId), (key.Code, key.Detail));
        Assert.Equal(("invalid_value", "FieldValue", FieldCommands.EmptyText), empty);
        Assert.False(session.CanUndo);
    }

    /// <summary>An exception in the commands of a call cancels its batch. So no batch stays open, and W stays as it was
    /// (review finding R9 of MCP1-2).</summary>
    [Fact]
    public void AnExceptionAfterTheBatchOpenedLeavesNoOpenBatchAndTheSameW()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var before = session.WorkingSection(Station);
        var call = Prepared(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");

        Assert.Throws<InvalidOperationException>(() => EditTools.Apply(call, scope =>
        {
            scope.Run(FieldCommands.SetFields(session, Station, [KeyValuePair.Create("pos", JsonNode.Parse("[1, 2, 3]"))]));
            Assert.NotNull(session.OpenBatch);
            throw new InvalidOperationException("A fault of the test.");
        }));

        Assert.Null(session.OpenBatch);
        Assert.Same(before, session.WorkingSection(Station));
        Assert.False(session.CanUndo);
        Assert.Equal(0, session.Revision);
        Assert.Equal("applied", (string?)Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""")["status"]);
    }

    /// <summary>A call that changes more sections than the limit refuses with limit and changes nothing (section 8.4 of
    /// the MCP1 design).</summary>
    [Fact]
    public void ACallAboveTheSectionLimitRefusesWithLimit()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        host.TestLimits = new AgentLimits { EditSections = 1 };
        var call = Prepared(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""");

        var error = Assert.Throws<EditException>(() => EditTools.Apply(call, scope =>
        {
            scope.Run(FieldCommands.SetFields(session, Station, [KeyValuePair.Create("pos", JsonNode.Parse("[1, 2, 3]"))]));
            scope.Run(FieldCommands.SetFields(session, SceneWorkspace.Object("Li01_dock"),
                [KeyValuePair.Create("pos", JsonNode.Parse("[1, 2, 3]"))]));
        })).Error;

        Assert.Equal((EditErrorKind.Limit, "EditSections"), (error.Kind, error.Detail));
        Assert.Equal("The edit changes 2 sections, and one call can change at most 1. Split the edit.", error.Message);
        Assert.False(session.CanUndo);
        Assert.Null(session.OpenBatch);
        Assert.Equal(("applied"), (string?)Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3] } }""")["status"]);
    }

    /// <summary>The edit limits of section 8.4 of the MCP1 design have their defaults. A value of the settings file
    /// outside the range takes the nearest value of the range.</summary>
    [Fact]
    public void TheEditLimitsHaveTheirDefaultsAndRanges()
    {
        var limits = new AgentLimits();
        var high = new AgentLimits { EditItems = 99999, EditSections = 99999, BatchSteps = 99999 }.Clamped();
        var low = new AgentLimits { EditItems = 0, EditSections = 0, BatchSteps = 0 }.Clamped();

        Assert.Equal((200, 2000, 100), (limits.EditItems, limits.EditSections, limits.BatchSteps));
        Assert.Equal((AgentLimits.EditItemsRange.Max, AgentLimits.EditSectionsRange.Max, AgentLimits.BatchStepsRange.Max),
            (high.EditItems, high.EditSections, high.BatchSteps));
        Assert.Equal((1, 1, 1), (low.EditItems, low.EditSections, low.BatchSteps));
    }

    /// <summary>The detail of a set_text field that holds no text id.</summary>
    private const string MissionErrorsNotTextId = OpenLancer.Data.Missions.Editing.MissionErrors.NotTextId;

    /// <summary>The call of a tool with checked arguments, for a test that runs the shared edit call itself.</summary>
    internal static ToolCall Prepared(AgentHost host, string tool, string arguments) =>
        new(host, tool, AgentTools.Find(tool)!.Input.Read(tool, JsonNode.Parse(arguments)!.AsObject()));

    /// <summary>The texts of a JSON array.</summary>
    internal static string[] Texts(JsonNode? array) => array!.AsArray().Select(t => (string)t!).ToArray();

    /// <summary>The values of the last line of a key as text, or null.</summary>
    internal static string? Line(EditSession session, SectionRef section, string key) =>
        session.Get(section)?.Lines(key).LastOrDefault() is { } line ? FieldEdit.FormatLine(line.Values) : null;
}
