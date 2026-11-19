using System;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The places of the new files of a mod, and the values of the lines that name them (decision O2). Each new file of a
/// mod goes below the folder of the mod, <c>DATA\MODS\&lt;mod id&gt;</c>. Two mods have two ids, so their new files
/// cannot collide.
/// </summary>
/// <remarks>
/// <para>Most lines name their file relative to the data folder, for example <c>[Base] file</c>, <c>[Room] file</c>,
/// <c>[Nebula] file</c>, <c>[Asteroids] file</c> and a cutscene path (see <see cref="DataValue"/>).</para>
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

    /// <summary>The folder of the new cutscenes of a mod, as an install path: <c>CUTSCENES</c> in the folder of the mod
    /// (decision D67).</summary>
    internal static string NewCutsceneFolder(string modId) => $@"{OwnFolder(modId)}\CUTSCENES";

    /// <summary>
    /// The value of a line that names a file relative to the data folder: the install path below the data folder, for
    /// example <c>MODS\my_mod\BASES\my_base.ini</c> for <c>DATA\MODS\my_mod\BASES\my_base.ini</c>. The engine resolves
    /// such a line through the data folder. Null for a path outside the data folder.
    /// </summary>
    /// <param name="installPath">An install path in any form.</param>
    public static string? DataValue(string installPath) =>
        VfsPath.Normalize(installPath) is { } normal && normal.Length > DataFolder.Length + 1 &&
        normal.StartsWith(DataFolder + "\\", StringComparison.OrdinalIgnoreCase)
            ? normal[(DataFolder.Length + 1)..]
            : null;
}
