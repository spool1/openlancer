using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
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
    /// The files that the <c>npc_ship_file</c> line of a script can name, sorted by their value. Each file exists in W
    /// and obeys the rule of the command check: it is below <c>DATA\MISSIONS</c> outside the encounters folder, and it
    /// has only <c>[NPCShipArch]</c> sections. The files are the ship build files of the missions, and each other file
    /// of W that the session opened, for example a new file that no line names now. The list includes the shared files
    /// directly in <c>DATA\MISSIONS</c>, for example <c>npcships.ini</c>. A path that a script names without a file is not
    /// in the list.
    /// </summary>
    public IReadOnlyList<ShipBuildFileChoice> ShipBuildFiles()
    {
        var missions = session.DataPath("MISSIONS");
        return session.MissionShipPaths.Concat(session.OpenFilesExceptScripts()).Distinct(VfsPath.Comparer)
            .Where(session.CanBeShipBuildFile)
            .Select(session.OpenDocument).OfType<EditDocument>().Where(d => d.State.Exists)
            .Select(d => new ShipBuildFileChoice(d.Path, MissionFiles.Display(d.Path), MissionFiles.IsDirectlyIn(d.Path, missions)))
            .OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when W has a file at the path that a value of an <c>npc_ship_file</c> line names, also a file that
    /// cannot be a ship build file.</summary>
    /// <param name="value">The value of the line, a path below the data folder.</param>
    public bool NamesFile(string value) =>
        MissionFiles.RelativeOf(value) is { } relative && session.DataPath(relative) is { } path &&
        (session.OpenDocument(path) is { } document ? document.State.Exists : session.HasFile(path));

    /// <summary>
    /// The ship builds that the <c>[NPC]</c> sections of a script name and that the game does not find when the
    /// <c>npc_ship_file</c> line names another file, in the order of the sections (review of #18). The game finds the
    /// build of a mission ship in the ship build file of its script first, then in the global ship build files (see
    /// <see cref="NpcShipFiles.GlobalFiles"/>). It does not spawn a ship whose build it does not find. The mission
    /// checks find a build in each ship build file, so they do not give this problem.
    /// </summary>
    /// <param name="script">The install path of the script.</param>
    /// <param name="path">The install path of the other file, or null for a new empty file. A path without a file in W
    /// has no builds.</param>
    public IReadOnlyList<string> LostBuilds(string script, string? path) =>
        session.OpenDocument(script) is { State.Exists: true } document
            ? LostBuilds(document.Working, BuildsOf(session.ShipBuildFileOf(document.Working)), BuildsOf(path))
            : [];

    /// <summary>The builds of <see cref="LostBuilds(string, string?)"/>: each build that an <c>[NPC]</c> section of the
    /// script names, that the old file has, and that the new file and the global ship build files do not have.</summary>
    /// <param name="script">The sections of the script.</param>
    /// <param name="before">The builds of the file that the line names before the change.</param>
    /// <param name="after">The builds of the file that the line names after the change.</param>
    internal IReadOnlyList<string> LostBuilds(IEnumerable<Section> script, IReadOnlySet<string> before, IReadOnlySet<string> after)
    {
        var global = NpcShipFiles.GlobalFiles.Select(session.DataPath).SelectMany(BuildsOf)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return script.Where(s => MissionIndex.Is(s.Name, "NPC")).Select(s => ItemIndex.First(s, "npc_ship_arch")?.Trim())
            .OfType<string>().Where(b => before.Contains(b) && !after.Contains(b) && !global.Contains(b))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The nicknames of the ship builds of a file of W, or none for a path without a file.</summary>
    /// <param name="path">An install path, or null for no file.</param>
    internal IReadOnlySet<string> BuildsOf(string? path) =>
        BuildNames(path != null && session.OpenDocument(path) is { State.Exists: true } document ? document.Working : null);

    /// <summary>The nicknames of the <c>[NPCShipArch]</c> sections of a file, or none for no file.</summary>
    internal static IReadOnlySet<string> BuildNames(IEnumerable<Section>? sections) =>
        (sections ?? []).Where(s => MissionIndex.Is(s.Name, "NPCShipArch")).Select(s => ItemIndex.First(s, "nickname")?.Trim())
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

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
