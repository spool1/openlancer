using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Commands;

/// <summary>A row of the equipment of a loadout: an <c>equip</c> line.</summary>
/// <param name="Equipment">The nickname of the equipment.</param>
/// <param name="Hardpoint">The hardpoint of the equipment, or null for internal equipment.</param>
public sealed record LoadoutEquipment(string Equipment, string? Hardpoint);

/// <summary>A row of the cargo of a loadout: a <c>cargo</c> line.</summary>
/// <param name="Good">The nickname of the equipment of the cargo, for example a commodity. The cargo line of the engine
/// names equipment.</param>
/// <param name="Count">How many units the cargo holds.</param>
public sealed record LoadoutCargo(string Good, int Count);

/// <summary>
/// The commands of the loadouts (section 7.3 of the MCP1 design). A new <c>[Loadout]</c> goes into the first file of
/// the role <c>loadouts</c> of the rule data, the ship loadout file in retail order. The agent tools
/// <c>create_loadout</c> and <c>scene_place_wreck</c> call them.
/// </summary>
/// <remarks>
/// <para>The archetype, each equipment and each cargo must be an entry of its list, as a picker of the editor gives
/// it. The command writes each value as the list spells it. Then the list checks of the rule data run on the new lines.
/// So a line with an error never reaches the edit view (decision D290 of the M23 design).</para>
/// <para>The rule data has no list of hardpoints. A model can have more hardpoints than the <c>hp_type</c> lines of
/// its ship name. So a hardpoint that these lines do not name gives a warning.</para>
/// <para>A value error throws, as the request form of the fields does (see <see cref="FieldCommands.SetFields"/>). A
/// rule of the edit, for example a nickname that another loadout has, gives a plan that refuses.</para>
/// </remarks>
public static class LoadoutCommands
{
    /// <summary>The list ids of the archetype of a loadout: the rule of <c>[Loadout] archetype</c> of the rule
    /// data.</summary>
    private static readonly string[] ArchetypeLists = ["ship", "solar_archetype"];

    /// <summary>
    /// Plans a new loadout with an archetype, equipment and cargo, as one entry of the edit history.
    /// </summary>
    /// <param name="archetype">The ship or the solar archetype of the loadout.</param>
    /// <exception cref="EditException">A value of a row is not an entry of its list, or the file cannot hold a line
    /// (<see cref="EditErrorKind.InvalidValue"/>).</exception>
    public static EditPlan Create(EditSession session, string nickname, string archetype,
        IReadOnlyList<LoadoutEquipment> equipment, IReadOnlyList<LoadoutCargo> cargo)
    {
        var plan = new BuildPlan($"Create {nickname.Trim()}");
        if (New(session, plan, nickname.Trim(), archetype, "archetype", ArchetypeLists, equipment, "equipment", cargo, "cargo")
            is not { } loadout)
            return plan;
        return plan.Runs(EditCommand.Group(plan.Label, [loadout.Command], session.Snapshot()), [loadout.Section], [],
            loadout.Section, loadout.Warnings);
    }

    /// <summary>A new loadout of a builder: the add of the section, the section, and the warnings.</summary>
    internal sealed record NewLoadout(EditCommand Command, SectionRef Section, IReadOnlyList<Diagnostic> Warnings);

    /// <summary>
    /// The add of a new loadout, for a builder. The checks of the file, the nickname and the rows run first, then the
    /// list checks of the lines. A rule of the edit gives a refusal of the plan and null.
    /// </summary>
    /// <param name="archetypeField">The name of the archetype in the request, for the errors.</param>
    /// <param name="archetypeLists">The lists of the archetype, in order.</param>
    /// <param name="equipmentField">The name of the rows of the equipment in the request.</param>
    /// <param name="cargoField">The name of the rows of the cargo in the request.</param>
    /// <exception cref="EditException">A value of a row is not valid (<see cref="EditErrorKind.InvalidValue"/>).</exception>
    internal static NewLoadout? New(EditSession session, BuildPlan plan, string nickname, string archetype,
        string archetypeField, IReadOnlyList<string> archetypeLists, IReadOnlyList<LoadoutEquipment> equipment,
        string equipmentField, IReadOnlyList<LoadoutCargo> cargo, string cargoField)
    {
        if (File(session) is not { } path)
        {
            plan.Refuse("", "The profile has no loadout file. The data needs a 'loadouts' line in freelancer.ini.");
            return null;
        }
        if (session.OpenDocument(path)?.ReadOnlyReason is { } readOnly)
        {
            plan.Refuse(path, $"{System.IO.Path.GetFileName(path)}: {readOnly}");
            return null;
        }
        var binary = session.IsBinaryFile(path);
        if (PatchFormatter.KeyError(nickname, binary) is { } keyError)
            throw Value("nickname", keyError);
        if (Taken(session, nickname) is { } taken)
        {
            plan.Refuse(path, $"The loadout {taken} exists in the data. Use another nickname.", null,
                MissionErrors.NameTaken);
            return null;
        }

        var rows = new RowValues(session);
        var arch = rows.Listed(archetype, archetypeField, archetypeLists);
        var hardpoints = arch != null ? ShipHardpoints(session, arch) : null;
        var warnings = new List<Diagnostic>();
        var lines = new List<(string Name, string Text)> { ("nickname", nickname), ("archetype", arch ?? archetype) };
        for (var i = 0; i < equipment.Count; i++)
        {
            var field = $"{equipmentField}[{i}]";
            var item = rows.Listed(equipment[i].Equipment, field + ".equipment", ["equipment"]);
            var hardpoint = equipment[i].Hardpoint is { } hp ? rows.Word(hp, field + ".hardpoint") : null;
            if (item == null || equipment[i].Hardpoint != null && hardpoint == null)
                continue;
            lines.Add(("equip", hardpoint == null ? item : $"{item}, {hardpoint}"));
            if (hardpoint != null && hardpoints != null && !hardpoints.Contains(hardpoint))
            {
                warnings.Add(Diagnostic.Warning(path, $"{field}: the hp_type lines of the ship {arch} do not name the " +
                                                      $"hardpoint {hardpoint}. Make sure that the model of the ship has it."));
            }
        }
        for (var i = 0; i < cargo.Count; i++)
        {
            if (rows.Listed(cargo[i].Good, $"{cargoField}[{i}].good", ["equipment"]) is { } good)
                lines.Add(("cargo", $"{good}, {cargo[i].Count}"));
        }
        rows.ThrowIfAny();

        if (BuilderText.Entries(session, path, lines, out var error) is not { } entries)
            throw Value(null, error!);
        var errors = BuilderText.ListErrors(session, path, "Loadout", nickname, entries, new HashSet<string>(), warnings);
        if (errors.Count > 0)
        {
            // Each row that passed the checks of its values has its line, in order.
            var first = errors[0];
            var field = first.Diagnostic.Key?.ToLowerInvariant() switch
            {
                "equip" => $"{equipmentField}[{first.Line}]",
                "cargo" => $"{cargoField}[{first.Line}]",
                "archetype" => archetypeField,
                _ => null,
            };
            throw BuilderText.ListError(field, errors);
        }
        var section = new SectionRef(path, new SectionIdentity("Loadout", nickname));
        return new NewLoadout(EditCommand.AddSection(path, "Loadout", entries), section, warnings);
    }

    /// <summary>The first file of the role <c>loadouts</c>, or null when the profile has none.</summary>
    internal static string? File(EditSession session) => session.DataFiles.Paths("loadouts").FirstOrDefault();

    /// <summary>The nickname of the loadout of the data that has a nickname, or null.</summary>
    internal static string? Taken(EditSession session, string nickname) =>
        session.Lists.List("loadout")?.Find(nickname)?.Value;

    /// <summary>The hardpoints that the <c>hp_type</c> lines of a ship name. Null for an archetype that is not a ship,
    /// and for a ship without such lines.</summary>
    private static HashSet<string>? ShipHardpoints(EditSession session, string archetype)
    {
        if (session.Lists.List("ship")?.Find(archetype)?.Section is not { } ship || session.Get(ship) is not { } view)
            return null;
        var result = view.Lines("hp_type").SelectMany(l => l.Values.Skip(1)).Select(v => v.ToString()!.Trim())
            .Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result.Count > 0 ? result : null;
    }

    private static EditException Value(string? field, string message) =>
        new(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, message) { Field = field });
}
