using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Inspector;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// A synthetic profile with the game data of the cutscene templates (section 12.1 of the M8b design): solar
/// archetypes with and without a model file, a ship, equipment of four section types, an asteroid and a mine, body
/// parts and costumes, effects, sounds, and two petalDB files. The script has an entity of each kind of template, and a
/// retail-like ship that the lists do not have.
/// </summary>
internal static class ThnDataWorkspace
{
    public const string CostumesPath = @"DATA\CHARACTERS\costumes.ini";
    public const string BodypartsPath = @"DATA\CHARACTERS\bodyparts.ini";
    public const string SpaceScript = @"DATA\MISSIONS\M01A\space.thn";
    public const string EquipmentPath = @"DATA\EQUIPMENT\misc_equip.ini";
    public const string MissionPath = @"DATA\MISSIONS\M01A\m01a.ini";

    public const string Script = """
        duration = 10
        entities = {
            { entity_name = "Man", type = DEFORMABLE, template_name = "trent" },
            { entity_name = "Ship", type = COMPOUND, template_name = "ship1", userprops = { category = "Spaceship" } },
            { entity_name = "Station", type = COMPOUND, template_name = "station1", userprops = { category = "solar" } },
            { entity_name = "Rock", type = COMPOUND, template_name = "$terrain_dyna_01", userprops = { category = "solar" } },
            { entity_name = "Gear", type = COMPOUND, template_name = "thruster1", userprops = { category = "equipment" } },
            { entity_name = "Mine", type = COMPOUND, template_name = "mine1", userprops = { category = "asteroid" } },
            { entity_name = "Cup", type = COMPOUND, template_name = "mod_cup", userprops = { category = "prop" } },
            { entity_name = "Bar", type = COMPOUND, template_name = "bar1", userprops = { category = "room" } },
            { entity_name = "Old", type = COMPOUND, template_name = "old_ship", userprops = { category = "ship" } },
            { entity_name = "Fx", type = PSYS, template_name = "fx1" },
            { entity_name = "Halo", type = PSYS, template_name = "lost_halo" },
            { entity_name = "Vis", type = PSYS, template_name = "visfx1" },
            { entity_name = "Engines", type = PSYS, template_name = "PlayerShipEngines" },
            { entity_name = "Voice", type = SOUND, template_name = "voice_line_01" },
            { entity_name = "Cam", type = CAMERA, cameraprops = { fovh = 10 } }
        }
        events = {}
        """;

    /// <summary>A workspace with the game data files in the base folder, the script, a script that the game plays
    /// only in space, the empty active mod "act" and the profile.</summary>
    public static TestWorkspace Create()
    {
        var ws = ThnSessions.Workspace(script: Script);
        ws.WriteBase(@"EXE\freelancer.ini", $"""
            [Freelancer]
            data path = ..\data

            {TestWorkspace.ResourcesSection}

            [Data]
            solar = solar\solararch.ini
            ships = ships\shiparch.ini
            equipment = equipment\misc_equip.ini
            asteroids = solar\asteroidarch.ini
            bodyparts = characters\bodyparts.ini
            costumes = characters\costumes.ini
            effects = fx\effects.ini
            sounds = audio\sounds.ini
            PetalDB = petaldb.ini
            PetalDB = petaldb_more.ini
            """);
        ws.WriteBase(@"DATA\SOLAR\solararch.ini", """
            [Solar]
            nickname = station1
            DA_archetype = solar\station1.cmp
            [Solar]
            nickname = no_model
            [Solar]
            nickname = lost_model
            DA_archetype = solar\lost.cmp
            """);
        ws.WriteBase(@"DATA\SOLAR\station1.cmp", "model");
        ws.WriteBase(@"DATA\SHIPS\shiparch.ini", "[Ship]\nnickname = ship1\nDA_archetype = ships\\ship1.cmp\n");
        ws.WriteBase(@"DATA\SHIPS\ship1.cmp", "model");
        // The engine gives a model to a [Thruster]. It makes a [Light] and a [Commodity] without a model, also with a
        // model file, and it makes no item of an [Armor].
        ws.WriteBase(EquipmentPath, """
            [Thruster]
            nickname = thruster1
            DA_archetype = equipment\thruster1.3db
            [Light]
            nickname = light1
            DA_archetype = equipment\light1.3db
            [Commodity]
            nickname = commodity1
            [Armor]
            nickname = armor1
            DA_archetype = equipment\armor1.3db
            """);
        ws.WriteBase(@"DATA\EQUIPMENT\thruster1.3db", "model");
        ws.WriteBase(@"DATA\EQUIPMENT\light1.3db", "model");
        ws.WriteBase(@"DATA\EQUIPMENT\armor1.3db", "model");
        ws.WriteBase(@"DATA\SOLAR\asteroidarch.ini", """
            [Asteroid]
            nickname = rock1
            DA_archetype = solar\rock1.3db
            [AsteroidMine]
            nickname = mine1
            DA_archetype = solar\mine1.3db
            """);
        ws.WriteBase(@"DATA\SOLAR\rock1.3db", "model");
        ws.WriteBase(@"DATA\SOLAR\mine1.3db", "model");
        ws.WriteBase(BodypartsPath, "[Body]\nnickname = body1\nmesh = characters\\body1.dfm\n");
        ws.WriteBase(CostumesPath, "[Costume]\nnickname = trent\nbody = body1\n");
        ws.WriteBase(@"DATA\FX\effects.ini", "[Effect]\nnickname = fx1\n[VisEffect]\nnickname = visfx1\n");
        ws.WriteBase(@"DATA\AUDIO\sounds.ini", "[Sound]\nnickname = sound1\nfile = audio\\sound1.wav\n");
        ws.WriteBase(@"DATA\petaldb.ini", "[ObjectTable]\nroom = bar1, rooms\\bar1.cmp\nprop = cup, props\\cup.3db\ncart = cart1, carts\\cart1.cmp\n");
        ws.WriteBase(@"DATA\petaldb_more.ini", "[ObjectTable]\nprop = mod_cup, props\\mod_cup.3db\n");
        ws.WriteBase(SpaceScript, """
            duration = 10
            entities = { { entity_name = "Ship", type = COMPOUND, template_name = "ship1", userprops = { category = "ship" } } }
            events = {}
            """);
        ws.WriteBase(MissionPath, "[Trigger]\nnickname = t1\nAct_CallThorn = Missions\\\\M01A\\\\space.thn\n");
        return ws;
    }

    /// <summary>Sets the template of an entity of the script.</summary>
    public static void SetTemplate(EditSession session, string entity, string template, string path = ScriptPath) =>
        session.Execute(ThnEdits.Set(new ThnRef(path, ThnElementKind.Entity,
            session.Cutscene(path)!.Entities.Single(e => ThnScriptModel.NameOf(e.Table) == entity).Id),
            new ThnPath("template_name"), new ThnText(template)));

    public static EntryView Line(string name, string value) => new(name, [new StringValue(value)]);

    /// <summary>The trigger of the mission file. Its <c>Act_CallThorn</c> line plays <see cref="SpaceScript"/>.</summary>
    public static SectionRef Trigger => new(MissionPath, new SectionIdentity("Trigger", "t1"));

    /// <summary>Sets the <c>Act_CallThorn</c> line of the trigger, with the guard of the mission files open.</summary>
    public static void CallThorn(EditSession session, string script) =>
        session.Execute(MissionTestData.Raw(EditCommand.SetLinesFromText(Trigger, "Act_CallThorn", [script])));

    public static SectionRef Costume(string nickname) => new(CostumesPath, new SectionIdentity("Costume", nickname));

    public static SectionRef Equipment(string type, string nickname) => new(EquipmentPath, new SectionIdentity(type, nickname));
}

/// <summary>
/// The save gate checks the templates and the costumes of a cutscene with the data lists of W (#19, section 12.1 of
/// the M8b design, decisions D96 and D120): a name of the active mod that the lists do not have blocks Save and Play,
/// with the reason. A crash keeps the start "The game crashes when it plays this cutscene.", and a name problem tells
/// what the scene does. An unsaved INI edit counts, a retail value stays out of the gate, and an undo heals.
/// </summary>
public sealed class ThnNameGateTests
{
    private const string Crash = "The game crashes when it plays this cutscene. ";

    private const string CostumeMissing = Crash + "entity \"Man\", template_name: The costume \"ghost\" is not in the game " +
                                          "data. A DEFORMABLE without userprops.Actor needs it.";

    /// <summary>The message of a costume whose body names no body part. The lists have the costume, so the message
    /// gives the reason of the list checks.</summary>
    private static string CostumeWithoutBody(string costume) =>
        Crash + $"entity \"Man\", template_name: The costume \"{costume}\" is not usable: 'body = no_body': no body has " +
        "the name 'no_body'. A DEFORMABLE without userprops.Actor needs it.";

    private const string GhostShip = Crash + "entity \"Ship\", template_name: The game data has no ship \"ghost_ship\".";

    private static string[] Errors(EditSession session, string path = ScriptPath) =>
        [.. session.CutsceneErrors(path).Select(e => e.Message)];

    /// <summary>The script names known items, so it has no problem. The retail-like ship that the lists do not have
    /// is in B too, so it stays out of the gate, also after an edit of another entity.</summary>
    [Fact]
    public void AKnownNameAndAnUnchangedLowerValueDoNotBlock()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        Assert.Empty(Errors(session));
        // B has the problem of the retail-like ship, with the same game data.
        var lower = ThnChecks.Errors(ScriptPath, session.LowerCutscene(ScriptPath)!.ToGlobals(), ThnScripts.Evaluator,
            new SessionThnData(session, ScriptPath));
        Assert.Equal(["entity \"Old\", template_name: The game data has no ship \"old_ship\"."], lower.Select(e => e.Message));

        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));
        Assert.Empty(Errors(session));
        AssertSaved(session.Save());
    }

    /// <summary>An unknown costume of a DEFORMABLE without an Actor crashes the game. The save stops with the reason,
    /// and an undo heals the problem.</summary>
    [Fact]
    public void AnUnknownCostumeBlocksTheSaveAndAnUndoHealsIt()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        ThnDataWorkspace.SetTemplate(session, "Man", "ghost");
        var refused = session.Save();

        Assert.Equal([CostumeMissing], Errors(session));
        Assert.False(refused.Saved);
        var error = Assert.Single(refused.Problems);
        Assert.Equal((CostumeMissing, ScriptPath), (error.Message, error.Source));
        Assert.Equal((ThnElementKind.Entity, "template_name"), (error.Cutscene!.Kind, error.Cutscene.Value));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);

        session.Undo();
        Assert.Empty(Errors(session));
        AssertSaved(session.Save());
    }

    /// <summary>An unsaved edit of costumes.ini that adds the costume heals the problem without a save. A costume of
    /// the active mod whose body names no body part is not usable: the engine drops it (GameItemDb.cs:974-978). So it
    /// blocks the save too, and the message tells why, because the lists have the costume.</summary>
    [Fact]
    public void AnUnsavedCostumeEditCountsAndACostumeWithoutABodyBlocks()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Man", "ghost");
        Assert.Equal([CostumeMissing], Errors(session));

        session.Execute(EditCommand.AddSection(ThnDataWorkspace.CostumesPath, "Costume",
            [ThnDataWorkspace.Line("nickname", "ghost"), ThnDataWorkspace.Line("body", "body1")]));
        Assert.Empty(Errors(session));

        session.Execute(EditCommand.AddSection(ThnDataWorkspace.CostumesPath, "Costume",
            [ThnDataWorkspace.Line("nickname", "broken"), ThnDataWorkspace.Line("body", "no_body")]));
        ThnDataWorkspace.SetTemplate(session, "Man", "broken");
        Assert.Equal([CostumeWithoutBody("broken")], Errors(session));
        Assert.Contains(session.SaveProblems(), p => p.Message == CostumeWithoutBody("broken"));
    }

    /// <summary>
    /// An INI edit of the active mod that removes or breaks the item of a template blocks the save of a cutscene that
    /// the active mod changes, also when the template does not change. The lower data has the item, so B does not have
    /// the problem. The edits are a delete and a rename of the costume, a body that names no body part, and an
    /// equipment without its <c>DA_archetype</c> line. An undo heals each problem.
    /// </summary>
    [Fact]
    public void AnItemThatTheActiveModRemovesOrBreaksBlocksTheSave()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));
        var trentMissing = CostumeMissing.Replace("ghost", "trent");
        Assert.Empty(Errors(session));

        session.Execute(EditCommand.DeleteSection(ThnDataWorkspace.Costume("trent")));
        Assert.Equal([trentMissing], Errors(session));
        Assert.Contains(session.SaveProblems(), p => p.Message == trentMissing);
        session.Undo();
        Assert.Empty(Errors(session));

        session.Execute(EditCommand.Rename(ThnDataWorkspace.Costume("trent"), "trent_new"));
        Assert.Equal([trentMissing], Errors(session));
        session.Undo();

        session.Execute(EditCommand.SetLinesFromText(ThnDataWorkspace.Costume("trent"), "body", ["no_body"]));
        Assert.Equal([CostumeWithoutBody("trent")], Errors(session));
        session.Undo();

        session.Execute(EditCommand.RemoveName(ThnDataWorkspace.Equipment("Thruster", "thruster1"), "DA_archetype"));
        Assert.Equal([Crash + "entity \"Gear\", template_name: The equipment \"thruster1\" has no model: its [Thruster] " +
                      "section has no DA_archetype line."], Errors(session));
        session.Undo();
        Assert.Empty(Errors(session));
        AssertSaved(session.Save());
    }

    /// <summary>
    /// The engine gives a model only to the equipment of some section types (GameItemDb.cs:1008-1303). It makes a
    /// <c>[Light]</c> or a <c>[Commodity]</c> without a model, also with a <c>DA_archetype</c> line and its file, so a
    /// COMPOUND with such an item crashes the game. It makes no item of an <c>[Armor]</c>, so the scene shows that
    /// entity without a model. The lists have these names, so each message tells why.
    /// </summary>
    [Fact]
    public void AnEquipmentOfASectionTypeWithoutAModelCrashesTheGame()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        ThnDataWorkspace.SetTemplate(session, "Gear", "light1");
        Assert.Equal([Crash + "entity \"Gear\", template_name: The equipment \"light1\" has no model: the game makes the " +
                      "[Light] items without a model."], Errors(session));
        Assert.False(session.Save().Saved);
        ThnDataWorkspace.SetTemplate(session, "Gear", "commodity1");
        Assert.Equal([Crash + "entity \"Gear\", template_name: The equipment \"commodity1\" has no model: the game makes the " +
                      "[Commodity] items without a model."], Errors(session));
        ThnDataWorkspace.SetTemplate(session, "Gear", "armor1");
        Assert.Equal(["entity \"Gear\", template_name: The equipment \"armor1\" is not usable: the game makes no equipment " +
                      "from [Armor] sections. The scene shows the entity without a model."], Errors(session));

        ThnDataWorkspace.SetTemplate(session, "Gear", "thruster1");
        Assert.Empty(Errors(session));
    }

    /// <summary>
    /// An unknown ship of a COMPOUND crashes the game, also with the category Spaceship. A solar archetype without a
    /// <c>DA_archetype</c> line, or with a model file that does not exist, crashes it too. The same COMPOUND in a script
    /// that the game plays only in space gives no problem, because the space scene makes no COMPOUND.
    /// </summary>
    [Fact]
    public void AnUnknownShipOrAMissingModelCrashesTheGameButNotInAScriptOfSpace()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        ThnDataWorkspace.SetTemplate(session, "Ship", "ghost_ship");
        ThnDataWorkspace.SetTemplate(session, "Station", "no_model");
        ThnDataWorkspace.SetTemplate(session, "Gear", "THRUSTER1");
        Assert.Equal([
            "The game crashes when it plays this cutscene. entity \"Ship\", template_name: The game data has no ship \"ghost_ship\".",
            "The game crashes when it plays this cutscene. entity \"Station\", template_name: The solar archetype \"no_model\" has " +
            "no model: its [Solar] section has no DA_archetype line.",
        ], Errors(session));
        ThnDataWorkspace.SetTemplate(session, "Station", "lost_model");
        Assert.Contains("The game crashes when it plays this cutscene. entity \"Station\", template_name: The solar archetype " +
                        "\"lost_model\" has no model: the model file 'solar\\lost.cmp' does not exist.", Errors(session));

        ThnDataWorkspace.SetTemplate(session, "Ship", "ghost_ship", ThnDataWorkspace.SpaceScript);
        Assert.Equal("ghost_ship", ((ThnText)session.Cutscene(ThnDataWorkspace.SpaceScript)!.Entities[0].Table["template_name"]!).Value);
        Assert.Empty(Errors(session, ThnDataWorkspace.SpaceScript));
    }

    /// <summary>
    /// The space rule reads the INI lines of W. An unsaved <c>Act_CallThorn</c> line that makes the script a script of
    /// space removes the COMPOUND error at once, also from the cached result. So the trigger and the COMPOUND save in one
    /// save, as in two saves.
    /// </summary>
    [Fact]
    public void AnUnsavedTriggerOfSpaceRemovesTheCompoundErrorAndBothEditsSaveInOneSave()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Ship", "ghost_ship");
        Assert.Equal([GhostShip], Errors(session));

        ThnDataWorkspace.CallThorn(session, @"Scripts\x.thn");

        Assert.Empty(Errors(session));
        AssertSaved(session.Save());
        Assert.Empty(Errors(session));
    }

    /// <summary>
    /// An unsaved removal of the only <c>Act_CallThorn</c> line of a script of space makes the game build its
    /// COMPOUNDs. So the cached result of the script changes at once, and the save stops with the error. An undo heals
    /// it.
    /// </summary>
    [Fact]
    public void AnUnsavedRemovalOfTheTriggerOfSpaceChecksTheCachedCompoundAgain()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Ship", "ghost_ship", ThnDataWorkspace.SpaceScript);
        Assert.Empty(Errors(session, ThnDataWorkspace.SpaceScript));

        session.Execute(MissionTestData.Raw(EditCommand.RemoveName(ThnDataWorkspace.Trigger, "Act_CallThorn")));

        Assert.Equal([GhostShip], Errors(session, ThnDataWorkspace.SpaceScript));
        var refused = session.Save();
        Assert.False(refused.Saved);
        Assert.Contains(refused.Problems, p => p.Message == GhostShip);

        session.Undo();
        Assert.Empty(Errors(session, ThnDataWorkspace.SpaceScript));
        AssertSaved(session.Save());
    }

    /// <summary>
    /// A saved change of the trigger of space checks the cached result again: the save of the new line makes the game
    /// build the COMPOUNDs of the old script of space. The save stops with the error, and the cutscene keeps it.
    /// </summary>
    [Fact]
    public void ASaveOfANewTriggerLineChecksTheOldScriptOfSpaceAgain()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Ship", "ghost_ship", ThnDataWorkspace.SpaceScript);
        AssertSaved(session.Save());
        Assert.Empty(Errors(session, ThnDataWorkspace.SpaceScript));

        ThnDataWorkspace.CallThorn(session, @"Scripts\x.thn");
        var refused = session.Save();

        Assert.False(refused.Saved);
        Assert.Contains(refused.Problems, p => p.Message == GhostShip);
        Assert.Equal([GhostShip], Errors(session, ThnDataWorkspace.SpaceScript));
    }

    /// <summary>
    /// A missing equipment, prop or effect does not crash the game, so its problem has no crash start. It still blocks
    /// the save, because the value of the active mod is outside a closed list. The mines, the visual effects and the
    /// props of each petalDB file are known. A template that the room fills in, PlayerShipEngines and a SOUND give no
    /// problem.
    /// </summary>
    [Fact]
    public void AMissingEquipmentPropOrEffectIsANameProblemThatBlocksTheSave()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        ThnDataWorkspace.SetTemplate(session, "Gear", "ghost_gear");
        ThnDataWorkspace.SetTemplate(session, "Cup", "cup");
        ThnDataWorkspace.SetTemplate(session, "Fx", "ghost_fx");
        ThnDataWorkspace.SetTemplate(session, "Vis", "PlayerShipEngines");
        ThnDataWorkspace.SetTemplate(session, "Station", "$terrain_dyna_02");
        ThnDataWorkspace.SetTemplate(session, "Voice", "unknown_voice_line");
        ThnDataWorkspace.SetTemplate(session, "Bar", "BAR1");
        string[] expected =
        [
            "entity \"Gear\", template_name: The game data has no equipment \"ghost_gear\". The scene shows the entity " +
            "without a model.",
            "entity \"Fx\", template_name: The game data has no effect \"ghost_fx\". The scene does not show this entity.",
        ];

        Assert.Equal(expected, Errors(session));
        Assert.All(session.CutsceneErrors(ScriptPath), e => Assert.Equal(DiagnosticSeverity.Error, e.Severity));
        var refused = session.Save();
        Assert.False(refused.Saved);
        Assert.Equal(expected, refused.Problems.Select(p => p.Message));

        ThnDataWorkspace.SetTemplate(session, "Gear", "thruster1");
        ThnDataWorkspace.SetTemplate(session, "Fx", "visfx1");
        Assert.Empty(Errors(session));
    }

    /// <summary>
    /// The engine finds the item of a template by the text as it is, without a trim, as <c>oltool validate</c> does. So
    /// a known costume with a space after it crashes the game, and a known effect with a space before it is a name
    /// problem. Both block the save.
    /// </summary>
    [Fact]
    public void AKnownNameWithASpaceAroundItBlocksTheSave()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        ThnDataWorkspace.SetTemplate(session, "Man", "trent ");
        ThnDataWorkspace.SetTemplate(session, "Fx", " fx1");

        Assert.Equal([CostumeMissing.Replace("\"ghost\"", "\"trent \""),
            "entity \"Fx\", template_name: The game data has no effect \" fx1\". The scene does not show this entity."],
            Errors(session));
        Assert.False(session.Save().Saved);
    }

    /// <summary>
    /// A PSYS template that starts with <c>$</c> names an effect as it is: the room fills in only the templates of the
    /// other types. So an unknown such effect blocks the save as a name problem.
    /// </summary>
    [Fact]
    public void AnEffectThatStartsWithADollarSignIsCheckedAsItIs()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");

        ThnDataWorkspace.SetTemplate(session, "Fx", "$ghost_fx");

        string[] expected =
            ["entity \"Fx\", template_name: The game data has no effect \"$ghost_fx\". The scene does not show this entity."];
        Assert.Equal(expected, Errors(session));
        var refused = session.Save();
        Assert.False(refused.Saved);
        Assert.Equal(expected, refused.Problems.Select(p => p.Message));
    }

    /// <summary>
    /// The retail data has a name problem, and a message names its entity. So the name problems compare by their
    /// elements: a rename of an entity whose effect the lists do not have adds no problem. A new missing name of the
    /// renamed entity is a new problem, and so is the same missing name on a new entity.
    /// </summary>
    [Fact]
    public void ARenameOfAnEntityWithALowerNameProblemAddsNoProblem()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        Assert.Equal(["entity \"Halo\", template_name: The game data has no effect \"lost_halo\". The scene does not show this entity."],
            ThnChecks.NameProblems(ScriptPath, session.LowerCutscene(ScriptPath)!.ToGlobals(), ThnScripts.Evaluator,
                new SessionThnData(session, ScriptPath)).Select(p => p.Message));

        session.Execute(ThnEdits.RenameEntity(Entity(session, "Halo"), "Halo_2"));
        Assert.Empty(Errors(session));
        AssertSaved(session.Save());

        ThnDataWorkspace.SetTemplate(session, "Halo_2", "lost_glow");
        Assert.Equal(["entity \"Halo_2\", template_name: The game data has no effect \"lost_glow\". The scene does not show this entity."],
            Errors(session));
        ThnDataWorkspace.SetTemplate(session, "Halo_2", "lost_halo");
        ThnDataWorkspace.SetTemplate(session, "Fx", "lost_halo");
        Assert.Equal(["entity \"Fx\", template_name: The game data has no effect \"lost_halo\". The scene does not show this entity."],
            Errors(session));
    }

    /// <summary>The cached result stays the same object while nothing that the checks read changes, also after an INI
    /// edit that changes no answer. An INI edit that changes an answer checks the cutscene again.</summary>
    [Fact]
    public void TheCacheFollowsTheAnswersOfTheGameData()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Man", "ghost");

        var first = session.CutsceneErrors(ScriptPath);
        Assert.Same(first, session.CutsceneErrors(ScriptPath));
        session.Execute(EditCommand.AddSection(ThnDataWorkspace.CostumesPath, "Costume",
            [ThnDataWorkspace.Line("nickname", "other"), ThnDataWorkspace.Line("body", "body1")]));
        Assert.Same(first, session.CutsceneErrors(ScriptPath));

        session.Execute(EditCommand.AddSection(ThnDataWorkspace.CostumesPath, "Costume",
            [ThnDataWorkspace.Line("nickname", "ghost"), ThnDataWorkspace.Line("body", "body1")]));
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        session.Undo();
        Assert.Equal([CostumeMissing], Errors(session));
    }

    /// <summary>
    /// The agent tools read the same check: <c>set_fields</c> applies a costume that the lists do not have and gives
    /// the problem in <c>new_problems</c>, <c>check</c> gives it, and <c>save</c> refuses with the text of the save gate
    /// of the editor. The fix gives the problem in <c>fixed_problems</c>.
    /// </summary>
    [Fact]
    public void TheAgentToolsGiveTheSameProblemAndSaveRefusesWithTheGateText()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var man = AgentCalls.Json($"thn:{ScriptPath}#entity/Man");

        var result = AgentCalls.Ok(host, "set_fields", $$"""{ "ref": {{man}}, "fields": { "template_name": "ghost" } }""");
        var check = AgentCalls.Ok(host, "check", """{ "scope": "all" }""");
        var gate = session.GateRefusal()!.Problems.Single(p => p.Severity == DiagnosticSeverity.Error);
        var (code, _, message) = AgentCalls.Error(host, "save");

        Assert.Equal("applied", (string?)result["status"]);
        var problem = Assert.Single(result["new_problems"]!.AsArray())!;
        Assert.Equal((CostumeMissing, $"thn:{ScriptPath}#entity/Man"), ((string?)problem["message"], (string?)problem["ref"]));
        Assert.Equal([CostumeMissing], check["problems"]!.AsArray().Select(p => (string?)p!["message"]));
        Assert.Equal(CostumeMissing, gate.Message);
        Assert.Equal("save_blocked", code);
        Assert.Equal($"The save stopped. 1 error blocks it. Correct it, then save again. {CostumeMissing} The error is in " +
                     $"thn:{ScriptPath}#entity/Man.", message);

        var fix = AgentCalls.Ok(host, "set_fields", $$"""{ "ref": {{man}}, "fields": { "template_name": "trent" } }""");
        Assert.Equal([CostumeMissing], fix["fixed_problems"]!.AsArray().Select(p => (string?)p!["message"]));
        Assert.Empty(fix["new_problems"]!.AsArray());
    }

    /// <summary>
    /// An agent edit of an INI section that breaks the template of a cutscene of the active mod gives the cutscene
    /// problem in <c>new_problems</c>, as the save gate gives it. The batch does not change the cutscene. The edit that
    /// puts back the line gives the problem in <c>fixed_problems</c>.
    /// </summary>
    [Fact]
    public void AnAgentIniEditThatBreaksATemplateGivesTheCutsceneProblem()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));
        var host = new TestAgentHost(session);
        var gear = AgentCalls.Json($"sec:{ThnDataWorkspace.EquipmentPath}#Thruster/thruster1");
        const string noModel = Crash + "entity \"Gear\", template_name: The equipment \"thruster1\" has no model: its " +
                               "[Thruster] section has no DA_archetype line.";

        var broken = AgentCalls.Ok(host, "set_fields", $$"""{ "ref": {{gear}}, "fields": { "DA_archetype": null } }""");
        var mended = AgentCalls.Ok(host, "set_fields",
            $$"""{ "ref": {{gear}}, "fields": { "DA_archetype": "equipment\\thruster1.3db" } }""");

        Assert.Equal("applied", (string?)broken["status"]);
        var problem = Assert.Single(broken["new_problems"]!.AsArray(), p => (string?)p!["message"] == noModel)!;
        Assert.Equal($"thn:{ScriptPath}#entity/Gear", (string?)problem["ref"]);
        Assert.Contains(noModel, mended["fixed_problems"]!.AsArray().Select(p => (string?)p!["message"]));
        Assert.DoesNotContain(noModel, mended["new_problems"]!.AsArray().Select(p => (string?)p!["message"]));
        Assert.Empty(Errors(session));
    }

    /// <summary>The Problems section of the Cutscene page shows an error with the label "Crash" and without its crash
    /// start. It shows a name problem whole, with the label "Name problem". So the row tells which problems crash the
    /// game.</summary>
    [Fact]
    public void TheProblemsSectionShowsANameProblemWhole()
    {
        const string name = "entity \"Gear\", template_name: The game data has no equipment \"x\". The scene shows the " +
                            "entity without a model.";

        Assert.Equal(name, ThnPage.ProblemText(Diagnostic.Error(ScriptPath, name)));
        Assert.Equal("Name problem", ThnPage.ProblemLabel(Diagnostic.Error(ScriptPath, name)));
        Assert.Equal("entity \"Man\", template_name: The costume \"ghost\" is not in the game data. A DEFORMABLE without " +
                     "userprops.Actor needs it.", ThnPage.ProblemText(Diagnostic.Error(ScriptPath, CostumeMissing)));
        Assert.Equal("Crash", ThnPage.ProblemLabel(Diagnostic.Error(ScriptPath, CostumeMissing)));
    }
}
