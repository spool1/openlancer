using System.IO;
using OpenLancer.Data;
using OpenLancer.Runtime;

namespace OpenLancer.Editor.Help;

/// <summary>The file of Help &gt; Getting started, or the reason why the item is off.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Reason">Why the file cannot open, or null when it exists.</param>
internal sealed record GettingStartedFile(string Path, string? Reason);

/// <summary>
/// The targets of the items of the Help menu. They have no ImGui call and no process start, so a test can check each
/// target.
/// </summary>
internal static class HelpLinks
{
    /// <summary>The web page of the OpenLancer repository.</summary>
    public const string Repository = "https://github.com/spool1/openlancer";

    /// <summary>The invite link of the OpenLancer Discord server, as in the README.</summary>
    public const string Discord = "https://discord.gg/BUh3nucfmP";

    /// <summary>The issue form for a bug report (<c>.github/ISSUE_TEMPLATE/bug_report.yml</c>).</summary>
    public const string ReportForm = Repository + "/issues/new?template=bug_report.yml";

    /// <summary>The text file that the release zip has next to the editor.</summary>
    public const string ReadmeFirst = "README-FIRST.txt";

    /// <summary>The reason why Getting started is off in a build without <see cref="ReadmeFirst"/>, for example a build
    /// from the repository.</summary>
    public const string NoReadmeFirst =
        "This build has no README-FIRST.txt next to the editor. The file comes with the release zip. Use Documentation " +
        "for the help pages.";

    /// <summary>The folder of Open log folder: the log files of the editor and of the game of Play.</summary>
    public static string LogFolder => RunLogs.Folder;

    /// <summary>The help pages of a build: the <c>docs</c> folder of its release tag. A development build gets the folder
    /// of its commit, and a build without a commit gets master (see <see cref="BuildVersion.DocsRef"/>).</summary>
    public static string Documentation(BuildVersion build) => $"{Repository}/tree/{build.DocsRef}/docs";

    /// <summary>The file of Getting started: <see cref="ReadmeFirst"/> in the folder of the editor.</summary>
    /// <param name="editorFolder">The folder of the editor program, for example <c>AppContext.BaseDirectory</c>.</param>
    public static GettingStartedFile GettingStarted(string editorFolder)
    {
        var path = Path.GetFullPath(Path.Combine(editorFolder, ReadmeFirst));
        return new GettingStartedFile(path, File.Exists(path) ? null : NoReadmeFirst);
    }
}
