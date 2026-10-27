using System;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Strings;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>Edits of whole items: a rename that follows the links, and the text of an item (review finding
/// F12).</summary>
public sealed class ItemEditsTests
{
    [Fact]
    public void ARenameChangesTheLinksInTheSectionsThatTheModCanChange()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));
        // The mod mounts the new gun on the vanilla package, and sells it at a new market section and in a vanilla one.
        session.Execute(EditCommand.SetLines(Good(GoodsPath, "s1_package"), "addon",
            [[new StringValue("eng1"), new StringValue("internal"), new Int32Value(1)],
             [new StringValue("gun2"), new StringValue("HpWeapon01"), new Int32Value(1)]]));
        session.Execute(EditCommand.AddSection(MarketPath, "BaseGood",
            [Line("base", new StringValue("b2")), Line("marketgood", new StringValue("GUN2"), new Int32Value(0))]));

        session.Execute(ItemEdits.Rename(session, Gun("gun2"), "gun3"));

        Assert.Equal("Rename gun2 to gun3", session.UndoLabel);
        Assert.Null(session.Get(Gun("gun2")));
        // Equipment and its good keep the same nickname, and each link names the new nickname.
        Assert.Equal(["gun3"], Firsts(session, Good(WeaponGoodsPath, "gun3"), "equipment"));
        Assert.Equal(["eng1", "gun3"], Firsts(session, Good(GoodsPath, "s1_package"), "addon"));
        Assert.Equal(["gun3"], Firsts(session, Market(2), "marketgood"));
        Assert.Equal(["gun3"], session.MarketLines("gun3").Select(l => l.Values[0].ToString()));
        // The munition keeps its nickname, so the gun still names it.
        Assert.Equal(["gun2_ammo"], Firsts(session, Gun("gun3"), "projectile_archetype"));
        Assert.Equal([Good(WeaponGoodsPath, "gun3")], session.Related(Gun("gun3"), ItemRelation.Good));

        // One undo takes back the whole rename.
        session.Undo();
        Assert.Equal(["gun2"], Firsts(session, Good(WeaponGoodsPath, "gun2"), "equipment"));
        Assert.Equal(["eng1", "gun2"], Firsts(session, Good(GoodsPath, "s1_package"), "addon"));
        Assert.Equal(["GUN2"], Firsts(session, Market(2), "marketgood"));
    }

    [Fact]
    public void ARenameOfAShipOrAHullGoodChangesTheLinksOfTheShip()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Ship("s1"), "s2")));

        session.Execute(ItemEdits.Rename(session, Ship("s2"), "s3"));
        session.Execute(ItemEdits.Rename(session, Good(GoodsPath, "s2_hull"), "s3_hull"));

        Assert.Equal(["s3"], Firsts(session, Good(GoodsPath, "s3_hull"), "ship"));
        Assert.Equal(["s3_hull"], Firsts(session, Good(GoodsPath, "s2_package"), "hull"));
        Assert.Equal([Good(GoodsPath, "s2_package")], session.Related(Ship("s3"), ItemRelation.Package));
    }

    [Fact]
    public void ARenameRefusesANicknameOfTheKindAndASectionOfALowerLayer()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));

        // eng1 is equipment in another file of the kind.
        Assert.Throws<InvalidOperationException>(() => ItemEdits.Rename(session, Gun("gun2"), "eng1"));
        Assert.Throws<ArgumentException>(() => ItemEdits.Rename(session, Gun("nothing"), "x"));
        // Only a new section gets a new key, so the edit refuses at once. The command of the rename gives the same
        // reason when it runs.
        var lower = Assert.Throws<ArgumentException>(() => ItemEdits.Rename(session, Gun("gun1"), "gun9"));
        Assert.Equal("[Gun] nickname = gun1: the editor can change the key only of a section that the active mod adds.",
            ErrorText.Of(lower));
        var same = Assert.Throws<ArgumentException>(() => ItemEdits.Rename(session, Gun("gun2"), "GUN2"));
        Assert.Equal("[Gun] nickname = gun2: the new key must differ from the old key in more than letter case.",
            ErrorText.Of(same));
        Assert.NotNull(session.Get(Good(WeaponGoodsPath, "gun1")));
        Assert.Equal("New gun2 from gun1", session.UndoLabel);
    }

    [Fact]
    public void SharedTextGivesTheOtherSectionsOfTheItemWithTheSameTextId()
    {
        using var ws = Create();
        var session = ws.Session("act");

        // A retail gun and its good have the same ids_name. The Inspector shows the gun, a shop shows the good.
        Assert.Equal([Good(WeaponGoodsPath, "gun1")], ItemEdits.SharedText(session, Gun("gun1"), "ids_name"));
        Assert.Equal([Gun("gun1")], ItemEdits.SharedText(session, Good(WeaponGoodsPath, "gun1"), "ids_name"));
        // The hull good of the ship has its own name. The good of the gun has no ids_info.
        Assert.Empty(ItemEdits.SharedText(session, Ship("s1"), "ids_name"));
        Assert.Empty(ItemEdits.SharedText(session, Gun("gun1"), "ids_info"));
    }

    [Fact]
    public void SetTextMakesTheKeyFromTheNicknameAndSetsThePlaceholders()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var good = Good(WeaponGoodsPath, "gun1");

        session.Execute(ItemEdits.SetText(session, Gun("gun1"), "ids_name", "Ion Gun", ItemEdits.SharedText(session, Gun("gun1"), "ids_name")));
        session.Execute(ItemEdits.SetText(session, Gun("gun1"), "ids_info", ItemEdits.ToInfocard("Fast.")));

        Assert.Equal(["$str:gun1"], Firsts(session, Gun("gun1"), "ids_name"));
        Assert.Equal(["$str:gun1"], Firsts(session, good, "ids_name"));
        Assert.Equal("Ion Gun", session.Text(new StringPlaceholder(StringKind.String, "gun1")));
        Assert.Equal(["$ifc:gun1.info"], Firsts(session, Gun("gun1"), "ids_info"));
        // A second edit of the sections that share the text keeps the key, and changes only the text.
        session.Execute(ItemEdits.SetText(session, good, "ids_name", "Ion Blaster",
            ItemEdits.SharedText(session, good, "ids_name")));
        Assert.Equal(["$str:gun1"], Firsts(session, good, "ids_name"));
        Assert.Equal("Ion Blaster", session.Text(new StringPlaceholder(StringKind.String, "gun1")));
        Assert.Equal("Set ids_name of gun1", session.UndoLabel);

        var result = session.Save();
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Contains("\"gun1\": \"Ion Blaster\"", File.ReadAllText(Path.Combine(ws.ModFolder("act"), "strings.json")));
        Assert.Throws<ArgumentException>(() => ItemEdits.SetText(session, Gun("gun1"), "hit_pts", "x"));
        Assert.Throws<ArgumentException>(() => ItemEdits.SetText(session, Market(0), "ids_name", "x"));
    }

    /// <summary>
    /// A gun and its good have the same nickname, so the same first key for a text. A text edit of one of them alone
    /// gives the other section its own key, and the text of the other section stays (review finding F12: the shared
    /// text is an offer).
    /// </summary>
    [Fact]
    public void ATextEditOfOneSectionKeepsTheTextOfEachOtherSection()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var good = Good(WeaponGoodsPath, "gun1");
        string? Text(StringKind kind, string key) => session.Text(new StringPlaceholder(kind, key));

        session.Execute(ItemEdits.SetText(session, Gun("gun1"), "ids_name", "Ion Gun"));
        session.Execute(ItemEdits.SetText(session, good, "ids_name", "Shop Gun"));
        // An infocard is RDL XML, as the Inspector writes it. The retail game shows no text for another text.
        session.Execute(ItemEdits.SetText(session, Gun("gun1"), "ids_info", ItemEdits.ToInfocard("Gun info")));
        session.Execute(ItemEdits.SetText(session, good, "ids_info", ItemEdits.ToInfocard("Good info")));

        Assert.Equal(["$str:gun1"], Firsts(session, Gun("gun1"), "ids_name"));
        Assert.Equal(["$str:gun1.good"], Firsts(session, good, "ids_name"));
        Assert.Equal(["$ifc:gun1.info"], Firsts(session, Gun("gun1"), "ids_info"));
        Assert.Equal(["$ifc:gun1.good.info"], Firsts(session, good, "ids_info"));
        Assert.Equal(("Ion Gun", "Shop Gun"), (Text(StringKind.String, "gun1"), Text(StringKind.String, "gun1.good")));
        Assert.Equal((ItemEdits.ToInfocard("Gun info"), ItemEdits.ToInfocard("Good info")),
            (Text(StringKind.Infocard, "gun1.info"), Text(StringKind.Infocard, "gun1.good.info")));

        // The offer joins the two sections in one text. A later edit of one of them alone gives it a new key. The
        // key of the text that no section uses now stays as it is.
        session.Execute(ItemEdits.SetText(session, Gun("gun1"), "ids_name", "Both", [good]));
        Assert.Equal(["$str:gun1"], Firsts(session, good, "ids_name"));
        session.Execute(ItemEdits.SetText(session, good, "ids_name", "Shop only"));
        Assert.Equal(["$str:gun1.good_2"], Firsts(session, good, "ids_name"));
        Assert.Equal(("Both", "Shop Gun", "Shop only"),
            (Text(StringKind.String, "gun1"), Text(StringKind.String, "gun1.good"), Text(StringKind.String, "gun1.good_2")));

        var result = session.Save();
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        session.Dispose();
        var reopened = ws.Session("act");
        Assert.Equal(["$str:gun1"], Firsts(reopened, Gun("gun1"), "ids_name"));
        Assert.Equal("Both", reopened.Text(new StringPlaceholder(StringKind.String, "gun1")));
        Assert.Equal("Shop only", reopened.Text(new StringPlaceholder(StringKind.String, "gun1.good_2")));
    }

    /// <summary>A new item can get the nickname that a renamed item had. The renamed item keeps its text key, so
    /// the text of the new item gets another key.</summary>
    [Fact]
    public void ATextEditDoesNotTakeTheKeyOfARenamedItem()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));
        session.Execute(ItemEdits.SetText(session, Gun("gun2"), "ids_name", "Gun Two"));
        session.Execute(ItemEdits.Rename(session, Gun("gun2"), "gun3"));
        // The munition gun2_ammo keeps its nickname, so the new item is an engine.
        var engine = Ref(MiscPath, "Engine", "gun2");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Ref(MiscPath, "Engine", "eng1"), "gun2")));

        session.Execute(ItemEdits.SetText(session, engine, "ids_name", "Engine Two"));

        Assert.Equal(["$str:gun2"], Firsts(session, Gun("gun3"), "ids_name"));
        Assert.Equal(["$str:gun2.engine"], Firsts(session, engine, "ids_name"));
        Assert.Equal("Gun Two", session.Text(new StringPlaceholder(StringKind.String, "gun2")));
    }

    /// <summary>The engine finds the equipment of a good by the nickname of the good. So a rename of a good also
    /// renames its equipment, and each link follows.</summary>
    [Fact]
    public void ARenameOfAGoodAlsoRenamesItsEquipment()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));
        session.Execute(EditCommand.SetLines(Good(GoodsPath, "s1_package"), "addon",
            [[new StringValue("gun2"), new StringValue("HpWeapon01"), new Int32Value(1)]]));

        session.Execute(ItemEdits.Rename(session, Good(WeaponGoodsPath, "gun2"), "gun3"));

        Assert.Null(session.Get(Gun("gun2")));
        Assert.NotNull(session.Get(Gun("gun3")));
        Assert.Equal(["gun3"], Firsts(session, Good(WeaponGoodsPath, "gun3"), "equipment"));
        Assert.Equal(["gun3"], Firsts(session, Good(GoodsPath, "s1_package"), "addon"));
        Assert.Equal([Good(WeaponGoodsPath, "gun3")], session.Related(Gun("gun3"), ItemRelation.Good));
    }

    [Fact]
    public void ARenameRefusesANicknameThatAPatchLineCannotHold()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));

        var problem = Assert.Throws<ArgumentException>(() => ItemEdits.Rename(session, Gun("gun2"), "bad,name"));
        Assert.Contains("','", problem.Message);
        Assert.Equal("New gun2 from gun1", session.UndoLabel);
    }

    /// <summary>A rename and a text edit hold values that they read from the edit view. So they do not run after an
    /// edit in between, which they would undo.</summary>
    [Fact]
    public void AnItemEditDoesNotRunAfterTheEditViewChanged()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Gun("gun1"), "gun2")));
        session.Execute(EditCommand.SetLines(Good(GoodsPath, "s1_package"), "addon",
            [[new StringValue("gun2"), new StringValue("HpWeapon01"), new Int32Value(1)]]));
        var rename = ItemEdits.Rename(session, Gun("gun2"), "gun3");
        var text = ItemEdits.SetText(session, Gun("gun2"), "ids_name", "Gun Two");

        session.Execute(EditCommand.SetLines(Good(GoodsPath, "s1_package"), "addon",
            [[new StringValue("gun2"), new StringValue("HpWeapon02"), new Int32Value(1)]]));

        Assert.Throws<InvalidOperationException>(() => session.Execute(rename));
        Assert.Throws<InvalidOperationException>(() => session.Execute(text));
        Assert.Equal(["gun2", "HpWeapon02", "1"],
            session.Get(Good(GoodsPath, "s1_package"))!.Lines("addon")[0].Values.Select(v => v.ToString()));
        // An undo puts back the state that the edits read, so they run again.
        session.Undo();
        session.Execute(rename);
        Assert.Equal(["gun3", "HpWeapon01", "1"],
            session.Get(Good(GoodsPath, "s1_package"))!.Lines("addon")[0].Values.Select(v => v.ToString()));
    }

    [Fact]
    public void ToInfocardMakesAParagraphOfEachLine()
    {
        Assert.Equal("<?xml version=\"1.0\" encoding=\"UTF-16\"?><RDL><PUSH/><TEXT>Fast &amp; &lt;cheap&gt;.</TEXT><PARA/>" +
                     "<PARA/><TEXT>Line 2</TEXT><PARA/><POP/></RDL>",
            ItemEdits.ToInfocard("Fast & <cheap>.\r\n\nLine 2"));
    }

    /// <summary>A mission line that names the item and that the edit cannot change gives a warning at once. Else the user
    /// learns about it only at the save (rule M23).</summary>
    [Fact]
    public void ARenameWarnsAboutAMissionLineThatItCannotChange()
    {
        using var ws = Create();
        ws.WriteBase(@"DATA\MISSIONS\OL_TEST\ol_test.ini", "[Mission]\nnpc_ship_file = missions\\ol_test\\npcships.ini\n");
        // Two builds with one nickname: a patch cannot find one of them, so the session cannot change them.
        ws.WriteBase(@"DATA\MISSIONS\OL_TEST\npcships.ini",
            "[NPCShipArch]\nnickname = ol_twin\nship_archetype = s2\n\n[NPCShipArch]\nnickname = ol_twin\nship_archetype = s2\n");
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Ship("s1"), "s2")));
        var warnings = new System.Collections.Generic.List<OpenLancer.Data.Diagnostic>();

        session.Execute(ItemEdits.Rename(session, Ship("s2"), "s3", warnings));

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("the editor cannot change the 'ship_archetype' line", w.Message));
    }
}
