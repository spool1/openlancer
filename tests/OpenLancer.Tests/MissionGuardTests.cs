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
    /// script back gets the same refusals as a direct change (final finding 5 of MCP1-3).</summary>
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
        Assert.Equal((EditStatus.Refused, MissionErrors.WorldFile), (pilotKey.Status, pilotKey.Errors[0].Detail));
        var pilotGone = Trick(@"missions\pilots_population.ini", new RemoveSection(pilot));
        Assert.Equal((EditStatus.Refused, MissionErrors.WorldFile), (pilotGone.Status, pilotGone.Errors[0].Detail));
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
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
