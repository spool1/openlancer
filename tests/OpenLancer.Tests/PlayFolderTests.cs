using System.IO;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The folder of the files that a play writes (decision D41).</summary>
public sealed class PlayFolderTests
{
    [Fact]
    public void ThePlayFolderIsNextToTheProfileWithTheProfileName()
    {
        using var ws = new TestWorkspace();
        var profile = Path.Combine(ws.Root, "my profiles", "my.sample v2.json");
        var folder = Path.Combine(ws.Root, "my profiles", "my.sample v2.play");

        Assert.Equal(folder, PlayFolder.Of(profile));
        Assert.Equal(Path.Combine(folder, "lancer.ini"), PlayFolder.ConfigPath(profile));
        Assert.Equal(Path.Combine(folder, "saves"), PlayFolder.SavesPath(profile));
        // The start save and the test layer of a mission play (sections 5.4 and 5.6 of the M6c design).
        Assert.Equal(Path.Combine(folder, "start", "start.fl"), PlayFolder.StartSavePath(profile));
        Assert.Equal(Path.Combine(folder, "start", "layer"), PlayFolder.TestLayerPath(profile));
        // The images of the agent tool play_capture.
        Assert.Equal(Path.Combine(folder, "captures"), PlayFolder.CapturesPath(profile));
        Assert.Equal("capture-3.png", PlayFolder.CaptureName(3));
        Assert.Equal(folder, PlayFolder.Of(Path.Combine(ws.Root, "my profiles", "..", "my profiles", "my.sample v2.json")));
    }

    [Fact]
    public void CreateMakesTheFoldersAndKeepsTheirFiles()
    {
        using var ws = new TestWorkspace();
        var profile = Path.Combine(ws.Root, "profiles", "play test.json");

        PlayFolder.Create(profile);

        Assert.True(Directory.Exists(PlayFolder.Of(profile)));
        Assert.True(Directory.Exists(PlayFolder.SavesPath(profile)));
        var config = PlayFolder.ConfigPath(profile);
        var save = Path.Combine(PlayFolder.SavesPath(profile), "AutoSave.fl");
        File.WriteAllText(config, "[Settings]");
        File.WriteAllText(save, "[Player]");

        PlayFolder.Create(profile);

        Assert.Equal("[Settings]", File.ReadAllText(config));
        Assert.Equal("[Player]", File.ReadAllText(save));
    }
}
