using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The places of the new files of a mod and the values that name them (decision O2): the folder of the mod, the place
/// of each kind of new file, the value relative to the data folder, the value relative to the folder of
/// <c>universe.ini</c>, and the test of a taken path. The names are invented.
/// </summary>
public sealed class ContentPathsTests
{
    [Fact]
    public void TheFolderOfAModHoldsItsOwnFilesInAnyLetterCase()
    {
        Assert.Equal(@"DATA\MODS\act", ContentPaths.OwnFolder("act"));
        Assert.True(ContentPaths.IsOwnFile(@"DATA\MODS\act\SYSTEMS\Ol01\Ol01.ini", "act"));
        Assert.True(ContentPaths.IsOwnFile(@"data/mods/ACT/x.ini", "act"));
        Assert.True(ContentPaths.IsOwnFile(@"DATA\MODS\act\CUTSCENES\shot.thn", "act"));
        // The folder itself, the folder of another mod, a mod id that starts with the id, and a path that leaves it.
        Assert.False(ContentPaths.IsOwnFile(@"DATA\MODS\act", "act"));
        Assert.False(ContentPaths.IsOwnFile(@"DATA\MODS\other\x.ini", "act"));
        Assert.False(ContentPaths.IsOwnFile(@"DATA\MODS\actor\x.ini", "act"));
        Assert.False(ContentPaths.IsOwnFile(@"DATA\MODS\act\..\other\x.ini", "act"));
        Assert.False(ContentPaths.IsOwnFile(@"DATA\UNIVERSE\SYSTEMS\Li01\li01.ini", "act"));
    }

    /// <summary>Each kind of new file has one place in the folder of the mod (decision O2). A cutscene keeps its folder
    /// (decision D67).</summary>
    [Fact]
    public void EachKindOfNewFileHasItsPlaceInTheFolderOfTheMod()
    {
        Assert.Equal(@"DATA\MODS\act\CUTSCENES", ContentPaths.NewCutsceneFolder("act"));
        Assert.Equal(@"DATA\MODS\act\SYSTEMS\Ol01\Ol01.ini", ContentPaths.NewSystemFile("act", "Ol01"));
        Assert.Equal(@"DATA\MODS\act\BASES\Ol01_01_Base.ini", ContentPaths.NewBaseFile("act", "Ol01_01_Base"));
        Assert.Equal(@"DATA\MODS\act\BASES\ROOMS\Ol01_01_Base_bar.ini",
            ContentPaths.NewRoomFile("act", "Ol01_01_Base", "bar"));
        Assert.Equal(@"DATA\MODS\act\SOLAR\NEBULA\ol01_cloud.ini", ContentPaths.NewVolumeFile("act", "Nebula", "ol01_cloud"));
        Assert.Equal(@"DATA\MODS\act\SOLAR\ASTEROIDS\ol01_rocks.ini",
            ContentPaths.NewVolumeFile("act", "asteroids", "ol01_rocks"));
        Assert.Throws<ArgumentException>(() => ContentPaths.NewVolumeFile("act", "Zone", "x"));
        Assert.Equal(@"DATA\MODS\act\AUDIO\voices.ini", ContentPaths.VoicesFile("act"));
        Assert.Equal(@"DATA\MODS\act\AUDIO\sounds.ini", ContentPaths.SoundsFile("act"));
        // Each place is an own file of the mod, so the editor edits it.
        foreach (var path in new[]
                 {
                     ContentPaths.NewSystemFile("act", "Ol01"), ContentPaths.NewBaseFile("act", "b"),
                     ContentPaths.NewRoomFile("act", "b", "r"), ContentPaths.NewVolumeFile("act", "Nebula", "n"),
                     ContentPaths.VoicesFile("act"), ContentPaths.SoundsFile("act"),
                 })
            Assert.True(EditDocument.EditsWholeFile(path, "act"), path);
    }

    /// <summary>A value relative to the data folder. The cutscene picker, the list of the cutscenes and the mission
    /// lines used the same rule before, so their values do not change.</summary>
    [Fact]
    public void ADataValueIsThePathBelowTheDataFolder()
    {
        Assert.Equal(@"MODS\act\BASES\Ol01_01_Base.ini", ContentPaths.DataValue(@"DATA\MODS\act\BASES\Ol01_01_Base.ini"));
        Assert.Equal(@"MODS\m8proof\CUTSCENES\bar_shot.thn", ContentPaths.DataValue(@"data/MODS//m8proof/CUTSCENES/bar_shot.thn"));
        Assert.Equal(@"SOLAR\NEBULA\cloud.ini", ContentPaths.DataValue(@"Data\SOLAR\NEBULA\cloud.ini"));
        Assert.Null(ContentPaths.DataValue(@"EXE\freelancer.ini"));
        Assert.Null(ContentPaths.DataValue("DATA"));
        Assert.Null(ContentPaths.DataValue(@"DATAX\x.ini"));
        Assert.Null(ContentPaths.DataValue(@"..\DATA\x.ini"));
        // The value gives the path back.
        Assert.Equal(@"DATA\MODS\act\BASES\b.ini", ThnReferences.PathOf(ContentPaths.DataValue(@"DATA\MODS\act\BASES\b.ini")!));
        // A mission line and the UI show the same value, and a path outside the data folder as it is.
        Assert.Equal(@"MISSIONS\M01A\m01a.ini", MissionFiles.Display(@"DATA\MISSIONS\M01A\m01a.ini"));
        Assert.Equal(@"EXE\freelancer.ini", MissionFiles.Display(@"EXE\freelancer.ini"));
    }

    /// <summary>The value of a <c>[system] file</c> line is relative to the folder of <c>universe.ini</c>. The map of the
    /// system files resolves a new <c>[system]</c> with the value to the same path.</summary>
    [Fact]
    public void AUniverseValueResolvesToItsPathThroughTheSystemFiles()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var path = ContentPaths.NewSystemFile("act", "Ol01");

        var value = ContentPaths.UniverseValue(session, path);
        Assert.Equal(@"..\MODS\act\SYSTEMS\Ol01\Ol01.ini", value);
        Assert.Equal(@"systems\Li01\li01.ini", ContentPaths.UniverseValue(session, @"data\universe\systems\Li01\li01.ini"));
        Assert.Equal(@"..\..\EXE\x.ini", ContentPaths.UniverseValue(session, @"EXE\x.ini"));
        Assert.Null(ContentPaths.UniverseValue(session, @"C:\x.ini"));

        session.Execute(EditCommand.AddSection(ListWorkspace.UniversePath, "system",
        [
            new EntryView("nickname", [new StringValue("Ol01")]),
            new EntryView("file", [new StringValue(value!)]),
        ]));
        Assert.Equal(path, session.SystemPath("Ol01"), ignoreCase: true);
        Assert.True(session.IsSystemFile(path));
    }

    /// <summary>Without a <c>universe.ini</c> in <c>freelancer.ini</c>, a <c>[system] file</c> value has no
    /// folder.</summary>
    [Fact]
    public void AUniverseValueNeedsAUniverseFile()
    {
        using var ws = EditSessionTests.Workspace();
        var session = ws.Session("act");

        Assert.Null(ContentPaths.UniverseValue(session, ContentPaths.NewSystemFile("act", "Ol01")));
    }

    /// <summary>A path is taken when W or the lower layer has a file at it, in any letter case. A new file of W takes
    /// its path, and its undo makes the path free again.</summary>
    [Fact]
    public void APathIsTakenWhenWOrTheLowerLayerHasItInAnyLetterCase()
    {
        using var ws = ListWorkspace.Create(
            lowerFiles: new() { [@"files\DATA\MODS\act\lower.ini"] = "[Thing]\nnickname = a\n" },
            activeFiles: new() { [@"files\DATA\MODS\act\saved.ini"] = "[Thing]\nnickname = b\n" });
        var session = ws.Session("act");
        const string newPath = @"DATA\MODS\act\new.ini";

        Assert.True(ContentPaths.Taken(session, ListWorkspace.Li01Path));
        Assert.True(ContentPaths.Taken(session, @"data\universe\SYSTEMS\li01\LI01.INI"));
        Assert.True(ContentPaths.Taken(session, @"DATA\MODS\ACT\LOWER.ini"));
        Assert.True(ContentPaths.Taken(session, @"DATA\MODS\act\saved.ini"));
        Assert.False(ContentPaths.Taken(session, newPath));
        Assert.False(ContentPaths.Taken(session, @"C:\x.ini"));

        session.Execute(EditCommand.NewFile(newPath, []));
        Assert.True(ContentPaths.Taken(session, newPath.ToUpperInvariant()));
        session.Undo();
        Assert.False(ContentPaths.Taken(session, newPath));
    }

    /// <summary>The sections of an INI text, for <see cref="EditCommand.NewFile"/>.</summary>
    internal static IReadOnlyList<SectionEntries> Sections(string text) =>
        Data.Ini.IniDocument.Parse("new.ini", System.Text.Encoding.ASCII.GetBytes(text)).Sections
            .Select(s => new SectionEntries(s.Name, s.Select(e => new EntryView(e.Name, e.ToArray())).ToList()))
            .ToList();
}
