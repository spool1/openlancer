using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Missions;

/// <summary>The state of a mission script in the edit view (section 5.1 of the M6a design).</summary>
public enum MissionScriptState
{
    /// <summary>No mod changes the script.</summary>
    BaseGame,

    /// <summary>A mod patch changes the script.</summary>
    Changed,

    /// <summary>A mod has its own file at the path of the script.</summary>
    Replaced,

    /// <summary>No file is at the path.</summary>
    Missing,
}

/// <summary>One mission script of the browser (section 14.1 of the M6a design).</summary>
/// <param name="Path">The install path in normal form.</param>
/// <param name="Slot">The story slot that loads the script, or null for another script.</param>
/// <param name="ModId">The mod of <see cref="State"/>: the last mod that changes or replaces the script, or null.</param>
/// <param name="InBase">True when the base folder without mods has the script.</param>
public sealed record MissionScriptInfo(string Path, StorySlot? Slot, MissionScriptState State, string? ModId, bool InBase);

/// <summary>
/// Finds the mission scripts (decision D535): the 14 slot paths of the story slot table, and each other <c>.ini</c> file
/// in <c>DATA\MISSIONS</c> and its folders, except <c>ENCOUNTERS</c>, that holds a <c>[Mission]</c> or a
/// <c>[Trigger]</c> section.
/// </summary>
public static class MissionFiles
{
    /// <summary>The folder of the encounter files below the missions folder. It holds no mission script.</summary>
    private const string EncountersFolder = "ENCOUNTERS";

    /// <summary>The install path of the script of a slot, or null when <c>freelancer.ini</c> has no data path.</summary>
    internal static string? SlotPath(IReadOnlyList<Section> freelancerIni, StorySlot slot) =>
        DataPaths.Resolve(freelancerIni, slot.Path);

    /// <summary>
    /// The install paths of the scripts of the scan: each <c>.ini</c> file in the missions folder and its folders, except
    /// the encounters folder, with a <c>[Mission]</c> or a <c>[Trigger]</c> section. In path order. A file that cannot
    /// be read is not a script.
    /// </summary>
    /// <param name="missionsFolder">The install path of <c>DATA\MISSIONS</c>.</param>
    public static IReadOnlyList<string> Scan(FileSystem files, string missionsFolder)
    {
        var result = new List<string>();
        foreach (var path in VfsFiles.Find(files, ".ini", missionsFolder))
        {
            if (!IsScanned(path, missionsFolder))
                continue;
            byte[] bytes;
            try
            {
                bytes = files.ReadAllBytes(path);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                continue;
            }
            if (HoldsScript(path, bytes))
                result.Add(VfsPath.Normalize(path) ?? path);
        }
        return result;
    }

    /// <summary>True when an INI file holds a <c>[Mission]</c> or a <c>[Trigger]</c> section. A text file needs only a
    /// scan of its header lines, with the skip rules of the engine parser.</summary>
    internal static bool HoldsScript(string path, byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("BINI"u8))
        {
            try
            {
                return IniDocument.Parse(path, bytes).Sections.Any(s => IsScriptSection(s.Name));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return false;
            }
        }
        var text = IniDocument.TextEncoding.GetString(bytes);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.AsSpan().Trim(" \t\r");
            if (line.Length < 2 || line[0] != '[')
                continue;
            var close = line.IndexOf(']');
            if (close > 0 && IsScriptSection(line[1..close].Trim().ToString()))
                return true;
        }
        return false;
    }

    /// <summary>True for a path that the scan reads: a file below the missions folder, outside the encounters
    /// folder.</summary>
    /// <param name="missionsFolder">The install path of <c>DATA\MISSIONS</c>.</param>
    internal static bool IsScanned(string path, string missionsFolder)
    {
        if (path.Length <= missionsFolder.Length + 1 || !path.StartsWith(missionsFolder, StringComparison.OrdinalIgnoreCase) ||
            path[missionsFolder.Length] != '\\')
            return false;
        return !path[(missionsFolder.Length + 1)..].StartsWith(EncountersFolder + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a file directly in the missions folder, for example the shared ship build file or a pilot file.
    /// Such a file is world data: it is never the ship build file of a script (review finding RB2).</summary>
    /// <param name="path">An install path.</param>
    /// <param name="missionsFolder">The install path of <c>DATA\MISSIONS</c>, or null when the data has none.</param>
    internal static bool IsDirectlyIn(string path, string? missionsFolder) =>
        missionsFolder != null &&
        VfsPath.Comparer.Equals(VfsPath.Normalize(System.IO.Path.GetDirectoryName(path) ?? "") ?? "", missionsFolder);

    /// <summary>True for a path that can be the own ship build file of a script: a file in a folder below the missions
    /// folder, outside the encounters folder. A file directly in the missions folder and an encounter file are world
    /// data that encounters and bases use too (review finding RB2, final finding 5 of MCP1-3).</summary>
    /// <param name="path">An install path.</param>
    /// <param name="missionsFolder">The install path of <c>DATA\MISSIONS</c>, or null when the data has none.</param>
    internal static bool CanBeOwnShipFile(string path, string? missionsFolder) =>
        missionsFolder == null || IsScanned(path, missionsFolder) && !IsDirectlyIn(path, missionsFolder);

    /// <summary>True for the section types that only a mission script has.</summary>
    internal static bool IsScriptSection(string type) =>
        type.Equals("Mission", StringComparison.OrdinalIgnoreCase) || type.Equals("Trigger", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The scripts of the browser: the slot scripts in slot order, then the other scripts of the scan in path order.
    /// </summary>
    /// <param name="slotPaths">The install path of each slot script, in slot order, or null.</param>
    /// <param name="scanned">The scripts of <see cref="Scan"/>.</param>
    /// <param name="state">Gives the state, the mod and the base presence of a path.</param>
    internal static IReadOnlyList<MissionScriptInfo> List(IReadOnlyList<(StorySlot Slot, string? Path)> slotPaths,
        IReadOnlyList<string> scanned, Func<string, (MissionScriptState State, string? ModId, bool InBase)> state)
    {
        var result = new List<MissionScriptInfo>();
        var seen = new HashSet<string>(VfsPath.Comparer);
        foreach (var (slot, path) in slotPaths)
        {
            var normal = path == null ? slot.Path : VfsPath.Normalize(path) ?? path;
            seen.Add(normal);
            var (s, mod, inBase) = path == null ? (MissionScriptState.Missing, null, false) : state(normal);
            result.Add(new MissionScriptInfo(normal, slot, s, mod, inBase));
        }
        foreach (var path in scanned.Where(p => seen.Add(p)).Order(StringComparer.OrdinalIgnoreCase))
        {
            var (s, mod, inBase) = state(path);
            result.Add(new MissionScriptInfo(path, null, s, mod, inBase));
        }
        return result;
    }

    /// <summary>The text of a path for the UI, for example <c>Missions\M01a\m01a.ini</c>: the path below the data
    /// folder.</summary>
    public static string Display(string path)
    {
        var at = path.IndexOf('\\');
        return at > 0 && path[..at].Equals("DATA", StringComparison.OrdinalIgnoreCase) ? path[(at + 1)..] : path;
    }

    /// <summary>The text of a relative path from a mission line, for example <c>missions\\m04\\npcships.ini</c> with
    /// doubled backslashes, in the normal form of a path below the data folder, or null.</summary>
    public static string? RelativeOf(string value)
    {
        var builder = new StringBuilder(value.Trim());
        builder.Replace('/', '\\');
        return VfsPath.Normalize(builder.ToString());
    }
}
