using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The places of the new files of a mod and the values that name them (decision O2). The names are invented.
/// </summary>
public sealed class ContentPathsTests
{
    /// <summary>The folder of a mod, and its folder of the new cutscenes (decision D67).</summary>
    [Fact]
    public void TheNewFilesOfAModGoIntoItsFolder()
    {
        Assert.Equal(@"DATA\MODS\act", ContentPaths.OwnFolder("act"));
        Assert.Equal(@"DATA\MODS\act\CUTSCENES", ContentPaths.NewCutsceneFolder("act"));
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
}
