using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using Xunit;
using OpenLancer.Data.Editing;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The typed inverses of the mission commands (section 4.8 of the M6b design, decision D661): a command and its inverse
/// give W engine-equal to W before. Undo gives the same state as the inverse, and Redo gives the same state as the
/// command.
/// </summary>
public sealed class MissionInverseTests
{
    public static IEnumerable<object[]> Kinds() => Commands.Keys.Select(k => new object[] { k });

    /// <summary>One sample of each command kind on the test script.</summary>
    internal static readonly Dictionary<string, Func<MissionCommand>> Commands = new()
    {
        ["add_trigger"] = () => new AddTrigger(TestScript)
        {
            Condition = LineSpec.Of("Cnd_Timer", ("seconds", 2)), Actions = [LineSpec.Of("Act_ActTrig", ("trigger", "ol_end"))],
        },
        ["remove_trigger_keep"] = () => new RemoveTrigger(T("ol_end")),
        ["remove_trigger_remove"] = () => new RemoveTrigger(T("ol_end")) { References = ReferencesMode.Remove },
        ["rename_trigger"] = () => new RenameTrigger(T("ol_end"), "ol_finish"),
        ["rename_trigger_alone"] = () => new RenameTrigger(T("ol_wave"), "ol_wave_b") { ChangeReferences = false },
        ["duplicate_trigger"] = () => new DuplicateTrigger(T("ol_wave")),
        ["set_trigger_start"] = () => new SetTriggerStart(T("ol_wave"), true),
        ["set_trigger_repeats"] = () => new SetTriggerRepeats(T("ol_start"), true),
        ["set_trigger_system"] = () => new SetTriggerSystem(T("ol_wave")) { System = "ol_system_2" },
        ["set_condition"] = () => new SetCondition(T("ol_wave"), LineSpec.Of("Cnd_True")),
        ["set_condition_value"] = () => new SetConditionValue(T("ol_end"), "mode", "SILENT"),
        ["add_action"] = () => new AddAction(T("ol_start"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_start"))) { Before = 3 },
        ["remove_action"] = () => new RemoveAction(Row("ol_wave", 4)),
        ["reorder_action"] = () => new ReorderAction(Row("ol_wave", 5)) { Before = 3 },
        ["set_action_value"] = () => new SetActionValue(Row("ol_wave", 3), "position", MissionValue.Of(1, 2, 3)),
        ["set_action_line"] = () => new SetActionLine(Row("ol_end", 3), LineSpec.Of("Act_ChangeState", ("state", "FAIL"))),
        ["link_triggers"] = () => new LinkTriggers(T("ol_end"), "ol_start", LinkKind.Restart),
        ["unlink_triggers"] = () => new UnlinkTriggers(T("ol_wave"), "ol_end", LinkKind.Restart),
        ["retarget_link"] = () => new RetargetLink(Row("ol_start", 3), "ol_end"),
        ["insert_trigger_on_link"] = () => new InsertTriggerOnLink(Row("ol_start", 4), LineSpec.Of("Cnd_Timer", ("seconds", 1))),
        ["insert_line"] = () => new InsertLine(Item("MsnShip", "ol_ship_2"), LineSpec.Of("radius", ("radius", 50))) { Before = 1 },
        ["remove_line"] = () => new RemoveLine(new RowRef(Item("MsnShip", "ol_ship_1"), 3)),
        ["move_line"] = () => new MoveLine(new RowRef(Item("MsnShip", "ol_ship_1"), 1)) { Before = 4 },
        ["set_line"] = () => new SetLine(new RowRef(Item("MsnShip", "ol_ship_1"), 1), LineSpec.Of("system", ("system", "ol_b"))),
        ["set_line_value"] = () => new SetLineValue(new RowRef(Item("MsnShip", "ol_ship_1"), 4), MissionValue.Of(7)) { Index = 1 },
        ["add_section"] = () => new AddSection(TestScript, "NNObjective", [LineSpec.Of("state", ("state", "ACTIVE"))]),
        ["remove_section"] = () => new RemoveSection(Item("MsnFormation", "ol_form")),
        ["restore_section"] = () => new RestoreSection(TestScript, 2, new RawSection("MsnLoot", [new RawEntry("nickname", ["ol_loot"])])),
        ["replace_section_body"] = () => new ReplaceSectionBody(Item("ObjList", "ol_orders"),
            [new RawEntry("nickname", ["ol_orders"]), new RawEntry("Delay", ["5"])]),
        ["set_text"] = () => new SetText("ol_key") { Text = "Some text." },
        ["set_line_text"] = () => new SetLineText(Row("ol_end", 3), "text", "Well done."),
        ["create_script"] = () => new CreateScript(@"DATA\MISSIONS\OL_NEW\ol_new.ini"),
        ["command_group"] = () => new CommandGroup([new SetTriggerStart(T("ol_end"), true), new RemoveTrigger(T("ol_wave"))]),
        ["add_mission_ship"] = () => new AddMissionShip(TestScript)
        {
            Pilot = new PilotSpec { Faction = "ol_faction", Voice = "ol_voice" },
            NewBuild = new ShipBuildSpec("ol_arch", "ol_loadout") { Level = "d1" },
            Position = MissionValue.Of(1, 2, 3), System = "ol_system", Labels = ["ol_wing"],
        },
        ["remove_mission_ship"] = () => new RemoveMissionShip(Item("MsnShip", "ol_ship_3")),
        ["place_mission_ship"] = () => new PlaceMissionShip(Item("MsnShip", "ol_ship_1"))
        {
            Near = new NearSpec("ol_station") { Heading = 90, Range = 500 },
        },
        ["set_ship_loadout_shared"] = () => new SetShipLoadout(Item("MsnShip", "ol_ship_1"), "ol_loadout_2", LoadoutScope.Shared),
        ["set_ship_loadout_this_ship"] = () => new SetShipLoadout(Item("MsnShip", "ol_ship_1"), "ol_loadout_2", LoadoutScope.ThisShip),
        ["add_item"] = () => new AddItem(TestScript, "ship_build") { Lines = [LineSpec.Of("loadout", ("loadout", "ol_loadout"))] },
        ["remove_item"] = () => new RemoveItem(Item("ObjList", "ol_orders")),
        ["duplicate_item"] = () => new DuplicateItem(Item("MsnShip", "ol_ship_1")),
        ["rename_item"] = () => new RenameItem(Item("MsnShip", "ol_ship_1"), "ol_ship_a"),
        ["set_item_key"] = () => new SetItemKey(Item("MsnShip", "ol_ship_1"), "label",
            [LineSpec.Of("label", ("label", "ol_a")), LineSpec.Of("label", ("label", "ol_b"))]),
        ["set_formation_members"] = () => new SetFormationMembers(Item("MsnFormation", "ol_form"), ["ol_ship_1", "ol_ship_2"]),
        ["add_label"] = () => new AddLabel(Item("MsnFormation", "ol_form"), "ol_group"),
        ["remove_label"] = () => new RemoveLabel(Item("MsnShip", "ol_ship_1"), "ol_wing"),
        ["move_section"] = () => new MoveSection(Item("MsnShip", "ol_ship_1")) { Before = Item("MsnFormation", "ol_form") },
        ["add_order"] = () => new AddOrder(Item("ObjList", "ol_orders"), LineSpec.Of("Delay", ("seconds", 3))) { Before = 1 },
        ["add_waypoint"] = () => new AddWaypoint(Item("ObjList", "ol_orders"), MissionValue.Of(10, 20, 30)),
        ["set_orders"] = () => new SetOrders(Item("ObjList", "ol_orders"),
            [LineSpec.Of("Delay", ("seconds", 3)), LineSpec.Of("Idle")]),
        ["add_trigger_placed"] = () => new AddTrigger(TestScript)
        {
            Condition = LineSpec.Raw("Cnd_True", "no_params"), Place = new GraphPoint(10, 20), Act = new ActRef(Script, "act_1"),
        },
        ["duplicate_trigger_placed"] = () => new DuplicateTrigger(T("ol_wave")) { Place = new GraphPoint(300, 40) },
        ["move_nodes"] = () => new MoveNodes(TestScript, [new NodePlace("ol_wave", 100, 200), new NodePlace("ol_end", -5, 7)]),
        ["add_act"] = () => new AddAct(TestScript) { Name = "The ambush", Text = "Two waves attack.", Members = ["ol_wave"] },
        ["set_act"] = () => new SetAct(new ActRef(Script, "act_1")) { Name = "The start", Text = "The player launches." },
        ["set_act_members"] = () => new SetActMembers(new ActRef(Script, "act_1"), ["ol_end", "ol_start"]),
        ["apply_fix"] = () => new ApplyFix(TestScript, "M04|ol_wave|system", "move"),
        ["new_ship_build_file"] = () => new NewShipBuildFile(TestScript, @"DATA\MISSIONS\OL_TEST\ships_new.ini"),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ACommandAndItsInverseGiveTheStateBeforeAndUndoAndRedoAgree(string kind)
    {
        using var m = Create();
        var before = m.State();

        var result = m.Apply(Commands[kind]());
        var after = m.State();
        Assert.NotEqual(before, after);
        var inverse = m.Missions.Apply(result.Inverse!);

        Assert.True(inverse.Status == EditStatus.Applied, string.Join("\n", inverse.Errors.Select(e => e.Message)));
        Assert.Equal(before, m.State());
        m.Session.Undo();
        Assert.Equal(after, m.State());
        m.Session.Undo();
        Assert.Equal(before, m.State());
        m.Session.Redo();
        Assert.Equal(after, m.State());
    }

    [Fact]
    public void EachCommandKindOfTheCatalogHasASample()
    {
        // DeleteScript, RevertSection, AcceptBase and RemoveAct need an edit before them, so they have their own tests.
        // The prefab commands need an instance, so PrefabInstanceTests has their samples and their inverse tests.
        var kinds = MissionJson.Kinds.Keys.Where(k => k is not ("delete_script" or "revert_section" or "accept_base" or "remove_act"))
            .ToHashSet();
        var samples = Commands.Values.Concat(PrefabInstanceTests.Commands.Values).Concat(PrefabModeTests.Commands.Values)
            .SelectMany(Kinds).ToHashSet();

        Assert.Empty(kinds.Except(samples));
    }

    /// <summary>The kinds of a command and of the commands of a group.</summary>
    private static IEnumerable<string> Kinds(Func<MissionCommand> make)
    {
        var command = make();
        yield return MissionJson.KindOf(command.GetType());
        if (command is CommandGroup group)
        {
            foreach (var inner in group.Commands)
                yield return MissionJson.KindOf(inner.GetType());
        }
    }

    [Fact]
    public void DeleteScriptAndItsInverseGiveTheScriptBack()
    {
        using var m = Create();
        const string path = @"DATA\MISSIONS\OL_NEW\ol_new.ini";
        m.Apply(new CreateScript(path) { ShipBuildFile = @"DATA\MISSIONS\OL_NEW\npcships.ini" });
        m.Apply(new AddTrigger(new ScriptRef(path)) { Condition = LineSpec.Of("Cnd_True") });
        var before = m.State();

        var result = m.Apply(new DeleteScript(new ScriptRef(path)));
        m.Apply(result.Inverse!);

        Assert.Equal(before, m.State());
    }

    [Fact]
    public void AnInverseRunsAfterAnEditOfAnotherTriggerAndRefusesAfterAnEditOfATouchedSection()
    {
        using var m = Create();
        var result = m.Apply(new AddAction(T("ol_wave"), LineSpec.Of("Act_ActTrig", ("trigger", "ol_start"))));
        m.Apply(new SetConditionValue(T("ol_end"), "count", 3));
        var before = m.Lines("ol_wave");

        m.Apply(result.Inverse!);

        Assert.Equal(before!.Take(before!.Length - 1), m.Lines("ol_wave"));
        m.Session.Undo();
        m.Apply(new SetConditionValue(T("ol_wave"), "seconds", 4));
        m.Refuse(result.Inverse!, MissionErrors.SectionChanged);
    }

    /// <summary>A seeded random run of 300 commands checks the inverse after each command. At the end, an undo of all
    /// gives a save that writes no file.</summary>
    [Fact]
    public void ARandomRunOf300CommandsChecksEachInverseAndUndoOfAllWritesNoFile()
    {
        using var m = Create();
        var random = new Random(1234);
        var applied = 0;
        for (var step = 0; step < 300; step++)
        {
            var command = RandomCommand(m, random, step);
            var before = m.State();
            var result = m.Missions.Apply(command);
            if (result.Status != EditStatus.Applied)
                continue;
            applied++;
            var after = m.State();
            var inverse = m.Missions.Apply(result.Inverse!);
            Assert.True(inverse.Status == EditStatus.Applied, $"Step {step}, {command}: " +
                                                              string.Join("\n", inverse.Errors.Select(e => e.Message)));
            Assert.Equal(before, m.State());
            m.Session.Undo();
            Assert.Equal(after, m.State());
        }
        Assert.True(applied > 150, $"Only {applied} commands applied.");

        while (m.Session.CanUndo)
            m.Session.Undo();
        var saved = m.Session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Empty(saved.Written);
        Assert.DoesNotContain(TestWorkspace.Files(m.Ws.ModFolder("act")).Keys, k => !k.EndsWith("mod.json", StringComparison.Ordinal));
    }

    private static MissionCommand RandomCommand(MissionEdits m, Random random, int step)
    {
        var document = m.Session.OpenDocument(Script)!;
        var triggers = document.Working.Where(s => s.Name == "Trigger")
            .Select(OpenLancer.Data.Missions.TriggerRules.Nickname).OfType<string>().ToList();
        var trigger = triggers[random.Next(triggers.Count)];
        var lines = m.Lines(trigger)!;
        var row = random.Next(lines.Length);
        var other = triggers[random.Next(triggers.Count)];
        return random.Next(12) switch
        {
            0 => new AddTrigger(TestScript) { Condition = LineSpec.Of("Cnd_Timer", ("seconds", random.Next(1, 9))) },
            1 => new AddAction(T(trigger), LineSpec.Of("Act_ActTrig", ("trigger", other))),
            2 => new RemoveLine(Row(trigger, row)),
            3 => new MoveLine(Row(trigger, row)) { Before = random.Next(lines.Length + 1) },
            4 => new SetTriggerStart(T(trigger), random.Next(2) == 0),
            5 => new SetTriggerRepeats(T(trigger), random.Next(2) == 0),
            6 => new RenameTrigger(T(trigger), $"ol_r{step}"),
            7 => new RemoveTrigger(T(trigger)) { References = random.Next(2) == 0 ? ReferencesMode.Keep : ReferencesMode.Remove },
            8 => new DuplicateTrigger(T(trigger)),
            9 => new LinkTriggers(T(trigger), other, (LinkKind)random.Next(3)),
            10 => new SetLine(Row(trigger, row), LineSpec.Of("Act_ActTrig", ("trigger", other))),
            _ => new SetCondition(T(trigger), random.Next(2) == 0 ? LineSpec.Of("Cnd_True")
                : LineSpec.Of("Cnd_Timer", ("seconds", random.Next(1, 9)))),
        };
    }

    [Fact]
    public void TheInverseOfARenameLeavesNoFalseMissingLineSoAnAgentRevertsItWithoutAConfirmation()
    {
        using var m = Create();
        var before = m.State();

        var trigger = m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));
        var item = m.Apply(new RenameItem(Item("MsnShip", "ol_ship_1"), "ol_ship_9"));
        var revertItem = m.Missions.Apply(new MissionBatch("Revert", [item.Inverse!]) { Origin = EditOrigin.Agent });
        var revertTrigger = m.Missions.Apply(new MissionBatch("Revert", [trigger.Inverse!]) { Origin = EditOrigin.Agent });

        Assert.Equal(EditStatus.Applied, revertItem.Status);
        Assert.Empty(revertItem.Missing);
        Assert.Equal(EditStatus.Applied, revertTrigger.Status);
        Assert.Empty(revertTrigger.Missing);
        Assert.Equal(before, m.State());
    }

    [Fact]
    public void TheInverseOfARenameSkipsTheFormRuleOfNames()
    {
        // A raw edit can give a new trigger a name that the form rule refuses, as retail has trigger names with more than
        // 47 characters (review finding RB4).
        var longName = "ol_" + new string('a', 50);
        using var m = Create();
        m.Apply(new RestoreSection(TestScript, 99, new RawSection("Trigger",
            [new RawEntry("nickname", [longName]), new RawEntry("Cnd_True", [])])));
        var before = m.State();

        var result = m.Apply(new RenameTrigger(T(longName), "ol_short"));
        var back = m.Apply(result.Inverse!);

        Assert.Equal(EditStatus.Applied, back.Status);
        Assert.Equal(before, m.State());
    }
}
