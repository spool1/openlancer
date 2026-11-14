using System.Globalization;

namespace OpenLancer.Data.PlayCopies;

/// <summary>The texts of the play copies for the user, in one place (section 10 of the M11 design). They are strict
/// Simplified Technical English.</summary>
public static class PlayCopyTexts
{
    public static string Make(string game, string folder) => $"OpenLancer makes the play copy of {game} in {folder}.";

    public static string Done(int links, int copies, string size) =>
        $"The play copy is ready: {links} links, {copies} copies, {size} on disk.";

    public static string LinkedFileChanged(string path, string original) =>
        $"{path} changed after OpenLancer made the play copy. It is a hard link to {original}, so your game has the " +
        "change too. If you did not change your game, another program changed it through the play copy. Repair your " +
        "game, for example with its installer.";

    public static string LinkReplaced(string path) =>
        $"A program replaced {path} in the play copy. Your game did not change. OpenLancer makes the link again.";

    public static string LinkRemoved(string path) =>
        $"A program removed {path} from the play copy. Your game did not change. OpenLancer makes the link again.";

    public static string SourceReplaced(string path) =>
        $"The game has a new version of {path}. OpenLancer links the play copy to the new file.";

    public static string InstalledFileChanged(string path, string kept) =>
        $"{path} changed in the play copy after the last install. OpenLancer kept that version in {kept} and writes " +
        "the file of the mods again.";

    public static string NewSourceFile(string path) => $"The game has a new file {path}. The play copy does not have it.";

    public static string SourceFileGone(string path) =>
        $"The game does not have {path} any more. The play copy still has it.";

    public static string Locked(string folder) =>
        $"Another OpenLancer window uses the play copy {folder}. Close it, then try again.";

    public static string NotAPlayCopy(string folder) =>
        $"The folder {folder} is not an OpenLancer play copy. OpenLancer removes and changes only its own play copies.";

    public static string OtherSource(string folder, string source) =>
        $"The folder {folder} is a play copy of {source}. Pick another folder for this game.";

    public static string FolderInUse(string folder) =>
        $"The folder {folder} exists and is not an OpenLancer play copy. Pick an empty folder.";

    public static string OverlapsGame(string folder, string game) =>
        $"The play copy folder {folder} overlaps the game folder {game}. OpenLancer does not write into the game " +
        "folder. Pick another folder.";

    public static string InGameFolder(string folder, string game) =>
        $"The play copy folder {folder} is in the game folder {game}. OpenLancer does not write into a game folder. Pick " +
        "another folder.";

    public static string OverlapsProtected(string folder, string other) =>
        $"The play copy folder {folder} overlaps {other}. Pick a folder outside the workspace, the mods and the " +
        "OpenLancer repo.";

    public static string IsALink(string folder) =>
        $"{folder} is a link. A play copy must be a plain folder.";

    public static string ProbeFailed(string drive, string reason) =>
        "OpenLancer cannot make the play copy next to the game. Select a folder on drive " +
        $"{drive}. OpenLancer makes hard links there, so the folder must be on the same drive as the game. ({reason})";

    /// <summary>The question after the probe failed in a folder that the user selected, on any drive.</summary>
    public static string FullCopyQuestion(string size) =>
        $"OpenLancer cannot make hard links in this folder. A play copy there needs a full copy of the game: {size}. " +
        "Make a full copy?";

    public static string ReadMe(string source) =>
        $"This folder is an OpenLancer play copy of {source}. Many files in DATA and DLLS are hard links to the files " +
        "of your game. Do not change, extract or install files in this folder with other programs. A change of a " +
        "linked file also changes your game. OpenLancer removes this folder when you click Remove play copy in File > " +
        "Game link.";

    public static string ReadMeFull(string source) =>
        $"This folder is an OpenLancer play copy of {source}. It is a full copy, so a change in this folder does not " +
        "change your game. OpenLancer removes this folder when you click Remove play copy in File > Game link.";

    /// <summary>A size in bytes for the user, for example "4.3 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0.0} KB"),
        _ => $"{bytes} bytes",
    };
}
