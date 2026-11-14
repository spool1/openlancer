using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The <c>template_name</c> row of a cutscene entity without ImGui (#19, section 12.1 of the M8b design): the data lists
/// of each entity type and category, the label, the closed and the open lists, the rows that keep the text field, and
/// the look of a value with the text of the save gate. The names are made up.
/// </summary>
public sealed class ThnTemplateRowTests
{
    private const string Crash = EditSession.CutsceneCrash + " ";

    /// <summary>An entity of each type and category, and the templates that keep the text field.</summary>
    private const string Script = """
        duration = 10
        entities = {
            { entity_name = "Man", type = DEFORMABLE, template_name = "trent" },
            { entity_name = "Actor", type = DEFORMABLE, template_name = "trent", userprops = { actor = "Trent" } },
            { entity_name = "EmptyActor", type = DEFORMABLE, template_name = "trent", userprops = { Actor = "" } },
            { entity_name = "Filled", type = DEFORMABLE, template_name = "$char_01" },
            { entity_name = "Station", type = COMPOUND, template_name = "station1", userprops = { category = "SOLAR" } },
            { entity_name = "Ship", type = COMPOUND, template_name = "ship1", userprops = { category = "ship" } },
            { entity_name = "Fighter", type = COMPOUND, template_name = "ship1", userprops = { category = "Spaceship" } },
            { entity_name = "Cup", type = COMPOUND, template_name = "cup", userprops = { category = "prop" } },
            { entity_name = "Bar", type = COMPOUND, template_name = "bar1", userprops = { category = "room" } },
            { entity_name = "Cart", type = COMPOUND, template_name = "cart1", userprops = { category = "equipment cart" } },
            { entity_name = "Gear", type = COMPOUND, template_name = "thruster1", userprops = { category = "equipment" } },
            { entity_name = "Rock", type = COMPOUND, template_name = "rock1", userprops = { category = "asteroid" } },
            { entity_name = "Terrain", type = COMPOUND, template_name = "$terrain_dyna_01", userprops = { category = "solar" } },
            { entity_name = "Player", type = COMPOUND, template_name = "playership", userprops = { category = "ship" } },
            { entity_name = "NoCategory", type = COMPOUND, template_name = "ship1" },
            { entity_name = "Odd", type = COMPOUND, template_name = "ship1", userprops = { category = "boat" } },
            { entity_name = "Fx", type = PSYS, template_name = "fx1" },
            { entity_name = "Filled_fx", type = PSYS, template_name = "$fx" },
            { entity_name = "Engines", type = PSYS, template_name = "PlayerShipEngines" },
            { entity_name = "Voice", type = SOUND, template_name = "voice_line_01" },
            { entity_name = "Cam", type = CAMERA, cameraprops = { fovh = 10 } }
        }
        events = {}
        """;

    /// <summary>
    /// The table of <see cref="ThnTemplateRules"/> gives the lists and the label of each row: the costume of a
    /// DEFORMABLE, the list of the category of a COMPOUND (the names compare without letter case), the effects and the
    /// visual effects of a PSYS, and the sounds of a SOUND. The costume list is open for a DEFORMABLE with an Actor,
    /// and the sound list is open. An empty Actor loads the costume, as in the engine. A CAMERA gets no template row.
    /// </summary>
    [Fact]
    public void EachEntityTypeAndCategoryGetsTheListsOfItsTemplate()
    {
        var rows = Rows();

        (string Lists, string? Label, bool Closed) Of(string name) =>
            (string.Join(" ", rows[name]!.Lists), rows[name]!.Label, rows[name]!.Closed);

        Assert.Equal(("costume", "Costume", true), Of("Man"));
        Assert.Equal(("costume", "Costume", false), Of("Actor"));
        Assert.Equal(("costume", "Costume", true), Of("EmptyActor"));
        Assert.Equal(("solar_archetype", "Solar archetype", true), Of("Station"));
        Assert.Equal(("ship", "Ship", true), Of("Ship"));
        Assert.Equal(("ship", "Ship", true), Of("Fighter"));
        Assert.Equal(("petal_prop", "Prop", true), Of("Cup"));
        Assert.Equal(("petal_room", "Room", true), Of("Bar"));
        Assert.Equal(("petal_cart", "Cart", true), Of("Cart"));
        Assert.Equal(("equipment", "Equipment", true), Of("Gear"));
        Assert.Equal(("asteroid asteroid_mine", "Asteroid", true), Of("Rock"));
        Assert.Equal(("effect vis_effect", "Effect", true), Of("Fx"));
        Assert.Equal(("sound", "Sound", false), Of("Voice"));
        Assert.Null(rows["Cam"]);
    }

    /// <summary>
    /// These templates name no item of a list, so their rows keep the text field with the reason: PlayerShip on a
    /// COMPOUND, PlayerShipEngines on a PSYS, a template that the room fills in, and a COMPOUND without a category
    /// that the engine knows. The engine reads the template of a PSYS as it is, so a PSYS template with a <c>$</c> gets
    /// the list.
    /// </summary>
    [Fact]
    public void SomeTemplatesKeepTheTextFieldWithTheReason()
    {
        var rows = Rows();

        Assert.StartsWith("PlayerShip names no item.", rows["Player"]!.TextReason);
        Assert.StartsWith("PlayerShipEngines names no effect.", rows["Engines"]!.TextReason);
        Assert.StartsWith("The room fills in a template that starts with $", rows["Terrain"]!.TextReason);
        Assert.StartsWith("The room fills in a template that starts with $", rows["Filled"]!.TextReason);
        Assert.StartsWith("The COMPOUND has no userprops.category that the engine knows.", rows["NoCategory"]!.TextReason);
        Assert.Equal(rows["NoCategory"]!.TextReason, rows["Odd"]!.TextReason);
        Assert.All(new[] { "Player", "Engines", "Terrain", "Filled", "NoCategory", "Odd" }, name => Assert.Empty(rows[name]!.Lists));
        Assert.Null(rows["Player"]!.Label);
        Assert.Equal("Solar archetype", rows["Terrain"]!.Label);
        Assert.Null(rows["Filled_fx"]!.TextReason);
        Assert.Equal(["effect", "vis_effect"], rows["Filled_fx"]!.Lists);
    }

    /// <summary>
    /// A value of the active mod outside a closed list shows "Missing (Label): value" with the color and the red frame
    /// of an error, and the text below the field is the text of the save gate without the element and the path. The
    /// crash start stays on a crash. A name problem has no crash start. A known value shows in the text color without
    /// a text below it.
    /// </summary>
    [Fact]
    public void AValueThatTheSaveGateBlocksShowsTheGateTextAsAnError()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Man", "ghost");
        ThnDataWorkspace.SetTemplate(session, "Gear", "ghost_gear");

        var (look, gate) = LookOf(session, "Man");
        var (gear, gearGate) = LookOf(session, "Gear");
        var (ship, shipGate) = LookOf(session, "Ship");

        Assert.Equal(("Missing (Costume): ghost", EditorTheme.Error, true, EditorTheme.Error),
            (look.Text, look.Color, look.Error, look.BelowColor));
        Assert.Equal(Crash + "The costume \"ghost\" is not in the game data. A DEFORMABLE without userprops.Actor needs it.",
            look.Below);
        Assert.Equal(Crash + "entity \"Man\", template_name: " + look.Below![Crash.Length..], gate!.Message);
        Assert.Equal(("Missing (Equipment): ghost_gear", EditorTheme.Error, true), (gear.Text, gear.Color, gear.Error));
        Assert.Equal("entity \"Gear\", template_name: " + gear.Below, gearGate!.Message);
        Assert.False(gear.Below!.StartsWith(Crash, System.StringComparison.Ordinal));
        Assert.Equal(("ship1", "", EditorTheme.Text, false, (string?)null), (ship.Text, ship.Dim, ship.Color, ship.Error, ship.Below));
        Assert.Null(shipGate);
    }

    /// <summary>
    /// A value that the lower cutscene has with the same problem stays out of the save gate, so it has the warning
    /// color without the red frame, with the text of the check. A missing name that the save gate does not check, here
    /// a COMPOUND in a script that the game plays only in space, has the warning color and tells why. A known name with
    /// a problem, here an equipment without a model, has the color of its problem.
    /// </summary>
    [Fact]
    public void ARetailValueAndAnUncheckedNameHaveTheWarningColor()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Ship", "ghost_ship", ThnDataWorkspace.SpaceScript);
        ThnDataWorkspace.SetTemplate(session, "Gear", "light1");

        var (old, oldGate) = LookOf(session, "Old");
        var (space, _) = LookOf(session, "Ship", ThnDataWorkspace.SpaceScript);
        var (light, _) = LookOf(session, "Gear", name: "light1");

        Assert.Null(oldGate);
        Assert.Equal(("Missing (Ship): old_ship", EditorTheme.Warning, false, EditorTheme.Warning),
            (old.Text, old.Color, old.Error, old.BelowColor));
        Assert.Equal(Crash + "The game data has no ship \"old_ship\".", old.Below);
        Assert.Equal(("Missing (Ship): ghost_ship", EditorTheme.Warning, false, ThnTemplateRow.NotChecked),
            (space.Text, space.Color, space.Error, space.Below));
        Assert.Equal(("light1", EditorTheme.Error, true), (light.Text, light.Color, light.Error));
        Assert.Equal(Crash + "The equipment \"light1\" has no model: the game makes the [Light] items without a model.",
            light.Below);
    }

    /// <summary>
    /// A value outside an open list shows "Not listed (Label): value" with a note and does not block the save: a SOUND,
    /// which can name a voice line, and a DEFORMABLE with an Actor. A DEFORMABLE without a template shows "None
    /// (Costume)" with the missing-key text of the save gate, so a new character can pick a costume.
    /// </summary>
    [Fact]
    public void AnOpenListGivesANoteAndAMissingCostumeGivesTheKeyText()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Voice", "unknown_voice_line");
        session.Execute(ThnEdits.Remove(Entity(session, "Man"), new ThnPath(ThnTemplateRow.Path)));

        var (voice, voiceGate) = LookOf(session, "Voice");
        var (man, manGate) = LookOf(session, "Man");

        Assert.Null(voiceGate);
        Assert.Equal(("Not listed (Sound): unknown_voice_line", EditorTheme.Warning, false, EditorTheme.TextDim),
            (voice.Text, voice.Color, voice.Error, voice.BelowColor));
        Assert.StartsWith("No sound of the game data has this name.", voice.Below);
        Assert.Equal(("None (Costume)", EditorTheme.Error, true), (man.Text, man.Color, man.Error));
        Assert.Equal(Crash + "The key is missing. A DEFORMABLE without userprops.Actor needs a costume.", man.Below);
        Assert.NotNull(manGate);
        var actor = new ThnTemplateRow(ThnTemplateRules.Costume, "Costume", false, "", "");
        Assert.Equal(("Not listed (Costume): ghost", EditorTheme.Warning, false),
            (actor.Look("ghost", null, null, null, false).Text, actor.Look("ghost", null, null, null, false).Color,
                actor.Look("ghost", null, null, null, false).Error));
        Assert.StartsWith("No costume of the game data has this name.", actor.OpenNote);
    }

    /// <summary>
    /// A pick of the list is one field edit of the template: the list routes each click and each arrow key to one
    /// <see cref="ThnFieldGesture"/>, which <c>PropertyGrid.ThnListPopup</c> opens. So the picks of one opening give
    /// one command and one entry of the edit history, and one undo puts back the template of the opening.
    /// </summary>
    [Fact]
    public void ThePicksOfOneOpeningAreOneEntryOfTheEditHistory()
    {
        using var ws = ThnDataWorkspace.Create();
        var session = ws.Session("act");
        var man = Entity(session, "Man");
        var path = new ThnPath(ThnTemplateRow.Path);
        session.Execute(EditCommand.AddSection(ThnDataWorkspace.CostumesPath, "Costume",
            [ThnDataWorkspace.Line("nickname", "newbie"), ThnDataWorkspace.Line("body", "body1")]));
        var undo = session.UndoLabel;

        var pick = ThnFieldGesture.Open(FakeCommandHost.Runner(session), new FieldKey(man, path.Text, FieldKey.AllLines),
            "trent", text => ThnFieldParse.Of(new ThnText(text)), ThnFieldCommand.Set(man, path));
        pick.Change("ghost");
        pick.Change("newbie");
        Assert.Null(pick.End(keep: true, interactive: true));

        Assert.Equal(new ThnText("newbie"), Value(session, man, path.Text));
        Assert.Equal("Set template_name", session.UndoLabel);
        Assert.Empty(session.CutsceneErrors(ScriptPath));
        session.Undo();
        Assert.Equal(new ThnText("trent"), Value(session, man, path.Text));
        Assert.Equal(undo, session.UndoLabel);
    }

    /// <summary>The rows of the entities of <see cref="Script"/>, by name.</summary>
    private static Dictionary<string, ThnTemplateRow?> Rows()
    {
        var script = ThnScriptModel.From(ThnScripts.Evaluate(Script), ThnElementId.Counter());
        return script.Entities.ToDictionary(e => ThnScriptModel.NameOf(e.Table)!,
            e => ThnTemplateRow.Of(e.Table, ThnScripts.Evaluator.Symbols));
    }

    /// <summary>The look of the template row of an entity from <see cref="ThnTemplateRow.LookIn"/>, which the Inspector
    /// calls, and the problem of the save gate at the template, or null.</summary>
    /// <param name="name">The display name of the entry, or null for the nickname.</param>
    private static (ThnTemplateLook Look, OpenLancer.Data.Diagnostic? Gate) LookOf(EditSession session, string entity,
        string path = ScriptPath, string? name = null)
    {
        var script = session.Cutscene(path)!;
        var element = script.Entities.Single(e => ThnScriptModel.NameOf(e.Table) == entity);
        var target = new ThnRef(session.CutscenePath(path)!, ThnElementKind.Entity, element.Id);
        var row = ThnTemplateRow.Of(element.Table, ThnScripts.Evaluator.Symbols)!;
        var current = element.Table[ThnTemplateRow.Path] is ThnText text ? text.Value : "";
        var list = session.Lists.List(row.Lists)!;
        var look = row.LookIn(session, path, script, target, list, current, entry => name ?? entry.Value);
        Assert.Same(current.Length == 0 ? null : list.Find(current), look.Entry);
        return (look, ThnFieldProblems.At(session.CutsceneErrors(path), script, target, ThnTemplateRow.Path));
    }
}
