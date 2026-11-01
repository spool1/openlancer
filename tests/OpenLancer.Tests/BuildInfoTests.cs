using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Publishing;
using OpenLancer.Mcp;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The version facts of the build: the parse of the informational version, the help page reference and the
/// engine commit that the build writes.</summary>
public sealed class BuildInfoTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void ADevelopmentBuildWithACommitGivesItsCommitAsTheHelpReference()
    {
        var facts = BuildInfo.Parse($"1.0.0-dev+{Hash}", null);

        Assert.Equal($"1.0.0-dev+{Hash}", facts.Informational);
        Assert.Equal("1.0.0-dev", facts.Version);
        Assert.Equal(Hash, facts.Commit);
        Assert.True(facts.IsDevelopment);
        Assert.Null(facts.EngineCommit);
        Assert.Equal(Hash, facts.DocsRef);
    }

    [Fact]
    public void APreReleaseGivesItsTagAsTheHelpReference()
    {
        var facts = BuildInfo.Parse("1.0.0-beta.1+abc1234", Hash);

        Assert.Equal("1.0.0-beta.1+abc1234", facts.Informational);
        Assert.Equal("1.0.0-beta.1", facts.Version);
        Assert.Equal("abc1234", facts.Commit);
        Assert.False(facts.IsDevelopment);
        Assert.Equal(Hash, facts.EngineCommit);
        Assert.Equal("v1.0.0-beta.1", facts.DocsRef);
        Assert.False(BuildInfo.Parse("1.0.0-rc.1+abc1234", null).IsDevelopment);
    }

    [Fact]
    public void AFinalVersionWithoutACommitGivesItsTag()
    {
        var facts = BuildInfo.Parse("1.0.0", "");

        Assert.Equal("1.0.0", facts.Informational);
        Assert.Equal("1.0.0", facts.Version);
        Assert.Null(facts.Commit);
        Assert.Null(facts.EngineCommit);
        Assert.False(facts.IsDevelopment);
        Assert.Equal("v1.0.0", facts.DocsRef);
    }

    [Fact]
    public void ADevelopmentBuildWithoutACommitGivesMaster()
    {
        // For example a local Release build without the release script: Source Link is off, so it has no commit.
        Assert.Equal("master", BuildInfo.Parse("1.0.0-dev", null).DocsRef);
        Assert.Equal("master", BuildInfo.Parse("1.0.0-dev+", null).DocsRef);
        Assert.Null(BuildInfo.Parse("1.0.0-dev+", null).Commit);
    }

    [Fact]
    public void TheBuildHasTheVersionOfTheBuildProps()
    {
        var props = XDocument.Load(RepoFiles.Of("src", "Directory.Build.props"));
        var prefix = props.Root!.Element("PropertyGroup")!.Element("VersionPrefix")!.Value;
        var suffix = props.Root.Element("PropertyGroup")!.Element("VersionSuffix")!.Value;

        Assert.Equal(suffix.Length == 0 ? prefix : $"{prefix}-{suffix}", BuildInfo.Version);
        Assert.StartsWith(BuildInfo.Version, BuildInfo.Informational);
        // The publish history and the agent server keep the commit.
        Assert.Equal(BuildInfo.Informational, PublishHistory.OpenLancerVersion);
        Assert.Equal(BuildInfo.Informational, McpBinding.Version);
    }

    [Fact]
    public void TheBuildHasTheEngineCommitOfTheSubmodule()
    {
        // The build reads only a checkout of its own, which has a .git entry.
        var engine = RepoFiles.Of("external", "Librelancer");
        var own = Path.Exists(Path.Combine(engine, ".git"));
        var head = own ? GitHead(engine) : null;
        Assert.SkipWhen(head == null, $"git found no checkout of the engine in {engine}.");

        Assert.Matches("^[0-9a-f]{40}$", BuildInfo.EngineCommit);
        Assert.Equal(head, BuildInfo.EngineCommit);
    }

    /// <summary>The HEAD commit of a git checkout, or null without git or without a checkout.</summary>
    private static string? GitHead(string folder)
    {
        var start = new ProcessStartInfo("git")
        {
            ArgumentList = { "-C", folder, "rev-parse", "HEAD" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            // No git on this computer.
            return null;
        }
    }
}
