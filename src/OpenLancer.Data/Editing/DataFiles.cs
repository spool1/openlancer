using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The files of each role of the rule data (section 7 of the M21 design): the install paths that the engine reads for
/// the role, and the roles of an install path. One path can have more than one role, for example
/// <c>fx\explosions.ini</c>.
/// </summary>
/// <remarks>
/// <para>The roles come from <c>freelancer.ini</c> of the edit view, which a patch cannot change. So they do not
/// change in a session, except the dynamic roles: the system files are the files that the <c>[system]</c> sections of W
/// of <c>universe.ini</c> name, the base files are the files that the <c>[Base]</c> sections name, the room files are the
/// files that the <c>[Room]</c> sections of the base files name (see <see cref="BaseFiles"/>), and the nebula and field
/// files are the files that the <c>[Nebula]</c> and <c>[Asteroids]</c> sections of the system files name (see
/// <see cref="FieldFiles"/>).</para>
/// <para>The <c>[Data]</c> names compare without letter case, as the engine compares them. Retail spells
/// <c>PetalDB</c> and <c>WeaponModDB</c>.</para>
/// </remarks>
internal sealed class DataFiles
{
    private readonly ListRules rules;
    private readonly SystemFiles systems;
    private readonly BaseFiles bases;
    private readonly FieldFiles fields;
    private readonly Func<IReadOnlyList<string>> missionFiles;
    private readonly Func<IReadOnlyList<string>> npcShipFiles;
    private readonly Dictionary<string, IReadOnlyList<string>> fixedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> fixedRoles = new(VfsPath.Comparer);

    /// <param name="freelancerIni">The sections of <c>freelancer.ini</c> of the edit view.</param>
    /// <param name="missionFiles">Gives the mission scripts of the edit view (section 14.1 of the M6a design).</param>
    /// <param name="npcShipFiles">Gives the files of the NPC ship builds of the edit view.</param>
    public DataFiles(IReadOnlyList<Section> freelancerIni, ListRules rules, SystemFiles systems, BaseFiles bases,
        FieldFiles fields, Func<IReadOnlyList<string>>? missionFiles = null, Func<IReadOnlyList<string>>? npcShipFiles = null)
    {
        this.rules = rules;
        this.systems = systems;
        this.bases = bases;
        this.fields = fields;
        this.missionFiles = missionFiles ?? (() => []);
        this.npcShipFiles = npcShipFiles ?? (() => []);
        var entries = DataPaths.Entries(freelancerIni).ToList();
        foreach (var role in rules.Roles.Values)
        {
            if (role.Dynamic)
                continue;
            var paths = Resolve(freelancerIni, entries, role);
            fixedPaths[role.Name] = paths;
            foreach (var path in paths)
            {
                if (!fixedRoles.TryGetValue(path, out var names))
                    fixedRoles[path] = names = [];
                names.Add(role.Name);
            }
        }
    }

    /// <summary>The install paths of a role, in load order, in <see cref="VfsPath.Normalize"/> form. Empty for an
    /// unknown role.</summary>
    public IReadOnlyList<string> Paths(string role)
    {
        if (fixedPaths.TryGetValue(role, out var paths))
            return paths;
        if (!rules.Roles.TryGetValue(role, out var definition))
            return [];
        if (definition.Universe)
            return systems.UniversePath is { } universe ? [universe] : [];
        if (definition.FieldFiles != null)
            return fields.Files(definition.FieldFiles);
        if (definition.MissionFiles)
            return missionFiles();
        if (definition.NpcShipFiles)
            return npcShipFiles();
        if (definition.BaseFiles)
            return bases.Files;
        return definition.SystemFiles ? systems.Files : definition.RoomFiles ? bases.RoomFiles : [];
    }

    /// <summary>The system files whose sections name a nebula or field file, for the scope <c>owner</c> (milestone
    /// M3). Empty for another file.</summary>
    public IReadOnlyList<string> OwnersOf(string path) => fields.OwnersOf(path);

    /// <summary>An object that changes when the nebula and field files or their owners change.</summary>
    public object FieldFilesState => fields.State;

    /// <summary>The roles of an install path. A path can have more than one role.</summary>
    public IReadOnlyList<string> RolesOf(string path)
    {
        var normal = VfsPath.Normalize(path);
        if (normal == null)
            return [];
        var result = fixedRoles.TryGetValue(normal, out var names) ? new List<string>(names) : [];
        foreach (var role in rules.Roles.Values)
        {
            if (role.Universe && systems.UniversePath is { } universe && VfsPath.Comparer.Equals(universe, normal) ||
                role.SystemFiles && systems.IsSystemFile(normal) || role.BaseFiles && bases.IsBaseFile(normal) ||
                role.RoomFiles && bases.IsRoomFile(normal) ||
                role.FieldFiles != null && fields.Contains(role.FieldFiles, normal) ||
                role.MissionFiles && missionFiles().Contains(normal, VfsPath.Comparer) ||
                role.NpcShipFiles && npcShipFiles().Contains(normal, VfsPath.Comparer))
                result.Add(role.Name);
        }
        return result;
    }

    /// <summary>False when retail reads the files of the role but the fork does not.</summary>
    public bool Engine(string role) => !rules.Roles.TryGetValue(role, out var definition) || definition.Engine;

    /// <summary>The install paths of a role with a fixed set of files in a <c>freelancer.ini</c>, as the engine finds
    /// them, or none for a role whose files come from other files. The jump graph of a target build reads the solar
    /// archetype files and the gate lock files with it.</summary>
    public static IReadOnlyList<string> FixedPaths(IReadOnlyList<Section> freelancerIni, ListRules rules, string role) =>
        rules.Roles.TryGetValue(role, out var definition) && !definition.Dynamic
            ? Resolve(freelancerIni, DataPaths.Entries(freelancerIni).ToList(), definition)
            : [];

    /// <summary>The paths of a role with a fixed set of files, as the engine finds them.</summary>
    private static IReadOnlyList<string> Resolve(IReadOnlyList<Section> freelancerIni,
        IReadOnlyList<(string Name, string Path)> entries, RoleDefinition role)
    {
        var result = new List<string>();
        if (role.Data != null)
        {
            var named = entries.Where(e => e.Name.Equals(role.Data, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Path).ToList();
            if (role.Last && named.Count > 0)
                named = [named[^1]];
            if (named.Count == 0 && role.Fallback != null && DataPaths.Resolve(freelancerIni, role.Fallback) is { } fallback)
                named = [fallback];
            result.AddRange(named);
        }
        foreach (var path in role.Paths)
        {
            if (DataPaths.Resolve(freelancerIni, path) is { } resolved)
                result.Add(resolved);
        }
        return result.Distinct(VfsPath.Comparer).ToList();
    }
}
