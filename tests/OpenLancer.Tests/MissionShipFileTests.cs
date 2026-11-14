using System.IO;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The ship build files that the <c>npc_ship_file</c> line of a script can name, the rule of a new ship build file and
/// the command <see cref="NewShipBuildFile"/> (#18), on the synthetic profile of <see cref="MissionEdits"/>.
/// </summary>
public sealed class MissionShipFileTests
{
    private const string OwnFolder = @"DATA\MISSIONS\OL_TEST";
    private const string NewShips = @"DATA\MISSIONS\OL_TEST\ships_new.ini";

    /// <summary>A profile with more scripts and files around the test script: the ship build file of another script, a
    /// world file and an encounter file that scripts name, a path without a file that a script names, and a folder
    /// without a ship build file.</summary>
    private static MissionEdits CreateWithWorld() => Create(setup: ws =>
    {
        ws.WriteBase(@"DATA\MISSIONS\OL_OTHER\npcships.ini", ShipsText.Replace("ol_build_1", "ol_other_build"));
        ws.WriteBase(@"DATA\MISSIONS\OL_MIXED\ol_mixed.ini", "[Mission]\nnpc_ship_file = missions\\ol_mixed\\mixed.ini\n");
        ws.WriteBase(@"DATA\MISSIONS\OL_MIXED\mixed.ini", SharedText.Replace("ol_shared_build", "ol_mixed_build") +
                                                          "\n[Pilot]\nnickname = ol_pilot\n");
        ws.WriteBase(@"DATA\MISSIONS\OL_ENC\ol_enc.ini", "[Mission]\nnpc_ship_file = missions\\encounters\\ol_enc.ini\n");
        ws.WriteBase(@"DATA\MISSIONS\ENCOUNTERS\ol_enc.ini", SharedText.Replace("ol_shared_build", "ol_enc_build"));
        ws.WriteBase(@"DATA\MISSIONS\OL_GONE\ol_gone.ini", "[Mission]\nnpc_ship_file = missions\\ol_gone\\gone.ini\n");
    });

    [Fact]
    public void TheListHasTheShipBuildFilesThatExistInWAndNoWorldFile()
    {
        using var m = CreateWithWorld();

        var files = m.Missions.ShipBuildFiles();

        // The shared file, the file of another script and the own file of the test script, sorted by value. The mixed file
        // has a section that is not a ship build, the encounter file is world data, and gone.ini and the npcships.ini of
        // OL_MIXED, OL_ENC and OL_GONE do not exist.
        Assert.Equal([@"MISSIONS\npcships.ini", @"MISSIONS\OL_OTHER\npcships.ini", @"MISSIONS\OL_TEST\npcships.ini"],
            files.Select(f => f.Value));
        Assert.Equal([true, false, false], files.Select(f => f.Shared));
        Assert.Equal(SharedShips, files[0].Path);
        // A new file of W is in the list before a save.
        m.Apply(new NewShipBuildFile(TestScript, NewShips));
        Assert.Contains(@"MISSIONS\OL_TEST\ships_new.ini", m.Missions.ShipBuildFiles().Select(f => f.Value));
    }

    [Fact]
    public void ANewShipFileRefusesABadNameAPathInUseAndAScriptDirectlyInTheMissionsFolder()
    {
        using var m = Create(setup: ws => ws.WriteBase(@"DATA\MISSIONS\ol_flat.ini", "[Mission]\n"));
        string? Refusal(string name) => m.Missions.NewShipFileRefusal(Script, MissionEditor.NewShipFilePath(Script, name));

        Assert.Equal(@"DATA\MISSIONS\OL_TEST\ships_new.ini", MissionEditor.NewShipFilePath(Script, "ships_new"));
        Assert.Null(Refusal("ships_new"));
        Assert.Equal("Type a name for the ship build file.", Refusal(""));
        Assert.Equal(ThnEdits.BadName, Refusal("my ships"));
        Assert.Equal(ThnEdits.BadName, Refusal("a.b"));
        Assert.Equal(ThnEdits.BadName, Refusal(@"..\ships"));
        Assert.Equal(ThnEdits.LongName, Refusal(new string('s', ThnEdits.MaxNameLength + 1)));
        Assert.Null(Refusal(new string('s', ThnEdits.MaxNameLength)));
        Assert.Equal(ThnEdits.DeviceName("nul"), Refusal("nul"));
        // The own file of the script exists in the base data, in any letter case.
        Assert.Equal(@"A file exists at DATA\MISSIONS\OL_TEST\npcships.ini. Use another name.", Refusal("npcships"));
        Assert.Equal(@"A file exists at DATA\MISSIONS\OL_TEST\NPCSHIPS.ini. Use another name.", Refusal("NPCSHIPS"));
        Assert.Equal(@"A new ship build file goes into the folder of its script, with the extension .ini. Give a path " +
                     @"such as DATA\MISSIONS\OL_TEST\npcships.ini.",
            m.Missions.NewShipFileRefusal(Script, @"DATA\MISSIONS\OL_OTHER\ships.ini"));
        Assert.Equal(m.Missions.NewShipFileRefusal(Script, @"DATA\MISSIONS\OTHER\x.ini"),
            m.Missions.NewShipFileRefusal(Script, @"DATA\MISSIONS\OL_TEST\ships.txt"));
        Assert.Equal(@"DATA\MISSIONS\ol_flat.ini is directly in DATA\MISSIONS. Encounters and bases use the files of that " +
                     "folder too, so this script cannot get its own ship build file there. Pick a ship build file that " +
                     "exists.",
            m.Missions.NewShipFileRefusal(@"DATA\MISSIONS\ol_flat.ini", MissionEditor.NewShipFilePath(@"DATA\MISSIONS\ol_flat.ini", "ships")));
        // A new file of W that a save did not write yet is in use too.
        m.Apply(new NewShipBuildFile(TestScript, NewShips));
        Assert.Equal(@"A file exists at DATA\MISSIONS\OL_TEST\ships_new.ini. Use another name.", Refusal("ships_new"));
    }

    [Fact]
    public void TheDefaultNameIsTheFirstFreeName()
    {
        using var m = Create();

        Assert.Equal("npcships_2", m.Missions.FreeShipFileName(Script));
        m.Apply(new NewShipBuildFile(TestScript, MissionEditor.NewShipFilePath(Script, "npcships_2")));
        Assert.Equal("npcships_3", m.Missions.FreeShipFileName(Script));
        Assert.Equal(MissionEditor.DefaultShipFileName, m.Missions.FreeShipFileName(@"DATA\MISSIONS\OL_NEW\ol_new.ini"));
    }

    [Fact]
    public void ASessionWithoutAnActiveModRefusesANewShipFile()
    {
        using var m = Create();
        var session = m.Ws.Session(null);

        Assert.Equal("The editor cannot change missions now. There is no active mod. Select a mod to edit.",
            session.Missions.NewShipFileRefusal(Script, NewShips));
    }

    [Fact]
    public void TheCommandMakesAnEmptyFileAndTheLineInOneUndoStep()
    {
        using var m = Create();
        var before = m.State();

        var result = m.Apply(new NewShipBuildFile(TestScript, NewShips));

        var after = m.State();
        Assert.Equal([@"npc_ship_file = MISSIONS\OL_TEST\ships_new.ini"], m.Lines(Item("Mission", null)));
        var file = m.Session.OpenDocument(NewShips)!;
        Assert.True(file.State.Exists);
        Assert.True(file.IsWholeFile);
        Assert.Empty(file.Working);
        Assert.Equal([new ScriptRef(NewShips).ToString()], result.Adds);
        // The guard of the mission files covers the new file, and the builds of the old file do not move.
        Assert.True(m.Missions.IsMissionFile(NewShips));
        Assert.False(m.Missions.IsMissionFile(Ships));
        Assert.True(m.Has(Item("NPCShipArch", "ol_build_1", Ships)));

        m.Session.Undo();
        Assert.Equal(before, m.State());
        Assert.False(m.Session.OpenDocument(NewShips)!.State.Exists);
        Assert.Equal([@"npc_ship_file = missions\ol_test\npcships.ini"], m.Lines(Item("Mission", null)));
        m.Session.Redo();
        Assert.Equal(after, m.State());
        m.Session.Undo();
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void TheInverseDeletesTheFileAndPointsTheLineBack()
    {
        using var m = Create();
        var before = m.State();
        var result = m.Apply(new NewShipBuildFile(TestScript, NewShips));

        var group = Assert.IsType<CommandGroup>(result.Inverse);
        Assert.Equal(new DeleteScript(new ScriptRef(NewShips)), group.Commands[0]);
        m.Apply(result.Inverse!);

        Assert.Equal(before, m.State());
        Assert.False(m.Session.OpenDocument(NewShips)!.State.Exists);
    }

    [Fact]
    public void ASaveWritesTheEmptyFileAndTheLine()
    {
        using var m = Create();
        m.Apply(new NewShipBuildFile(TestScript, NewShips));

        var saved = m.Session.Save();

        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.True(File.Exists(Path.Combine(m.Ws.ModFolder("act"), "files", NewShips)));
        Assert.Equal("", m.Ws.ReadModFile(Path.Combine("files", NewShips)).Trim());
        var again = m.Ws.SecondSession("act");
        Assert.Equal(NewShips, again.ShipBuildFileOf(Script), ignoreCase: true);
        Assert.True(again.Missions.IsMissionFile(NewShips));
    }

    [Fact]
    public void TheCommandGivesTheRefusalOfThePopup()
    {
        using var m = Create();
        var inUse = m.Missions.NewShipFileRefusal(Script, Ships);

        var error = m.Refuse(new NewShipBuildFile(TestScript, Ships), MissionErrors.PathInUse);
        Assert.Equal(inUse, error.Message);
        Assert.Equal("path", error.Field);
        Assert.Equal(ThnEdits.BadName,
            m.Refuse(new NewShipBuildFile(TestScript, @"DATA\MISSIONS\OL_TEST\my ships.ini"), MissionErrors.NameInvalid).Message);
        Assert.Equal(EditErrorKind.InvalidArgument,
            m.Refuse(new NewShipBuildFile(TestScript, @"DATA\MISSIONS\OL_OTHER\ships.ini"), MissionErrors.BadPath).Kind);
        // An agent sends the same command as JSON and gets the same text.
        var batch = MissionJson.ReadBatch($$"""
            {"label":"x","commands":[{"kind":"new_ship_build_file","script":"msn:{{Script.Replace(@"\", @"\\")}}","path":"{{Ships.Replace(@"\", @"\\")}}"}]}
            """);
        Assert.Equal(inUse, Assert.Single(m.Missions.Apply(batch).Errors).Message);
    }
}
