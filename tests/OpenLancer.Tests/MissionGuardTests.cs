using System;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>The guard of the mission files (section 4.1 of the M6b design, decision D660): only the mission editor
/// changes a mission script, the ship build file of a script and the editor files of the mod.</summary>
public sealed class MissionGuardTests
{
    [Fact]
    public void AnEditCommandOnAMissionScriptOrItsShipBuildFileOutsideTheLayerThrows()
    {
        using var m = Create();
        var wave = new SectionRef(Script, new SectionIdentity("Trigger", "ol_wave"));
        var build = new SectionRef(Ships, new SectionIdentity("NPCShipArch", "ol_build_1"));
        var before = m.State();

        var error = Assert.Throws<InvalidOperationException>(() =>
            m.Session.Execute(EditCommand.SetLines(wave, "Cnd_Timer", [[new Int32Value(5)]])));
        Assert.Throws<InvalidOperationException>(() =>
            m.Session.Execute(EditCommand.SetLinesFromText(build, "loadout", ["ol_loadout_2"])));
        Assert.Throws<InvalidOperationException>(() =>
            m.Session.Execute(EditCommand.SetModFile($@"graphs\{Script}", "[Graph]")));

        Assert.Equal($"{Script} is a mission file. Change it with the mission commands, so that the checks, the references " +
                     "and the undo of the missions follow the edit.", error.Message, ignoreCase: true);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void TheGuardDoesNotRefuseTheSharedFilesOfTheMissions()
    {
        using var m = Create(setup: ws => ws.WriteBase(@"DATA\MISSIONS\pilots_population.ini",
            "[Pilot]\nnickname = ol_pilot\ngun_id = ol_gun\n"));
        var shared = new SectionRef(SharedShips, new SectionIdentity("NPCShipArch", "ol_shared_build"));
        var pilot = new SectionRef(@"DATA\MISSIONS\pilots_population.ini", new SectionIdentity("Pilot", "ol_pilot"));

        m.Session.Execute(EditCommand.SetLinesFromText(shared, "loadout", ["ol_loadout_2"]));
        m.Session.Execute(EditCommand.SetLinesFromText(pilot, "gun_id", ["ol_gun_2"]));

        Assert.Equal("loadout = ol_loadout_2", m.Lines(new ItemRef(SharedShips, shared.Id))![2]);
        Assert.Equal(2, m.Session.EditedPaths.Count);
    }

    [Fact]
    public void TheRestoreOfARecoveryFileWithAMissionPatchWorks()
    {
        using var m = Create();
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 9));
        m.Apply(new AddItem(TestScript, "ship_build") { Nickname = "ol_build_2" });
        Assert.Empty(m.Session.WriteRecovery());
        m.Session.Abandon();

        var again = m.Ws.Session("act");
        Assert.True(again.HasRecovery);
        Assert.Empty(again.RestoreRecovery());

        var wave = again.OpenDocument(Script)!.Working.First(s => s.Name == "Trigger" && s.Any(e => e.Name == "nickname" && e[0].ToString() == "ol_wave"));
        Assert.Equal("Cnd_Timer = 9", MissionEdits.Line(wave[2]));
        Assert.Contains(again.OpenDocument(Ships)!.Working, s => SectionIdentity.KeyOf(s) == "ol_build_2");
        Assert.Equal("Restore unsaved edits", again.UndoLabel);
    }

    [Fact]
    public void TheMissionEditorIsTheWritePathOfTheSession()
    {
        using var m = Create();

        Assert.Same(m.Missions, m.Session.Missions);
        Assert.True(m.Session.IsGuardedMissionFile(Script));
        Assert.True(m.Session.IsGuardedMissionFile(Ships));
        Assert.False(m.Session.IsGuardedMissionFile(SharedShips));
    }

    [Fact]
    public void OnlyTheRestoreOfUnsavedEditsHasTheOriginRecovery()
    {
        using var m = Create();
        var before = m.State();

        Assert.Throws<ArgumentException>(() => m.Session.BeginBatch("Restore", EditOrigin.Recovery));
        var result = m.Missions.Apply(new MissionBatch("Restore", [new SetTriggerStart(T("ol_wave"), true)])
        {
            Origin = EditOrigin.Recovery,
        });

        Assert.Equal((EditStatus.Refused, EditErrorKind.InvalidArgument), (result.Status, result.Errors[0].Kind));
        Assert.Null(m.Session.OpenBatch);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void TheCommandsOnlyReadTheWorldFilesOfTheMissions()
    {
        const string pilots = @"DATA\MISSIONS\pilots_population.ini";
        const string encounter = @"DATA\MISSIONS\ENCOUNTERS\ol_encounter.ini";
        using var m = Create(setup: ws =>
        {
            ws.WriteBase(pilots, "[Pilot]\nnickname = ol_pilot\n");
            ws.WriteBase(encounter, "[EncounterFormation]\nnickname = ol_encounter\n");
        });

        m.Refuse(new RemoveSection(ItemRef.Of(pilots, "Pilot", "ol_pilot")), MissionErrors.WorldFile);
        var error = m.Refuse(new AddSection(new ScriptRef(encounter), "Trigger", [LineSpec.Of("Cnd_True")]), MissionErrors.WorldFile);

        Assert.Equal($"The mission commands cannot change {encounter}. Encounters and bases use the data of this file too. " +
                     "Change the file in the Inspector.", error.Message);
    }

    /// <summary>A batch that points the script at a file directly in DATA\MISSIONS, changes that file and points the
    /// script back gets the same refusals as a direct change (final finding 5 of MCP1-3). The shared ship build file is a
    /// ship build file, so the script can name it. A pilot file is not, so the first command refuses.</summary>
    [Fact]
    public void AScriptThatNamesAWorldFileDoesNotMakeItAMissionFile()
    {
        const string pilots = @"DATA\MISSIONS\pilots_population.ini";
        using var m = Create(setup: ws => ws.WriteBase(pilots, "[Pilot]\nnickname = ol_pilot\ngun_id = ol_gun\n"));
        var mission = ItemRef.Of(Script, "Mission", null);
        SetItemKey ShipFile(string file) => new(mission, "npc_ship_file", [LineSpec.Raw("npc_ship_file", file)]);
        var shared = ItemRef.Of(SharedShips, "NPCShipArch", "ol_shared_build");
        var pilot = ItemRef.Of(pilots, "Pilot", "ol_pilot");
        var before = m.State();
        MissionEditResult Trick(string file, MissionCommand change) =>
            m.Missions.Apply(new MissionBatch("Trick", [ShipFile(file), change, ShipFile(@"missions\ol_test\npcships.ini")]));

        var changed = Trick(@"missions\npcships.ini", new SetItemKey(shared, "loadout", [LineSpec.Raw("loadout", "ol_loadout_2")]));
        Assert.Equal((EditStatus.Refused, MissionErrors.SharedBuildChange), (changed.Status, changed.Errors[0].Detail));
        var removed = Trick(@"missions\npcships.ini", new RemoveSection(shared));
        Assert.Equal((EditStatus.Refused, MissionErrors.WorldFile), (removed.Status, removed.Errors[0].Detail));
        var renamed = Trick(@"missions\npcships.ini", new RenameItem(shared, "ol_other_build"));
        Assert.Equal((EditStatus.Refused, MissionErrors.SharedBuildRename), (renamed.Status, renamed.Errors[0].Detail));
        var pilotKey = Trick(@"missions\pilots_population.ini", new SetItemKey(pilot, "gun_id", [LineSpec.Raw("gun_id", "ol_gun_2")]));
        Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile, 0),
            (pilotKey.Status, pilotKey.Errors[0].Detail, pilotKey.Errors[0].Index));
        var pilotGone = Trick(@"missions\pilots_population.ini", new RemoveSection(pilot));
        Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile), (pilotGone.Status, pilotGone.Errors[0].Detail));
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    /// <summary>
    /// An encounter file is world data, also in a folder below DATA\MISSIONS (final finding 5 of MCP1-3). A command that
    /// points the script at it refuses with NotShipBuildFile, so a later command of the call cannot add a build to it.
    /// A script that already names it, for example after a change of the file outside the editor, does not make it a
    /// mission file: a second call gets WorldFile, as a direct change does.
    /// </summary>
    [Fact]
    public void AnEncounterFileIsNeverTheShipBuildFileOfAScript()
    {
        const string encounter = @"DATA\MISSIONS\ENCOUNTERS\ol_encounter.ini";
        var encounterText = "[EncounterFormation]\nship_by_class = 1, 1, ol_fighters\n";
        using var m = Create(setup: ws => ws.WriteBase(encounter, encounterText));
        var mission = ItemRef.Of(Script, "Mission", null);
        SetItemKey ShipFile(string file) => new(mission, "npc_ship_file", [LineSpec.Raw("npc_ship_file", file)]);
        var build = new AddItem(TestScript, "ship_build") { Nickname = "ol_enc_build" };
        var section = new AddSection(new ScriptRef(encounter), "NPCShipArch", [LineSpec.Raw("nickname", "ol_enc_build")]);
        var before = m.State();

        foreach (MissionCommand change in new MissionCommand[] { build, section })
        {
            var trick = m.Missions.Apply(new MissionBatch("Trick",
                [ShipFile(@"missions\encounters\ol_encounter.ini"), change, ShipFile(@"missions\ol_test\npcships.ini")]));
            Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile, 0),
                (trick.Status, trick.Errors[0].Detail, trick.Errors[0].Index));
        }
        var error = m.Refuse(ShipFile(@"missions\encounters\ol_encounter.ini"), MissionErrors.NotShipBuildFile);
        Assert.Equal(EditErrorKind.InvalidValue, error.Kind);
        Assert.Equal($"The npc_ship_file line of {Script} cannot name {encounter}, because that file is not a ship build " +
            @"file. Other data of the game can use its sections. Name a file in a folder under DATA\MISSIONS outside the " +
            "encounters folder, with only [NPCShipArch] sections. Or name a new file.", error.Message);
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);

        using var named = Create(Text.Replace(@"missions\ol_test\npcships.ini", @"missions\encounters\ol_encounter.ini"),
            setup: ws => ws.WriteBase(encounter, encounterText));
        Assert.False(named.Session.IsGuardedMissionFile(encounter));
        named.Refuse(build, MissionErrors.WorldFile);
        named.Refuse(section, MissionErrors.WorldFile);
    }

    /// <summary>
    /// A command group gets the check of the ship build files after each of its commands (final finding 5 of MCP1-3, the
    /// group form). A group that points the script at a file of a mission folder that is not a ship build file, adds a
    /// build or a section to it and points the script back refuses with NotShipBuildFile and changes nothing. A script
    /// that already names such a file does not make it a mission file: the guard does not cover it, and a command that
    /// changes it gets WorldFile.
    /// </summary>
    [Fact]
    public void ACommandGroupCannotPointAScriptAtAWorldFileOfAMissionFolder()
    {
        const string scene = @"DATA\MISSIONS\OL_TEST\ol_scene.ini";
        const string sceneText = "[Room]\nnickname = ol_room\n";
        using var m = Create(setup: ws => ws.WriteBase(scene, sceneText));
        var mission = ItemRef.Of(Script, "Mission", null);
        SetItemKey ShipFile(string file) => new(mission, "npc_ship_file", [LineSpec.Raw("npc_ship_file", file)]);
        var build = new AddItem(TestScript, "ship_build") { Nickname = "ol_scene_build" };
        var section = new AddSection(new ScriptRef(scene), "NPCShipArch", [LineSpec.Raw("nickname", "ol_scene_build")]);
        var before = m.State();

        foreach (MissionCommand change in new MissionCommand[] { build, section })
        {
            var group = new CommandGroup([ShipFile(@"missions\ol_test\ol_scene.ini"), change, ShipFile(@"missions\ol_test\npcships.ini")]);
            var trick = m.Missions.Apply(new MissionBatch("Trick", [group]));
            Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile, 0),
                (trick.Status, trick.Errors[0].Detail, trick.Errors[0].Index));
            // A group inside a group gets the same check.
            var nested = m.Missions.Apply(new MissionBatch("Trick", [new CommandGroup([new CommandGroup(group.Commands)])]));
            Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile), (nested.Status, nested.Errors[0].Detail));
        }
        Assert.Equal(before, m.State());
        Assert.Equal(["[Room]", "nickname = ol_room"], m.Session.OpenDocument(scene)!.Working
            .SelectMany(s => s.Select(MissionEdits.Line).Prepend($"[{s.Name}]")));
        Assert.Empty(m.Session.EditedPaths);
        Assert.False(m.Session.CanUndo);

        using var named = Create(Text.Replace(@"missions\ol_test\npcships.ini", @"missions\ol_test\ol_scene.ini"),
            setup: ws => ws.WriteBase(scene, sceneText));
        Assert.False(named.Session.IsGuardedMissionFile(scene));
        named.Refuse(build, MissionErrors.WorldFile);
        named.Refuse(section, MissionErrors.WorldFile);
        named.Session.Execute(EditCommand.SetLinesFromText(new SectionRef(scene, new SectionIdentity("Room", "ol_room")),
            "ol_note", ["1"]));
    }

    /// <summary>The npc_ship_file line can name a ship build file of another script, the shared ship build file or a new
    /// file. It cannot name a script or another file with a section that is not a ship build (final finding 5 of
    /// MCP1-3).</summary>
    [Fact]
    public void TheShipBuildFileOfAScriptIsAFileOfShipBuildsOnly()
    {
        const string other = @"DATA\MISSIONS\OL_OTHER\ol_other.ini";
        const string scene = @"DATA\MISSIONS\OL_TEST\ol_scene.ini";
        using var m = Create(setup: ws =>
        {
            ws.WriteBase(other, "[Mission]\nnpc_ship_file = missions\\ol_other\\npcships.ini\n");
            ws.WriteBase(@"DATA\MISSIONS\OL_OTHER\npcships.ini", "[NPCShipArch]\nnickname = ol_other_build\n");
            ws.WriteBase(scene, "[Room]\nnickname = ol_room\n");
        });
        var mission = ItemRef.Of(Script, "Mission", null);
        SetItemKey ShipFile(string file) => new(mission, "npc_ship_file", [LineSpec.Raw("npc_ship_file", file)]);

        m.Refuse(ShipFile(@"missions\ol_other\ol_other.ini"), MissionErrors.NotShipBuildFile);
        m.Refuse(ShipFile(@"missions\ol_test\ol_scene.ini"), MissionErrors.NotShipBuildFile);
        m.Refuse(ShipFile(@"ships\ol_ships.ini"), MissionErrors.NotShipBuildFile);
        m.Apply(ShipFile(@"missions\ol_other\npcships.ini"));
        m.Apply(ShipFile(@"missions\npcships.ini"));
        m.Apply(ShipFile(@"missions\ol_test\ol_new_ships.ini"));
        Assert.Equal([@"npc_ship_file = missions\ol_test\ol_new_ships.ini"], m.Lines(mission));
        // A command that keeps the line of the script does not check its file again.
        m.Apply(new SetItemKey(mission, "reward", [LineSpec.Raw("reward", "500")]));
    }

    /// <summary>
    /// A ship build file has only [NPCShipArch] sections (item 20 of the user decisions of 2026-11-01 of MCP1-3). So a
    /// command that puts another section into the ship build file of a script refuses with NotShipBuildFile, also in a
    /// command group and in a new ship build file, and nothing changes. A new script cannot take the place of a ship
    /// build file that a script names either. The file stays in the guard of the mission files, so the inverse of an
    /// edit of the file reverts the edit through the mission commands.
    /// </summary>
    [Fact]
    public void ACommandCannotPutAnotherSectionIntoTheShipBuildFileOfAScript()
    {
        const string newShips = @"DATA\MISSIONS\OL_TEST\ol_new_ships.ini";
        using var m = Create();
        var ships = new ScriptRef(Ships);
        var room = new AddSection(ships, "Room", [LineSpec.Raw("nickname", "ol_room")]);
        var trigger = new AddSection(ships, "Trigger", [LineSpec.Of("Cnd_True")]);
        var restored = new RestoreSection(ships, 0, new RawSection("Room", [new RawEntry("nickname", ["ol_room"])]));
        var build = new AddItem(TestScript, "ship_build") { Nickname = "ol_build_2" };
        var before = m.State();

        var error = m.Refuse(room, MissionErrors.NotShipBuildFile);
        Assert.Equal(EditErrorKind.InvalidValue, error.Kind);
        Assert.Equal($"{Ships} is the ship build file of {Script}. A ship build file has only [NPCShipArch] sections, so " +
                     "it cannot get a [Room] section. Put the section in another file.", error.Message);
        m.Refuse(trigger, MissionErrors.NotShipBuildFile);
        m.Refuse(restored, MissionErrors.NotShipBuildFile);
        m.Refuse(new CommandGroup([build, room]), MissionErrors.NotShipBuildFile);
        m.Refuse(new CommandGroup([new CommandGroup([build, restored])]), MissionErrors.NotShipBuildFile);
        var batch = m.Missions.Apply(new MissionBatch("Room", [build, room]));
        Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile, 1),
            (batch.Status, batch.Errors[0].Detail, batch.Errors[0].Index));
        // A new ship build file gets the same check after the command that makes it.
        var mission = ItemRef.Of(Script, "Mission", null);
        var newFile = m.Missions.Apply(new MissionBatch("New file",
        [
            new SetItemKey(mission, "npc_ship_file", [LineSpec.Raw("npc_ship_file", @"missions\ol_test\ol_new_ships.ini")]),
            build,
            new AddSection(new ScriptRef(newShips), "Room", [LineSpec.Raw("nickname", "ol_room")]),
        ]));
        Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile, 2),
            (newFile.Status, newFile.Errors[0].Detail, newFile.Errors[0].Index));
        var script = m.Missions.Apply(new MissionBatch("New script",
        [
            new SetItemKey(mission, "npc_ship_file", [LineSpec.Raw("npc_ship_file", @"missions\ol_test\ol_new_ships.ini")]),
            new CreateScript(newShips),
        ]));
        Assert.Equal((EditStatus.Refused, MissionErrors.NotShipBuildFile, 1),
            (script.Status, script.Errors[0].Detail, script.Errors[0].Index));
        Assert.Equal(before, m.State());
        Assert.Empty(m.Session.EditedPaths);
        Assert.False(m.Session.CanUndo);
        Assert.True(m.Session.IsGuardedMissionFile(Ships));

        // A build is a correct section, and the inverse of its add takes it out again through the mission commands.
        var added = m.Apply(new AddSection(ships, "NPCShipArch", [LineSpec.Raw("nickname", "ol_build_2")]));
        Assert.True(m.Session.IsGuardedMissionFile(Ships));
        m.Apply(added.Inverse!);
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void ALineOfABuildOfTheSharedShipBuildFileNeedsAConfirmation()
    {
        using var m = Create();
        var shared = ItemRef.Of(SharedShips, "NPCShipArch", "ol_shared_build");
        var before = m.State();
        SetItemKey Loadout() => new(shared, "loadout", [LineSpec.Raw("loadout", "ol_loadout_2")]);

        var unconfirmed = m.Missions.Apply(Loadout());
        Assert.Equal((EditStatus.Refused, MissionErrors.SharedBuildChange), (unconfirmed.Status, unconfirmed.Errors[0].Detail));
        Assert.Equal(before, m.State());
        var confirmed = m.Missions.Apply(new MissionBatch("Shared loadout", [Loadout()]) { Confirm = true });
        Assert.Equal(EditStatus.Applied, confirmed.Status);
        Assert.Equal("loadout = ol_loadout_2", m.Lines(shared)[2]);

        // The inverse is a change of the shared file too, so it needs the confirmation, and it gives the line back.
        var back = m.Missions.Apply(new MissionBatch("Back", [confirmed.Inverse!]) { Confirm = true });
        Assert.Equal(EditStatus.Applied, back.Status);
        Assert.Equal(before, m.State());
    }
}
