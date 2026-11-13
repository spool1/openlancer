using System;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>
/// The rule of install paths in requests and in the session (decision D763 of the MCP1 design). An install path is
/// relative to the game folder, for example <c>DATA\SHIPS\loadouts.ini</c>. <see cref="VfsPath.Normalize"/> alone is
/// not a boundary: it keeps a drive, and a file read joins a rooted path as it is. So each path of a request goes
/// through <see cref="Parse"/> before other code sees it, and the session refuses the same paths.
/// </summary>
public static class InstallPaths
{
    /// <summary>The detail code of a path that is not an install path.</summary>
    public const string NotInstallPath = "NotInstallPath";

    /// <summary>The normal form of an install path (see <see cref="VfsPath.Normalize"/>). The path can use <c>\</c> or
    /// <c>/</c>.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the path is empty, holds a control
    /// character or a <c>:</c> (a drive or a stream name), starts with <c>\</c> or <c>/</c>, goes above the game
    /// folder, or has a part that starts or ends with a space or a dot or names a Windows device.</exception>
    public static string Parse(string? path) =>
        Problem(path) is { } problem
            ? throw new EditException(new EditError(EditErrorKind.InvalidArgument, NotInstallPath, problem))
            : VfsPath.Normalize(path!)!;

    /// <summary>The normal form of an install path, or null when <see cref="Parse"/> refuses the path.</summary>
    public static string? TryParse(string? path) => Problem(path) == null ? VfsPath.Normalize(path!) : null;

    /// <summary>Why a path is not an install path, as a message, or null for an install path.</summary>
    public static string? Problem(string? path)
    {
        string Refuse(string reason) =>
            $"The path '{path}' is not an install path. {reason} Use a path such as DATA\\SHIPS\\loadouts.ini.";

        if (string.IsNullOrWhiteSpace(path))
            return "The path is empty. Use a path such as DATA\\SHIPS\\loadouts.ini.";
        foreach (var c in path)
        {
            if (char.IsControl(c))
                return Refuse("It holds a control character.");
        }
        if (path.Contains(':'))
            return Refuse("An install path cannot hold a drive or a ':'.");
        if (path[0] is '\\' or '/')
            return Refuse("An install path is relative to the game folder, so it cannot start with '\\' or '/'.");
        var normal = VfsPath.Normalize(path);
        if (normal == null)
            return Refuse("It goes above the game folder.");
        if (normal.Length == 0)
            return Refuse("It names no file.");
        // Windows removes a space or a dot at the end of a part when it opens a file. So such a part names the file of
        // another path, and two keys of the session could save one file. No path of the game data has a part that
        // starts with a space or a dot.
        foreach (var part in normal.Split('\\'))
        {
            if (part[0] is ' ' or '.' || part[^1] is ' ' or '.')
                return Refuse($"The part '{part}' starts or ends with a space or a dot.");
            if (IsDeviceName(part))
                return Refuse($"The part '{part}' is the name of a Windows device.");
        }
        return null;
    }

    /// <summary>
    /// True for a part that Windows opens as a device, not as a file: <c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>,
    /// <c>COM0</c> to <c>COM9</c>, <c>LPT0</c> to <c>LPT9</c>, <c>CONIN$</c> and <c>CONOUT$</c>, in any letter case and
    /// also with an extension, for example <c>nul.ini</c>. A write to such a path writes to the device. No file of the
    /// game data can have such a name.
    /// </summary>
    internal static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
               stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                    stem.StartsWith("LPT", StringComparison.Ordinal)) &&
               (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
    }
}
