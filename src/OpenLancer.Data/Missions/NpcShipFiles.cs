using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions;

/// <summary>
/// The files of the NPC ship builds (<c>[NPCShipArch]</c>) that the mission scripts can name (section 14.1 of the M6a
/// design). The target checks of M11 and the mission index use the same rule.
/// </summary>
internal static class NpcShipFiles
{
    /// <summary>The ship build files that the game reads for all missions, encounters and bases, relative to the data
    /// folder. The game finds the build of a mission ship in the ship build file of its script first, then in these
    /// files.</summary>
    public static readonly IReadOnlyList<string> GlobalFiles = [@"MISSIONS\npcships.ini", @"MISSIONS\npcships_test.ini"];

    /// <summary>
    /// The files of NPC ship builds:
    /// <list type="bullet">
    /// <item><c>MISSIONS\npcships.ini</c> and <c>MISSIONS\npcships_test.ini</c>,</item>
    /// <item>the <c>npcships.ini</c> of each folder of <c>MISSIONS</c>,</item>
    /// <item>each file that an <c>npc_ship_file</c> line of a <c>[Mission]</c> of <paramref name="files"/> names,</item>
    /// <item>each file of <paramref name="files"/> with an <c>[NPCShipArch]</c>.</item>
    /// </list>
    /// The paths are install paths in normal form, without repeats, in the order of the rules above.
    /// </summary>
    /// <param name="dataPath">Gives the install path of a path relative to the data folder, or null.</param>
    /// <param name="folders">Gives the names of the folders in a folder.</param>
    /// <param name="files">Files to read for the last two rules, with their sections, or null for a file that does not
    /// exist.</param>
    public static IReadOnlyList<string> Find(Func<string, string?> dataPath, Func<string, IReadOnlyList<string>> folders,
        IEnumerable<(string Path, IReadOnlyList<Section>? Sections)> files)
    {
        var result = new List<string>();
        result.AddRange(GlobalFiles.Select(dataPath).OfType<string>());
        if (dataPath("MISSIONS") is { } missions)
            result.AddRange(folders(missions).Select(f => VfsPath.Normalize($@"{missions}\{f}\npcships.ini")).OfType<string>());
        foreach (var (path, sections) in files)
        {
            if (sections == null)
                continue;
            if (sections.Any(s => Is(s.Name, "NPCShipArch")))
                result.Add(path);
            result.AddRange(sections.Where(s => Is(s.Name, "Mission")).Select(s => ItemIndex.First(s, "npc_ship_file"))
                .OfType<string>().Select(dataPath).OfType<string>());
        }
        return result.Distinct(VfsPath.Comparer).ToList();
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
