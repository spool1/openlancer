using System.Linq;
using OpenLancer.Data.Editing;
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
        follow.Watch(Dock, Dock, userInput: true, cameraInput: false);
        Assert.True(follow.On);
        // The Scene selects the item of a pending section in a frame without input.
        follow.Watch(Dock, new FakeSceneItem(Dock), userInput: false, cameraInput: false);
        Assert.True(follow.On);

        follow.Watch(Dock, Hole, userInput: true, cameraInput: false);
        Assert.False(follow.On);
        Assert.True(editor.Settings.FollowAgent);

        follow.Set(true);
        Assert.True(follow.On);
        follow.Watch(Hole, Hole, userInput: false, cameraInput: true);
        Assert.False(follow.On);
        follow.Set(false);
        Assert.False(editor.Settings.FollowAgent);
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

    /// <summary>A preview and a refused edit change nothing of the view.</summary>
    [Fact]
    public void APreviewAndARefusalChangeNothingOfTheView()
    {
        var (ws, _, editor, host) = Editor();
        using var _ = ws;
        var view = editor.FollowView;
        view.Selection = new FakeSceneItem(Hole);

        Ok(host, "scene_create_zone", Zone.Replace("}", ", \"preview\": true }"));
        Error(host, "delete_items", """{ "refs": ["obj:Li01_01"] }""");

        Assert.Empty(view.Shown);
        Assert.Equal(new FakeSceneItem(Hole), view.Selection);
        Assert.True(editor.Follow.On);
        Assert.Single(editor.Log.Entries, e => e.Source == AgentController.Source);
    }
}
