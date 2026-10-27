using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The tools <c>rename_item</c>, <c>delete_items</c> and <c>duplicate_items</c> (section 7.2 of the MCP1 design). The
/// tests cover one entry with the edit origin Agent and the confirmation of a delete and a rename. They also cover the
/// limit of the items and the refusals of the plans.
/// </summary>
public sealed class AgentItemToolTests
{
    /// <summary>A system with a station that has a child, both with a position.</summary>
    private const string Outpost = """
        [Object]
        nickname = Li01_outpost
        archetype = station1
        pos = 100, 0, 0
        [Object]
        nickname = Li01_outpost_buoy
        archetype = buoy
        parent = Li01_outpost
        pos = 150, 10, 0
        [LightSource]
        nickname = Li01_lamp
        pos = 0, 0, 0
        """;

    [Fact]
    public void RenameItemRenamesAnItemOfASystemInOneAgentEntry()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var before = session.WorkingSection(SceneWorkspace.Object("Li01_dock"));

        var result = Ok(host, "rename_item", """{ "ref": "obj:Li01_dock", "new_name": "Li01_dock2" }""");

        Assert.Equal("Agent: Rename Li01_dock to Li01_dock2", (string?)result["undo_label"]);
        var rename = Assert.Single(result["renames"]!.AsArray())!;
        Assert.Equal(("obj:Li01_dock", "obj:Li01_dock2"), ((string?)rename["from"], (string?)rename["to"]));
        Assert.Equal("obj:Li01_dock2", (string?)result["select"]);
        Assert.NotNull(session.Get(SceneWorkspace.Object("Li01_dock2")));
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);

        session.Undo();
        Assert.Same(before, session.WorkingSection(SceneWorkspace.Object("Li01_dock")));
        Assert.Null(session.Get(SceneWorkspace.Object("Li01_dock2")));
    }

    /// <summary>A rename that warns needs confirm: true (decision D242). Its preview tells it and changes
    /// nothing.</summary>
    [Fact]
    public void ARenameThatWarnsNeedsAConfirmation()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var preview = Ok(host, "rename_item", """{ "ref": "obj:Li01_01", "new_name": "Li01_01b", "preview": true }""");
        var asks = Error(host, "rename_item", """{ "ref": "obj:Li01_01", "new_name": "Li01_01b" }""");
        Assert.False(session.CanUndo);
        var done = Ok(host, "rename_item", """{ "ref": "obj:Li01_01", "new_name": "Li01_01b", "confirm": true }""");

        Assert.True((bool)preview["needs_confirmation"]!);
        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), (asks.Code, asks.Detail));
        Assert.StartsWith("The edit 'Rename Li01_01 to Li01_01b' changes more than its targets.", asks.Message);
        Assert.Contains("KnowledgeMap.ini", asks.Message);
        Assert.EndsWith("To apply the edit, call the tool again with confirm: true.", asks.Message);
        Assert.Equal("applied", (string?)done["status"]);
        Assert.Equal(1, session.Revision);
    }

    [Fact]
    public void ARenameToANicknameThatAnotherItemHasGivesNameTaken()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var taken = Error(host, "rename_item", """{ "ref": "obj:Li01_dock", "new_name": "Li01_child" }""");

        Assert.Equal(("name_taken", "NameTaken"), (taken.Code, taken.Detail));
        Assert.Contains("Li01_child", taken.Message);
        Assert.False(session.CanUndo);
    }

    /// <summary>A data item calls the rename of the Database (review finding R4 of MCP1-2). An item that the active mod
    /// adds gets the new name with each line that names it. An item of the lower layer refuses.</summary>
    [Fact]
    public void RenameItemRenamesADataItemThatTheActiveModAdds()
    {
        using var ws = ItemWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, ItemWorkspace.Gun("gun1"), "gun2")));

        var renamed = Ok(host, "rename_item", $$"""{ "ref": {{Json(ItemRefs.Of(ItemWorkspace.Gun("gun2")))}}, "new_name": "gun3" }""");
        var lower = Error(host, "rename_item", $$"""{ "ref": {{Json(ItemRefs.Of(ItemWorkspace.Gun("gun1")))}}, "new_name": "gun9" }""");

        Assert.Equal("Agent: Rename gun2 to gun3", (string?)renamed["undo_label"]);
        Assert.Equal(ItemRefs.Of(ItemWorkspace.Gun("gun3")), (string?)renamed["renames"]![0]!["to"]);
        Assert.NotNull(session.Get(ItemWorkspace.Gun("gun3")));
        Assert.NotNull(session.Get(ItemWorkspace.Good(ItemWorkspace.WeaponGoodsPath, "gun3")));
        Assert.Equal("refused", lower.Code);
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);
    }

    /// <summary>The rename of a data item changes the equip line of a loadout of the mod. The result gives the loadout
    /// in its changes, and the rename needs no confirmation (review finding of MCP1-2, step 3).</summary>
    [Fact]
    public void ARenameOfADataItemChangesTheLoadoutLinesThatNameIt()
    {
        using var ws = ItemWorkspace.WithLoadouts("");
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, ItemWorkspace.Gun("gun1"), "gun2")));
        session.Execute(EditCommand.AddSection(ItemWorkspace.LoadoutsPath, "Loadout",
        [
            ItemWorkspace.Line("nickname", new StringValue("lo_mod")),
            ItemWorkspace.Line("equip", new StringValue("gun2"), new StringValue("HpWeapon01")),
        ]));
        var loadout = ItemWorkspace.Ref(ItemWorkspace.LoadoutsPath, "Loadout", "lo_mod");

        var renamed = Ok(host, "rename_item", $$"""{ "ref": {{Json(ItemRefs.Of(ItemWorkspace.Gun("gun2")))}}, "new_name": "gun3" }""");

        Assert.Equal("applied", (string?)renamed["status"]);
        Assert.Contains(ItemRefs.Canonical(session, loadout), Texts(renamed["changes"]));
        Assert.Empty(renamed["missing"]!.AsArray());
        Assert.Empty(renamed["new_problems"]!.AsArray());
        Assert.Equal(["gun3"], ItemWorkspace.Firsts(session, loadout, "equip"));
    }

    /// <summary>A loadout line that the active mod cannot patch keeps the old nickname. The preview gives it in
    /// <c>missing</c>, and the rename needs confirm: true (section 4.0.5 of the M6b design, item 6).</summary>
    [Fact]
    public void ARenameOfADataItemAsksForTheLinesThatItCannotChange()
    {
        // Two loadouts with one nickname: a patch cannot find one of them, so the session cannot change them.
        using var ws = ItemWorkspace.WithLoadouts("""
            [Loadout]
            nickname = lo_twin
            equip = gun2

            [Loadout]
            nickname = lo_twin
            equip = gun2
            """);
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, ItemWorkspace.Gun("gun1"), "gun2")));
        var gun = Json(ItemRefs.Of(ItemWorkspace.Gun("gun2")));

        var preview = Ok(host, "rename_item", $$"""{ "ref": {{gun}}, "new_name": "gun3", "preview": true }""");
        var asks = Error(host, "rename_item", $$"""{ "ref": {{gun}}, "new_name": "gun3" }""");
        var done = Ok(host, "rename_item", $$"""{ "ref": {{gun}}, "new_name": "gun3", "confirm": true }""");

        Assert.True((bool)preview["needs_confirmation"]!);
        Assert.Equal(2, preview["missing"]!.AsArray().Count);
        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), (asks.Code, asks.Detail));
        Assert.Contains("show 'Missing'", asks.Message);
        Assert.Equal("applied", (string?)done["status"]);
        Assert.All(done["missing"]!.AsArray(), m => Assert.Equal("equip", (string?)m!["key"]));
        Assert.Equal(["gun2"],
            ItemWorkspace.Firsts(session, ItemWorkspace.Ref(ItemWorkspace.LoadoutsPath, "Loadout", "lo_twin"), "equip"));
    }

    /// <summary>The preview of a delete gives the cascade, the fixes and the "Missing" lines, and does not change the
    /// revision. The delete then needs confirm: true and gives one entry. One undo puts back W (section 14.3, steps 33
    /// to 35 of the MCP1 design).</summary>
    [Fact]
    public void ADeleteOfAStationWithChildrenAsksAndUndoPutsItBack()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var before = session.Sections(SceneWorkspace.Li01Path).Select(s => session.WorkingSection(s)).ToList();
        var plan = ScenePlans.Delete(session, [Station]);

        var preview = Ok(host, "delete_items", """{ "refs": ["obj:Li01_01"], "preview": true }""");
        var asks = Error(host, "delete_items", """{ "refs": ["obj:Li01_01"] }""");
        var done = Ok(host, "delete_items", """{ "refs": ["obj:Li01_01"], "confirm": true }""");

        Assert.Equal("0", (string?)preview["rev"]);
        Assert.True((bool)preview["needs_confirmation"]!);
        Assert.Equal(plan.Deletes.Select(d => (ItemRefs.Canonical(session, d.Section), AgentTools.Snake(d.Why.ToString()))),
            preview["deletes"]!.AsArray().Select(d => ((string)d!["ref"]!, (string)d["why"]!)));
        Assert.Contains(preview["deletes"]!.AsArray(), d => (string?)d!["ref"] == "obj:Li01_child" && (string?)d["why"] == "cascade");
        Assert.Equal(("needs_confirmation", EditTools.ConfirmChanges), (asks.Code, asks.Detail));
        Assert.Contains("It also removes obj:Li01_child, obj:Li01_grandchild.", asks.Message);
        Assert.StartsWith("Agent: Delete", (string?)done["undo_label"]);
        Assert.Equal(preview["deletes"]!.AsArray().Select(d => (string?)d!["ref"]),
            done["deletes"]!.AsArray().Select(d => (string?)d!["ref"]));
        Assert.Null(session.Get(SceneWorkspace.Object("Li01_grandchild")));

        session.Undo();
        Assert.Equal(before, session.Sections(SceneWorkspace.Li01Path).Select(s => session.WorkingSection(s)));
        Assert.False(session.CanUndo);
    }

    /// <summary>For an agent, a plan that leaves "Missing" lines needs confirm: true, also when the editor does not ask
    /// (section 4.0.5 of the M6b design, item 6).</summary>
    [Fact]
    public void APlanThatLeavesMissingLinesNeedsAConfirmationOfTheAgent()
    {
        var (ws, session, _) = Scene();
        using var __ = ws;
        var plan = new CommandPlan(EditCommand.Group("Delete x", []))
        {
            MissionMissing = [new ResultMissing(@"trg:DATA\MISSIONS\M01A\m01a.ini#t1", "Act_Spawn", "The game ignores the line.", false)],
        };

        Assert.False(plan.NeedsConfirmation);
        Assert.Equal("The edit 'Delete x' changes more than its targets. After the edit, lines of " +
                     @"trg:DATA\MISSIONS\M01A\m01a.ini#t1 show 'Missing'.", EditTools.ConfirmationOf(session, plan));
        Assert.Null(EditTools.ConfirmationOf(session, new CommandPlan(EditCommand.Group("Set x", []))));
    }

    [Fact]
    public void ADeleteAboveTheItemLimitOrOfADataItemRefuses()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var many = string.Join(", ", Enumerable.Repeat(Json("obj:Li01_dock"), 201));

        var limit = Error(host, "delete_items", $$"""{ "refs": [{{many}}] }""");
        host.TestLimits = new AgentLimits { EditItems = 1 };
        var small = Error(host, "delete_items", """{ "refs": ["obj:Li01_dock", "obj:Li01_child"] }""");
        var data = Error(host, "delete_items", """{ "refs": ["base:Li01_01_Base"] }""");
        var missing = Call(host, "delete_items", """{ "refs": ["obj:none"] }""");

        Assert.Equal(("limit", "EditItems", "One call can name at most 200 items in refs. Split the call."), limit);
        Assert.Equal(("limit", "EditItems"), (small.Code, small.Detail));
        Assert.Equal("refused", data.Code);
        Assert.Contains("the Scene can delete only an object", data.Message);
        Assert.Equal(("not_found", "refs[0]"), ((string?)missing.Content["code"], (string?)missing.Content["field"]));
        Assert.False(session.CanUndo);
    }

    /// <summary>duplicate_items with an offset moves the copy of a station and the copy of its child by the offset. The
    /// move is in the same entry (review finding R6 of MCP1-2).</summary>
    [Fact]
    public void ADuplicateWithAnOffsetMovesEachCopyAndItsChildren()
    {
        var (ws, session, host) = Scene(SceneWorkspace.Li01Text + "\n" + Outpost);
        using var _ = ws;

        var preview = Ok(host, "duplicate_items",
            """{ "refs": ["obj:Li01_outpost", "light:Li01/Li01_lamp"], "offset": [1000, 0, -5], "preview": true }""");
        var result = Ok(host, "duplicate_items", """{ "refs": ["obj:Li01_outpost", "light:Li01/Li01_lamp"], "offset": [1000, 0, -5] }""");

        var adds = Texts(result["adds"]);
        Assert.Equal(3, adds.Length);
        Assert.Equal(adds, Texts(preview["adds"]));
        Assert.Empty(result["changes"]!.AsArray());
        Assert.Equal("Agent: Duplicate Li01_outpost and 2 more", (string?)result["undo_label"]);
        var copies = adds.Select(a => ItemRefs.ResolveSection(session, ItemRefs.Parse(a))).ToList();
        Assert.Equal(["1100, 0, -5", "1150, 10, -5", "1000, 0, -5"], copies.Select(c => Line(session, c, "pos")));
        Assert.Equal("100, 0, 0", Line(session, SceneWorkspace.Object("Li01_outpost"), "pos"));
        Assert.Equal(1, session.Revision);

        session.Undo();
        Assert.All(copies, c => Assert.Null(session.Get(c)));
    }

    [Fact]
    public void ADuplicateWithoutAnOffsetKeepsThePlaceAndAGateRefuses()
    {
        var (ws, session, host) = Scene(SceneWorkspace.Li01Text + "\n" + Outpost);
        using var _ = ws;

        var copy = Ok(host, "duplicate_items", """{ "refs": ["obj:Li01_outpost"] }""");
        var gate = Error(host, "duplicate_items", """{ "refs": ["obj:Li01_to_Li02"] }""");

        var first = ItemRefs.ResolveSection(session, ItemRefs.Parse(Texts(copy["adds"])[0]));
        Assert.Equal("100, 0, 0", Line(session, first, "pos"));
        Assert.Equal("refused", gate.Code);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>The command through the command runner of the editor and the tool give the same W (section 13.1, item
    /// 7 of the MCP1 design). The runner gives the edit origin User and the tool gives Agent, with the same
    /// label.</summary>
    [Theory]
    [InlineData("rename_item", """{ "ref": "obj:Li01_01", "new_name": "Li01_01b", "confirm": true }""")]
    [InlineData("delete_items", """{ "refs": ["obj:Li01_01"], "confirm": true }""")]
    [InlineData("duplicate_items", """{ "refs": ["obj:Li01_01"] }""")]
    [InlineData("set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [1, 2, 3], "loadout": null } }""")]
    public void TheRunnerAndTheToolGiveTheSameWOfTheScene(string tool, string arguments)
    {
        Func<EditSession, EditPlan> command = tool switch
        {
            "rename_item" => s => SceneCommands.Rename(s, Station, "Li01_01b"),
            "delete_items" => s => SceneCommands.Delete(s, [Station]),
            "duplicate_items" => s => SceneCommands.Duplicate(s, [Station]),
            _ => s => FieldCommands.SetFields(s, Station,
            [
                KeyValuePair.Create("pos", JsonNode.Parse("[1, 2, 3]")),
                KeyValuePair.Create("loadout", (JsonNode?)null),
            ]),
        };
        using var a = SceneWorkspace.Create();
        var expected = a.Session("act");
        var runner = FakeCommandHost.Runner(expected).Run(command);
        using var b = SceneWorkspace.Create();
        var session = b.Session("act");

        Ok(new TestAgentHost(session), tool, arguments);

        Assert.Equal(EditStatus.Applied, runner.Status);
        foreach (var path in new[] { SceneWorkspace.Li01Path, SceneWorkspace.MissionPath, SceneWorkspace.KnowledgeMapPath })
            Assert.Equal(W(expected, path), W(session, path));
        Assert.Equal((EditOrigin.User, EditOrigin.Agent), (expected.UndoOrigin, session.UndoOrigin));
        Assert.Equal(EditBatch.AgentPrefix + expected.UndoLabel, session.UndoLabel);
    }

    [Fact]
    public void TheRunnerAndTheToolGiveTheSameWAndTextOfASetText()
    {
        var gun = ItemWorkspace.Gun("gun1");
        using var a = ItemWorkspace.Create();
        var expected = a.Session("act");
        FakeCommandHost.Runner(expected).Run(s => FieldCommands.SetText(s, gun, "ids_name", "My Gun"));
        using var b = ItemWorkspace.Create();
        var session = b.Session("act");

        Ok(new TestAgentHost(session), "set_text", $$"""{ "ref": {{Json(ItemRefs.Of(gun))}}, "field": "name", "text": "My Gun" }""");

        Assert.Equal(W(expected, ItemWorkspace.WeaponsPath), W(session, ItemWorkspace.WeaponsPath));
        Assert.Equal(new ItemTexts(expected, _ => "", _ => "").TextOf(gun, "ids_name"),
            new ItemTexts(session, _ => "", _ => "").TextOf(gun, "ids_name"));
        Assert.Equal(EditBatch.AgentPrefix + expected.UndoLabel, session.UndoLabel);
    }

    /// <summary>Each section of a file of W with its lines, in file order.</summary>
    private static string[] W(EditSession session, string path) =>
        session.Sections(path).SelectMany(s => session.Get(s)!.Entries
            .Select(e => $"{e.Name} = {FieldEdit.FormatLine(e.Values)}").Prepend($"[{s.Id}]")).ToArray();
}
