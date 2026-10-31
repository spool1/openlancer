using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The rules of the mission workspace that need no editor window (section 4 of the M6a design): the selection of a line
/// of a problem, and the check of a kept selection of the Mission tab.
/// </summary>
public sealed class MissionWorkspaceTests
{
    private const string Script = @"DATA\MISSIONS\M01A\m01a.ini";

    [Fact]
    public void AProblemSelectsItsLineOrItsSection()
    {
        var trigger = new SectionRef(Script, new SectionIdentity("Trigger", "t1"));
        var ship = new SectionRef(Script, new SectionIdentity("MsnShip", "s1"));
        var other = new SectionRef(Script, new SectionIdentity("Constants", null));
        var mission = new SectionRef(Script, new SectionIdentity("Mission", null));

        Assert.Equal(new MissionRef(Script, MissionItemKind.Row, trigger, Row: 3), MissionRef.OfSection(trigger, 3));
        Assert.Equal(new MissionRef(Script, MissionItemKind.Trigger, trigger), MissionRef.OfSection(trigger, -1));
        // A line of another section selects the section.
        Assert.Equal(new MissionRef(Script, MissionItemKind.Ship, ship), MissionRef.OfSection(ship, 2));
        Assert.Equal(MissionItemKind.OtherSection, MissionRef.OfSection(other, -1).Kind);
        Assert.Equal(MissionRef.Of(Script), MissionRef.OfSection(mission, 0));
    }

    /// <summary>
    /// Follow mode shows the item of a reference of an agent edit in the Mission tab (section 8.5 of the MCP1 design): a
    /// trigger, a line or a section of the script. Before the mission run reads a script, only the reference of the
    /// script shows, as its root. A template of a prefab and a bad reference show nothing.
    /// </summary>
    [Fact]
    public void FollowModeFindsTheItemOfAReferenceInTheMissionTab()
    {
        using var m = MissionEdits.Create();
        var script = MissionEdits.Script;
        var analysis = m.Missions.Model(script).Analysis;
        var wave = new SectionRef(script, new SectionIdentity("Trigger", "ol_wave"));

        Assert.Equal(new MissionRef(script, MissionItemKind.Trigger, wave),
            MissionWorkspace.FollowItem(script, $"trg:{script}#ol_wave", analysis, null));
        Assert.Equal(new MissionRef(script, MissionItemKind.Row, wave, Row: 2),
            MissionWorkspace.FollowItem(script, $"line:{script}#Trigger/ol_wave/2", analysis, null));
        Assert.Equal(MissionItemKind.Ship, MissionWorkspace.FollowItem(script, $"sec:{script}#MsnShip/ol_ship_1", analysis, null)?.Kind);
        Assert.Equal(MissionRef.Of(script), MissionWorkspace.FollowItem(script, $"msn:{script}", null, null));
        Assert.Null(MissionWorkspace.FollowItem(script, $"trg:{script}#ol_wave", null, null));
        Assert.Null(MissionWorkspace.FollowItem("prefab:mod:ol_gate", "trg:prefab:mod:ol_gate#ol_open", analysis, null));
        Assert.Null(MissionWorkspace.FollowItem(script, "trg:", null, null));
    }

    [Fact]
    public void AKeptSelectionStaysWhileTheRunHasItsSection()
    {
        using var folder = new MissionFolder();
        folder.Write(Script, Story);
        var result = MissionRun.Execute(folder.Inputs());
        var start = result.Of(Script)!.Index.Trigger("t_start")!.Section.Ref;
        var kept = new MissionRef(Script, MissionItemKind.Trigger, start);
        var gone = new MissionRef(Script, MissionItemKind.Trigger, new SectionRef(Script, new SectionIdentity("Trigger", "t_gone")));

        Assert.Equal(kept, MissionWorkspace.Valid(result, kept));
        // A selection whose section is gone becomes its mission.
        Assert.Equal(MissionRef.Of(Script), MissionWorkspace.Valid(result, gone));
        // A selection of a script that the run does not have, and a selection of another kind, are not valid.
        Assert.Null(MissionWorkspace.Valid(result, MissionRef.Of(@"DATA\MISSIONS\M02\m02.ini")));
        Assert.Null(MissionWorkspace.Valid(result, new object()));
        // Before the first run, each mission selection stays.
        Assert.Equal(gone, MissionWorkspace.Valid(null, gone));
    }

    /// <summary>A run that ran while a script changed takes the newer model of the script. A new script needs a new
    /// run (section 6.11 of the M6b design).</summary>
    [Fact]
    public void ARunTakesTheNewerModelOfAScriptThatChangedWhileItRan()
    {
        using var m = MissionEdits.Create();
        var run = MissionRun.Execute(m.Missions.RunInputs());
        m.Apply(new SetTriggerStart(MissionEdits.T("ol_wave"), true));
        var scripts = m.Missions.ScriptPaths();

        var newer = MissionWorkspace.WithNewerModels(run, [MissionEdits.Script], scripts, p => m.Missions.Model(p).Analysis);
        var unchanged = MissionWorkspace.WithNewerModels(run, [], scripts, p => m.Missions.Model(p).Analysis);
        var added = MissionWorkspace.WithNewerModels(run, [@"DATA\MISSIONS\OL_NEW\ol_new.ini"],
            [.. scripts, @"DATA\MISSIONS\OL_NEW\ol_new.ini"], p => m.Missions.Model(p).Analysis);

        Assert.Same(m.Missions.Model(MissionEdits.Script).Analysis, newer!.Of(MissionEdits.Script));
        Assert.Same(run.Of(MissionEdits.Script), unchanged!.Of(MissionEdits.Script));
        Assert.Null(added);
    }

    /// <summary>The raw block of each file and the raw view of a script give one Console text for a refused edit, with
    /// the whole text of the user (decision D114).</summary>
    [Fact]
    public void ARefusedRawEditKeepsTheTextOfTheUser()
    {
        Assert.Equal("Line 2: The game reads no such key. The editor kept the lines before the edit. Your text:\n[Trigger]\nx",
            MissionWorkspace.RawRefusal(["Line 2: The game reads no such key."], "[Trigger]\nx"));
    }

    /// <summary>The note of the Mission tab while the session takes no edits (section 8.11 of the M6b design).</summary>
    [Fact]
    public void TheReadOnlyNoteGivesTheReason()
    {
        Assert.Equal("The editor cannot change missions now. There is no active mod. Select a mod to edit.",
            MissionErrors.NotEditableMessage("There is no active mod. Select a mod to edit."));
    }
}
