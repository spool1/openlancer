using System.IO;
using System.Runtime.CompilerServices;

namespace OpenLancer.Tests;

/// <summary>
/// The files of the repository for the tests that read them, for example ROADMAP.md. The build output can be outside
/// the repository (<c>--artifacts-path</c>), so the helper finds the root from its own source file.
/// </summary>
internal static class RepoFiles
{
    /// <summary>The root folder of the repository.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>The full path of a file or folder of the repository, from its parts below the root.</summary>
    public static string Of(params string[] parts) => Path.Combine([Root, .. parts]);

    private static string FindRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}
