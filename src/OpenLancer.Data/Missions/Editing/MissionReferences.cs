using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>One value of a mission line that names a name of the game data outside the scripts.</summary>
/// <param name="Script">The install path of the script.</param>
/// <param name="Section">The section of the line.</param>
/// <param name="Row">The index of the line in its section.</param>
/// <param name="Value">The index of the value in the line.</param>
/// <param name="Key">The key of the line.</param>
public sealed record MissionNameUse(string Script, SectionRef Section, int Row, int Value, string Key);

/// <summary>
/// The reference provider of the mission editor for the world edits (section 4.10 of the M6b design, decision D668).
/// The M3 rename of a system object or a base, and the Database rename of a good, an equipment item or a ship, ask it
/// for the mission lines that name the item. The rename then changes these lines through the mission editor, in the
/// same entry of the edit history. A delete leaves them "Missing" (rule M23).
/// </summary>
public sealed class MissionReferences
{
    private readonly EditSession session;

    internal MissionReferences(EditSession session)
    {
        this.session = session;
    }

    /// <summary>The kinds of the mission values that name a system object: the system objects, and the names of the
    /// label rule that are not items of the script.</summary>
    public static readonly IReadOnlyList<MissionValueKind> ObjectKinds = [MissionValueKind.SystemObject, MissionValueKind.Object];

    /// <summary>The values of the lines of the mission scripts that name a name of some kinds, without letter case. A
    /// name of the label rule counts only when the script has no ship, formation, solar, loot or label with the
    /// name.</summary>
    public IReadOnlyList<MissionNameUse> Uses(IReadOnlyCollection<MissionValueKind> kinds, string name)
    {
        var result = new List<MissionNameUse>();
        foreach (var document in Scripts())
        {
            foreach (var (section, row, value) in MissionLinks.Naming(MissionSchema.Retail, document.Working, kinds, name))
            {
                if (kinds.Contains(MissionValueKind.Object) && !kinds.Contains(MissionValueKind.Ship) &&
                    NamesScriptItem(document, name))
                    continue;
                result.Add(new MissionNameUse(document.Path, new SectionRef(document.Path, document.IdentityAt(section)), row,
                    value, document.Working[section][row].Name));
            }
        }
        return result;
    }

    /// <summary>
    /// The session command that gives the mission lines of a name its new name, or null when no line names it. A line of
    /// a section that the editor cannot change stays, and a warning tells it.
    /// </summary>
    /// <param name="changed">Gets each section of a mission script whose lines the command changes, once, for example a
    /// trigger. Null drops them.</param>
    internal EditCommand? Rename(IReadOnlyCollection<MissionValueKind> kinds, string oldName, string newName,
        List<Diagnostic> warnings, List<SectionRef>? changed = null)
    {
        var commands = new List<MissionCommand>();
        foreach (var use in Uses(kinds, oldName))
        {
            var document = session.OpenDocument(use.Script)!;
            var index = document.IndexOf(use.Section.Id);
            if ((session.ReadOnlyReason ?? document.ReadOnlyReasonAt(index)) is { } reason)
            {
                warnings.Add(Diagnostic.Warning(use.Script,
                    $"{use.Section.Id}: the editor cannot change the '{use.Key}' line: {reason} The line names the old " +
                    "name, and the save stops until you fix it.") with { Target = use.Section, Key = use.Key });
                continue;
            }
            commands.Add(new SetLineValue(new RowRef(new ItemRef(use.Script, use.Section.Id), use.Row) { ExpectKey = use.Key },
                MissionValue.Of(newName)) { Index = use.Value });
            if (changed != null && !changed.Contains(use.Section))
                changed.Add(use.Section);
        }
        return commands.Count == 0
            ? null
            : new MissionPlanCommand($"Rename {oldName} to {newName} in the missions", session.Missions, commands);
    }

    /// <summary>The mission lines that show "Missing" after a delete of names (decision D81). The save stops at each of
    /// them until a fix (rule M23).</summary>
    public IReadOnlyList<ResultMissing> Missing(IReadOnlyCollection<MissionValueKind> kinds, IEnumerable<string> names) =>
        names.SelectMany(name => Uses(kinds, name)).Select(use => new ResultMissing(
                ItemRefs.Canonical(session, use.Section), use.Key,
                "The game ignores this line, because the game data has no item with this name.", true))
            .ToList();

    /// <summary>The mission files of the session: the mission scripts of W and their ship build files.</summary>
    private IEnumerable<EditDocument> Scripts() =>
        session.Missions.ScriptPaths().Concat(session.ShipBuildFilesOfScripts()).Distinct(VfsPath.Comparer)
            .Select(session.OpenDocument).OfType<EditDocument>().Where(d => d.State.Exists);

    /// <summary>True when a script has a ship, a formation, a solar, a loot or a label with a name.</summary>
    private static bool NamesScriptItem(EditDocument document, string name)
    {
        foreach (var section in document.Working)
        {
            var kind = MissionSection.KindOf(section.Name);
            if (kind is not (MissionSectionKind.Ship or MissionSectionKind.Formation or MissionSectionKind.Solar or
                MissionSectionKind.Loot or MissionSectionKind.RandomEncounter))
                continue;
            if (kind != MissionSectionKind.RandomEncounter &&
                string.Equals(SectionIdentity.KeyOf(section)?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            if (section.Any(e => MissionIndex.Is(e.Name, "label") && e.Count > 0 && MissionIndex.Is(MissionIndex.Text(e[0]), name)))
                return true;
        }
        return false;
    }
}
