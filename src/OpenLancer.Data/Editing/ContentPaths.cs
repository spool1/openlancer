using System;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The places of the new files of a mod, and the values of the lines that name them (decision O2). Each new INI file of
/// a mod goes below the folder of the mod, <c>DATA\MODS\&lt;mod id&gt;</c>. Two mods have two ids, so their new files
/// cannot collide. The editor edits each INI file of that folder of the active mod (see
/// <see cref="EditDocument.EditsWholeFile"/>).
/// </summary>
/// <remarks>
/// <para>A line names a file in one of two ways. A <c>[system]</c> of <c>universe.ini</c> names its file relative to
/// the folder of <c>universe.ini</c> (see <see cref="UniverseValue"/>). The other lines name their file relative to the
/// data folder (see <see cref="DataValue"/>). Examples are <c>[Base] file</c>, <c>[Room] file</c>,
/// <c>[Nebula] file</c>, <c>[Asteroids] file</c> and a cutscene path.</para>
/// <para>The place members compose the path only. The caller checks the names first.</para>
/// </remarks>
public static class ContentPaths
{
    /// <summary>The folder that the values of <see cref="DataValue"/> are relative to, as retail
    /// <c>freelancer.ini</c> gives it.</summary>
    private const string DataFolder = "DATA";

    /// <summary>The folder of the new files of a mod, as an install path: <c>DATA\MODS\&lt;mod id&gt;</c> (decision
    /// O2).</summary>
    internal static string OwnFolder(string modId) => $@"{DataFolder}\MODS\{modId}";

    /// <summary>True when an install path names a file below the folder of a mod (see <see cref="OwnFolder"/>), in
    /// any letter case.</summary>
    /// <param name="path">An install path in any form.</param>
    internal static bool IsOwnFile(string path, string modId)
    {
        var prefix = OwnFolder(modId) + '\\';
        return VfsPath.Normalize(path) is { } normal && normal.Length > prefix.Length &&
               normal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The folder of the new cutscenes of a mod, as an install path: <c>CUTSCENES</c> in the folder of the mod
    /// (decision D67).</summary>
    internal static string NewCutsceneFolder(string modId) => $@"{OwnFolder(modId)}\CUTSCENES";

    /// <summary>The file of a new star system of a mod: <c>SYSTEMS\&lt;nickname&gt;\&lt;nickname&gt;.ini</c> in the
    /// folder of the mod (decision O2). Its <c>[system]</c> names it with <see cref="UniverseValue"/>.</summary>
    internal static string NewSystemFile(string modId, string nickname) =>
        $@"{OwnFolder(modId)}\SYSTEMS\{nickname}\{nickname}.ini";

    /// <summary>The file of a new base of a mod: <c>BASES\&lt;base&gt;.ini</c> in the folder of the mod (decision O2).
    /// Its <c>[Base]</c> names it with <see cref="DataValue"/>.</summary>
    internal static string NewBaseFile(string modId, string baseNickname) => $@"{OwnFolder(modId)}\BASES\{baseNickname}.ini";

    /// <summary>The copy of a room file for a new base of a mod: <c>BASES\ROOMS\&lt;base&gt;_&lt;room&gt;.ini</c> in the
    /// folder of the mod (decision O2). The <c>[Room]</c> of the base file names it with
    /// <see cref="DataValue"/>.</summary>
    internal static string NewRoomFile(string modId, string baseNickname, string room) =>
        $@"{OwnFolder(modId)}\BASES\ROOMS\{baseNickname}_{room}.ini";

    /// <summary>
    /// The file of a new nebula or asteroid field of a mod: <c>SOLAR\NEBULA\&lt;name&gt;.ini</c> or
    /// <c>SOLAR\ASTEROIDS\&lt;name&gt;.ini</c> in the folder of the mod (decision O2). The retail files have the same
    /// two folders. The <c>[Nebula]</c> or <c>[Asteroids]</c> section of a system file names it with
    /// <see cref="DataValue"/>.
    /// </summary>
    /// <param name="sectionType">The section type of the system file that names the file: <c>Nebula</c> or
    /// <c>Asteroids</c>, in any letter case.</param>
    /// <exception cref="ArgumentException">The section type is not one of the two.</exception>
    internal static string NewVolumeFile(string modId, string sectionType, string name)
    {
        var folder = sectionType.ToUpperInvariant() switch
        {
            "NEBULA" => "NEBULA",
            "ASTEROIDS" => "ASTEROIDS",
            _ => throw new ArgumentException($"A [{sectionType}] section names no nebula or asteroid field file.",
                nameof(sectionType)),
        };
        return $@"{OwnFolder(modId)}\SOLAR\{folder}\{name}.ini";
    }

    /// <summary>The voices file of a mod: <c>AUDIO\voices.ini</c> in the folder of the mod (decision O2). The voice
    /// banks are not in it: the games read a bank only from <c>DATA\AUDIO</c>.</summary>
    internal static string VoicesFile(string modId) => $@"{OwnFolder(modId)}\AUDIO\voices.ini";

    /// <summary>The sounds file of a mod: <c>AUDIO\sounds.ini</c> in the folder of the mod (decision O2), with the
    /// sound files beside it.</summary>
    internal static string SoundsFile(string modId) => $@"{OwnFolder(modId)}\AUDIO\sounds.ini";

    /// <summary>
    /// The value of a line that names a file relative to the data folder: the install path below the data folder. An
    /// example is <c>MODS\my_mod\BASES\my_base.ini</c> for <c>DATA\MODS\my_mod\BASES\my_base.ini</c>. The engine
    /// resolves such a line through the data folder. Null for a path outside the data folder.
    /// </summary>
    /// <param name="installPath">An install path in any form.</param>
    public static string? DataValue(string installPath) =>
        VfsPath.Normalize(installPath) is { } normal && normal.Length > DataFolder.Length + 1 &&
        normal.StartsWith(DataFolder + "\\", StringComparison.OrdinalIgnoreCase)
            ? normal[(DataFolder.Length + 1)..]
            : null;

    /// <summary>
    /// The value of a <c>[system] file</c> line of <c>universe.ini</c> that names a file: the install path relative to
    /// the folder of the <c>universe.ini</c> of the edit view. An example is
    /// <c>..\MODS\my_mod\SYSTEMS\Ol01\Ol01.ini</c>. The map of the system files resolves the value against the same
    /// folder (see <see cref="SystemFiles.Folder"/>). Null when <c>freelancer.ini</c> names no <c>universe.ini</c>, or
    /// the path is not an install path.
    /// </summary>
    internal static string? UniverseValue(EditSession session, string installPath)
    {
        if (session.Systems.Folder is not { } folder || InstallPaths.TryParse(installPath) is not { } normal)
            return null;
        var from = folder.Length == 0 ? [] : folder.Split('\\');
        var to = normal.Split('\\');
        var same = 0;
        while (same < from.Length && same < to.Length - 1 && from[same].Equals(to[same], StringComparison.OrdinalIgnoreCase))
            same++;
        return string.Join('\\', Enumerable.Repeat("..", from.Length - same).Concat(to.Skip(same)));
    }

    /// <summary>
    /// True when W or the lower layer has a file at an install path, in any letter case (see
    /// <see cref="EditSession.HasFile"/>). A new file cannot take such a path. A file that W deleted is free again.
    /// False for a path that is not an install path.
    /// </summary>
    internal static bool Taken(EditSession session, string installPath) =>
        InstallPaths.TryParse(installPath) is { } normal && session.HasFile(normal);
}
