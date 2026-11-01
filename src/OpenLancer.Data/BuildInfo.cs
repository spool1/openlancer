using System;
using System.Linq;
using System.Reflection;

namespace OpenLancer.Data;

/// <summary>
/// The version facts of this build. Each part of OpenLancer that shows or writes the version reads it here, for
/// example the publish history and the agent server.
/// </summary>
public static class BuildInfo
{
    /// <summary>The key of the assembly metadata that holds the engine commit. The build of OpenLancer.Data writes it
    /// from the property <c>OpenLancerEngineCommit</c>, or else from the engine submodule.</summary>
    internal const string EngineCommitKey = "EngineCommit";

    /// <summary>All the version facts of this build in one value, for a view that shows several of them, for example
    /// Help &gt; About.</summary>
    public static BuildVersion Current { get; } = Read(typeof(BuildInfo).Assembly);

    /// <summary>The full informational version, for example <c>1.0.0-dev+&lt;commit&gt;</c>. It has the commit when the
    /// build has it.</summary>
    public static string Informational => Current.Informational;

    /// <summary>The version without the commit, for example <c>1.0.0-dev</c> or <c>1.0.0-beta.1</c>.</summary>
    public static string Version => Current.Version;

    /// <summary>The OpenLancer commit of the build, or null when the build has none.</summary>
    public static string? Commit => Current.Commit;

    /// <summary>The engine commit of the build, or null when the build has none.</summary>
    public static string? EngineCommit => Current.EngineCommit;

    /// <summary>True for a development build: the suffix of the version starts with "dev".</summary>
    public static bool IsDevelopment => Current.IsDevelopment;

    /// <summary>The git reference of the help pages of this build: the release tag, else the commit, else master.</summary>
    public static string DocsRef => Current.DocsRef;

    /// <summary>
    /// The facts of an informational version and an engine commit. The commit is the text after the first "+". The
    /// suffix is the text after the first "-" of the version.
    /// </summary>
    internal static BuildVersion Parse(string informational, string? engineCommit)
    {
        var plus = informational.IndexOf('+');
        var version = plus < 0 ? informational : informational[..plus];
        var commit = plus < 0 || plus == informational.Length - 1 ? null : informational[(plus + 1)..];
        var dash = version.IndexOf('-');
        var development = dash >= 0 && version[(dash + 1)..].StartsWith("dev", StringComparison.Ordinal);
        return new BuildVersion(informational, version, commit, string.IsNullOrEmpty(engineCommit) ? null : engineCommit,
            development);
    }

    private static BuildVersion Read(Assembly assembly) => Parse(
        // The SDK always writes this attribute. Without it, the facts are those of a development build without a commit.
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev",
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == EngineCommitKey)?.Value);
}

/// <summary>The version facts of one build. <see cref="BuildInfo"/> gives the facts of this build.</summary>
public sealed record BuildVersion(string Informational, string Version, string? Commit, string? EngineCommit,
    bool IsDevelopment)
{
    /// <summary>The git reference of the help pages: the tag <c>v&lt;version&gt;</c> of a build that is not a
    /// development build, else the commit, else master.</summary>
    public string DocsRef => !IsDevelopment ? "v" + Version : Commit ?? "master";
}
