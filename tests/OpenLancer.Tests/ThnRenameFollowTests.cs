using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnScripts;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// A rename of a ship or equipment that the active mod adds also changes the template of each cutscene COMPOUND that
/// names it, in the same entry of the edit history (#81, option 1). The INI rename changes each line that names the
/// item (decision D668), and Unity keeps the references to a renamed asset. A COMPOUND names a ship with the category
/// ship or spaceship, and equipment with the category equipment. A lower cutscene gets the change as a THN patch at the
/// save. A cutscene that the editor cannot change keeps the old name, and the rename warns.
/// </summary>
public sealed class ThnRenameFollowTests
{
    private const string ShipsPath = @"DATA\SHIPS\shiparch.ini";
    private const string GoodsPath = @"DATA\EQUIPMENT\goods.ini";
    private const string LowerScript = @"DATA\SCRIPTS\lower_hero.thn";
    private const string OtherScript = @"DATA\SCRIPTS\other_uses.thn";

    /// <summary>A lower script that names the new ship of the active mod in two COMPOUNDs, with other letter cases in
    /// the template and in the category. The lower data does not have the ship.</summary>
    private const string LowerHero = """
        duration = 5
        entities = {
            { entity_name = "Hero", type = COMPOUND, template_name = "my_ship", userprops = { category = "ship" } },
            { entity_name = "Wing", type = COMPOUND, template_name = "MY_SHIP", userprops = { category = "SpaceShip" } }
        }
        events = {}
        """;

    /// <summary>The entities of <see cref="LowerHero"/> with a ship COMPOUND that a later entity with the same name
    /// hides.</summary>
    private const string HeroWithTwins = """
        duration = 5
        entities = {
            { entity_name = "Hero", type = COMPOUND, template_name = "my_ship", userprops = { category = "ship" } },
            { entity_name = "Wing", type = COMPOUND, template_name = "MY_SHIP",
              userprops = { category = "SpaceShip" } },
            { entity_name = "Twin", type = COMPOUND, template_name = "my_ship", userprops = { category = "ship" } },
            { entity_name = "Twin", type = CAMERA }
        }
        events = {}
        """;

    /// <summary>
    /// A lower script that names the new ship as a word only in templates that do not name a ship: a solar COMPOUND, a
    /// COMPOUND without a category, a PSYS, a DEFORMABLE and a SOUND. A ship COMPOUND has the name with a space before
    /// it, so the engine finds no ship. The first entity "Twin" is a ship COMPOUND, but the later entity with the same
    /// name hides it, so the engine does not make it.
    /// </summary>
    private const string OtherUses = """
        duration = 5
        entities = {
            { entity_name = "Station", type = COMPOUND, template_name = "my_ship", userprops = { category = "solar" } },
            { entity_name = "Plain", type = COMPOUND, template_name = "my_ship" },
            { entity_name = "Fx", type = PSYS, template_name = "my_ship" },
            { entity_name = "Man", type = DEFORMABLE, template_name = "my_ship", userprops = { Actor = "Man" } },
            { entity_name = "Voice", type = SOUND, template_name = "my_ship" },
            { entity_name = "Spaced", type = COMPOUND, template_name = " my_ship", userprops = { category = "ship" } },
            { entity_name = "Twin", type = COMPOUND, template_name = "my_ship", userprops = { category = "ship" } },
            { entity_name = "Twin", type = CAMERA }
        }
        events = {}
        """;

    private static SectionRef Ship(string nickname) => new(ShipsPath, new SectionIdentity("Ship", nickname));

    private static SectionRef Good(string nickname) => new(GoodsPath, new SectionIdentity("Good", nickname));

    /// <summary>A session of the workspace with the new ship <c>my_ship</c> of the active mod, a copy of
    /// <c>ship1</c>.</summary>
    private static EditSession WithNewShip(TestWorkspace ws)
    {
        var session = ws.Session("act");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, Ship("ship1"), "my_ship")));
        return session;
    }

    private static ThnTable Compound(string name, string template, string category) =>
        Record(F("entity_name", T(name)), F("type", S("COMPOUND")), F("template_name", T(template)),
            F("userprops", Record(F("category", T(category)))));

    /// <summary>The template of each entity of W of a script, in order.</summary>
    private static List<string?> Templates(EditSession session, string path) =>
        [.. session.Cutscene(path)!.Entities.Select(e => (e.Table["template_name"] as ThnText)?.Value)];

    /// <summary>The script and the entity name of each changed entity of a plan.</summary>
    private static List<(string Path, string? Name)> Named(EditSession session, IEnumerable<ThnRef> entities) =>
    [
        .. entities.Select(e =>
            (e.Path, ThnScriptModel.NameOf(session.Cutscene(e.Path)!.Element(e.Kind, e.Id)!.Table))),
    ];

    private static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.DoesNotContain(result.Problems, p => p.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>
    /// The active mod adds my_ship. A new cutscene names it with the category Spaceship, and an unsaved edit of the
    /// lower script names it with the category ship. The rename changes both templates, and needs no confirmation. One
    /// undo puts back the INI and both cutscenes, and a redo changes them again. The save writes the INI patch, the
    /// whole file of the new cutscene and the THN patch of the lower script, and the save gate gives no error.
    /// </summary>
    [Fact]
    public void ARenameChangesTheTemplatesOfANewAndAnEditedCutsceneInOneEntry()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = WithNewShip(ws);
        var newPath = session.NewCutscenePath("hero")!;
        session.Execute(ThnEdits.NewCutscene(newPath, ThnTemplates.NewScript()));
        session.Execute(ThnEdits.AddEntity(newPath, Compound("Hero", "my_ship", "Spaceship")));
        ThnDataWorkspace.SetTemplate(session, "Old", "my_ship");
        var (newBefore, lowerBefore) = (Templates(session, newPath), Templates(session, ScriptPath));

        var plan = DatabaseCommands.Rename(session, Ship("my_ship"), "my_ship2");

        Assert.True(plan.CanRun);
        Assert.Equal([(newPath, "Hero"), (ScriptPath, "Old")], Named(session, plan.CutsceneChanges));
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Mentions);
        Assert.False(plan.NeedsConfirmation);
        Assert.Null(EditTools.ConfirmationOf(session, plan));

        session.Execute(plan.Command!);
        var (newAfter, lowerAfter) = (Templates(session, newPath), Templates(session, ScriptPath));

        Assert.Equal("Rename my_ship to my_ship2", session.UndoLabel);
        Assert.NotNull(session.Get(Ship("my_ship2")));
        Assert.Equal(newBefore.Select(t => t == "my_ship" ? "my_ship2" : t), newAfter);
        Assert.Contains("my_ship2", newAfter);
        Assert.Equal(lowerBefore.Select(t => t == "my_ship" ? "my_ship2" : t), lowerAfter);

        session.Undo();
        Assert.Equal(newBefore, Templates(session, newPath));
        Assert.Equal(lowerBefore, Templates(session, ScriptPath));
        Assert.NotNull(session.Get(Ship("my_ship")));
        Assert.Null(session.Get(Ship("my_ship2")));

        session.Redo();
        Assert.Equal(newAfter, Templates(session, newPath));
        Assert.Equal(lowerAfter, Templates(session, ScriptPath));

        Assert.Null(session.GateRefusal());
        var saved = session.Save();
        AssertSaved(saved);
        var mod = ws.ModFolder("act");
        Assert.Equal(
        [
            Path.Combine(mod, "files", newPath), Path.Combine(mod, PatchFile), Path.Combine(mod, "patches", ShipsPath),
        ], saved.Written.Order(System.StringComparer.OrdinalIgnoreCase));
        Assert.Contains("\"my_ship2\"", PatchText(ws));
        Assert.Contains("\"my_ship2\"", File.ReadAllText(Path.Combine(mod, "files", newPath)));
        Assert.Contains("nickname = my_ship2", ws.ReadModPatch(ShipsPath));
    }

    /// <summary>
    /// A lower script that the active mod does not change names the new ship, a name that the lower data does not
    /// have. The word index finds the script, and both COMPOUNDs follow the rename, also with other letter cases. The
    /// script is no mention, because the rename changes it. The save writes a THN patch with only the changed
    /// templates, not a copy of the script.
    /// </summary>
    [Fact]
    public void AnUnchangedLowerCutsceneGetsAThnPatch()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(LowerScript, LowerHero);
        var session = WithNewShip(ws);
        Assert.True(ThnBreaks.IsUnchanged(session, LowerScript));

        var plan = DatabaseCommands.Rename(session, Ship("my_ship"), "my_ship2");
        session.Execute(plan.Command!);
        var saved = session.Save();

        Assert.Equal([(LowerScript, "Hero"), (LowerScript, "Wing")], Named(session, plan.CutsceneChanges));
        Assert.Empty(plan.Mentions);
        Assert.False(plan.NeedsConfirmation);
        Assert.Equal(["my_ship2", "my_ship2"], Templates(session, LowerScript));
        AssertSaved(saved);
        var patch = ws.ReadModPatch(LowerScript);
        Assert.StartsWith("ol_thn_patch = 1", patch.TrimStart(), System.StringComparison.Ordinal);
        Assert.Contains("\"my_ship2\"", patch);
        Assert.DoesNotContain("duration", patch);
        Assert.Equal(ThnFileKind.PatchedHere, session.CutsceneSource(LowerScript).Kind);
    }

    /// <summary>A rename of a good of the active mod also renames its equipment, because the engine finds the
    /// equipment of a good by its nickname. So a COMPOUND of the category equipment that names the equipment
    /// follows.</summary>
    [Fact]
    public void ARenameOfAGoodChangesTheTemplatesOfItsEquipment()
    {
        using var ws = ThnDataWorkspace.Create(@"goods = equipment\goods.ini");
        ws.WriteBase(GoodsPath,
            "[Good]\nnickname = thruster1\nequipment = thruster1\ncategory = equipment\nprice = 10\n");
        var session = ws.Session("act");
        var thruster = ThnDataWorkspace.Equipment("Thruster", "thruster1");
        session.Execute(ItemTemplates.Build(ItemTemplates.Plan(session, thruster, "my_gear")));
        ThnDataWorkspace.SetTemplate(session, "Gear", "my_gear");

        var plan = DatabaseCommands.Rename(session, Good("my_gear"), "my_gear2");
        session.Execute(plan.Command!);

        Assert.Equal([(ScriptPath, "Gear")], Named(session, plan.CutsceneChanges));
        Assert.NotNull(session.Get(ThnDataWorkspace.Equipment("Thruster", "my_gear2")));
        Assert.Contains("my_gear2", Templates(session, ScriptPath));
        Assert.DoesNotContain("my_gear", Templates(session, ScriptPath));
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        Assert.Null(session.GateRefusal());
    }

    /// <summary>
    /// The same text in a template that does not name a ship does not change: a solar COMPOUND, a COMPOUND without a
    /// category, a PSYS, a DEFORMABLE, a SOUND, and a ship COMPOUND whose text has a space before the name. A ship
    /// COMPOUND that a later entity with the same name hides keeps the name with no warning, because the engine does
    /// not make it. The script stays a mention, so the rename asks for a confirmation.
    /// </summary>
    [Fact]
    public void TheSameTextInATemplateOfAnotherKindDoesNotChange()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(OtherScript, OtherUses);
        var session = WithNewShip(ws);
        var before = Templates(session, OtherScript);

        var plan = DatabaseCommands.Rename(session, Ship("my_ship"), "my_ship2");
        session.Execute(plan.Command!);

        Assert.Empty(plan.CutsceneChanges);
        Assert.Empty(plan.Warnings);
        Assert.Equal([OtherScript], plan.Mentions.Select(m => m.Path));
        Assert.True(plan.NeedsConfirmation);
        Assert.Equal(before, Templates(session, OtherScript));
        Assert.False(session.HasUnsavedCutsceneEdits(OtherScript));
    }

    /// <summary>
    /// A cutscene that the editor cannot change keeps the old name: here the active mod has a whole file and a THN
    /// patch for it. The rename warns with what the game does, and tells that the save gate does not check such a
    /// cutscene. So the rename needs a confirmation, and the question of the agent gives the warning. A COMPOUND that a
    /// later entity with the same name hides gives no warning, because the engine does not make it.
    /// </summary>
    [Fact]
    public void ACutsceneThatTheEditorCannotChangeKeepsTheNameAndWarns()
    {
        using var ws = ThnDataWorkspace.Create();
        ThnBreaks.WriteModFile(ws, WholeFile, HeroWithTwins);
        ThnBreaks.WriteModFile(ws, PatchFile, "ol_thn_patch = 1\n");
        var session = WithNewShip(ws);
        var reason = session.CutsceneReadOnlyReason(ScriptPath);
        Assert.NotNull(reason);

        var plan = DatabaseCommands.Rename(session, Ship("my_ship"), "my_ship2");
        session.Execute(plan.Command!);

        Assert.Empty(plan.CutsceneChanges);
        Assert.Equal(
        [
            $"The entity \"Hero\" of the cutscene {ScriptPath} keeps the template my_ship, because the editor cannot " +
            $"change it. {reason} After the rename, the game data has no ship my_ship. The game crashes when it " +
            "plays this cutscene. The save gate does not check a cutscene that the editor cannot change.",
            $"The entity \"Wing\" of the cutscene {ScriptPath} keeps the template MY_SHIP, because the editor cannot " +
            $"change it. {reason} After the rename, the game data has no ship MY_SHIP. The game crashes when it " +
            "plays this cutscene. The save gate does not check a cutscene that the editor cannot change.",
        ], plan.Warnings.Select(w => w.Message));
        Assert.All(plan.Warnings, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
        Assert.True(plan.NeedsConfirmation);
        Assert.Contains(plan.Warnings[0].Message, EditTools.ConfirmationOf(session, plan));
        Assert.Equal(["my_ship", "MY_SHIP", "my_ship", null], Templates(session, ScriptPath));
    }

    /// <summary>
    /// The agent tool rename_item gives the changed cutscene entities in changes, as thn: references, and no new
    /// problem. It needs no confirmation. A preview gives the same changes and changes nothing. One undo puts back the
    /// ship and both cutscenes.
    /// </summary>
    [Fact]
    public void RenameItemGivesTheChangedCutsceneEntities()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(LowerScript, LowerHero);
        var session = WithNewShip(ws);
        ThnDataWorkspace.SetTemplate(session, "Old", "my_ship");
        var host = new TestAgentHost(session);
        var ship = AgentCalls.Json(ItemRefs.Of(Ship("my_ship")));
        var arguments = $$"""{ "ref": {{ship}}, "new_name": "my_ship2" """;
        string[] entities =
            [$"thn:{LowerScript}#entity/Hero", $"thn:{LowerScript}#entity/Wing", $"thn:{ScriptPath}#entity/Old"];
        var (lowerBefore, scriptBefore) = (Templates(session, LowerScript), Templates(session, ScriptPath));
        static List<string> Changes(JsonObject result) => [.. result["changes"]!.AsArray().Select(c => (string)c!)];

        var preview = AgentCalls.Ok(host, "rename_item", arguments + """, "preview": true }""");

        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Equal(entities, Changes(preview).Where(c => c.StartsWith("thn:", System.StringComparison.Ordinal)));
        Assert.Empty(preview["new_problems"]!.AsArray());
        Assert.False((bool)preview["needs_confirmation"]!);
        Assert.Equal(lowerBefore, Templates(session, LowerScript));
        Assert.Equal(scriptBefore, Templates(session, ScriptPath));
        Assert.NotNull(session.Get(Ship("my_ship")));

        var done = AgentCalls.Ok(host, "rename_item", arguments + "}");

        Assert.Equal("applied", (string?)done["status"]);
        Assert.Equal(Changes(preview), Changes(done));
        Assert.Empty(done["new_problems"]!.AsArray());
        Assert.Equal(["my_ship2", "my_ship2"], Templates(session, LowerScript));
        Assert.Contains("my_ship2", Templates(session, ScriptPath));

        AgentCalls.Ok(host, "undo");

        Assert.Equal(lowerBefore, Templates(session, LowerScript));
        Assert.Equal(scriptBefore, Templates(session, ScriptPath));
        Assert.NotNull(session.Get(Ship("my_ship")));
        Assert.Null(session.Get(Ship("my_ship2")));
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
    }

    /// <summary>A rename holds the cutscene entities that it changes. When a template changes before the rename runs,
    /// the rename does not run, because it would undo that change.</summary>
    [Fact]
    public void ARenameDoesNotRunAfterAnEditOfATemplateThatItChanges()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = WithNewShip(ws);
        ThnDataWorkspace.SetTemplate(session, "Old", "my_ship");
        var plan = DatabaseCommands.Rename(session, Ship("my_ship"), "my_ship2");

        ThnDataWorkspace.SetTemplate(session, "Old", "ship1");

        Assert.Throws<System.InvalidOperationException>(() => session.Execute(plan.Command!));
        Assert.NotNull(session.Get(Ship("my_ship")));
        Assert.Contains("ship1", Templates(session, ScriptPath));
        Assert.DoesNotContain("my_ship2", Templates(session, ScriptPath));
    }

    /// <summary>
    /// In one edit_batch, a step sets the template of an entity to the new ship, and a later step renames the ship. The
    /// word index does not have the edit of the first step, and the batch has not committed it. The rename still finds
    /// the entity, because it reads each cutscene with unsaved edits. The result gives the entity once.
    /// </summary>
    [Fact]
    public void ARenameInABatchChangesATemplateOfAnEarlierStep()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = WithNewShip(ws);
        var host = new TestAgentHost(session);
        var ship = AgentCalls.Json(ItemRefs.Of(Ship("my_ship")));
        var old = AgentCalls.Json($"thn:{ScriptPath}#entity/Old");

        var result = AgentCalls.Ok(host, "edit_batch", $$"""
            { "steps": [
              { "tool": "set_fields", "arguments": { "ref": {{old}}, "fields": { "template_name": "my_ship" } } },
              { "tool": "rename_item", "arguments": { "ref": {{ship}}, "new_name": "my_ship2" } } ] }
            """);

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal([$"thn:{ScriptPath}#entity/Old"], result["changes"]!.AsArray().Select(c => (string)c!)
            .Where(c => c.StartsWith("thn:", System.StringComparison.Ordinal)));
        Assert.Contains("my_ship2", Templates(session, ScriptPath));
        Assert.Empty(result["new_problems"]!.AsArray());
    }

    /// <summary>A rename that asks for another reason names the changed cutscenes in its question, as it names the
    /// mission scripts. Here another script names the ship as text only.</summary>
    [Fact]
    public void TheQuestionOfARenameNamesTheChangedCutscenes()
    {
        using var ws = ThnDataWorkspace.Create();
        ws.WriteBase(LowerScript, LowerHero);
        ws.WriteBase(OtherScript, OtherUses);
        var session = WithNewShip(ws);
        var host = new TestAgentHost(session);
        var ship = AgentCalls.Json(ItemRefs.Of(Ship("my_ship")));

        var (code, _, message) =
            AgentCalls.Error(host, "rename_item", $$"""{ "ref": {{ship}}, "new_name": "my_ship2" }""");

        Assert.Equal("needs_confirmation", code);
        Assert.Contains($"It also changes 2 cutscene templates that name the item, in {LowerScript}.", message);
        Assert.Contains($"These files name the item as text: {OtherScript}.", message);
        Assert.Equal(["my_ship", "MY_SHIP"], Templates(session, LowerScript));
    }
}
