using System.IO;
using OpenLancer.Data;
using OpenLancer.Runtime;

namespace OpenLancer.Editor.Help;

/// <summary>The file of Help &gt; Read me first, or the reason why the item is off.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Reason">Why the file cannot open, or null when it exists.</param>
internal sealed record ReadmeFirstFile(string Path, string? Reason);

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

    /// <summary>The reason why Read me first is off in a build without <see cref="ReadmeFirst"/>, for example a build
    /// from the repository.</summary>
    public const string NoReadmeFirst =
        "This build has no README-FIRST.txt next to the editor. The file comes with the release zip. Use Documentation " +
        "for the help pages.";

    /// <summary>The folder of Open log folder: the log files of the editor and of the game of Play.</summary>
    public static string LogFolder => RunLogs.Folder;

    /// <summary>The start page of the help of a build: the README of its release tag, which tells how to use the editor
    /// and links each help page. The <c>docs</c> folder has no start page. A development build gets the README of its
    /// commit, and a build without a commit gets master (see <see cref="BuildVersion.DocsRef"/>).</summary>
    public static string Documentation(BuildVersion build) => $"{Repository}/blob/{build.DocsRef}/README.md";

    /// <summary>The help page Getting started of a build, <c>docs/GETTING_STARTED.md</c>, at the same commit or tag as
    /// <see cref="Documentation"/>. The item and the page have the same name.</summary>
    public static string GettingStarted(BuildVersion build) =>
        $"{Repository}/blob/{build.DocsRef}/docs/GETTING_STARTED.md";

    /// <summary>The file of Read me first: <see cref="ReadmeFirst"/> in the folder of the editor.</summary>
    /// <param name="editorFolder">The folder of the editor program, for example <c>AppContext.BaseDirectory</c>.</param>
    public static ReadmeFirstFile ReadmeFirstOf(string editorFolder)
    {
        var path = Path.GetFullPath(Path.Combine(editorFolder, ReadmeFirst));
        return new ReadmeFirstFile(path, File.Exists(path) ? null : NoReadmeFirst);
    }
}
