using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using LibreLancer;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The cutscene tools of section 7.5 of the MCP1 design: <c>cutscene_create</c>, <c>cutscene_add_entity</c>,
/// <c>cutscene_add_event</c>, <c>cutscene_move_events</c>, <c>cutscene_set_duration</c> and <c>cutscene_set_flags</c>. The tests cover section 13.1, items 5 to 8 and 10 to
/// 12: one entry with the edit origin Agent, the preview, the parity with the UI path, the error codes, the limit, the
/// event guards (decision D767) and the write scope.
/// </summary>
public sealed partial class AgentCutsceneToolTests
{
    private const string Script = "thn:" + ScriptPath;
    private const string NewPath = @"DATA\MODS\act\CUTSCENES\my_intro.thn";

    /// <summary>A name of 65 characters, one more than <see cref="ThnEdits.MaxNameLength"/>.</summary>
    private const string LongName = "a123456789a123456789a123456789a123456789a123456789a123456789a1234";

    /// <summary>The calls of the six tools on the script of <see cref="ThnSessions"/>, with the label of the entry, and
    /// the plan of the same edit through the command layer of the UI.</summary>
    public static TheoryData<string, string, string> Calls => new()
    {
        { "cutscene_create", $$"""{ "name": "my_intro", "duration": 8 }""", "New cutscene my_intro" },
        {
            "cutscene_add_entity",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "CAMERA", "name": "my_cam_2", "position": [30, 10, 0], "orientation": [0, 90, 0] }""",
            "Add my_cam_2"
        },
        {
            "cutscene_add_event",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "START_PSYS", "time": 1.5, "targets": ["fx"], "length": 2 }""",
            "Add START_PSYS"
        },
        { "cutscene_set_duration", $$"""{ "cutscene": {{Json(Script)}}, "seconds": 4 }""", "Set duration" },
        { "cutscene_set_flags", $$"""{ "entity": {{Json(Script + "#entity/Fx")}}, "flags": ["HIDDEN"] }""", "Set flags" },
        {
            "cutscene_move_events",
            $$"""{ "events": [ { "ref": {{Json(Script + "#event/1")}}, "expect_type": "START_PSYS", "expect_time": 5 } ], "time_delta": 0.5, "length_delta": 1 }""",
            "Move event"
        },
    };

    /// <summary>The plan of the UI for each call of <see cref="Calls"/>.</summary>
    private static CutscenePlan UiPlan(EditSession session, string tool) => tool switch
    {
        "cutscene_create" => CutsceneCommands.Create(session, "my_intro"),
        "cutscene_add_entity" => CutsceneCommands.AddEntity(session, ScriptPath, "CAMERA", "my_cam_2", null,
            new Vector3(30, 10, 0), MathHelper.MatrixFromEulerDegrees(new Vector3(0, 90, 0))),
        "cutscene_add_event" => CutsceneCommands.AddEvent(session, ScriptPath, "START_PSYS", 1.5, ["fx"], 2),
        "cutscene_set_duration" => CutsceneCommands.SetDuration(session, ScriptPath, 4),
        "cutscene_set_flags" => CutsceneCommands.SetFlags(session, Entity(session, "Fx"),
            new HashSet<string> { ThnFlags.Hidden }),
        _ => CutsceneCommands.MoveEvents(session, [Event(session, 1)], 0.5f, 1),
    };

    /// <summary>The texts of W of the script and of the new cutscene, to compare two states.</summary>
    private static string State(EditSession session) =>
        ThnWriter.Write(W(session).ToGlobals()) + "\n" +
        (session.Cutscene(NewPath) is { } created ? ThnWriter.Write(created.ToGlobals()) : "none");

    private static (TestWorkspace Ws, EditSession Session, TestAgentHost Host) Open(string script = ThnSessions.Script,
        Dictionary<string, string>? activeFiles = null)
    {
        var ws = Workspace(activeFiles, script);
        var session = ws.Session("act");
        return (ws, session, new TestAgentHost(session));
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public void EachToolIsOneAgentEntryAndOneUndoPutsBackW(string tool, string arguments, string label)
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);

        var result = Ok(host, tool, arguments);

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal("Agent: " + label, (string?)result["undo_label"]);
        Assert.Equal((EditOrigin.Agent, "Agent: " + label), (session.UndoOrigin, session.UndoLabel));
        Assert.Equal([(tool, "Agent: " + label)], host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));
        Assert.NotEqual(before, State(session));
        Assert.Equal(tool == "cutscene_create", session.NewCutscenes.Contains(NewPath));

        session.Undo();
        Assert.False(session.CanUndo);
        Assert.Equal(before, State(session));
        Assert.Empty(session.NewCutscenes);
    }

    /// <summary>Each tool gives the references of its items: the new script, the script of a new duration, the entity of
    /// the flags and the moved event, as each is after the call.</summary>
    [Fact]
    public void EachToolGivesTheReferencesOfItsItems()
    {
        var (ws, _, host) = Open();
        using var __ = ws;

        var created = Ok(host, "cutscene_create", """{ "name": "my_intro" }""");
        var duration = Ok(host, "cutscene_set_duration", $$"""{ "cutscene": {{Json(Script)}}, "seconds": 4 }""");
        var flags = Ok(host, "cutscene_set_flags", $$"""{ "entity": {{Json(Script + "#entity/fx")}}, "flags": ["HIDDEN"] }""");
        var moved = Ok(host, "cutscene_move_events",
            $$"""{ "events": [ { "ref": {{Json(Script + "#event/1")}} }, { "ref": {{Json(Script + "#event/1")}} } ], "time_delta": 0.5 }""");

        Assert.Equal(["thn:" + NewPath], AgentEditToolTests.Texts(created["adds"]));
        Assert.Equal("thn:" + NewPath, (string?)created["select"]);
        Assert.Equal([Script], AgentEditToolTests.Texts(duration["changes"]));
        Assert.Equal([Script + "#entity/Fx"], AgentEditToolTests.Texts(flags["changes"]));
        Assert.Equal([Script + "#event/1"], AgentEditToolTests.Texts(moved["changes"]));
        Assert.Equal("Agent: Move event", (string?)moved["undo_label"]);
        Assert.Equal([ScriptPath], AgentEditToolTests.Texts(moved["files"]));
    }

    /// <summary>A preview keeps the revision, the history, the unsaved state, the edited paths, the new cutscenes and the
    /// open cutscenes. It gives the adds and the changes of the real call (section 13.1, item 6 of the MCP1
    /// design).</summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public void APreviewChangesNothingAndGivesTheResultOfTheRealCall(string tool, string arguments, string label)
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);
        var paths = session.EditedPaths.ToList();
        var open = session.OpenCutscenes.ToList();
        var changed = 0;
        session.Changed += _ => changed++;

        var preview = Ok(host, tool, """{ "preview": true, """ + arguments[2..]);

        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Equal(label, (string?)preview["label"]);
        Assert.Equal("0", (string?)preview["rev"]);
        Assert.Equal((0, false, false), (changed, session.IsDirty, session.CanUndo));
        Assert.Equal(paths, session.EditedPaths);
        Assert.Equal(open, session.OpenCutscenes);
        Assert.Empty(session.NewCutscenes);
        Assert.Equal(before, State(session));
        Assert.Empty(host.Edits);

        var real = Ok(host, tool, arguments);
        Assert.Equal(real["adds"]!.ToJsonString(), preview["adds"]!.ToJsonString());
        Assert.Equal(real["changes"]!.ToJsonString(), preview["changes"]!.ToJsonString());
        Assert.Equal(real["select"]?.ToJsonString(), preview["select"]?.ToJsonString());
    }

    /// <summary><c>with_patch</c> gives the text that the save writes for a cutscene: the THN patch of a script that the
    /// lower layer has, and the whole script of a new cutscene.</summary>
    [Fact]
    public void WithPatchGivesTheThnTextOfAPreview()
    {
        var (ws, session, host) = Open();
        using var _ = ws;

        var patch = Ok(host, "cutscene_set_duration",
            $$"""{ "cutscene": {{Json(Script)}}, "seconds": 4, "preview": true, "with_patch": true }""");
        var created = Ok(host, "cutscene_create", """{ "name": "my_intro", "preview": true, "with_patch": true }""");
        Ok(host, "cutscene_set_duration", $$"""{ "cutscene": {{Json(Script)}}, "seconds": 4 }""");
        AssertSaved(session.Save());

        Assert.Equal(PatchText(ws), (string?)patch["patch"]![ScriptPath]);
        Assert.Equal(ThnWriter.Write(ThnTemplates.NewScript()), (string?)created["patch"]![NewPath]);
    }

    /// <summary>The tool path and the UI path through the runner of the editor give the same W (section 13.1, item 7 of
    /// the MCP1 design).</summary>
    [Theory]
    [MemberData(nameof(Calls))]
    public void TheToolAndTheUiGiveTheSameW(string tool, string arguments, string label)
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        using var ui = Workspace();
        var uiSession = ui.Session("act");

        Ok(host, tool, arguments);
        var plan = UiPlan(uiSession, tool);
        var applied = FakeCommandHost.Runner(uiSession).Run(plan);
        if (tool == "cutscene_create")
            FakeCommandHost.Runner(uiSession).Run(CutsceneCommands.SetDuration(uiSession, plan.Path, 8));

        Assert.Equal(EditStatus.Applied, applied.Status);
        Assert.Equal(label, plan.Label);
        Assert.Equal(State(uiSession), State(session));
    }

    /// <summary>Each error of the tools, with its code, its detail and its argument. Nothing changes.</summary>
    [Theory]
    [InlineData("cutscene_create", """{ "name": "..\\x" }""", "refused", MissionErrors.NameInvalid, "name")]
    [InlineData("cutscene_create", """{ "name": "C:x" }""", "refused", MissionErrors.NameInvalid, "name")]
    [InlineData("cutscene_create", """{ "name": "a:b" }""", "refused", MissionErrors.NameInvalid, "name")]
    [InlineData("cutscene_create", $$"""{ "name": "{{LongName}}" }""", "refused", MissionErrors.NameInvalid, "name")]
    [InlineData("cutscene_create", """{ "name": "my_intro", "duration": -1 }""", "invalid_value", MissionErrors.FieldValue, "duration")]
    [InlineData("cutscene_set_duration", """{ "cutscene": "thn:DATA\\SCRIPTS\\x.thn", "seconds": -1 }""", "invalid_value", MissionErrors.FieldValue, "seconds")]
    [InlineData("cutscene_set_duration", """{ "cutscene": "thn:DATA\\SCRIPTS\\x.thn#entity/Fx", "seconds": 1 }""", "invalid_argument", ItemRefs.BadReference, "cutscene")]
    [InlineData("cutscene_set_duration", """{ "cutscene": "obj:Li01_01", "seconds": 1 }""", "invalid_argument", ItemRefs.BadReference, "cutscene")]
    [InlineData("cutscene_set_duration", """{ "cutscene": "thn:C:\\x.thn", "seconds": 1 }""", "invalid_argument", "NotInstallPath", "cutscene")]
    [InlineData("cutscene_set_duration", """{ "cutscene": "thn:..\\x.thn", "seconds": 1 }""", "invalid_argument", "NotInstallPath", "cutscene")]
    [InlineData("cutscene_set_flags", """{ "entity": "thn:C:\\x.thn#entity/a", "flags": [] }""", "invalid_argument", "NotInstallPath", "entity")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:..\\x.thn#event/0" } ] }""", "invalid_argument", "NotInstallPath", "events[0].ref")]
    [InlineData("cutscene_set_duration", """{ "cutscene": "thn:DATA\\none.thn", "seconds": 1 }""", "not_found", ItemRefs.NotFound, "cutscene")]
    [InlineData("cutscene_set_flags", """{ "entity": "thn:DATA\\SCRIPTS\\x.thn", "flags": [] }""", "invalid_argument", ItemRefs.BadReference, "entity")]
    [InlineData("cutscene_set_flags", """{ "entity": "thn:DATA\\SCRIPTS\\x.thn#event/0", "flags": [] }""", "invalid_argument", ItemRefs.BadReference, "entity")]
    [InlineData("cutscene_set_flags", """{ "entity": "thn:DATA\\SCRIPTS\\x.thn#entity/Nobody", "flags": [] }""", "not_found", ItemRefs.NotFound, "entity")]
    [InlineData("cutscene_set_flags", """{ "entity": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam_A", "flags": ["SPATIAL"] }""", "invalid_value", MissionErrors.FieldValue, "flags")]
    [InlineData("cutscene_set_flags", """{ "entity": "thn:DATA\\SCRIPTS\\x.thn#entity/Voice_trent", "flags": ["LIT_DYNAMIC"] }""", "invalid_value", MissionErrors.FieldValue, "flags")]
    [InlineData("cutscene_set_flags", """{ "entity": "thn:DATA\\SCRIPTS\\x.thn#entity/Cam_A", "flags": ["SHINY"] }""", "invalid_value", MissionErrors.FieldValue, "flags")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/0" } ], "time_delta": -1 }""", "invalid_value", MissionErrors.FieldValue, "time_delta")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1" } ], "length_delta": -5 }""", "invalid_value", MissionErrors.FieldValue, "length_delta")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1" } ], "time_delta": 1, "length_delta": 1e39 }""", "invalid_value", MissionErrors.FieldValue, "length_delta")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1" } ], "time_delta": 1e39, "length_delta": 1 }""", "invalid_value", MissionErrors.FieldValue, "time_delta")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1" }, { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/0" } ], "length_delta": 1 }""", "invalid_value", MissionErrors.FieldValue, "length_delta")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#entity/Fx" } ], "time_delta": 1 }""", "invalid_argument", ItemRefs.BadReference, "events[0].ref")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/9" } ], "time_delta": 1 }""", "not_found", ItemRefs.NotFound, "events[0].ref")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "expect_type": "START_SOUND" } ], "time_delta": 1 }""", "stale_revision", MissionErrors.RowChanged, "events[0].ref")]
    [InlineData("cutscene_move_events", """{ "events": [ { "ref": "thn:DATA\\SCRIPTS\\x.thn#event/1", "expect_time": 6 } ], "time_delta": 1 }""", "stale_revision", MissionErrors.RowChanged, "events[0].ref")]
    public void EachErrorHasItsCodeAndChangesNothing(string tool, string arguments, string code, string detail, string field)
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);

        var result = Call(host, tool, arguments);

        Assert.True(result.IsError, result.Text);
        Assert.Equal((code, detail, field),
            ((string?)result.Content["code"], (string?)result.Content["detail"], (string?)result.Content["field"]));
        Assert.Equal(before, State(session));
        Assert.Equal(0, session.Revision);
        Assert.False(session.CanUndo);
    }

    /// <summary>The messages of the errors tell the reason in the words of the editor.</summary>
    [Fact]
    public void TheErrorsGiveTheReasonsOfTheEditor()
    {
        var (ws, _, host) = Open();
        using var __ = ws;
        Ok(host, "cutscene_create", """{ "name": "my_intro" }""");

        var taken = Error(host, "cutscene_create", """{ "name": "MY_INTRO" }""");
        var name = Error(host, "cutscene_create", """{ "name": "a:b" }""");
        var spatial = Error(host, "cutscene_set_flags", $$"""{ "entity": {{Json(Script + "#entity/Cam_A")}}, "flags": ["SPATIAL"] }""");
        var early = Error(host, "cutscene_move_events",
            $$"""{ "events": [ { "ref": {{Json(Script + "#event/0")}} } ], "time_delta": -1 }""");
        var duration = Error(host, "cutscene_set_duration", $$"""{ "cutscene": {{Json(Script)}}, "seconds": -1 }""");
        var two = Error(host, "cutscene_move_events",
            $$"""{ "events": [ { "ref": {{Json(Script + "#event/0")}} }, { "ref": {{Json("thn:" + NewPath + "#event/0")}} } ] }""");
        var kind = Error(host, "cutscene_set_flags", $$"""{ "entity": {{Json(Script)}}, "flags": [] }""");
        var stale = Error(host, "cutscene_move_events",
            $$"""{ "events": [ { "ref": {{Json(Script + "#event/1")}}, "expect_type": "START_SOUND" } ] }""");
        var longName = Error(host, "cutscene_create", $$"""{ "name": "{{LongName}}" }""");
        var cutLength = Error(host, "cutscene_move_events",
            $$"""{ "events": [ { "ref": {{Json(Script + "#event/0")}} } ], "length_delta": 1 }""");

        Assert.Equal(("name_taken", MissionErrors.PathInUse), (taken.Code, taken.Detail));
        Assert.Equal(ThnEdits.PathInUse, taken.Message);
        Assert.Equal(ThnEdits.BadName, name.Message);
        Assert.Equal("Use a name of at most 64 characters.", longName.Message);
        Assert.Equal("Event 0 is a SET_CAMERA event. An event of this type has no length.", cutLength.Message);
        Assert.Equal("SPATIAL is not a flag of an entity that is not a SOUND. The flags are REFERENCE, LIT_DYNAMIC, " +
                     "LIT_AMBIENT, HIDDEN.", spatial.Message);
        Assert.Equal(ThnEdits.StartsBeforeZero, early.Message);
        Assert.Equal(ThnEdits.NegativeDuration, duration.Message);
        Assert.Equal(("invalid_argument", CutsceneEditTools.OneCutscene), (two.Code, two.Detail));
        Assert.Equal(AgentTexts.Error("cutscene_events_one_cutscene"), two.Message);
        Assert.Equal(AgentTexts.Error("cutscene_entity_ref", ("ref", Script)), kind.Message);
        Assert.Equal($"Event 1 of {ScriptPath} is not a START_SOUND event at 5 s now. The cutscene changed after you read " +
                     "it. Read the cutscene again, then try again.", stale.Message);
    }

    /// <summary>A cutscene that takes no edits refuses with its reason. A flags value that the engine cannot read refuses
    /// with the reason too.</summary>
    [Fact]
    public void AReadOnlyCutsceneAndAFlagsValueThatTheEngineCannotReadRefuse()
    {
        var (ws, _, host) = Open(activeFiles: new() { [PatchFile] = BrokenPatch });
        using var __ = ws;
        var (badWs, _, badHost) = Open(ThnSessions.Script.Replace("type = PSYS, psysprops = {}",
            "type = PSYS, flags = \"x\", psysprops = {}", StringComparison.Ordinal));
        using var ___ = badWs;

        var readOnly = Error(host, "cutscene_set_duration", $$"""{ "cutscene": {{Json(Script)}}, "seconds": 4 }""");
        var flags = Error(badHost, "cutscene_set_flags", $$"""{ "entity": {{Json(Script + "#entity/Fx")}}, "flags": [] }""");

        Assert.Equal(("refused", EditPlan.PlanRefused), (readOnly.Code, readOnly.Detail));
        Assert.Equal(("refused", EditPlan.PlanRefused), (flags.Code, flags.Detail));
        Assert.Contains("The engine reads a number or a flag name there.", flags.Message);
    }

    /// <summary>A host that takes no edits gives read_only, Pause gives paused, and a hidden group Cutscenes gives
    /// not_available (decisions D725 and D734).</summary>
    [Fact]
    public void TheHostGivesReadOnlyPausedAndNotAvailable()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var arguments = $$"""{ "cutscene": {{Json(Script)}}, "seconds": 4 }""";

        host.Reason = "The mod is in the OpenLancer repo.";
        var readOnly = Error(host, "cutscene_set_duration", arguments);
        host.Reason = null;
        host.TestPaused = true;
        var paused = Error(host, "cutscene_set_duration", arguments);
        host.TestPaused = false;
        host.Hidden.Add(ToolGroup.Cutscenes);
        var hidden = Error(host, "cutscene_set_duration", arguments);

        Assert.Equal(("read_only", MissionErrors.NotEditable), (readOnly.Code, readOnly.Detail));
        Assert.Equal(("paused", "Paused"), (paused.Code, paused.Detail));
        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.Equal(0, session.Revision);
    }

    /// <summary>The host without a window never edits a mod in the OpenLancer repo (section 13.2, item 7 of the MCP1
    /// design).</summary>
    [Fact]
    public void TheHostWithoutAWindowGivesReadOnlyForARepoMod()
    {
        using var ws = Workspace();
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "");

        using var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions());
        var error = Error(host, "cutscene_create", """{ "name": "my_intro" }""");

        Assert.Equal(("read_only", MissionErrors.NotEditable), (error.Code, error.Detail));
        Assert.Contains("part of the OpenLancer repo", error.Message);
    }

    /// <summary>One call can name at most 200 events (section 8.4 of the MCP1 design). The call above the limit
    /// changes nothing.</summary>
    [Fact]
    public void AMoveOfMoreEventsThanTheLimitGivesLimit()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var row = $$"""{ "ref": {{Json(Script + "#event/1")}} }""";

        var error = Call(host, "cutscene_move_events",
            $$"""{ "events": [ {{string.Join(", ", Enumerable.Repeat(row, 201))}} ], "time_delta": 1 }""");
        var atLimit = Ok(host, "cutscene_move_events",
            $$"""{ "events": [ {{string.Join(", ", Enumerable.Repeat(row, 200))}} ], "time_delta": 1 }""");

        Assert.Equal(("limit", EditTools.EditItems, "events"),
            ((string?)error.Content["code"], (string?)error.Content["detail"], (string?)error.Content["field"]));
        Assert.Equal(AgentTexts.Error("limit_events", ("max", 200)), (string?)error.Content["message"]);
        Assert.Equal("applied", (string?)atLimit["status"]);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>One call can guard at most 200 events in <c>expect_events</c> (section 13.1, item 10 of the MCP1
    /// design). The rows do not have to be unique, so the limit counts the rows. The call above the limit changes
    /// nothing.</summary>
    [Theory]
    [InlineData("delete_items")]
    [InlineData("duplicate_items")]
    public void MoreGuardsThanTheLimitGiveLimit(string tool)
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = State(session);
        var row = $$"""{ "ref": {{Json(Script + "#event/1")}}, "expect_type": "START_PSYS" }""";
        string Arguments(int rows) =>
            $$"""{ "refs": [ {{Json(Script + "#event/1")}} ], "expect_events": [ {{string.Join(", ", Enumerable.Repeat(row, rows))}} ] }""";

        var error = Call(host, tool, Arguments(201)).Content;
        Assert.Equal(before, State(session));
        var atLimit = Ok(host, tool, Arguments(200));

        Assert.Equal(("limit", EditTools.EditItems, "expect_events"),
            ((string?)error["code"], (string?)error["detail"], (string?)error["field"]));
        Assert.Equal(AgentTexts.Error("limit_guards", ("max", 200)), (string?)error["message"]);
        Assert.Equal("applied", (string?)atLimit["status"]);
    }

    /// <summary>
    /// Each key of <c>fields</c> of a cutscene item is one plan. So one call sets at most as many keys as the section
    /// limit (section 8.4 of the MCP1 design), in all its requests and in all steps of <c>edit_batch</c>. A call at the
    /// limit applies. A call above the limit gives <c>limit</c> with the field <c>fields</c>, and nothing changes. In
    /// <c>edit_batch</c>, the limit is a limit of the call, so the code stays <c>limit</c>.
    /// </summary>
    [Fact]
    public void MoreCutsceneFieldsThanTheSectionLimitGiveLimit()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        host.TestLimits = new AgentLimits { EditSections = 3 };
        var before = State(session);
        var cam = Json(Script + "#entity/Cam_A");
        const string four = """{ "cameraprops.fovh": 20, "cameraprops.hvaspect": 2, "cameraprops.nearplane": 2, "cameraprops.farplane": 50 }""";

        var set = Call(host, "set_fields", $$"""{ "ref": {{cam}}, "fields": {{four}} }""").Content;
        var add = Call(host, "cutscene_add_entity",
            $$"""{ "cutscene": {{Json(Script)}}, "type": "CAMERA", "name": "my_cam", "fields": {{four}} }""").Content;
        var batch = Call(host, "edit_batch", $$"""
            { "steps": [
                { "tool": "set_fields", "arguments": { "ref": {{cam}}, "fields": { "cameraprops.fovh": 20, "cameraprops.hvaspect": 2 } } },
                { "tool": "set_fields", "arguments": { "ref": {{cam}}, "fields": { "cameraprops.nearplane": 2, "cameraprops.farplane": 50 } } }
            ] }
            """).Content;
        Assert.Equal(before, State(session));
        var atLimit = Ok(host, "set_fields",
            $$"""{ "ref": {{cam}}, "fields": { "cameraprops.fovh": 20, "cameraprops.hvaspect": 2, "cameraprops.nearplane": 2 } }""");

        Assert.Equal(("limit", EditTools.CutsceneFields, "fields"),
            ((string?)set["code"], (string?)set["detail"], (string?)set["field"]));
        Assert.Equal(AgentTexts.Error("limit_cutscene_fields", ("count", 4), ("max", 3)), (string?)set["message"]);
        Assert.Equal(("limit", "fields"), ((string?)add["code"], (string?)add["field"]));
        Assert.Equal(("limit", EditTools.CutsceneFields, "steps[1].arguments.fields", 1),
            ((string?)batch["code"], (string?)batch["detail"], (string?)batch["field"], (int?)batch["index"]));
        Assert.Equal("applied", (string?)atLimit["status"]);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>A new cutscene goes only into DATA\MODS\&lt;mod id&gt;\CUTSCENES of the active mod, and the save writes
    /// it there as a whole file (section 8.2, item 4 of the MCP1 design).</summary>
    [Fact]
    public void ANewCutsceneGoesOnlyIntoTheCutscenesFolderOfTheActiveMod()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var before = TestWorkspace.Files(ws.ModFolder("act")).Keys.ToList();

        Ok(host, "cutscene_create", """{ "name": "my_intro", "duration": 8 }""");
        AssertSaved(session.Save());

        var added = TestWorkspace.Files(ws.ModFolder("act")).Keys.Except(before).ToList();
        Assert.Equal([Path.Combine("files", NewPath)], added.Where(f => f.EndsWith(".thn", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(8, session.Cutscene(NewPath)!.Duration);
    }

    /// <summary>A name of 64 characters applies, and the save writes its script. A longer name refuses, so the agent
    /// cannot make a cutscene that the save cannot write.</summary>
    [Fact]
    public void ANameAtTheLimitSavesAndALongerNameRefuses()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var name = LongName[..ThnEdits.MaxNameLength];
        var path = $@"DATA\MODS\act\CUTSCENES\{name}.thn";

        var tooLong = Error(host, "cutscene_create", $$"""{ "name": "{{LongName}}" }""");
        Ok(host, "cutscene_create", $$"""{ "name": "{{name}}" }""");
        AssertSaved(session.Save());

        Assert.Equal(("refused", MissionErrors.NameInvalid), (tooLong.Code, tooLong.Detail));
        Assert.True(File.Exists(Path.Combine(ws.ModFolder("act"), "files", path)));
        Assert.Equal([path], session.NewCutscenes);
    }

    /// <summary>A batch with a new script, its duration and a move of its event is one entry. Each step gives its
    /// references as they are after the step. The batch gives the new script only, because each element of a new
    /// script is new too.</summary>
    [Fact]
    public void ABatchWithANewScriptADurationAndAMoveIsOneEntry()
    {
        var (ws, session, host) = Open();
        using var _ = ws;
        var created = Json("thn:" + NewPath);
        var cut = Json("thn:" + NewPath + "#event/0");

        var result = Ok(host, "edit_batch", $$"""
            { "label": "Make my intro", "steps": [
                { "tool": "cutscene_create", "arguments": { "name": "my_intro" } },
                { "tool": "cutscene_set_duration", "arguments": { "cutscene": {{created}}, "seconds": 8 } },
                { "tool": "cutscene_move_events", "arguments": { "events": [ { "ref": {{cut}}, "expect_type": "SET_CAMERA", "expect_time": 0 } ], "time_delta": 1 } }
            ] }
            """);

        Assert.Equal("Agent: Make my intro", (string?)result["undo_label"]);
        Assert.Equal(["thn:" + NewPath], AgentEditToolTests.Texts(result["adds"]));
        Assert.Empty(result["changes"]!.AsArray());
        var steps = result["steps"]!.AsArray();
        Assert.Equal(["thn:" + NewPath], AgentEditToolTests.Texts(steps[0]!["adds"]));
        Assert.Equal(["thn:" + NewPath], AgentEditToolTests.Texts(steps[1]!["changes"]));
        Assert.Equal(["thn:" + NewPath + "#event/0"], AgentEditToolTests.Texts(steps[2]!["changes"]));
        Assert.Equal(1, session.Revision);
        Assert.Equal((8f, 1f), (session.Cutscene(NewPath)!.Duration,
            ((ThnNumber)session.Cutscene(NewPath)!.Events[0].Table.List[0]).Value));

        session.Undo();
        Assert.Null(session.Cutscene(NewPath));
        Assert.Empty(session.NewCutscenes);
    }

    /// <summary>The result gives the cutscene errors that the call adds and removes (section 12.6, item 4 of the MCP1
    /// design). The flag REFERENCE keeps the scene from making the monitor, so the SET_CAMERA with the monitor as its
    /// camera is not an error until the flag goes.</summary>
    [Fact]
    public void TheResultGivesTheCutsceneErrorsThatTheCallAddsAndRemoves()
    {
        var (ws, _, host) = Open(ThnSessions.Script.Replace("{ entity_name = \"Monitor\", type = MONITOR }",
            "{ entity_name = \"Monitor\", type = MONITOR, flags = REFERENCE }", StringComparison.Ordinal)
            .Replace("{ 0, SET_CAMERA, { \"Monitor\", \"Cam_A\" } }", "{ 0, SET_CAMERA, { \"Cam_A\", \"Monitor\" } }",
                StringComparison.Ordinal));
        using var _ = ws;
        var monitor = Json(Script + "#entity/Monitor");

        var added = Ok(host, "cutscene_set_flags", $$"""{ "entity": {{monitor}}, "flags": [] }""");
        var removed = Ok(host, "cutscene_set_flags", $$"""{ "entity": {{monitor}}, "flags": ["REFERENCE"] }""");

        var problem = Assert.Single(added["new_problems"]!.AsArray())!;
        Assert.Equal(Script + "#event/0", (string?)problem["ref"]);
        Assert.Equal(("mod", "act"), ((string?)problem["origin"], (string?)problem["mod_id"]));
        Assert.EndsWith("SET_CAMERA needs a CAMERA there.", (string?)problem["message"]);
        Assert.Equal((string?)problem["message"], (string?)Assert.Single(removed["fixed_problems"]!.AsArray())!["message"]);
        Assert.Empty(removed["new_problems"]!.AsArray());
    }

    /// <summary>
    /// Two cutscenes with the same error: <c>check</c> with the scope <c>all</c> gives each error with the reference of
    /// its entity and the origin of the active mod, so the agent can tell which script to correct. The error
    /// <c>save_blocked</c> names the same references in its message, and its target is the first one.
    /// </summary>
    [Fact]
    public void CheckAllAndSaveBlockedNameTheCutsceneOfEachError()
    {
        var (ws, _, host) = Open();
        using var _ = ws;
        const string other = @"thn:DATA\MODS\act\CUTSCENES\my_other.thn";
        foreach (var name in new[] { "my_intro", "my_other" })
        {
            Ok(host, "cutscene_create", $$"""{ "name": {{Json(name)}} }""");
            Ok(host, "cutscene_add_entity",
                $$"""{ "cutscene": {{Json($@"thn:DATA\MODS\act\CUTSCENES\{name}.thn")}}, "type": "DEFORMABLE", "name": "nobody" }""");
        }

        var check = Ok(host, "check", """{ "scope": "all" }""");
        var saved = Call(host, "save").Content;

        var problems = check["problems"]!.AsArray();
        Assert.Equal(["thn:" + NewPath + "#entity/nobody", other + "#entity/nobody"],
            problems.Select(p => (string?)p!["ref"]));
        Assert.All(problems, p => Assert.Equal(("mod", "act"), ((string?)p!["origin"], (string?)p["mod_id"])));
        Assert.Equal(("save_blocked", "thn:" + NewPath + "#entity/nobody"), ((string?)saved["code"], (string?)saved["target"]));
        Assert.Contains($"{(string?)problems[0]!["message"]} The error is in thn:{NewPath}#entity/nobody.", (string?)saved["message"]);
        Assert.Contains($"The error is in {other}#entity/nobody.", (string?)saved["message"]);
    }
}
