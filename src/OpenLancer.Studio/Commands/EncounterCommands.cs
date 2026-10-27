using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Commands;

/// <summary>A faction of an encounter, or a faction weight of a zone: a faction and its weight.</summary>
public sealed record FactionShare(string Faction, double Weight);

/// <summary>An encounter group of a zone: one <c>encounter</c> line and its <c>faction</c> lines.</summary>
/// <param name="Encounter">The nickname of the encounter, as an <c>[EncounterParameters]</c> section names it.</param>
/// <param name="Level">The level of the encounter.</param>
/// <param name="Chance">The chance of the encounter, from 0 to 1.</param>
/// <param name="Factions">The factions of the ships of the encounter.</param>
public sealed record EncounterGroup(string Encounter, int Level, double Chance, IReadOnlyList<FactionShare> Factions);

/// <summary>A <c>density_restriction</c> line of a zone: the most ships of a class.</summary>
public sealed record DensityRestriction(int Count, string Class);

/// <summary>
/// The commands of the encounters of a zone (decision D732 of the MCP1 design). The agent tools
/// <c>scene_set_encounters</c> and <c>scene_place_patrol_route</c> call them. The encounter group editor of M3b can use
/// them too.
/// </summary>
/// <remarks>
/// <para>The lines of a zone are in this order: the <c>faction_weight</c> lines, the <c>density_restriction</c> lines,
/// then each <c>encounter</c> line with its <c>faction</c> lines. Retail has this order in each zone. The engine gives a
/// <c>faction</c> or <c>faction_weight</c> line after an encounter line to that encounter, so the order must stay. These
/// lines are one block, and a patch keeps the linked group <c>encounter</c>, <c>faction</c> and
/// <c>faction_weight</c> (see <see cref="SectionKeys.LinkedGroups"/>).</para>
/// <para>The system file lists each encounter of its zones in an <c>[EncounterParameters]</c> section. When the system
/// has no such section for an encounter, and another system has one, the same entry adds a copy of it. This is a fix of
/// the plan. An encounter that no system has gives an error.</para>
/// </remarks>
public static class EncounterCommands
{
    /// <summary>The names of the block of the encounter lines of a zone, in the order of the block.</summary>
    internal static readonly string[] BlockNames = ["faction_weight", "density_restriction", "encounter", "faction"];

    /// <summary>The section type that names an encounter and its file in a system file.</summary>
    internal const string ParametersType = "EncounterParameters";

    /// <summary>
    /// Plans the encounter groups of a zone, as one entry of the edit history. The encounter groups replace each
    /// encounter line of the zone with its faction lines. The faction weights and the density restrictions replace the
    /// lines of the zone before its first encounter line. A null list keeps these lines.
    /// </summary>
    /// <exception cref="EditException">A value of a row is not valid (<see cref="EditErrorKind.InvalidValue"/>).</exception>
    public static EditPlan SetGroups(EditSession session, SectionRef zone, IReadOnlyList<EncounterGroup> encounters,
        IReadOnlyList<FactionShare>? factionWeights, IReadOnlyList<DensityRestriction>? densityRestrictions)
    {
        var plan = new BuildPlan($"Set the encounters of {zone.Id.Key}");
        if (SceneSections.KindOf(session, zone) != SceneKind.Zone)
            return plan.Refuse(zone.Path, $"{zone.Id}: only a zone of a system has encounters.", zone);
        if (session.Get(zone) is not { } view)
            return plan.Refuse(zone.Path, $"The edit view has no section {zone.Id}.", zone, MissionErrors.ItemNotFound);
        if (FieldBinding.SectionLock(session, zone, view) is { } locked)
            return plan.Refuse(zone.Path, locked, zone, MissionErrors.ReadOnlySection);
        var document = session.OpenDocument(zone.Path)!;
        var index = document.IndexOf(zone.Id);

        // The lines of the zone before its first encounter line, which a null list keeps.
        var zoneLines = view.Entries.TakeWhile(e => !SceneSections.Is(e.Name, "encounter")).ToList();
        var block = Lines(session, zone.Path, encounters, factionWeights, densityRestrictions, out var parameters);
        var typed = new Dictionary<string, List<IReadOnlyList<ValueBase>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in BlockNames)
        {
            var texts = block.Where(l => SceneSections.Is(l.Name, name)).Select(l => l.Text).ToList();
            // The merge gives the n-th patch line of a name the value types of the n-th line of that name in B.
            typed[name] = EditCommand.ReadLines(document, index, name, texts, out var error) ??
                          throw new EditException(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue,
                              error!));
        }
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<EntryView>();
        foreach (var (name, _) in block)
        {
            var n = used.GetValueOrDefault(name);
            used[name] = n + 1;
            lines.Add(new EntryView(name, typed[name][n]));
        }
        bool Is(EntryView line, string name) => SceneSections.Is(line.Name, name);
        var ordered = new List<EntryView>();
        ordered.AddRange((factionWeights == null ? zoneLines : lines).Where(l => Is(l, "faction_weight")));
        ordered.AddRange((densityRestrictions == null ? zoneLines : lines).Where(l => Is(l, "density_restriction")));
        ordered.AddRange(lines.Where(l => Is(l, "encounter") || Is(l, "faction")));

        var (adds, fixes, commands) = Added(session, zone.Path, parameters);
        commands.Add(EditCommand.SetLineBlock(zone, BlockNames, ordered, plan.Label));
        return plan.Runs(EditCommand.Group(plan.Label, commands, session.Snapshot()), adds, [zone], null, [], fixes);
    }

    /// <summary>An <c>[EncounterParameters]</c> section that a plan adds to a system file: the encounter, its file and
    /// the system that has the section.</summary>
    internal sealed record NewParameters(string Encounter, string File, string? From);

    /// <summary>
    /// The text of each line of the block of the encounters of a zone, in the order of the block (see
    /// <see cref="EncounterCommands"/>). The values of the rows come from their lists. A null list gives no lines.
    /// </summary>
    /// <param name="path">The install path of the system file of the zone.</param>
    /// <param name="parameters">The <c>[EncounterParameters]</c> sections that the system file needs.</param>
    /// <exception cref="EditException">A value of a row is not valid (<see cref="EditErrorKind.InvalidValue"/>).</exception>
    internal static List<(string Name, string Text)> Lines(EditSession session, string path,
        IReadOnlyList<EncounterGroup> encounters, IReadOnlyList<FactionShare>? factionWeights,
        IReadOnlyList<DensityRestriction>? densityRestrictions, out List<NewParameters> parameters)
    {
        var rows = new RowValues(session);
        var lines = new List<(string Name, string Text)>();
        var needed = new List<NewParameters>();
        for (var i = 0; i < (factionWeights?.Count ?? 0); i++)
        {
            var row = factionWeights![i];
            if (rows.Listed(row.Faction, $"faction_weights[{i}].faction", ["faction"]) is { } faction)
                lines.Add(("faction_weight", $"{faction}, {BuilderText.Number(row.Weight)}"));
        }
        for (var i = 0; i < (densityRestrictions?.Count ?? 0); i++)
        {
            var row = densityRestrictions![i];
            if (rows.Word(row.Class, $"density_restrictions[{i}].class") is { } kind)
                lines.Add(("density_restriction", $"{row.Count}, {kind}"));
        }
        for (var i = 0; i < encounters.Count; i++)
        {
            var row = encounters[i];
            var field = $"encounters[{i}]";
            var encounter = Encounter(session, path, row.Encounter, field + ".encounter", rows, needed);
            var factions = row.Factions.Select((f, j) =>
                    (Faction: rows.Listed(f.Faction, $"{field}.factions[{j}].faction", ["faction"]), f.Weight))
                .ToList();
            if (encounter == null)
                continue;
            lines.Add(("encounter", $"{encounter}, {row.Level}, {BuilderText.Number(row.Chance)}"));
            lines.AddRange(factions.Where(f => f.Faction != null)
                .Select(f => ("faction", $"{f.Faction}, {BuilderText.Number(f.Weight)}")));
        }
        rows.ThrowIfAny();
        parameters = needed;
        return lines;
    }

    /// <summary>
    /// The adds of the <c>[EncounterParameters]</c> sections that a plan needs, at the end of the system file: the new
    /// sections, a fix of the plan for each one, and the commands.
    /// </summary>
    internal static (List<SectionRef> Adds, List<PlannedFix> Fixes, List<EditCommand> Commands) Added(EditSession session,
        string path, IReadOnlyList<NewParameters> parameters)
    {
        var adds = new List<SectionRef>();
        var fixes = new List<PlannedFix>();
        var commands = new List<EditCommand>();
        foreach (var parameter in parameters)
        {
            var entries = BuilderText.Entries(session, path, [("nickname", parameter.Encounter), ("filename", parameter.File)],
                              out var error) ??
                          throw new EditException(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue,
                              error!));
            var section = new SectionRef(path, new SectionIdentity(ParametersType, parameter.Encounter));
            commands.Add(EditCommand.AddSection(path, ParametersType, entries));
            adds.Add(section);
            fixes.Add(new PlannedFix(section, "nickname",
                $"The system had no [{ParametersType}] section for the encounter {parameter.Encounter}. The edit adds a " +
                $"copy of the section of the system {parameter.From}."));
        }
        return (adds, fixes, commands);
    }

    /// <summary>
    /// The encounter of a row as the system spells it, or null with an error. An encounter that the system does not have
    /// and another system has goes into <paramref name="needed"/>, once.
    /// </summary>
    private static string? Encounter(EditSession session, string path, string value, string field, RowValues rows,
        List<NewParameters> needed)
    {
        if (session.Lists.List("encounter_of_system", path)?.Find(value) is { } own)
            return own.Value;
        if (needed.FirstOrDefault(n => n.Encounter.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)) is { } planned)
            return planned.Encounter;
        foreach (var file in session.Systems.Files.Where(f => !VfsPath.Comparer.Equals(f, path)))
        {
            if (session.Lists.List("encounter_of_system", file)?.Find(value) is not { Section: { } section } entry ||
                session.Get(section)?.Lines("filename").LastOrDefault() is not { Values.Count: > 0 } line)
                continue;
            needed.Add(new NewParameters(entry.Value, line.Values[0].ToString()!.Trim(), session.SystemNicknameOf(file)));
            return entry.Value;
        }
        var system = session.SystemNicknameOf(path);
        rows.Add(field, $"No [{ParametersType}] section of a system has the encounter '{value}'. Pick an encounter from " +
                        $"list_values with the list encounter_of_system and the system {system}." +
                        ItemRefs.Hint(value, session.Lists.List("encounter_of_system", path)?.Entries.Select(e => e.Value) ?? []));
        return null;
    }
}
