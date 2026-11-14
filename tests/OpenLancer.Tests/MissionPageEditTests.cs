using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The edit bindings of the mission pages (section 8.4 of the M6b design) on the synthetic script of
/// <see cref="MissionEdits"/>: each binding gives the right command, the command gives the lines of W, and a read-only
/// section gives its reason.
/// </summary>
public sealed class MissionPageEditTests
{
    /// <summary>The page of an item now, with the read-only reasons of the session.</summary>
    private static MissionPageModel Page(MissionEdits m, MissionRef item)
    {
        var result = MissionRun.Execute(m.Missions.RunInputs());
        var analysis = m.Missions.Model(Script).Analysis;
        return MissionPageModel.For(new PageContext(result, analysis, false, false)
        {
            ReadOnlyOf = m.Missions.ReadOnlyReason,
        }, item);
    }

    private static MissionRef TriggerItem(MissionEdits m, string name) =>
        new(Script, MissionItemKind.Trigger, m.Missions.Model(Script).Index.Trigger(name)!.Section.Ref);

    private static MissionRef SectionItem(MissionEdits m, MissionSectionKind kind, string name) =>
        MissionRef.OfSection(m.Missions.Model(Script).Index.Item(kind, name)!.Ref, -1);

    private static PageField Field(MissionPageModel page, string section, string label) =>
        page.Section(section)!.Fields.First(f => f.Label == label);

    private static void Run(MissionEdits m, MissionBatch? batch)
    {
        Assert.NotNull(batch);
        var result = m.Missions.Apply(batch);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void TheTriggerComponentAndTheEventGiveTheirCommands()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_wave"));

        var starts = Field(page, "trigger", "Starts with the mission").Edit!;
        var repeats = Field(page, "trigger", "Repeats").Edit!;
        var @event = Field(page, "startsWhen", "Event").Edit!;
        var delay = Field(page, "startsWhen", "Delay").Edit!;

        Assert.Equal((EditWidget.Bool, "false"), (starts.Widget, starts.Value));
        Assert.Equal(new SetTriggerStart(T("ol_wave"), true), Assert.Single(starts.Make!(true)!.Commands));
        Assert.Equal(new SetTriggerRepeats(T("ol_wave"), true), Assert.Single(repeats.Make!(true)!.Commands));
        Assert.Equal((EditWidget.Words, "Cnd_Timer"), (@event.Widget, @event.Value));
        Assert.Equal(MissionSchema.Retail.Conditions.Count, @event.Choices.Count);
        Assert.Equal("Start", @event.Choices[0].Group);
        Assert.Equal((EditWidget.Number, "3"), (delay.Widget, delay.Value));
        Run(m, @event.Make!("Cnd_Destroyed"));
        Assert.Equal("Cnd_Destroyed = none", m.Lines("ol_wave")[2]);
        Run(m, Field(Page(m, TriggerItem(m, "ol_wave")), "startsWhen", "Target").Edit!.Make!("ol_ship_2"));
        Assert.Equal("Cnd_Destroyed = ol_ship_2", m.Lines("ol_wave")[2]);
    }

    [Fact]
    public void AnActionValueAndTheRowMenuGiveTheirCommands()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_wave"));
        var spawn = page.Section("group.spawn")!.Fields;

        var ship = spawn.First(f => f.Label == "Ship").Edit!;
        Assert.Equal((EditWidget.Picker, "ol_ship_1", "mission_ship+mission_formation"), (ship.Widget, ship.Value, ship.List));
        Run(m, ship.Make!("ol_ship_2"));
        Assert.Equal("Act_SpawnShip = ol_ship_2", m.Lines("ol_wave")[3]);

        page = Page(m, TriggerItem(m, "ol_wave"));
        var heading = page.Section("group.spawn")!.Fields.First(f => f.Heading);
        Assert.Equal(["Move down", "Duplicate", "Remove", "Show in raw INI"], heading.Actions.Select(a => a.Label));
        Assert.IsType<RawLineLink>(heading.Actions[^1].Link);
        Run(m, heading.Actions.Single(a => a.Label == "Move down").Batch);
        Assert.Equal("Act_SpawnShip = ol_ship_2", m.Lines("ol_wave")[4]);
        page = Page(m, TriggerItem(m, "ol_wave"));
        Run(m, page.Section("group.spawn")!.Fields.First(f => f.Heading).Actions.Single(a => a.Label == "Remove").Batch);
        Assert.DoesNotContain(m.Lines("ol_wave"), l => l.StartsWith("Act_SpawnShip"));
    }

    [Fact]
    public void StartedByAddsAndRemovesStartLinesInTheOtherTrigger()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_end"));
        var started = page.Section("startedBy")!;
        var stopped = page.Section("stoppedBy")!;

        Assert.Equal(["ol_start", "ol_wave", "Add"], started.Fields.Select(f => f.Label));
        Assert.Equal(EditWidget.AddPicker, started.Fields[^1].Edit!.Widget);
        Run(m, started.Fields[0].Actions.Single().Batch);
        Assert.DoesNotContain("Act_ActTrig = ol_end", m.Lines("ol_start"));
        Run(m, Page(m, TriggerItem(m, "ol_end")).Section("stoppedBy")!.Fields[^1].Edit!.Add!("ol_start"));
        Assert.Equal("Act_DeactTrig = ol_end", m.Lines("ol_start")[^1]);
        Assert.Equal(["ol_wave", "Add"], stopped.Fields.Select(f => f.Label));
    }

    [Fact]
    public void TheAdvancedSectionMovesALineBeforeTheNicknameAndSetsTheSystemHint()
    {
        using var m = Create();
        var page = Page(m, TriggerItem(m, "ol_wave"));
        var advanced = page.Section("advanced")!;

        var before = advanced.Fields.Single(f => f.Struck);
        Assert.Equal(["Move it after the nickname", "Remove the line"], before.Actions.Select(a => a.Label));
        Run(m, before.Actions[0].Batch);
        Assert.Equal(["nickname = ol_wave", "system = ol_system"], m.Lines("ol_wave").Take(2));
        Run(m, Field(Page(m, TriggerItem(m, "ol_end")), "advanced", "System").Edit!.Make!("ol_system"));
        Assert.Equal("system = ol_system", m.Lines("ol_end")[1]);
        var order = Page(m, TriggerItem(m, "ol_end")).Section("advanced")!.Fields.Where(f => f.Actions.Count > 0).ToList();
        Assert.Equal(["Move down"], order[0].Actions.Select(a => a.Label));
    }

    [Fact]
    public void TheShipPageSetsItsKeysItsLabelsAndItsLoadoutForThisShipOnly()
    {
        using var m = Create();
        var page = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1"));

        var position = Field(page, "ship", "Start position").Edit!;
        Assert.True(position.PickInScene);
        Run(m, position.Make!(MissionValue.Of(1, 2, 3)));
        Assert.Contains("position = 1, 2, 3", m.Lines(Item("MsnShip", "ol_ship_1")));

        var labels = Field(page, "ship", "Labels");
        Run(m, labels.Edit!.MakeList!(labels.List!.Add(1, new ListFieldRow("label", ["ol_new"], null) { Changed = true })));
        Assert.Contains("label = ol_new", m.Lines(Item("MsnShip", "ol_ship_1")));

        var build = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1")).Section("build")!;
        Assert.Equal("Used by 2 ships", build.Fields.Single(f => f.Label == "Use of the build").Value);
        Run(m, build.Actions.Single(a => a.Label == "This ship only").Batch);
        var after = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1")).Section("build")!;
        Assert.Equal("Used by 1 ship", after.Fields.Single(f => f.Label == "Use of the build").Value);
    }

    [Fact]
    public void ThePageMenuOfAShipDuplicatesDeletesAndRenamesIt()
    {
        using var m = Create();
        var page = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_3"));

        Assert.Equal(["Duplicate", "Delete"], page.PageActions.Select(a => a.Label));
        Assert.IsType<RemoveMissionShip>(Assert.Single(page.PageActions[1].Batch!.Commands));
        Run(m, page.Rename!("ol_ship_c"));
        Assert.True(m.Has(Item("MsnShip", "ol_ship_c")));
        Assert.Null(page.Rename!("ol_ship_3"));
    }

    [Fact]
    public void TheFormationMembersAndTheOrdersAreListsWithOneCommand()
    {
        using var m = Create();
        var formation = Page(m, SectionItem(m, MissionSectionKind.Formation, "ol_form"));
        var members = formation.Section("members")!.Fields.Single(f => f.List != null);

        var batch = members.Edit!.MakeList!(members.List!.Add(1, new ListFieldRow("ship", ["ol_ship_3"], null) { Changed = true }))!;
        var command = Assert.IsType<SetFormationMembers>(Assert.Single(batch.Commands));
        Assert.Equal(Item("MsnFormation", "ol_form"), command.Formation);
        Assert.Equal(["ol_ship_2", "ol_ship_3"], command.Ships);
        Run(m, batch);

        var orders = Page(m, SectionItem(m, MissionSectionKind.OrderList, "ol_orders"));
        var list = orders.Section("orders")!.Fields.Single(f => f.List != null);
        var point = Field(orders, "orders", "Point 1").Edit!;
        Assert.True(point.PickInScene);
        Run(m, point.Make!(MissionValue.Of(5, 6, 7)));
        Assert.Equal("GotoVec = goto_cruise, 5, 6, 7, 100", m.Lines(Item("ObjList", "ol_orders"))[1]);
        list = Page(m, SectionItem(m, MissionSectionKind.OrderList, "ol_orders")).Section("orders")!.Fields.Single(f => f.List != null);
        Run(m, list.Edit!.MakeList!(list.List!.Remove(0)));
        Assert.Equal(["nickname = ol_orders"], m.Lines(Item("ObjList", "ol_orders")));
    }

    [Fact]
    public void TheLabelPageAddsAndRemovesMembers()
    {
        using var m = Create();
        var page = Page(m, new MissionRef(Script, MissionItemKind.Label, Key: "ol_wing"));
        var members = page.Section("members")!.Fields;

        Assert.Equal(["Mission ship", "Mission ship", "Add"], members.Select(f => f.Label));
        Run(m, members[^1].Edit!.Add!("ol_form"));
        Assert.Contains("label = ol_wing", m.Lines(Item("MsnFormation", "ol_form")));
        Run(m, members[0].Actions.Single().Batch);
        Assert.DoesNotContain("label = ol_wing", m.Lines(Item("MsnShip", "ol_ship_1")));
    }

    /// <summary>The Mission page of a script with the ship build files, the rule of a new file and the marks of the
    /// session.</summary>
    private static MissionPageModel MissionPage(MissionEdits m, string script = Script) =>
        MissionPageModel.For(new PageContext(MissionRun.Execute(m.Missions.RunInputs()), m.Missions.Model(script).Analysis,
            false, false)
        {
            ReadOnlyOf = m.Missions.ReadOnlyReason, ChangedOf = m.Missions.ChangedLines,
            ShipBuildFiles = m.Missions.ShipBuildFiles, NewShipFileRefusal = m.Missions.NewShipFileRefusal,
            FreeShipFileName = m.Missions.FreeShipFileName, LostBuilds = m.Missions.LostBuilds,
            NamesFile = m.Missions.NamesFile,
        }, MissionRef.Of(script));

    [Fact]
    public void TheMissionPageSetsTheShipBuildFile()
    {
        using var m = Create();
        var page = Page(m, MissionRef.Of(Script));

        var file = Field(page, "files", "Ship build file").Edit!;
        Assert.Equal(@"missions\ol_test\npcships.ini", file.Value);
        Run(m, file.Make!(@"missions\ol_test\ships_b.ini"));
        Assert.Equal([@"npc_ship_file = missions\ol_test\ships_b.ini"], m.Lines(Item("Mission", null)));
    }

    /// <summary>The field "Ship build file" is a dropdown of the files that the line can name (#18). The shared file has a
    /// note, a pick sets the line, and a pick of the file of the line does nothing.</summary>
    [Fact]
    public void TheShipBuildFileFieldListsTheFilesAndAPickSetsTheLine()
    {
        using var m = Create(setup: ws =>
        {
            ws.WriteBase(@"DATA\MISSIONS\OL_OTHER\npcships.ini", ShipsText.Replace("ol_build_1", "ol_other_build"));
            ws.WriteBase(@"DATA\MISSIONS\ENCOUNTERS\ol_enc.ini", ShipsText.Replace("ol_build_1", "ol_enc_build"));
        });

        var field = Field(MissionPage(m), "files", "Ship build file");
        var edit = field.Edit!;

        Assert.Equal(EditWidget.Words, edit.Widget);
        Assert.Equal([@"MISSIONS\npcships.ini", @"MISSIONS\OL_OTHER\npcships.ini", @"MISSIONS\OL_TEST\npcships.ini"],
            edit.Choices.Select(c => c.Value));
        Assert.Equal(edit.Choices.Select(c => c.Value), edit.Choices.Select(c => c.Label));
        Assert.Equal(MissionPageModel.SharedShipFileNote, Assert.Single(edit.ChoiceNotes).Value);
        Assert.Equal(@"MISSIONS\npcships.ini", edit.ChoiceNotes.Single().Key);
        // The build of ol_npc_1 is only in the own file, so each other file has the warning. The build of ol_npc_shared
        // is in the shared file, so the game finds it with each file.
        const string lost = @"With this file, the game cannot find the ship build ol_build_1 of this mission. This build " +
                            @"is only in MISSIONS\OL_TEST\npcships.ini. The game does not spawn the ships that use it.";
        Assert.Equal(new Dictionary<string, string> { [@"MISSIONS\npcships.ini"] = lost, [@"MISSIONS\OL_OTHER\npcships.ini"] = lost },
            edit.ChoiceWarnings);
        // The value of the line names the own file, so the dropdown shows that choice, without a note.
        Assert.Equal(@"MISSIONS\OL_TEST\npcships.ini", edit.Value);
        Assert.Null(edit.Note);
        Assert.False(field.Changed);
        Assert.EndsWith(MissionPageModel.KeepsBuildsNote, field.Help);
        Assert.Null(edit.Make!(@"MISSIONS\OL_TEST\NPCSHIPS.ini"));

        var pick = edit.Make!(@"MISSIONS\OL_OTHER\npcships.ini")!;
        var set = Assert.IsType<SetItemKey>(Assert.Single(pick.Commands));
        Assert.Equal((Item("Mission", null), "npc_ship_file"), (set.Item, set.Key));
        Assert.Equal([@"MISSIONS\OL_OTHER\npcships.ini"], Assert.Single(set.Lines).Texts!);
        Run(m, pick);
        Assert.Equal([@"npc_ship_file = MISSIONS\OL_OTHER\npcships.ini"], m.Lines(Item("Mission", null)));
        var picked = Field(MissionPage(m), "files", "Ship build file");
        Assert.True(picked.Changed);
        Run(m, picked.Edit!.Make!(@"MISSIONS\npcships.ini"));
        Assert.Equal(MissionPageModel.SharedShipFileNote, Field(MissionPage(m), "files", "Ship build file").Edit!.Note);
    }

    /// <summary>"New file..." of the field "Ship build file" gives a free name, the install path, the refusal of the
    /// command and the batch of Create (#18).</summary>
    [Fact]
    public void NewFileOfTheShipBuildFileFieldMakesTheFileWithTheRuleOfTheCommand()
    {
        using var m = Create();

        var item = Field(MissionPage(m), "files", "Ship build file").Edit!.NewFile!;

        Assert.Equal("npcships_2", item.DefaultName);
        Assert.Equal(@"DATA\MISSIONS\OL_TEST\npcships_2.ini", item.PathOf(item.DefaultName));
        Assert.Null(item.Refusal(item.DefaultName));
        Assert.Equal(@"A file exists at DATA\MISSIONS\OL_TEST\npcships.ini. Use another name.", item.Refusal("npcships"));
        Assert.Equal(Data.Thn.ThnEdits.BadName, item.Refusal("my ships"));
        Assert.Equal("Type a name for the ship build file.", item.Refusal(""));
        Assert.Equal(new NewShipBuildFile(TestScript, @"DATA\MISSIONS\OL_TEST\npcships_2.ini"),
            Assert.Single(item.Make(item.DefaultName).Commands));
        Assert.Equal(@"With the new file, the game cannot find the ship build ol_build_1 of this mission. This build is " +
                     @"only in MISSIONS\OL_TEST\npcships.ini. The game does not spawn the ships that use it.", item.Warning);
        Run(m, item.Make(item.DefaultName));
        Assert.Equal([@"npc_ship_file = MISSIONS\OL_TEST\npcships_2.ini"], m.Lines(Item("Mission", null)));
        var edit = Field(MissionPage(m), "files", "Ship build file").Edit!;
        Assert.Equal(@"MISSIONS\OL_TEST\npcships_2.ini", edit.Value);
        Assert.Contains(@"MISSIONS\OL_TEST\npcships_2.ini", edit.Choices.Select(c => c.Value));
        Assert.Equal("npcships_3", edit.NewFile!.DefaultName);
    }

    /// <summary>A value of the field "Ship build file" that names no file shows the problem of the checks at the line,
    /// or the note that the editor makes the file with the first build.</summary>
    [Fact]
    public void AShipBuildFileValueWithoutAFileGivesItsNote()
    {
        using var m = Create(Text.Replace(@"missions\ol_test\npcships.ini", @"missions\ol_test\later.ini"));

        var base_ = Field(MissionPage(m), "files", "Ship build file").Edit!;
        Run(m, base_.Make!(@"missions\ol_test\later_2.ini"));
        var mod = Field(MissionPage(m), "files", "Ship build file").Edit!;

        // The line of the base data has no shown problem, so the field tells that the editor makes the file.
        Assert.Equal(@"missions\ol_test\later.ini", base_.Value);
        Assert.Equal(MissionPageModel.NoShipFileNote, base_.Note);
        Assert.Null(base_.NoteSeverity);
        var problem = m.Missions.Model(Script).Analysis.Problems
            .Single(p => p.Message.Contains("later_2.ini", System.StringComparison.Ordinal));
        Assert.Equal((problem.Message, (Data.DiagnosticSeverity?)problem.Severity), (mod.Note, mod.NoteSeverity));
        // The line names no file, so the game finds no build in it now, and no choice and no new file warns.
        Assert.Empty(mod.ChoiceWarnings);
        Assert.Null(mod.NewFile!.Warning);
    }

    /// <summary>A value of the field "Ship build file" that names a file that exists and that cannot be a ship build file
    /// gets its own note, not the note of a value without a file (review of #18). The checks give no problem at these
    /// lines.</summary>
    [Fact]
    public void AShipBuildFileValueOfAWorldFileGivesItsNote()
    {
        const string mixed = @"DATA\MISSIONS\OL_MIXED\ol_mixed.ini";
        const string encounter = @"DATA\MISSIONS\OL_ENC\ol_enc.ini";
        using var m = Create(setup: ws =>
        {
            ws.WriteBase(mixed, "[Mission]\nnpc_ship_file = missions\\ol_mixed\\mixed.ini\n");
            ws.WriteBase(@"DATA\MISSIONS\OL_MIXED\mixed.ini", ShipsText + "\n[Pilot]\nnickname = ol_pilot\n");
            ws.WriteBase(encounter, "[Mission]\nnpc_ship_file = missions\\encounters\\ol_enc.ini\n");
            ws.WriteBase(@"DATA\MISSIONS\ENCOUNTERS\ol_enc.ini", ShipsText);
        });

        foreach (var script in new[] { mixed, encounter })
        {
            var edit = Field(MissionPage(m, script), "files", "Ship build file").Edit!;
            Assert.Equal((MissionPageModel.NotShipFileNote, (Data.DiagnosticSeverity?)null), (edit.Note, edit.NoteSeverity));
        }
    }

    [Fact]
    public void AReadOnlySectionGivesItsReasonToEachBindingAndThePage()
    {
        using var m = Create(Text + "\n[MsnShip]\nnickname = ol_ship_3\n");
        var twin = m.Missions.Model(Script).Document.OfKind(MissionSectionKind.Ship).Last();

        var reason = m.Missions.ReadOnlyReason(twin.Ref);
        var page = Page(m, MissionRef.OfSection(twin.Ref, -1));

        Assert.NotNull(reason);
        Assert.Equal(reason, page.ReadOnly);
        Assert.Empty(page.PageActions);
        Assert.Null(m.Missions.ReadOnlyReason(SectionItem(m, MissionSectionKind.Ship, "ol_ship_1").Section!));
        var context = new PageContext(MissionRun.Execute(m.Missions.RunInputs()), m.Missions.Model(Script).Analysis, false, false);
        var readOnly = MissionPageModel.For(context, TriggerItem(m, "ol_wave"));
        Assert.All(readOnly.Sections.SelectMany(s => s.Fields).Select(f => f.Edit).OfType<FieldEdit>(),
            e => Assert.Equal(MissionPageModel.FieldNote, e.ReadOnly));
        Assert.Null(readOnly.Section("stoppedBy")?.Fields.FirstOrDefault(f => f.Label == "Add"));
    }
    /// <summary>"Pick in Scene" takes a point on the plane of the camera pivot (section 8.9 of the M6b design).</summary>
    [Fact]
    public void PickInSceneTakesThePointOnThePlaneOfThePivot()
    {
        var eye = new System.Numerics.Vector3(0, 100, 0);
        var down = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(1, -1, 0));

        Assert.Equal(new System.Numerics.Vector3(50, 50, 0),
            Editor.Assets.ScenePlacement.PivotPlanePoint(eye, down, new System.Numerics.Vector3(0, 50, 0)));
        // A ray that does not meet the plane in front of the camera gives the point at the distance of the pivot.
        Assert.Equal(new System.Numerics.Vector3(0, 150, 0), Editor.Assets.ScenePlacement.PivotPlanePoint(eye,
            System.Numerics.Vector3.UnitY, new System.Numerics.Vector3(0, 50, 0)));
        var pick = new ScenePick("ship.0", "Start position", null, new object(), _ => { });
        Assert.Equal("Click a point in the Scene to set Start position. Esc cancels.", pick.Hint);
    }
    [Fact]
    public void TheActPageSetsItsNameItsTextAndMovesItsTriggers()
    {
        using var m = Create(Text + "\n[Trigger]\nnickname = ol_alone\nCnd_True = no_params\n");
        m.Apply(new AddAct(TestScript) { Name = "The end", Members = ["ol_end"] });
        var page = ActPage(m, "a:1");

        Run(m, Field(page, "act", "Name").Edit!.Make!("The launch"));
        page = ActPage(m, "a:1");
        Assert.Equal("The launch", page.Title);
        Run(m, Field(page, "act", "Walkthrough text").Edit!.Make!("The player launches."));
        // A trigger that the mission reaches is always in an act, so its menu moves it to another act.
        var wave = page.Section("triggers")!.Fields.First(f => f.Label == "ol_wave");
        Assert.Equal(["Move to The end"], wave.Actions.Select(a => a.Label));
        Run(m, wave.Actions.Single().Batch);

        var acts = m.Missions.Model(Script).Acts!;
        Assert.Equal(("The launch", "The player launches."), (acts.Acts[0].Name, acts.Acts[0].Text));
        Assert.DoesNotContain(acts.Acts[0].Triggers, t => t.Nickname == "ol_wave");
        Assert.Contains(acts.Acts[1].Triggers, t => t.Nickname == "ol_wave");
        Assert.Equal(["Delete"], page.PageActions.Select(a => a.Label));
    }

    [Fact]
    public void RemoveOnTheActPageTakesATriggerThatTheMissionDoesNotReachOutOfTheAct()
    {
        using var m = Create(Text + "\n[Trigger]\nnickname = ol_alone\nCnd_True = no_params\n");
        m.Apply(new AddAct(TestScript) { Name = "The end", Members = ["ol_end", "ol_alone"] });

        var alone = ActPage(m, "a:2").Section("triggers")!.Fields.First(f => f.Label == "ol_alone");
        Run(m, alone.Actions.Single(a => a.Label == "Remove").Batch);

        var acts = m.Missions.Model(Script).Acts!;
        Assert.DoesNotContain(acts.Acts.SelectMany(a => a.Triggers), t => t.Nickname == "ol_alone");
        Assert.Contains(acts.NotStarted, t => t.Nickname == "ol_alone");
    }

    /// <summary>The page of an act, with the acts of the graph file.</summary>
    private static MissionPageModel ActPage(MissionEdits m, string key) =>
        MissionPageModel.For(new PageContext(MissionRun.Execute(m.Missions.RunInputs()), m.Missions.Model(Script).Analysis,
            false, false) { ReadOnlyOf = m.Missions.ReadOnlyReason, Acts = m.Missions.Model(Script).Acts },
            new MissionRef(Script, MissionItemKind.Act, Key: key));

    /// <summary>The page of an item with the marks of the active mod and the read-only reasons of the session.</summary>
    private static MissionPageModel MarkedPage(MissionEdits m, MissionRef item) =>
        MissionPageModel.For(new PageContext(MissionRun.Execute(m.Missions.RunInputs()), m.Missions.Model(Script).Analysis,
            false, false)
        {
            ReadOnlyOf = m.Missions.ReadOnlyReason, ChangedOf = m.Missions.ChangedLines, BaseOf = m.Missions.ChangedBase,
            SharedBuildFile = m.Missions.IsSharedBuildFile,
        }, item);

    [Fact]
    public void AKeyThatTheSectionDoesNotHaveShowsNotSetAndItsWidgetAddsIt()
    {
        using var m = Create();
        var page = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_3"));

        var start = Field(page, "ship", "Start");
        Assert.Equal((EditWidget.Words, "position"), (start.Edit!.Widget, start.Edit.Value));
        var position = Field(page, "ship", "Start position");
        Assert.Equal((MissionPageModel.NotSet, EditWidget.Position, ""), (position.Value, position.Edit!.Widget, position.Edit.Value));
        Assert.Equal(EditWidget.Picker, Field(page, "ship", "System").Edit!.Widget);
        Assert.Empty(Field(page, "ship", "Labels").List!.Rows);
        Run(m, position.Edit.Make!(MissionValue.Of(1, 2, 3)));
        Run(m, Field(page, "ship", "System").Edit!.Make!("ol_system"));
        Assert.Contains("position = 1, 2, 3", m.Lines(Item("MsnShip", "ol_ship_3")));
        Assert.Contains("system = ol_system", m.Lines(Item("MsnShip", "ol_ship_3")));

        // "Start" switches to a place near an object, which the user then picks.
        Run(m, Field(Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_3")), "ship", "Start").Edit!.Make!("near"));
        Assert.Contains(m.Lines(Item("MsnShip", "ol_ship_3")), l => l.StartsWith("rel_pos = ", System.StringComparison.Ordinal));
        Assert.DoesNotContain(m.Lines(Item("MsnShip", "ol_ship_3")), l => l.StartsWith("position = ", System.StringComparison.Ordinal));

        // A read-only page shows no key that the section does not have (UI rule 4).
        var readOnly = MissionPageModel.For(new PageContext(MissionRun.Execute(m.Missions.RunInputs()),
            m.Missions.Model(Script).Analysis, false, false), SectionItem(m, MissionSectionKind.Ship, "ol_ship_2"));
        Assert.DoesNotContain(readOnly.Section("ship")!.Fields, f => f.Label is "Start" or "Start position" or "System");
    }

    [Fact]
    public void AValueThatTheActiveModSetsHasTheBlueBar()
    {
        using var m = Create();
        Run(m, Field(Page(m, TriggerItem(m, "ol_wave")), "startsWhen", "Delay").Edit!.Make!(5));

        var wave = MarkedPage(m, TriggerItem(m, "ol_wave"));
        var end = MarkedPage(m, TriggerItem(m, "ol_end"));

        Assert.True(Field(wave, "startsWhen", "Delay").Changed);
        Assert.True(Field(wave, "startsWhen", "Event").Changed);
        Assert.False(Field(wave, "trigger", "Starts with the mission").Changed);
        Assert.False(Field(end, "startsWhen", "Event").Changed);
        Assert.Equal(MissionPageModel.EventTip, Field(end, "startsWhen", "Event").Help);
    }

    [Fact]
    public void ASectionWhoseBaseChangedAfterTheEditShowsTheBanner()
    {
        using var m = Create(modFiles: new()
        {
            [@"patches\" + Script] = "[Trigger]\nnickname = ol_end\nol.replace = true\nol.base = 0123456789abcdef\nCnd_True = no_params\n",
        });

        var change = MarkedPage(m, TriggerItem(m, "ol_end")).Base!;

        Assert.Equal(["nickname = ol_end", "Cnd_Destroyed = ol_ship_1, -1", "Act_LockDock = Player, ol_station, lock",
            "Act_ChangeState = SUCCEED"], change.Base);
        Assert.Equal(["nickname = ol_end", "Cnd_True = no_params"], change.Mine);
        Assert.IsType<AcceptBase>(Assert.Single(change.Keep.Commands));
        Assert.IsType<RevertSection>(Assert.Single(change.Revert.Commands));
        Assert.Null(MarkedPage(m, TriggerItem(m, "ol_wave")).Base);
        Run(m, change.Keep);
        Assert.Null(MarkedPage(m, TriggerItem(m, "ol_end")).Base);
    }

    [Fact]
    public void AFieldOfASharedShipBuildAsksFirst()
    {
        using var m = Create();
        var build = MarkedPage(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_3")).Section("build")!;
        var loadout = build.Fields.First(f => f.Edit?.List?.Contains("loadout", System.StringComparison.Ordinal) == true).Edit!;

        Assert.Null(loadout.ReadOnly);
        Assert.True(loadout.Confirms);
        Assert.StartsWith("This ship build is in a shared file.", build.Fields.Single(f => f.Label == "Use of the build").Help);
        var batch = loadout.Make!("ol_loadout_2")!;
        var asked = m.Missions.Apply(batch);
        Assert.True(asked.NeedsConfirmation);
        Run(m, batch with { Confirm = true });
        Assert.Contains("loadout = ol_loadout_2", m.Lines(ItemRef.Of(SharedShips, "NPCShipArch", "ol_shared_build")));
        Assert.False(MarkedPage(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1")).Section("build")!.Fields
            .First(f => f.Edit?.List?.Contains("loadout", System.StringComparison.Ordinal) == true).Edit!.Confirms);
    }

    [Fact]
    public void ThisShipOnlyGivesTheShipItsOwnPilot()
    {
        using var m = Create();
        var pilot = Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1")).Section("pilot")!;

        Assert.Equal("Used by 2 ships", pilot.Fields.Single(f => f.Label == "Use of the pilot").Value);
        Assert.Equal(MissionPageModel.UseAnotherPilotTip, pilot.Fields[0].Help);
        Run(m, pilot.Actions.Single(a => a.Label == "This ship only").Batch);

        Assert.Contains("npc = ol_npc_1_2", m.Lines(Item("MsnShip", "ol_ship_1")));
        Assert.Contains("npc = ol_npc_1", m.Lines(Item("MsnShip", "ol_ship_2")));
        Assert.Equal(m.Lines(Item("NPC", "ol_npc_1")).Skip(1), m.Lines(Item("NPC", "ol_npc_1_2")).Skip(1));
        Assert.Empty(Page(m, SectionItem(m, MissionSectionKind.Ship, "ol_ship_1")).Section("pilot")!.Actions);
    }

    [Fact]
    public void TheTypeOfAnObjectiveWritesTheValuesOfTheNewType()
    {
        using var m = Create(Text + "\n[NNObjective]\nnickname = ol_obj\nstate = HIDDEN\ntype = ids, 1001\n\n" +
                             "[NNObjective]\nnickname = ol_new_obj\n");
        var page = Page(m, SectionItem(m, MissionSectionKind.Objective, "ol_obj"));

        var type = Field(page, "objective", "Type");
        Assert.Equal("A text", type.Value);
        Assert.Contains(("navmarker", "A point in space", (string?)null), type.Edit!.Choices);
        Run(m, type.Edit.Make!("navmarker"));
        Assert.Equal("type = navmarker, none, 1001, 0, 0, 0, 0", m.Lines(Item("NNObjective", "ol_obj"))[^1]);

        page = Page(m, SectionItem(m, MissionSectionKind.Objective, "ol_obj"));
        Run(m, Field(page, "objective", "System").Edit!.Make!("ol_system"));
        page = Page(m, SectionItem(m, MissionSectionKind.Objective, "ol_obj"));
        var position = Field(page, "objective", "Position").Edit!;
        Assert.Equal("ol_system", position.PickSystem);
        Run(m, position.Make!(MissionValue.Of(1, 2, 3)));
        Assert.Equal("type = navmarker, ol_system, 1001, 0, 1, 2, 3", m.Lines(Item("NNObjective", "ol_obj"))[^1]);

        // An objective without a type line gets one from "Type".
        var empty = Field(Page(m, SectionItem(m, MissionSectionKind.Objective, "ol_new_obj")), "objective", "Type");
        Assert.Equal(MissionPageModel.NotSet, empty.Value);
        Run(m, empty.Edit!.Make!("ids"));
        Assert.Equal("type = ids, 0", m.Lines(Item("NNObjective", "ol_new_obj"))[^1]);
    }
}
