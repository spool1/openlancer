using OpenLancer.Data;
using OpenLancer.Data.Games;

namespace OpenLancer.Editor.Help;

/// <summary>
/// The short text that Help &gt; Report a problem copies for the first field of the bug report form. It has the
/// version, the commit, the linked game and the Windows version. It has no path, no user name and no content of a game
/// file.
/// </summary>
internal static class ReportHeader
{
    /// <summary>The game line when the open profile has no linked game, or no profile is open.</summary>
    public const string NoGame = "no linked game";

    /// <summary>The game line when the open profile has a linked game without a report. The check of the game runs or
    /// did not finish, so the editor does not know the kind and the version of the game.</summary>
    public const string CheckPending = "check not finished";

    /// <summary>The header text.</summary>
    /// <param name="build">The version facts of the editor.</param>
    /// <param name="game">The report of the game of the open profile, or null.</param>
    /// <param name="checkPending">True when the open profile has a linked game without a report, because its check
    /// runs or did not finish.</param>
    /// <param name="system">The name and the version of the operating system, for example
    /// <c>RuntimeInformation.OSDescription</c>.</param>
    public static string Text(BuildVersion build, GameReport? game, bool checkPending, string system) =>
        $"OpenLancer {build.Version}\n" +
        $"Commit: {build.Commit ?? AboutDialog.NoCommit}\n" +
        $"Game: {GameName(game, checkPending)}\n" +
        $"Windows: {system}";

    /// <summary>The kind and the version of the game, for example "HD Edition 0.7.1". The short name never holds the
    /// folder of the game, also not for a source build (see <see cref="GameReport.ShortName"/>).</summary>
    private static string GameName(GameReport? game, bool checkPending) =>
        game is { IsGame: true } ? game.ShortName
        : game == null && checkPending ? CheckPending
        : NoGame;
}
