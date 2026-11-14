using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>A file that the <c>[Mission] npc_ship_file</c> line of a script can name (#18).</summary>
/// <param name="Path">The install path, in the letter case of the session.</param>
/// <param name="Value">The value of the line: the path below the data folder (see <see cref="MissionFiles.Display"/>).</param>
/// <param name="Shared">True for a shared file directly in <c>DATA\MISSIONS</c>. Encounters and bases use its builds
/// too.</param>
public sealed record ShipBuildFileChoice(string Path, string Value, bool Shared);

/// <summary>The ship build files of the field "Ship build file" of the Mission page, and the rule of a new ship build
/// file (#18).</summary>
public sealed partial class MissionEditor
{
    /// <summary>The first name that <see cref="FreeShipFileName"/> tries.</summary>
    public const string DefaultShipFileName = "npcships";

    /// <summary>
    /// The files that the <c>npc_ship_file</c> line of a script can name, sorted by their value. Each file is a file of
    /// the ship build files of the missions that exists in W and that obeys the rule of the command check: it is below
    /// <c>DATA\MISSIONS</c> outside the encounters folder, and it has only <c>[NPCShipArch]</c> sections. The list
    /// includes the shared file <c>DATA\MISSIONS\npcships.ini</c>. A path that a script names without a file is not in
    /// the list.
    /// </summary>
    public IReadOnlyList<ShipBuildFileChoice> ShipBuildFiles()
    {
        var missions = session.DataPath("MISSIONS");
        return session.MissionShipPaths
            .Where(session.CanBeShipBuildFile)
            .Select(session.OpenDocument).OfType<EditDocument>().Where(d => d.State.Exists)
            .Select(d => new ShipBuildFileChoice(d.Path, MissionFiles.Display(d.Path), MissionFiles.IsDirectlyIn(d.Path, missions)))
            .OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The install path of a new ship build file of a script: <c>&lt;folder of the script&gt;\&lt;name&gt;.ini</c>.
    /// <see cref="NewShipFileRefusal"/> checks it.</summary>
    /// <param name="script">The install path of the script.</param>
    public static string NewShipFilePath(string script, string name) =>
        $@"{VfsPath.Parent(VfsPath.Normalize(script) ?? script)}\{name}.ini";

    /// <summary>
    /// Why the <c>npc_ship_file</c> line of a script cannot name a new ship build file at a path, or null (#18). The
    /// popup of "New file..." and the command <see cref="NewShipBuildFile"/> use this check. The session has no active
    /// mod or takes no edits. The script is directly in <c>DATA\MISSIONS</c>. The path is not
    /// <c>&lt;folder of the script&gt;\&lt;name&gt;.ini</c>, or the name does not obey the name rule of New cutscene.
    /// The edit view or W has a file at the path in any letter case.
    /// </summary>
    /// <param name="script">The install path of the script.</param>
    /// <param name="path">The install path of the new file.</param>
    public string? NewShipFileRefusal(string script, string path) => NewShipFileProblem(script, path)?.Message;

    /// <summary>The error of <see cref="NewShipFileRefusal"/>, with its detail code, or null.</summary>
    internal EditError? NewShipFileProblem(string script, string path)
    {
        if (session.ActiveMod == null)
            return MissionErrors.NotEditableError("There is no active mod. Select a mod to edit.");
        if (session.ReadOnlyReason is { } reason)
            return MissionErrors.NotEditableError(reason);
        var missions = session.DataPath("MISSIONS");
        var normalScript = VfsPath.Normalize(script) ?? script;
        if (MissionFiles.IsDirectlyIn(normalScript, missions))
            return MissionErrors.ShipFileOfScriptInMissionsError(script);
        var folder = VfsPath.Parent(normalScript);
        // A name with a folder separator, for example a name that starts with "..\", names another folder. So the name
        // rule refuses it before the folder rule, as New cutscene does.
        const string extension = ".ini";
        var prefix = folder + '\\';
        if (path.Length > prefix.Length + extension.Length && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
            !ThnEdits.IsNameText(path[prefix.Length..^extension.Length]))
            return MissionErrors.ShipFileNameError(ThnEdits.BadName);
        if (path.Equals(prefix + extension, StringComparison.OrdinalIgnoreCase))
            return MissionErrors.ShipFileNameError(MissionErrors.NoShipFileName);
        // The name rule refuses the name of a Windows device, so the folder rule does not use the install path rule.
        var normal = VfsPath.Normalize(path);
        if (normal == null || !VfsPath.Comparer.Equals(VfsPath.Parent(normal), folder) ||
            !normal.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            return MissionErrors.ShipFileFolderError(folder);
        if (ThnEdits.NameProblem(System.IO.Path.GetFileNameWithoutExtension(normal)) is { } problem)
            return MissionErrors.ShipFileNameError(problem);
        if (!MissionFiles.CanBeOwnShipFile(normal, missions))
            return MissionErrors.BadShipFilePathError(path);
        if (session.HasFile(normal))
            return MissionErrors.ShipFileInUseError(normal);
        return null;
    }

    /// <summary>The default name of a new ship build file of a script: <c>npcships</c>, or <c>npcships_2</c> and up when
    /// the folder of the script has a file of that name.</summary>
    /// <param name="script">The install path of the script.</param>
    public string FreeShipFileName(string script)
    {
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? DefaultShipFileName : $"{DefaultShipFileName}_{i.ToString(CultureInfo.InvariantCulture)}";
            if (NewShipFileProblem(script, NewShipFilePath(script, name))?.Detail != MissionErrors.PathInUse)
                return name;
        }
    }
}
