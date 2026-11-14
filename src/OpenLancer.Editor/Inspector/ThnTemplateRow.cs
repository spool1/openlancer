using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>The problems of the checks of a cutscene at one field of the Inspector, without ImGui.</summary>
internal static class ThnFieldProblems
{
    /// <summary>The first problem at a path of an element, or null.</summary>
    /// <param name="problems">Problems of the checks of the cutscene, for example of
    /// <see cref="EditSession.CutsceneErrors"/>.</param>
    /// <param name="script">The version of W that gave the problems.</param>
    public static Diagnostic? At(IReadOnlyList<Diagnostic> problems, ThnScriptModel script, ThnRef target, string path) =>
        problems.FirstOrDefault(p => p.Cutscene is { } place && place.Kind == target.Kind && place.Value == path &&
                                     place.ElementIn(script)?.Id == target.Id);

    /// <summary>
    /// The text of a problem below its field: the message of the check without the element and the path, because the
    /// row shows them. An error keeps the start that says that the game crashes. So the text is the text of the save
    /// gate after "entity "name", path: ".
    /// </summary>
    public static string Text(Diagnostic problem, string path)
    {
        var start = path + ": ";
        if (problem.Subject is not { } subject || !subject.StartsWith(start, StringComparison.Ordinal))
        {
            return problem.Message;
        }

        var text = subject[start.Length..];
        return problem.Message.StartsWith(EditSession.CutsceneCrash, StringComparison.Ordinal)
            ? $"{EditSession.CutsceneCrash} {text}"
            : text;
    }
}

/// <summary>The closed field of a template row and the text below it.</summary>
/// <param name="Text">The text of the field.</param>
/// <param name="Dim">The dim text at the right of the field, for example the nickname of an entry with a name.</param>
/// <param name="Error">True for the red frame of a value that the save gate refuses.</param>
/// <param name="Below">The text below the field, or null.</param>
/// <param name="Entry">The entry of the list with the value, or null.</param>
internal sealed record ThnTemplateLook(string Text, string Dim, Vector4 Color, bool Error, string? Below,
    Vector4 BelowColor, ListEntry? Entry);

/// <summary>
/// The <c>template_name</c> row of an entity of a cutscene (section 12.1 of the M8b design), without ImGui. A DEFORMABLE
/// names a costume, a COMPOUND an item of its category, a PSYS an effect and a SOUND a sound. The table of
/// <see cref="ThnTemplateRules"/> gives the data lists of each kind, so the row and the save gate read one table. The
/// row gets the list picker. A template that names no item of a list keeps the text field, and the row tells why.
/// </summary>
/// <param name="Rule">The rule of the template, or null for a row without a list.</param>
/// <param name="Label">The label of the row, for example "Costume", or null to keep the label of the key.</param>
/// <param name="Closed">False when a value outside the list is valid: a SOUND, and a DEFORMABLE with
/// <c>userprops.Actor</c>. Such a value gets a note and does not block the save.</param>
/// <param name="Evidence">The engine lines that use the template.</param>
/// <param name="Note">The note of the row.</param>
internal sealed record ThnTemplateRow(ThnTemplateRule? Rule, string? Label, bool Closed, string Evidence, string Note)
{
    /// <summary>The path of the template of an entity.</summary>
    public const string Path = "template_name";

    /// <summary>The text below the field of a missing name that the save gate does not check.</summary>
    public const string NotChecked =
        "The game does not build this entity from its template here, so the save gate does not check the name.";

    /// <summary>The note of a row whose list the editor cannot make.</summary>
    public const string NoList = "The editor cannot make the list of this kind from the game data, so the row keeps " +
                                 "its text field.";

    private const string CostumeEvidence = "ThnScriptInstance.cs:255-264";
    private const string CompoundEvidence = "ThnScriptInstance.cs:113-160";
    private const string PsysEvidence = "ThnScriptInstance.cs:192-205";
    private const string SoundEvidence = "ThnScriptInstance.cs:282-288";

    private const string Unsaved = "The list has the items of the edit view, also the unsaved edits.";

    /// <summary>Why the row keeps its text field, or null when it gets the list picker.</summary>
    public string? TextReason { get; init; }

    /// <summary>The data lists of the template, in order, or none for a row without a list.</summary>
    public IReadOnlyList<string> Lists => TextReason == null ? Rule?.Lists ?? [] : [];

    /// <summary>
    /// The row of the template of an entity, or null when the type of the entity names no item, for example a CAMERA.
    /// These templates keep the text field with a reason: PlayerShip on a COMPOUND, PlayerShipEngines on a PSYS, a
    /// template that the room fills in, and a template of a COMPOUND without a category that the engine knows.
    /// </summary>
    public static ThnTemplateRow? Of(ThnTable entity, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var template = entity[Path] is ThnText { Value: var text } ? text : "";
        switch (ThnSchema.TypeOf(ThnElementKind.Entity, entity, symbols))
        {
            case ThnTemplateRules.CharacterType:
                return ThnTemplateRules.LoadsCostume(entity)
                    ? Listed(ThnTemplateRules.Costume, true, CostumeEvidence,
                        $"The costume of the character: its body, its head and its hands. {Unsaved}", template)
                    : Listed(ThnTemplateRules.Costume, false, CostumeEvidence,
                        "The costume of the character. The entity has userprops.Actor, so the scene loads this costume " +
                        "only when no room NPC or RTC character plays the actor.", template);
            case "COMPOUND":
                if (Is(template, ThnTemplateRules.PlayerShip))
                {
                    return Text(CompoundEvidence, "PlayerShip names no item. The scene puts the player ship in its " +
                                                  "place (ThnScriptInstance.cs:79-96), so the row has no list.");
                }

                if (ThnTemplateRules.UserProp(entity, "category") is not ThnText category ||
                    !ThnTemplateRules.Categories.TryGetValue(category.Value, out var rule))
                {
                    return Text(CompoundEvidence, "The COMPOUND has no userprops.category that the engine knows. The " +
                                                  "category tells which list the template names, so the row has no list.");
                }

                return Listed(rule, true, CompoundEvidence,
                    $"The {rule.What} that the COMPOUND shows. Its userprops.category \"{category.Value}\" gives the kind " +
                    $"of the item. {Unsaved}", template);
            case "PSYS":
                return Is(template, ThnTemplateRules.PlayerShipEngines)
                    ? Text(PsysEvidence, "PlayerShipEngines names no effect. The scene gives this entity the engine " +
                                         "effect of the player ship (ThnScriptInstance.cs:98-104), so the row has no list.")
                    : Listed(ThnTemplateRules.Effect, true, PsysEvidence,
                        $"The effect that the PSYS shows: an effect or a visual effect. {Unsaved}", template);
            case "SOUND":
                return Listed(ThnTemplateRules.Sound, false, SoundEvidence,
                    "The sound that the SOUND plays. A SOUND can also name a voice line, so a name outside the list is " +
                    "valid.", template);
            default:
                return null;
        }
    }

    /// <summary>The text of the click hint of the field.</summary>
    public string ClickToPick => $"Click to pick the {Label?.ToLowerInvariant()}.";

    /// <summary>
    /// The look of the value of the row in a cutscene of a session, as the Inspector shows it. The text below the field
    /// comes from all problems of W at the template (see <see cref="EditSession.CutsceneChecks"/>). The error state
    /// comes from the problems that the save gate refuses (see <see cref="EditSession.CutsceneErrors"/>). So a retail
    /// value with a problem has the warning color, and a value of the active mod that the save gate refuses has the
    /// error color.
    /// </summary>
    /// <param name="path">The install path of the cutscene.</param>
    /// <param name="script">The version of W of the cutscene.</param>
    /// <param name="entity">The entity of the row.</param>
    /// <param name="list">The data list of the row (see <see cref="Lists"/>).</param>
    /// <param name="current">The value of the field.</param>
    /// <param name="name">Gives the display name of an entry of the list.</param>
    public ThnTemplateLook LookIn(EditSession edits, string path, ThnScriptModel script, ThnRef entity, ListView list,
        string current, Func<ListEntry, string> name)
    {
        var entry = current.Length == 0 ? null : list.Find(current);
        var problem = ThnFieldProblems.At(edits.CutsceneChecks(path), script, entity, Path);
        var blocked = ThnFieldProblems.At(edits.CutsceneErrors(path), script, entity, Path) != null;
        return Look(current, entry, entry == null ? null : name(entry), problem, blocked);
    }

    /// <summary>
    /// The closed field and the text below it for a value, as <c>PropertyGrid.Lists</c> shows a listed value. A value
    /// outside a closed list shows "Missing (Label): value". It has the error color and the red frame when the save
    /// gate refuses it (a value of the active mod), and else the warning color, for example for a retail value. The
    /// text below the field is the text of the check (see <see cref="ThnFieldProblems.Text"/>). A value outside an
    /// open list shows "Not listed (Label): value" with a note, and it does not block the save.
    /// </summary>
    /// <param name="current">The value of the field.</param>
    /// <param name="entry">The entry of the list with the value, or null.</param>
    /// <param name="name">The display name of the entry, or null.</param>
    /// <param name="problem">The problem of the checks of W at the template, or null (see
    /// <see cref="EditSession.CutsceneChecks"/>).</param>
    /// <param name="blocked">True when the save gate refuses the problem (see
    /// <see cref="EditSession.CutsceneErrors"/>).</param>
    public ThnTemplateLook Look(string current, ListEntry? entry, string? name, Diagnostic? problem, bool blocked)
    {
        var check = problem == null ? null : ThnFieldProblems.Text(problem, Path);
        var color = blocked ? EditorTheme.Error : problem != null ? EditorTheme.Warning : EditorTheme.Text;
        var below = blocked ? EditorTheme.Error : EditorTheme.Warning;
        if (current.Length == 0)
        {
            return new ThnTemplateLook($"None ({Label})", "", problem == null ? EditorTheme.TextDim : color, blocked,
                check, below, null);
        }

        if (entry != null)
        {
            var shown = string.IsNullOrWhiteSpace(name) ? entry.Value : name;
            var dim = shown.Equals(entry.Value, StringComparison.OrdinalIgnoreCase) ? "" : entry.Value;
            return new ThnTemplateLook(shown, dim, color, blocked, check, below, entry);
        }

        return Closed
            ? new ThnTemplateLook($"Missing ({Label}): {current}", "", blocked ? EditorTheme.Error : EditorTheme.Warning,
                blocked, check ?? NotChecked, below, null)
            : new ThnTemplateLook($"Not listed ({Label}): {current}", "", EditorTheme.Warning, false,
                check ?? OpenNote, EditorTheme.TextDim, null);
    }

    /// <summary>The note below the field of a value outside an open list.</summary>
    public string OpenNote => Rule?.Kind == ThnItemKind.Sound
        ? "No sound of the game data has this name. A SOUND can also name a voice line, so the save gate does not " +
          "check it."
        : "No costume of the game data has this name. The scene loads the costume only when no room NPC or RTC " +
          "character plays the actor of userprops.Actor. So the save gate does not check it.";

    /// <summary>The row of a template with a list. A template that the room fills in keeps the text field.</summary>
    private static ThnTemplateRow Listed(ThnTemplateRule rule, bool closed, string evidence, string note, string template)
    {
        var row = new ThnTemplateRow(rule, rule.Label, closed && rule.Closed, evidence, note);
        return template.StartsWith('$') && ThnTemplateRules.RoomFillsIn(rule.Kind)
            ? row with
            {
                TextReason = "The room fills in a template that starts with $, for example with its terrain " +
                             "(ThnScriptInstance.cs:106-109). The editor cannot tell the item, so the row has no list.",
            }
            : row;
    }

    /// <summary>The row of a template that names no item of a list.</summary>
    private static ThnTemplateRow Text(string evidence, string reason) =>
        new(null, null, false, evidence, reason) { TextReason = reason };

    private static bool Is(string template, string name) => template.Equals(name, StringComparison.OrdinalIgnoreCase);
}
