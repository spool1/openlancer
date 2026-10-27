using System.IO;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The prop group tools of section 7.3 of the MCP1 design. A placement is one entry of the edit history. A save, a
/// rename and a delete of a group are asset writes (section 8.3, item 6). They need confirm: true, and their preview
/// writes no file. They are not on the undo stack, and they cannot be a step of a batch. A group of another mod refuses
/// a rename and a delete.
/// </summary>
public sealed class AgentPropGroupToolTests
{
    /// <summary>A placement is one entry with the W of the command of the UI. Its result selects the first member, as a
    /// Duplicate does (decision D288 of the M23 design).</summary>
    [Fact]
    public void APlacementIsOneEntryWithTheWOfTheCommandOfTheUi()
    {
        using var a = GroupWorkspace.Create();
        var expected = a.Session("act");
        var group = PropGroupCommands.Save(expected, PropGroups.Draft(expected, [SceneWorkspace.Object("Li01_14")]), "trio",
            "Trio").Group!;
        FakeCommandHost.Runner(expected).Run(PropGroupCommands.Place(expected, "Li01", group, new Vector3(0, 0, 5000),
            SceneWorkspace.Object("Li01_dock")));

        using var b = GroupWorkspace.Create();
        var session = b.Session("act");
        var host = new TestAgentHost(session);
        PropGroupCommands.Save(session, PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]), "trio", "Trio");
        var preview = Ok(host, "prop_group_place",
            """{ "group": "group:trio", "system": "Li01", "anchor": [0, 0, 5000], "parent": "obj:Li01_dock", "preview": true }""");
        var result = Ok(host, "prop_group_place",
            """{ "group": "group:trio", "system": "Li01", "anchor": [0, 0, 5000], "parent": "obj:Li01_dock" }""");

        Assert.Equal(AgentSceneToolTests.W(expected, SceneWorkspace.Li01Path), AgentSceneToolTests.W(session, SceneWorkspace.Li01Path));
        Assert.Equal("Agent: Place Trio", (string?)result["undo_label"]);
        Assert.Equal(Texts(preview["adds"]), Texts(result["adds"]));
        Assert.Equal(3, Texts(result["adds"]).Length);
        Assert.Equal(Texts(result["adds"])[0], (string?)result["select"]);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>A member line can name an entry that the profile does not have. Such a line refuses the placement, and
    /// the error gives each such line (decision D290 of the M23 design). leave_out_error_lines places the rest, as "Place
    /// without these lines" does.</summary>
    [Fact]
    public void APlacementWithErrorLinesRefusesUntilTheAgentLeavesThemOut()
    {
        using var ws = GroupWorkspace.Create();
        GroupWorkspace.WriteGroup(ws, "lost", "[PropGroup]\nnickname = lost\nformat = 1\n[Object]\nmember = a\n" +
                                              "archetype = station1\nloadout = lo_missing\n");
        var session = ws.Session("act");
        var host = new TestAgentHost(session);

        var refused = Error(host, "prop_group_place", """{ "group": "group:lost", "system": "Li01", "anchor": [0, 0, 0] }""");
        var placed = Ok(host, "prop_group_place",
            """{ "group": "group:lost", "system": "Li01", "anchor": [0, 0, 0], "leave_out_error_lines": true }""");

        Assert.Equal(("refused", SceneEditTools.PlacementErrors), (refused.Code, refused.Detail));
        Assert.StartsWith("Some lines of group:lost name entries that this profile does not have.", refused.Message);
        Assert.Contains("a, loadout: ", refused.Message);
        Assert.Equal("applied", (string?)placed["status"]);
        Assert.Contains(placed["warnings"]!.AsArray(), w => ((string)w!["message"]!).Contains("the 'loadout' line goes."));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>
    /// A save asks for confirm: true, and its preview writes no file. With confirm: true, it writes the group into the
    /// folder of the active mod. The result says that the change is not on the undo stack, and the history has no new
    /// entry. A rename and a delete follow the same rules.
    /// </summary>
    [Fact]
    public void AnAssetWriteAsksForConfirmItsPreviewWritesNothingAndItIsNotOnTheUndoStack()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var folder = GroupWorkspace.PropsFolder(ws);
        const string save = """{ "refs": ["obj:Li01_14"], "nickname": "trio", "name": "Trio" """;

        var preview = Ok(host, "prop_group_save", save + """, "preview": true }""");
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
        var asks = Error(host, "prop_group_save", save + "}");
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
        var saved = Ok(host, "prop_group_save", save + """, "confirm": true }""");
        var renamePreview = Ok(host, "prop_group_rename", """{ "group": "group:trio", "nickname": "quay", "preview": true }""");
        var renamed = Ok(host, "prop_group_rename", """{ "group": "group:trio", "nickname": "quay", "confirm": true }""");
        var deleted = Ok(host, "prop_group_delete", """{ "group": "group:quay", "confirm": true }""");

        Assert.Equal(("previewed", true), ((string?)preview["status"], (bool)preview["needs_confirmation"]!));
        Assert.Equal(["group:trio"], Texts(preview["adds"]));
        Assert.Equal("The group has 3 members: 14, 14_large_transport_1, large_transport_2.",
            Texts(preview["notes"]).Single());
        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), (asks.Code, asks.Detail));
        Assert.Equal("The edit 'Save prop group trio' writes files at once. This change is not on the undo stack. To write " +
                     "the files, call the tool again with confirm: true.", asks.Message);
        Assert.Equal(("applied", "The editor wrote the files: Save prop group trio. This change is not on the undo stack."),
            ((string?)saved["status"], (string?)saved["summary"]));
        Assert.Equal(["group:trio"], Texts(saved["adds"]));
        Assert.Equal("group:quay", (string?)renamePreview["renames"]![0]!["to"]);
        Assert.Equal(("group:trio", "group:quay"), ((string?)renamed["renames"]![0]!["from"], (string?)renamed["renames"]![0]!["to"]));
        Assert.Equal("group:quay", (string?)deleted["deletes"]![0]!["ref"]);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.False(session.CanUndo);
        Assert.Equal(0, session.Revision);
        Assert.Equal(["prop_group_save", "prop_group_rename", "prop_group_delete"], host.Edits.Select(e => e.Tool));
    }

    /// <summary>
    /// An asset write refuses as a step of edit_batch, because no cancel of the batch can undo it (section 4.0.4,
    /// item 9 of the M6b design). A rename and a delete of a group of another mod refuse, and a save writes only into the
    /// folder of the active mod. A used nickname gives name_taken.
    /// </summary>
    [Fact]
    public void AnAssetWriteRefusesInABatchAndForAGroupOfAnotherMod()
    {
        using var ws = GroupWorkspace.Create();
        GroupWorkspace.WriteGroup(ws, "low_group", "[PropGroup]\nnickname = low_group\nformat = 1\n[Object]\nmember = a\n" +
                                                   "archetype = station1\n", "lower");
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var lowerFiles = Directory.GetFiles(GroupWorkspace.PropsFolder(ws, "lower"));

        var step = Error(host, "edit_batch",
            """{ "steps": [{ "tool": "prop_group_save", "arguments": { "refs": ["obj:Li01_14"], "confirm": true } }] }""");
        var rename = Error(host, "prop_group_rename", """{ "group": "group:lower/low_group", "nickname": "mine", "confirm": true }""");
        var delete = Error(host, "prop_group_delete", """{ "group": "group:lower/low_group", "confirm": true }""");
        var saved = Ok(host, "prop_group_save", """{ "refs": ["obj:Li01_dock"], "nickname": "low_group", "confirm": true }""");
        var taken = Error(host, "prop_group_save", """{ "refs": ["obj:Li01_dock"], "nickname": "low_group", "confirm": true }""");
        var bad = Call(host, "prop_group_save", """{ "refs": ["obj:Li01_dock"], "nickname": "Bad Name", "confirm": true }""");

        Assert.Equal(("refused", "StepTool"), (step.Code, step.Detail));
        Assert.Equal("Step 0: prop_group_save writes files at once, outside the Undo history. Call it alone, not in " +
                     "edit_batch.", step.Message);
        Assert.Equal(("refused", PropGroupCommands.OtherMod), (rename.Code, rename.Detail));
        Assert.Equal(("refused", PropGroupCommands.OtherMod), (delete.Code, delete.Detail));
        Assert.Equal(lowerFiles, Directory.GetFiles(GroupWorkspace.PropsFolder(ws, "lower")));
        Assert.Equal(Path.Combine(GroupWorkspace.PropsFolder(ws), "low_group.ini"),
            PropGroups.Of(session).Single(g => g.Nickname == "low_group" && g.Editable).File);
        Assert.Equal("group:low_group", Texts(saved["adds"]).Single());
        Assert.Equal(("name_taken", PropGroupCommands.NicknameTaken), (taken.Code, taken.Detail));
        Assert.Equal(("invalid_value", "nickname"), ((string?)bad.Content["code"], (string?)bad.Content["field"]));
    }

    /// <summary>
    /// An asset write and its preview give the revision of the session. An asset write does not change it. So the agent
    /// can pass it as if_revision of its next call (section 8.6, item 3 of the MCP1 design).
    /// </summary>
    [Fact]
    public void AnAssetWriteGivesTheRevisionOfTheSession()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        Ok(host, "scene_create_zone", """{ "system": "Li01", "position": [0, 0, 0] }""");
        const string save = """{ "refs": ["obj:Li01_14"], "nickname": "trio" """;

        var preview = Ok(host, "prop_group_save", save + """, "preview": true }""");
        var saved = Ok(host, "prop_group_save", save + """, "if_revision": "1", "confirm": true }""");
        var renamed = Ok(host, "prop_group_rename", """{ "group": "group:trio", "nickname": "quay", "confirm": true }""");
        var deleted = Ok(host, "prop_group_delete", """{ "group": "group:quay", "confirm": true }""");
        var next = Ok(host, "scene_create_zone",
            $$"""{ "system": "Li01", "position": [0, 0, 10], "if_revision": {{Json((string)deleted["rev"]!)}} }""");

        Assert.Equal(["1", "1", "1", "1"],
            new[] { preview, saved, renamed, deleted }.Select(r => (string?)r["rev"]));
        Assert.Equal(("applied", "2"), ((string?)next["status"], (string?)next["rev"]));
    }

    /// <summary>A rename to the nickname that the group has gives no_change and writes nothing. The host learns no
    /// edit, so the Console gets no entry (addition 10 of the contract of the M6b-1 build). A group of one member tells
    /// its member with the singular text.</summary>
    [Fact]
    public void ARenameToTheNicknameOfTheGroupGivesNoChange()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var saved = Ok(host, "prop_group_save", """{ "refs": ["obj:Li01_dock"], "nickname": "dock", "confirm": true }""");
        var file = Path.Combine(GroupWorkspace.PropsFolder(ws), "dock.ini");
        var before = File.ReadAllBytes(file);

        var preview = Ok(host, "prop_group_rename", """{ "group": "group:dock", "nickname": "dock", "preview": true }""");
        var same = Ok(host, "prop_group_rename", """{ "group": "group:dock", "nickname": "dock", "confirm": true }""");

        Assert.Equal("The group has 1 member: dock.", Texts(saved["notes"]).Single());
        foreach (var result in new[] { preview, same })
        {
            Assert.Equal(("no_change", "The data has these values already, so the edit changed nothing."),
                ((string?)result["status"], (string?)result["summary"]));
            Assert.Empty(result["renames"]!.AsArray());
        }
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(["prop_group_save"], host.Edits.Select(e => e.Tool));
    }

    /// <summary>
    /// Another program writes a group file after the tools read the props folder. Each prop group tool reads the folder
    /// again, as the editor does when it gets the focus. So a save or a rename to the nickname of the file gives
    /// name_taken and keeps the file, and list_prop_groups and prop_group_place know the group. The write and the rename
    /// of the data layer also refuse to replace a file of the folder (review finding of MCP1-2, step 4).
    /// </summary>
    [Fact]
    public void AGroupFileThatAnotherProgramWroteIsKnownAndNoWriteReplacesIt()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        Ok(host, "prop_group_save", """{ "refs": ["obj:Li01_dock"], "nickname": "dock", "confirm": true }""");
        Assert.Single(Ok(host, "list_prop_groups")["groups"]!.AsArray());
        string Other(string nickname) => GroupWorkspace.WriteGroup(ws, nickname,
            $"[PropGroup]\nnickname = {nickname}\nformat = 1\n[Object]\nmember = keepme\narchetype = station1\n");
        var files = new[] { Other("foo"), Other("bar") };
        var texts = files.Select(File.ReadAllText).ToList();

        var save = Error(host, "prop_group_save", """{ "refs": ["obj:Li01_14"], "nickname": "foo", "confirm": true }""");
        var rename = Error(host, "prop_group_rename", """{ "group": "group:dock", "nickname": "bar", "confirm": true }""");
        var listed = Ok(host, "list_prop_groups");
        var placed = Ok(host, "prop_group_place", """{ "group": "group:foo", "system": "Li01", "anchor": [0, 0, 0] }""");
        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]);
        var dock = PropGroups.Of(session).Single(g => g.Nickname == "dock");
        var third = Other("qux");
        texts.Add(File.ReadAllText(third));
        var write = Assert.Throws<System.ArgumentException>(() => PropGroups.Write(session, draft, "qux", "Qux"));
        Assert.Throws<System.ArgumentException>(() => PropGroups.Rename(session, dock, "qux"));
        var command = PropGroupCommands.Save(session, draft, "qux", "Qux");

        Assert.Equal(("name_taken", PropGroupCommands.NicknameTaken), (save.Code, save.Detail));
        Assert.Equal(("name_taken", PropGroupCommands.NicknameTaken), (rename.Code, rename.Detail));
        Assert.Equal(["group:bar", "group:dock", "group:foo"],
            listed["groups"]!.AsArray().Select(g => (string?)g!["ref"]).Order());
        Assert.Equal("applied", (string?)placed["status"]);
        Assert.StartsWith("A prop group with this nickname exists.", write.Message);
        Assert.Equal((EditErrorKind.NameTaken, PropGroupCommands.NicknameTaken),
            (command.Error?.Kind, command.Error?.Detail));
        Assert.Equal(texts, new[] { files[0], files[1], third }.Select(File.ReadAllText));
        Assert.True(File.Exists(Path.Combine(GroupWorkspace.PropsFolder(ws), "dock.ini")));
    }

    /// <summary>A session that takes no edits gives read_only for an asset write, and Pause gives paused (decision D734
    /// of the MCP1 design). Nothing is written.</summary>
    [Fact]
    public void AnAssetWriteGivesReadOnlyAndPaused()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session) { Reason = "Another editor has the mod open." };
        var folder = GroupWorkspace.PropsFolder(ws);

        var readOnly = Error(host, "prop_group_save", """{ "refs": ["obj:Li01_14"], "confirm": true }""");
        host.Reason = null;
        host.TestPaused = true;
        var paused = Error(host, "prop_group_save", """{ "refs": ["obj:Li01_14"], "confirm": true }""");

        Assert.Equal(("read_only", "Another editor has the mod open."), (readOnly.Code, readOnly.Message));
        Assert.Equal("paused", paused.Code);
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }
}

/// <summary>The data tool <c>create_from_template</c> of section 7.3 of the MCP1 design: the "New from template" of the
/// Database as one entry of the edit history.</summary>
public sealed class AgentDataToolTests
{
    /// <summary>The tool gives the W of the command of the Database through the command runner (section 13.1, item 7 of
    /// the MCP1 design). The new item is selected, and one undo removes the copies.</summary>
    [Fact]
    public void ANewItemFromATemplateGivesTheWOfTheDatabaseCommand()
    {
        using var a = ItemWorkspace.Create();
        var expected = a.Session("act");
        FakeCommandHost.Runner(expected).Run(DatabaseCommands.CreateFromTemplate(expected, ItemWorkspace.Gun("gun1"), "gun2"));

        using var b = ItemWorkspace.Create();
        var session = b.Session("act");
        var host = new TestAgentHost(session);
        var result = Ok(host, "create_from_template", """{ "kind": "equipment", "template": "gun1", "nickname": "gun2" }""");

        foreach (var path in new[] { ItemWorkspace.WeaponsPath, ItemWorkspace.WeaponGoodsPath })
            Assert.Equal(AgentSceneToolTests.W(expected, path), AgentSceneToolTests.W(session, path));
        Assert.Equal("Agent: New gun2 from gun1", (string?)result["undo_label"]);
        Assert.Equal(ItemRefs.Canonical(session, ItemWorkspace.Gun("gun2")), (string?)result["select"]);
        Assert.Contains(ItemRefs.Canonical(session, ItemWorkspace.Gun("gun2")), Texts(result["adds"]));
        session.Undo();
        Assert.Null(session.Get(ItemWorkspace.Gun("gun2")));
    }

    /// <summary>A template takes a nickname or a reference of the kind of the request. An unknown template gives
    /// not_found, and an item of another kind gives invalid_argument. A nickname that the files have refuses with the
    /// problem of the plan.</summary>
    [Fact]
    public void TheTemplateMustBeAnItemOfTheKind()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);

        var none = Error(host, "create_from_template", """{ "kind": "ship", "template": "gun1", "nickname": "gun2" }""");
        var kind = Error(host, "create_from_template",
            $$"""{ "kind": "ship", "template": {{Json(ItemRefs.Of(ItemWorkspace.Gun("gun1")))}}, "nickname": "gun2" }""");
        var taken = Error(host, "create_from_template", """{ "kind": "equipment", "template": "gun1", "nickname": "gun1" }""");
        var byRef = Ok(host, "create_from_template",
            $$"""{ "kind": "equipment", "template": {{Json(ItemRefs.Of(ItemWorkspace.Gun("gun1")))}}, "nickname": "gun3" }""");

        Assert.Equal(("not_found", ItemRefs.NotFound), (none.Code, none.Detail));
        Assert.StartsWith("The list ship has no item gun1.", none.Message);
        Assert.Equal(("invalid_argument", DataEditTools.TemplateKind), (kind.Code, kind.Detail));
        Assert.Equal("refused", taken.Code);
        Assert.Contains("gun1", taken.Message);
        Assert.Equal("applied", (string?)byRef["status"]);
        Assert.Equal(1, session.Revision);
    }
}
