using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The commands of the wrecks (decision D730 of the MCP1 design). A wreck is a solar object of a wreck type of the rule
/// data with a loadout of a ship. The player shoots it to free its cargo. The agent tool <c>scene_place_wreck</c> calls
/// them.
/// </summary>
public static class WreckCommands
{
    /// <summary>The detail code of an archetype of another type than the wreck types of the rule data.</summary>
    public const string NotAWreck = "NotAWreck";

    /// <summary>
    /// Plans a new wreck in a system, as one entry of the edit history. The entry adds a new <c>[Loadout]</c> to the
    /// first file of the loadouts role (see <see cref="LoadoutCommands"/>). The loadout has the ship archetype, the
    /// equipment rows and the cargo rows. The entry also adds a new object with the archetype, the position and the
    /// rotation. The object gets the <c>loadout</c> line and the lines of the wreck rule of the rule data.
    /// </summary>
    /// <remarks>
    /// <para>Retail puts each wreck loadout into a ship loadout file, and each one names a ship archetype. A wreck
    /// archetype has the type of the wreck rule, <c>mission_satellite</c> in retail. The object gets the next free
    /// nickname <c>&lt;system&gt;_wreck_&lt;n&gt;</c> when the request gives none (decision D235 of the M3 design). Its
    /// loadout gets the nickname of the object with <c>_loadout</c>, or the next free number after it.</para>
    /// <para>The texts of the name and the infocard need the object in the edit view (see
    /// <see cref="FieldCommands.SetText"/>). So the caller sets them after this plan, in the same batch.</para>
    /// </remarks>
    /// <param name="system">The nickname of the system.</param>
    /// <param name="rotation">The rotation in degrees, or null for no <c>rotate</c> line.</param>
    /// <param name="archetype">The solar archetype of the wreck.</param>
    /// <param name="shipArchetype">The ship that the wreck was: the archetype of its loadout.</param>
    /// <param name="loot">The cargo rows of the loadout.</param>
    /// <param name="equipment">The equipment rows of the loadout.</param>
    /// <param name="nickname">The nickname of the object, or null for the next free nickname.</param>
    /// <exception cref="EditException">A value is not valid (<see cref="EditErrorKind.InvalidValue"/>).</exception>
    public static EditPlan Place(EditSession session, string system, Vector3 position, Vector3? rotation, string archetype,
        string shipArchetype, IReadOnlyList<LoadoutCargo> loot, IReadOnlyList<LoadoutEquipment> equipment,
        string? nickname)
    {
        var plan = new BuildPlan(nickname == null ? "Place wreck" : $"Place wreck {nickname.Trim()}");
        if (session.Lists.RuleData.Builders.Wreck is not { } rule)
            return plan.Refuse("", "The rule data has no wreck builder.");
        if (ScenePlans.SystemFileProblem(session, system, out var path) is { } fileProblem)
            return plan.Refuse(fileProblem.Source, fileProblem.Message);

        var rows = new RowValues(session);
        var arch = rows.Listed(archetype, "archetype", ["solar_archetype"]);
        rows.ThrowIfAny();
        var type = ScenePlans.TypeOf(session, arch!);
        if (type == null || !rule.Types.Contains(type))
        {
            throw new EditException(new EditError(EditErrorKind.InvalidValue, NotAWreck,
                $"The archetype {arch} has the type {type ?? "none"}. A wreck needs an archetype of the type " +
                $"{string.Join(" or ", rule.Types)}. Use list_values with the list solar_archetype and the text " +
                $"{rule.Types[0].ToUpperInvariant()}.") { Field = "archetype" });
        }

        var binary = session.IsBinaryFile(path);
        string name;
        if (nickname?.Trim() is { } given)
        {
            if (PatchFormatter.KeyError(given, binary) is { } keyError)
            {
                throw new EditException(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, keyError)
                {
                    Field = "nickname",
                });
            }
            if (session.Nicknames.Taken(given) is { } taken)
            {
                return plan.Refuse(path, $"The {taken.Section.Id.Type.ToLowerInvariant()} {taken.Name} has the " +
                                         "nickname. Use another nickname.", taken.Section, MissionErrors.NameTaken);
            }
            name = given;
        }
        else
        {
            name = session.Nicknames.FirstFree($"{system}_wreck".ToLowerInvariant(), 1);
        }
        plan.Label = $"Place wreck {name}";

        var stem = name + "_loadout";
        var loadoutName = LoadoutCommands.Taken(session, stem) == null
            ? stem
            : session.Nicknames.FirstFree(stem, 1, taken: n => LoadoutCommands.Taken(session, n) != null);
        if (LoadoutCommands.New(session, plan, loadoutName, shipArchetype, "ship_archetype", ["ship"], equipment,
                "equipment", loot, "loot") is not { } loadout)
            return plan;

        var lines = new List<(string Name, string Text)>
        {
            ("nickname", name), ("pos", ScenePlans.PositionText(position)),
        };
        if (rotation is { } turn)
            lines.Add(("rotate", BuilderText.Angles(turn)));
        lines.Add(("archetype", arch!));
        lines.Add(("loadout", loadoutName));
        lines.AddRange(rule.Lines.Select(l => (l.Key, l.Value!)));
        if (BuilderText.Entries(session, path, lines, out var error) is not { } entries)
            throw new EditException(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, error!));
        var warnings = loadout.Warnings.ToList();
        // The loadout line names the new loadout, which the edit view does not have yet.
        var errors = BuilderText.ListErrors(session, path, SceneSections.ObjectType, name, entries,
            new HashSet<string>(), warnings,
            p => SceneSections.Is(p.Diagnostic.Key ?? "", "loadout") && SceneSections.Is(p.Value ?? "", loadoutName));
        if (errors.Count > 0)
            throw BuilderText.ListError("archetype", errors);
        warnings.AddRange(ScenePlans.DanglingWarnings(session, name, path));

        var added = new SectionRef(path, new SectionIdentity(SceneSections.ObjectType, name));
        var command = EditCommand.Group(plan.Label,
            [loadout.Command, EditCommand.AddSection(path, SceneSections.ObjectType, entries)], session.Snapshot());
        return plan.Runs(command, [added, loadout.Section], [], added, warnings);
    }
}
