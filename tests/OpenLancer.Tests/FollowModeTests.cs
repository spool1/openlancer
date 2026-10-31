using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// Follow mode of the agent edits (section 8.5 of the MCP1 design, decisions D719 and D720), with the editor host and a
/// fake view of the window. With follow mode on, the editor opens and frames the item of each agent edit, and the
/// selection follows Unity. With follow mode off, the agent never changes the view or the selection, except that a
/// selected section that an agent edit removes goes.
/// </summary>
public sealed class FollowModeTests
{
    private const string Zone = """{ "system": "Li01", "shape": "sphere", "position": [0, 0, 0], "nickname": "Zone_test_01" }""";

    private static readonly SectionRef Dock = SceneWorkspace.Object("Li01_dock");
    private static readonly SectionRef Hole = SceneWorkspace.Zone("Zone_hole");

    private static (TestWorkspace Ws, EditSession Session, FakeAgentEditor Editor, EditorAgentHost Host) Editor(
        TestWorkspace? workspace = null)
    {
        var ws = workspace ?? SceneWorkspace.Create();
        var session = ws.Session("act");
        var editor = new FakeAgentEditor { Edits = session };
        return (ws, session, editor, new EditorAgentHost(editor));
    }

    [Fact]
    public void FollowModeIsOnByDefault()
    {
        var editor = new FakeAgentEditor();

        Assert.True(editor.Settings.FollowAgent);
        Assert.True(editor.Follow.On);
        Assert.Empty(SteLint.Problems(AgentFollow.Tip));
    }

    /// <summary>The settings file keeps the last value that the user clicked. A stop by a camera input or a selection of
    /// the user does not change it, and a settings file without the value gives on.</summary>
    [Fact]
    public void TheSettingsFileKeepsTheClickOfTheUser()
    {
        var settings = OpenLancer.Editor.EditorSettings.Transient();
        var follow = new AgentFollow(settings.Agent, new FakeFollowView());

        follow.Stop();
        Assert.True(OpenLancer.Editor.EditorSettings.Parse(settings.ToJson(), System.DateTime.UtcNow).Agent.FollowAgent);
        follow.Set(false);
        Assert.False(OpenLancer.Editor.EditorSettings.Parse(settings.ToJson(), System.DateTime.UtcNow).Agent.FollowAgent);
        Assert.True(OpenLancer.Editor.EditorSettings.Parse("""{ "Agent": { "Port": 47815 } }""", System.DateTime.UtcNow).Agent.FollowAgent);
    }

    /// <summary>An agent create opens the system of the new item, selects it and frames it, as a create of the user
    /// selects the new item (decision D249).</summary>
    [Fact]
    public void AnAgentCreateSelectsAndFramesTheNewItem()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        var view = editor.FollowView;
        view.Selection = new FakeSceneItem(Hole);

        Ok(host, "scene_create_zone", Zone);

        var zone = SceneWorkspace.Zone("Zone_test_01");
        Assert.Equal([(zone, "Li01", true)], view.Shown);
        Assert.Equal(new FakeSceneItem(zone), view.Selection);
    }

    /// <summary>An item without a place in the Scene, for example a loadout, shows in the Inspector. The view does not
    /// change.</summary>
    [Fact]
    public void AnItemWithoutAPlaceInTheSceneShowsInTheInspector()
    {
        var (ws, _, editor, host) = Editor(BuilderWorkspace.Create());
        using var _ = ws;

        Ok(host, "create_loadout", """{ "nickname": "lo_new", "archetype": "station1" }""");

        Assert.Empty(editor.FollowView.Shown);
        Assert.Equal(BuilderWorkspace.Loadout("lo_new"), editor.FollowView.Selection);
    }

    /// <summary>A change of an item frames it and keeps the selection.</summary>
    [Fact]
    public void AChangeFramesTheItemAndKeepsTheSelection()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        var view = editor.FollowView;
        view.Selection = new FakeSceneItem(Hole);

        Ok(host, "set_fields", """{ "ref": "obj:Li01_dock", "fields": { "pos": [1, 2, 3] } }""");

        Assert.Equal([(Dock, "Li01", false)], view.Shown);
        Assert.Equal(new FakeSceneItem(Hole), view.Selection);
    }

    /// <summary>An agent delete clears the selection, as a delete of the user does. An undo of the agent then selects the
    /// first item of the delete again.</summary>
    [Fact]
    public void ADeleteClearsTheSelectionAndAnAgentUndoSelectsTheItemAgain()
    {
        var (ws, session, editor, host) = Editor();
        using var _ = ws;
        var view = editor.FollowView;
        view.Selection = new FakeSceneItem(Hole);

        Ok(host, "delete_items", """{ "refs": ["obj:Li01_dock"], "confirm": true }""");
        Assert.Null(view.Selection);
        Assert.Null(session.Get(Dock));

        Ok(host, "undo");
        Assert.Equal(new FakeSceneItem(Dock), view.Selection);
        Assert.Equal([(Dock, "Li01", true)], view.Shown);
    }

    /// <summary>A fault of follow mode comes after the commit or the undo. So the call keeps its result: the agent gets
    /// the change, and the Console gets the fault as an Error entry (review finding of MCP1-2, step 8).</summary>
    [Fact]
    public void AFaultOfFollowModeKeepsTheResultOfTheCall()
    {
        var (ws, session, editor, host) = Editor();
        using var _ = ws;
        editor.FollowView.Fails = true;

        var created = Ok(host, "scene_create_zone", Zone);
        Assert.Equal("applied", (string?)created["status"]);
        Assert.NotNull(session.Get(SceneWorkspace.Zone("Zone_test_01")));
        Ok(host, "delete_items", """{ "refs": ["zone:Zone_test_01"], "confirm": true }""");
        Ok(host, "undo");
        Assert.NotNull(session.Get(SceneWorkspace.Zone("Zone_test_01")));

        var entries = editor.Log.Entries.Where(e => e.Source == AgentController.Source).ToArray();
        Assert.Equal(["Create Zone_test_01.", $"{EditorAgentHost.ShowFailedText} The test Scene fails.",
                "Delete Zone_test_01.", "Undo Agent: Delete Zone_test_01.", $"{EditorAgentHost.ShowFailedText} The test Scene fails."],
            entries.Select(e => e.Message));
        Assert.Equal(2, entries.Count(e => e.Severity == OpenLancer.Editor.Logs.ConsoleSeverity.Error));
    }

    /// <summary>An undo of the user of an agent delete finds the first item of the delete, so the Scene selects it again
    /// (decision D249). This works also with follow mode off, because the user asked for the undo. A label of an entry
    /// of the user is not an agent delete.</summary>
    [Fact]
    public void AnUndoOfTheUserFindsTheItemOfAnAgentDelete()
    {
        var (ws, session, editor, host) = Editor();
        using var _ = ws;
        editor.Follow.Set(false);

        Ok(host, "delete_items", """{ "refs": ["obj:Li01_dock"], "confirm": true }""");
        var label = session.UndoLabel!;
        Assert.Null(editor.Follow.DeletedItem(label));
        session.Undo();

        Assert.Equal(Dock, editor.Follow.DeletedItem(label));
        Assert.Null(editor.Follow.DeletedItem(label.Replace("Agent: ", "")));
        Assert.Empty(editor.FollowView.Shown);
    }

    /// <summary>A rename keeps the selection: a selected section gets its new reference.</summary>
    [Fact]
    public void ARenameKeepsTheSelection()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        var view = editor.FollowView;
        view.Selection = Dock;

        Ok(host, "rename_item", """{ "ref": "obj:Li01_dock", "new_name": "Li01_new_dock", "confirm": true }""");

        var renamed = SceneWorkspace.Object("Li01_new_dock");
        Assert.Equal(renamed, view.Selection);
        Assert.Equal([(renamed, "Li01", false)], view.Shown);
    }

    /// <summary>A selection or a camera input of the user stops follow mode. A selection of the agent comes in the session
    /// part of its call, before the panels draw, so it is not a change of the frame. The setting keeps its value, and a
    /// click on the check box turns follow mode on again.</summary>
    [Fact]
    public void ASelectionOfTheUserStopsFollowModeAndASelectionOfTheAgentDoesNot()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        var follow = editor.Follow;
        var view = editor.FollowView;

        // select_items of the agent changes the selection in the session part. The panels then draw without a change.
        Ok(host, "select_items", """{ "refs": ["obj:Li01_dock"] }""");
        view.Selection = Dock;
        follow.Watch(Frame(Dock, Dock, input: true));
        Assert.True(follow.On);
        // The Scene selects the item of a pending section in a frame without input.
        follow.Watch(Frame(Dock, new FakeSceneItem(Dock), input: false));
        Assert.True(follow.On);

        follow.Watch(Frame(Dock, Hole, input: true));
        Assert.False(follow.On);
        Assert.True(editor.Settings.FollowAgent);

        follow.Set(true);
        Assert.True(follow.On);
        follow.Watch(Frame(Hole, Hole, input: false) with { CameraAfter = (Vector3.One, Quaternion.Identity, 1) });
        Assert.False(follow.On);
        follow.Set(false);
        Assert.False(editor.Settings.FollowAgent);
    }

    /// <summary>A frame of the panels with a selection before and after them, and a camera that does not move.</summary>
    private static FollowFrame Frame(object? before, object? after, bool input) =>
        new(before, after, (Vector3.Zero, Quaternion.Identity, 1), (Vector3.Zero, Quaternion.Identity, 1), input);

    /// <summary>A change of the position, the orientation or the fly speed of the Scene camera in the panels is a camera
    /// input of the user. This is also true without a click in the frame, for example for a key that flies the camera.
    /// The same pose is not a camera input.</summary>
    [Fact]
    public void EachChangeOfTheCameraPoseIsACameraInputOfTheUser()
    {
        var still = Frame(Dock, Dock, input: false);
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f);

        Assert.False(still.UserActed);
        Assert.False(Frame(Dock, Dock, input: true).UserActed);
        Assert.True((still with { CameraAfter = (Vector3.UnitX, Quaternion.Identity, 1) }).UserActed);
        Assert.True((still with { CameraAfter = (Vector3.Zero, turned, 1) }).UserActed);
        Assert.True((still with { CameraAfter = (Vector3.Zero, Quaternion.Identity, 2) }).UserActed);
    }

    /// <summary>With follow mode off, an agent create changes nothing of the view and the selection (decision
    /// D720).</summary>
    [Fact]
    public void WithFollowModeOffAnAgentCreateChangesNothing()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        editor.Follow.Set(false);
        var view = editor.FollowView;
        view.Selection = new FakeSceneItem(Hole);

        Ok(host, "scene_create_zone", Zone);
        Ok(host, "set_fields", """{ "ref": "obj:Li01_dock", "fields": { "pos": [1, 2, 3] } }""");

        Assert.Empty(view.Shown);
        Assert.Equal(new FakeSceneItem(Hole), view.Selection);
    }

    /// <summary>With follow mode off, an agent delete of the selected item leaves no selection of a section that is
    /// gone, as after an undo. An agent undo selects nothing.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithFollowModeOffAnAgentDeleteOfTheSelectedItemLeavesNoStaleSelection(bool sceneItem)
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        editor.Follow.Set(false);
        var view = editor.FollowView;
        view.Selection = sceneItem ? new FakeSceneItem(Dock) : Dock;

        Ok(host, "delete_items", """{ "refs": ["obj:Li01_dock"], "confirm": true }""");
        Assert.Null(view.Selection);

        Ok(host, "undo");
        Assert.Null(view.Selection);
        Assert.Empty(view.Shown);
    }

    /// <summary>With follow mode off, an agent delete of another item keeps the selection, and an agent rename of the
    /// selected section keeps it under the new reference.</summary>
    [Fact]
    public void WithFollowModeOffTheSelectionOfAnotherItemStays()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        editor.Follow.Set(false);
        var view = editor.FollowView;
        view.Selection = Dock;

        Ok(host, "delete_items", """{ "refs": ["zone:Zone_hole"], "confirm": true }""");
        Assert.Equal(Dock, view.Selection);
        Ok(host, "rename_item", """{ "ref": "obj:Li01_dock", "new_name": "Li01_new_dock", "confirm": true }""");

        Assert.Equal(SceneWorkspace.Object("Li01_new_dock"), view.Selection);
        Assert.Empty(view.Shown);
    }

    /// <summary>An item of the Scene keeps its old section until the Scene applies the rename, and the Scene keeps the
    /// item. So a rename of the selected item of the Scene does not clear the selection.</summary>
    [Fact]
    public void ARenameOfTheSelectedItemOfTheSceneKeepsTheItem()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        editor.Follow.Set(false);
        var view = editor.FollowView;
        view.Selection = new FakeSceneItem(Dock);

        Ok(host, "rename_item", """{ "ref": "obj:Li01_dock", "new_name": "Li01_new_dock", "confirm": true }""");

        Assert.Equal(new FakeSceneItem(Dock), view.Selection);
    }

    /// <summary>A write of a prop group changes an asset and no entry of the history. So its save and its delete keep the
    /// selection and the view, as an asset of the Unity Project window does.</summary>
    [Fact]
    public void AWriteOfAPropGroupKeepsTheSelection()
    {
        var (ws, _, editor, host) = Editor(GroupWorkspace.Create());
        using var _ = ws;
        var view = editor.FollowView;
        var selected = new FakeSceneItem(SceneWorkspace.Object("Li01_14"));
        view.Selection = selected;

        Ok(host, "prop_group_save", """{ "refs": ["obj:Li01_14"], "nickname": "trio", "confirm": true }""");
        Ok(host, "prop_group_delete", """{ "group": "group:trio", "confirm": true }""");

        Assert.Equal(selected, view.Selection);
        Assert.Empty(view.Shown);
        Assert.Equal(["Save prop group trio.", "Delete prop group trio."],
            editor.Log.Entries.Where(e => e.Source == AgentController.Source).Select(e => e.Message));
    }

    /// <summary>A system that the Scene cannot show, for example while the game data loads, gives the Inspector the
    /// new item.</summary>
    [Fact]
    public void AnItemThatTheSceneCannotShowGoesToTheInspector()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        editor.FollowView.CanShow = false;

        Ok(host, "scene_create_zone", Zone);

        Assert.Equal(SceneWorkspace.Zone("Zone_test_01"), editor.FollowView.Selection);
    }

    /// <summary>A preview, a question and a refused edit change nothing of the view. Only the refusal adds a Console
    /// entry.</summary>
    [Fact]
    public void APreviewAQuestionAndARefusalChangeNothingOfTheView()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        var view = editor.FollowView;
        view.Selection = new FakeSceneItem(Hole);

        Ok(host, "scene_create_zone", Zone.Replace("}", ", \"preview\": true }"));
        Error(host, "delete_items", """{ "refs": ["obj:Li01_01"] }""");
        var refused = Error(host, "set_fields", """{ "ref": "obj:none", "fields": { "pos": [1, 2, 3] } }""");

        Assert.Empty(view.Shown);
        Assert.Equal(new FakeSceneItem(Hole), view.Selection);
        Assert.True(editor.Follow.On);
        Assert.Equal($"set_fields: {refused.Message}",
            Assert.Single(editor.Log.Entries, e => e.Source == AgentController.Source).Message);
    }

    // ---- Mission edits (step 2 of MCP1-3) ----

    /// <summary>The install path of the test script as JSON text.</summary>
    private static readonly string S = MissionEdits.Script.Replace(@"\", @"\\");

    private static readonly string AddTrigger = $$"""
        { "kind": "add_trigger", "script": "msn:{{S}}", "nickname": "ol_agent",
          "condition": { "key": "Cnd_Timer", "values": { "seconds": 2 } },
          "actions": [ { "key": "Act_ActTrig", "values": { "trigger": "ol_end" } } ] }
        """;

    private static readonly string Wait9 =
        $$"""{ "kind": "set_condition_value", "trigger": "trg:{{S}}#ol_wave", "name": "seconds", "value": 9 }""";

    private static readonly string RemoveEnd = $$"""{ "kind": "remove_trigger", "trigger": "trg:{{S}}#ol_end" }""";

    private static readonly string Added = $"trg:{MissionEdits.Script}#ol_agent";

    /// <summary>A selected item of the Mission tab that the edits of the tests keep.</summary>
    private static readonly FakeMissionItem Start = new($"trg:{MissionEdits.Script}#ol_start");

    /// <summary>The editor host of a session of the mission test profile.</summary>
    private static (MissionEdits M, FakeAgentEditor Editor, EditorAgentHost Host) Missions()
    {
        var m = MissionEdits.Create();
        var editor = new FakeAgentEditor { Edits = m.Session };
        return (m, editor, new EditorAgentHost(editor));
    }

    private static string MissionEdit(string command, bool confirm = false) =>
        $$"""{ "commands": [ {{command}} ]{{(confirm ? ", \"confirm\": true" : "")}} }""";

    /// <summary>An agent create of a trigger opens its script in the Mission tab and selects the new trigger, as a create
    /// of the user selects the new item (decision D249). The Scene does not change.</summary>
    [Fact]
    public void AnAgentMissionCreateOpensTheMissionTabAndSelectsTheNewTrigger()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        var view = editor.FollowView;
        view.Selection = Start;

        Ok(host, "mission_edit", MissionEdit(AddTrigger));

        var (script, reference, select) = Assert.Single(view.MissionShown);
        Assert.True(OpenLancer.Data.IO.VfsPath.Comparer.Equals(MissionEdits.Script, script));
        Assert.Equal((Added, true), (reference, select));
        Assert.Equal(new FakeMissionItem(Added), view.Selection);
        Assert.Empty(view.Shown);
    }

    /// <summary>A change of a line frames its trigger in the Mission tab and keeps the selection. So does a change of
    /// another section of the script, for example a mission ship.</summary>
    [Fact]
    public void AMissionChangeFramesTheItemAndKeepsTheSelection()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        var view = editor.FollowView;
        view.Selection = Start;

        Ok(host, "mission_edit", MissionEdit(Wait9));
        Ok(host, "mission_edit", MissionEdit($$"""
            { "kind": "set_item_key", "item": "sec:{{S}}#MsnShip/ol_ship_1", "key": "npc",
              "lines": [ { "key": "npc", "texts": [ "ol_npc_shared" ] } ] }
            """));

        Assert.Equal([($"line:{MissionEdits.Script}#Trigger/ol_wave/2", false), ($"sec:{MissionEdits.Script}#MsnShip/ol_ship_1", false)],
            view.MissionShown.Select(s => (s.Reference, s.Select)));
        Assert.Equal(Start, view.Selection);
    }

    /// <summary>A rename of a trigger frames it under its new name and keeps the selection.</summary>
    [Fact]
    public void AMissionRenameKeepsTheSelection()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        var view = editor.FollowView;
        view.Selection = Start;

        Ok(host, "mission_edit", MissionEdit($$"""{ "kind": "rename_trigger", "trigger": "trg:{{S}}#ol_wave", "new_name": "ol_wave_2" }"""));

        Assert.Equal(($"trg:{MissionEdits.Script}#ol_wave_2", false), (view.MissionShown[0].Reference, view.MissionShown[0].Select));
        Assert.Equal(Start, view.Selection);
    }

    /// <summary>An agent delete of a trigger clears the selection. An undo of the agent then selects the trigger again in
    /// the Mission tab.</summary>
    [Fact]
    public void AMissionDeleteClearsTheSelectionAndAnAgentUndoSelectsTheTriggerAgain()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        var view = editor.FollowView;
        view.Selection = new FakeMissionItem($"trg:{MissionEdits.Script}#ol_end");

        Ok(host, "mission_edit", MissionEdit(RemoveEnd, confirm: true));
        Assert.Null(view.Selection);

        Ok(host, "undo");
        var end = $"trg:{MissionEdits.Script}#ol_end";
        Assert.Equal([(end, true)], view.MissionShown.Select(s => (s.Reference, s.Select)));
        Assert.Equal(new FakeMissionItem(end), view.Selection);
    }

    /// <summary>An undo of the user of an agent delete of a trigger gives the Scene no item to select. The undo of a
    /// mission edit of the user keeps the selection of the Mission tab, so the undo of an agent mission edit does
    /// too.</summary>
    [Fact]
    public void AnUndoOfTheUserOfAnAgentMissionDeleteGivesTheSceneNoItem()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        editor.Follow.Set(false);

        Ok(host, "mission_edit", MissionEdit(RemoveEnd, confirm: true));
        var label = m.Session.UndoLabel!;
        m.Session.Undo();

        Assert.NotNull(m.Session.Get(new SectionRef(MissionEdits.Script, new SectionIdentity("Trigger", "ol_end"))));
        Assert.Null(editor.Follow.DeletedItem(label));
    }

    /// <summary>With follow mode off, an agent mission edit changes nothing of the view and the selection.</summary>
    [Fact]
    public void WithFollowModeOffAMissionEditChangesNothing()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        editor.Follow.Set(false);
        var view = editor.FollowView;
        view.Selection = Start;

        Ok(host, "mission_edit", MissionEdit(AddTrigger));
        Ok(host, "mission_edit", MissionEdit(Wait9));

        Assert.Empty(view.MissionShown);
        Assert.Empty(view.Shown);
        Assert.Equal(Start, view.Selection);
    }

    /// <summary>Follow mode does not leave prefab mode, as Unity keeps prefab mode open when the scene changes. An agent
    /// mission edit then shows only in the Console: no view changes, and a create, a delete and an undo keep the
    /// selection of prefab mode. Follow mode stays on.</summary>
    [Fact]
    public void InPrefabModeAMissionEditShowsOnlyInTheConsole()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        var view = editor.FollowView;
        view.InPrefabMode = true;
        var template = new FakeMissionItem("trg:prefab:mod:ol_gate#ol_open");
        view.Selection = template;

        Ok(host, "mission_edit", MissionEdit(AddTrigger));
        Ok(host, "mission_edit", MissionEdit(Wait9));
        Ok(host, "mission_edit", MissionEdit(RemoveEnd, confirm: true));
        Ok(host, "undo");

        Assert.Empty(view.MissionShown);
        Assert.Empty(view.Shown);
        Assert.Equal(template, view.Selection);
        Assert.True(editor.Follow.On);
        Assert.Equal(4, editor.Log.Entries.Count(e => e.Source == AgentController.Source));
    }

    /// <summary>
    /// A selected line of a trigger keeps its line when an agent edit moves its row, as at a row move of the user
    /// (section 8.10 of the M6b design). This is also true with follow mode off, because the selection stays on the same
    /// line. A selection of another line of the trigger does not change.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASelectedLineKeepsItsLineWhenAnAgentEditMovesItsRow(bool follow)
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        editor.Follow.Set(follow);
        var view = editor.FollowView;
        var wave = new SectionRef(MissionEdits.Script, new SectionIdentity("Trigger", "ol_wave"));
        string Reorder(int row, int before) =>
            $$"""{ "kind": "reorder_action", "row": { "ref": "line:{{S}}#Trigger/ol_wave/{{row}}" }, "before": {{before}} }""";
        view.Selection = new MissionRef(MissionEdits.Script, MissionItemKind.Row, wave, Row: 5);

        Ok(host, "mission_edit", MissionEdit(Reorder(5, 3)));
        var moved = view.Selection;
        view.Selection = new MissionRef(MissionEdits.Script, MissionItemKind.Row, wave, Row: 2);
        Ok(host, "mission_edit", MissionEdit(Reorder(3, 5)));

        Assert.Equal(new MissionRef(MissionEdits.Script, MissionItemKind.Row, wave, Row: 3), moved);
        Assert.Equal(new MissionRef(MissionEdits.Script, MissionItemKind.Row, wave, Row: 2), view.Selection);
    }

    /// <summary>
    /// In prefab mode, an agent edit of the ship build file of a script shows only in the Console too. The ship build
    /// file is a mission file, but not a script. So a change, a delete and an agent undo of an <c>[NPCShipArch]</c> keep
    /// the selection of prefab mode. An undo of the user of the delete gives the Scene no item, as for a script.
    /// </summary>
    [Fact]
    public void InPrefabModeAnEditOfAShipBuildFileShowsOnlyInTheConsole()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        var view = editor.FollowView;
        view.InPrefabMode = true;
        var template = new FakeMissionItem("trg:prefab:mod:ol_gate#ol_open");
        view.Selection = template;
        var build = $"sec:{MissionEdits.Ships.Replace(@"\", @"\\")}#NPCShipArch/ol_build_1";

        Ok(host, "mission_edit", MissionEdit($$"""
            { "kind": "set_item_key", "item": "{{build}}", "key": "level", "lines": [ { "key": "level", "texts": [ "d6" ] } ] }
            """));
        Assert.Equal(template, view.Selection);
        Ok(host, "mission_edit", MissionEdit($$"""{ "kind": "remove_section", "section": "{{build}}" }""", confirm: true));
        Assert.Equal(template, view.Selection);
        var label = m.Session.UndoLabel!;
        Ok(host, "undo");

        Assert.Equal(template, view.Selection);
        Assert.Empty(view.MissionShown);
        Assert.Empty(view.Shown);
        Assert.Null(editor.Follow.DeletedItem(label));
    }

    /// <summary>A trigger that the Mission tab cannot show, for example before the mission run reads its script, shows
    /// in the Inspector. A section of the ship build file of a script is not an item of the Mission tab, so it shows in
    /// the Inspector too.</summary>
    [Fact]
    public void AMissionItemThatTheMissionTabCannotShowGoesToTheInspector()
    {
        var (m, editor, host) = Missions();
        using var _ = m;
        var view = editor.FollowView;
        view.CanShowMission = false;

        Ok(host, "mission_edit", MissionEdit(AddTrigger));
        Assert.Equal(new SectionRef(MissionEdits.Script, new SectionIdentity("Trigger", "ol_agent")), view.Selection);

        view.CanShowMission = true;
        Ok(host, "mission_edit", MissionEdit($$"""
            { "kind": "set_item_key", "item": "sec:{{MissionEdits.Ships.Replace(@"\", @"\\")}}#NPCShipArch/ol_build_1",
              "key": "level", "lines": [ { "key": "level", "texts": [ "d6" ] } ] }
            """));
        Assert.Empty(view.MissionShown);
        Assert.Equal(new SectionRef(MissionEdits.Ships, new SectionIdentity("NPCShipArch", "ol_build_1")), view.Selection);
    }
}
