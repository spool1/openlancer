namespace OpenLancer.Data.Games;

/// <summary>
/// The texts of the game link (milestone M11a) for the user, in one place: the errors of a folder without a game, the
/// findings of a check, and the texts of the write guard. They are strict Simplified Technical English.
/// </summary>
public static class GameTexts
{
    // Root finding (section 6.1 of the M11a design).
    public const string NotAGameFolder =
        "This folder is not a Freelancer game folder. The file EXE\\freelancer.ini is not in it.";

    public const string LibrelancerData = "This folder contains Librelancer data, not a Freelancer game.";

    public const string Disc = "This folder contains the Freelancer installation disc. Install the game from the disc. " +
                               "Then select the folder of the installed game.";

    public static string TwoGames(string names) =>
        $"This folder contains more than one game: {names}. Select one of these folders.";

    // The checks of the root (section 6.2).
    public const string FolderMissing = "The folder does not exist.";

    public static string FolderNotReadable(string reason) => $"OpenLancer cannot read the folder: {reason}";

    public static string IniNotReadable(string reason) => $"OpenLancer cannot read EXE\\freelancer.ini: {reason}";

    public const string NoDataPath = "The [Freelancer] section has no data path line.";

    public static string DataPathMissing(string path) =>
        $"The data path in EXE\\freelancer.ini names {path}. That folder does not exist.";

    public static string NotComplete(string path) => $"The game data is not complete. The file {path} is missing.";

    // The findings (section 7.5).

    /// <param name="reference">The name of the INI files of the file list (<see cref="GameDetector.DataName"/>), for
    /// example "retail".</param>
    public static string ChangedData(int count, string reference) =>
        (count == 1 ? "1 INI file is" : $"{count} INI files are") + $" different from the {reference} files. " +
        "OpenLancer uses the files in the game folder. Your mods can depend on these changes.";

    public const string JflpChangesFiles = "JFLP changes EXE\\dacom.ini and can change more files.";

    public const string UnknownVersion = "OpenLancer does not know this version of the game files. You can use the " +
                                         "folder. Some checks can be wrong for this version.";

    public static string CodeFilesMissing(string names) =>
        $"Some code files are missing: {names}. OpenLancer cannot identify the version.";

    public const string OnePointZeroCodeChanged =
        "The code files are different from the files of Freelancer 1.0. Their file version is 1.0.";

    public static string OnePointOneByFileVersion(string version) =>
        $"The file version of Common.dll is {version}. This version is after 1.0, so OpenLancer shows 1.1. The code " +
        "files do not have the hashes of the English patch 1.1.";

    /// <summary>The note of Freelancer 1.1. It tells why the check compares the INI files with the retail files, and
    /// does not name 1.0 as the version of the game.</summary>
    /// <param name="changed">True when INI files are changed. <see cref="ChangedData"/> then tells how many.</param>
    public static string OnePointOneData(bool changed) =>
        "Patch 1.1 changes only code files. So 1.0 and 1.1 have the same INI files." +
        (changed ? "" : " All INI files of this game are the same as the retail files.");

    public const string OnePointZeroTarget = "OpenLancer exports mods for this game as Freelancer 1.1 mods. Players on " +
                                             "1.0 cannot join servers that run 1.1.";

    public static string OtherHdeVersion(string version) =>
        $"OpenLancer checks mods against HD Edition 0.7.1 only. This folder contains HD Edition {version}.";

    public static string HdeVersionUnknown(string dll) => $"OpenLancer cannot find the HD Edition version in {dll}.";

    public static string HdeDllMissing(string dll) =>
        $"EXE\\freelancer.ini names {dll}. The EXE folder does not contain this file.";

    public const string JflpDllMissing = "EXE\\dacom.ini names jflp.dll. The EXE folder does not contain this file.";

    public static string FileNotReadable(string path, string reason) =>
        $"OpenLancer cannot read {path}: {reason} The check did not use this file.";

    public static string NotSupported(string product) =>
        $"OpenLancer does not support {product}. You can open its data in the editor. You cannot export mods for this game.";

    public const string GameDataOnly =
        "This folder has no Freelancer.exe. It contains game data only. You can make mods with it.";

    public const string NoCodeFiles = "This copy has no code files. So OpenLancer cannot tell 1.0 from 1.1.";

    public const string HdeNotCompared = "OpenLancer has no file list of this HD Edition version. OpenLancer did not " +
                                         "compare the data files.";

    /// <summary>The text after <see cref="ChangedData"/> for HD Edition (milestone M11).</summary>
    public const string HdeInstallerOptions = "The installer options of HD Edition change some of these files.";

    public static string Plugins(string names) => $"Plugins in dacom.ini: {names}.";

    // The source build and the play copies (milestone M11).
    public static string SourceBuildIniNotReadable(string reason) => $"OpenLancer cannot read bin\\freelancer.ini: {reason}";

    public static string SourceBuildDataPath(string path) =>
        $"The source build reads its data from {path}. OpenLancer can play a source build only with data path = ..\\DATA.";

    /// <summary>Why a source build is not the base of a profile, and what to do. New profile shows it as its tooltip,
    /// and the first-run screen and Relink show it as the error.</summary>
    public const string SourceBuildNoBase = "A source build is a play target only. It cannot be the base of a profile. " +
                                            "Use a retail game folder as the base of the profile. Then play the profile " +
                                            "in this build with Play > Play in source build.";

    /// <summary>The tooltip of a source build in the Game link window. The README table "Supported games" names the
    /// supported source build, so the tooltip names no project.</summary>
    public const string SourceBuildTooltip = "A game that you built from source code, with bin\\freelancer.exe and its " +
                                             "DATA folder. The README table Supported games names the supported " +
                                             "source build.";

    /// <summary>The note of a source build with retail INI files.</summary>
    /// <param name="build">The name of the build of the source build, for example "Freelancer 1.1".</param>
    /// <param name="changed">True when INI files are changed. <see cref="ChangedData"/> then tells how many.</param>
    public static string SourceBuildData(string build, bool changed) =>
        (changed ? "" : "The DATA folder has the retail INI files without changes. ") +
        $"OpenLancer plays this source build as {build}.";

    public const string SourceBuildUnknownData = "OpenLancer does not know the INI files of the DATA folder of this " +
                                                 "source build. Play in source build needs the retail INI files.";

    public static string PlayCopy(string source) =>
        $"This folder is an OpenLancer play copy of {source}. Link the game folder {source}.";

    public const string PlayCopyUnknownSource =
        "This folder is an OpenLancer play copy. Link the game folder that the play copy comes from.";

    public static string VirtualStore(int count, string path) =>
        $"Windows keeps changed copies of {count} game files in {path}. The game uses these copies. OpenLancer uses the " +
        "files in the game folder.";

    // The fields and the write guard.
    public const string ProfileNameRule = "Use lowercase letters, digits, '-' or '_'. Start with a letter.";

    public const string ProfileNameExists = "A profile with this name exists. Type another name.";

    public const string WorkspaceInGame =
        "Select a folder outside the game folder. OpenLancer does not write into the game folder.";

    public const string ActiveModInGame = "The active mod is in the game folder. OpenLancer does not write into the " +
                                          "game folder. Move the mod folder to a folder outside the game folder.";

    public const string ProfileInGame = "The profile file is in the game folder. OpenLancer does not write into the " +
                                        "game folder. Move the profile file to a folder outside the game folder.";

    public const string PlayRefused =
        "Play did not start. The profile file is in the game folder. Play writes its files next to the profile file.";

    public const string ModsFolderInGame =
        "The mods folder is in the game folder. OpenLancer does not write into the game folder.";

    /// <param name="modInGame">True when the mod itself is in the game folder. Else the active mod or the profile file
    /// is in it, and the session completes no save of any mod.</param>
    public static string SaveNotRecovered(string id, bool modInGame) =>
        $"A save of mod '{id}' is not complete. " +
        (modInGame ? "The mod is in the game folder" : "The active mod or the profile file is in the game folder") +
        ", so OpenLancer did not complete the save.";
}
