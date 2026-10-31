using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;

using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Missions;
using Xunit;
using static OpenLancer.Tests.MissionEdits;
using static OpenLancer.Tests.PrefabInstanceTests;

namespace OpenLancer.Tests;

/// <summary>
/// The prefab parts of the mission studio (sections 5.6 to 5.9, 8.3 and 8.6 of the M6b design) as models: the page of a
/// linked instance with its overrides, the page of a matched instance, the Prefab page and the value choice of prefab
/// mode, the library group "Prefabs", the graph gestures of prefab frames and the tree of prefab mode. Each binding
/// gives its command, and the command gives the state of W. All names are invented.
/// </summary>
public sealed class PrefabStudioTests
{
    /// <summary>The context of a page of the test script now, with the reasons and the overrides of the session, as the
    /// Inspector gives them. A built-in prefab and a prefab of a lower mod have the reason of their banner.</summary>
    internal static PageContext Context(MissionEdits m, MissionAnalysis? analysis = null, PrefabModeModel? prefab = null) =>
        new(MissionRun.Execute(m.Missions.RunInputs()), analysis ?? m.Missions.Model(Script).Analysis, false, false)
        {
            ReadOnlyOf = section => PrefabRef.OfScript(section.Path) is { } p ? m.Missions.PrefabMode(p)?.ReadOnlyReason
                : m.Missions.ReadOnlyReason(section),
            ScriptReadOnly = path => PrefabRef.OfScript(path) is { } p ? m.Missions.PrefabMode(p)?.ReadOnlyReason
                : m.Missions.ReadOnlyReason(path),
            SessionReadOnly = m.Session.ReadOnlyReason,
            UnsavedOverrides = m.Missions.UnsavedOverrides,
            FreePrefabId = m.Missions.FreePrefabId,
            Prefab = prefab,
        };

    /// <summary>The page of a prefab instance by its id.</summary>
    private static MissionPageModel InstancePage(MissionEdits m, string id)
    {
        var instance = m.Missions.Model(Script).Prefabs!.Instances.Single(i => i.Id == id);
        return MissionPageModel.For(Context(m), new MissionRef(Script, MissionItemKind.PrefabInstance, Key: instance.Key));
    }

    private static PageField Field(MissionPageModel page, string section, string label) =>
        page.Section(section)!.Fields.First(f => f.Label == label);

    /// <summary>Checks that a batch has one command with the JSON form of the expected command. A list of a command
    /// compares by reference, so the test compares the JSON forms.</summary>
    private static void One(MissionCommand expected, MissionBatch? batch)
    {
        Assert.NotNull(batch);
        Assert.Equal(MissionJson.Write(expected), MissionJson.Write(Assert.Single(batch.Commands)));
    }

    private static void Run(MissionEdits m, MissionBatch? batch)
    {
        Assert.NotNull(batch);
        var result = m.Missions.Apply(batch);
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => e.Message)));
    }

    // ---- Linked instances ----

    [Fact]
    public void AFieldOfALinkedInstanceSetsItsValueAndAnOverrideIsBoldWithItsMenu()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var page = InstancePage(m, "ol_w1");
        var delay = Field(page, "fields", "Spawn after");
        var formation = Field(page, "fields", "Enemy formation");

        Assert.Equal((EditWidget.Number, "5", "5 s"), (delay.Edit!.Widget, delay.Edit.Value, delay.Value));
        Assert.Equal((EditWidget.Picker, "ol_form"), (formation.Edit!.Widget, formation.Edit.Value));
        Assert.False(delay.Override);
        One(new SetPrefabField(I("ol_w1"), "delay", ["7"]), delay.Edit.Make!(7));
        One(new SetPrefabField(I("ol_w1"), "formation", ["ol_form"]), formation.Edit.Make!("ol_form"));
        Run(m, delay.Edit.Make!(7));

        page = InstancePage(m, "ol_w1");
        delay = Field(page, "fields", "Spawn after");
        Assert.Equal((true, true), (delay.Override, delay.Changed));
        Assert.Equal(["Revert", "Apply to prefab 'Gate ambush'", "Show in raw INI"], delay.Actions.Select(a => a.Label));
        Assert.Equal([MissionPageModel.RevertTip, MissionPageModel.ApplyTip, null], delay.Actions.Select(a => a.Tooltip));
        // "Show in raw INI" goes to the line of the field in its member: Cnd_Timer of the spawn trigger.
        var spawn = new SectionRef(Script, new SectionIdentity("Trigger", "ol_w1_spawn"));
        Assert.Equal(new RawLineLink(spawn, 1), delay.Actions[2].Link);
        var formationMenu = Field(page, "fields", "Enemy formation").Actions;
        Assert.Equal(["Show in raw INI"], formationMenu.Select(a => a.Label));
        Assert.Equal(new RawLineLink(spawn, 2), formationMenu[0].Link);
        // A per-instance field never shows as an override (decision D674).
        Assert.False(Field(page, "fields", "Enemy formation").Override);
        Assert.Equal(["Overrides (1)", "Revert Spawn after", "Apply Spawn after", "Revert all", "Apply all", "Unpack", "Delete instance"],
            page.PageActions.Select(a => a.Label));
        Assert.True(page.PageActions[0].IsHeading);

        // A save takes the blue bar away. The violet bar stays.
        var saved = m.Session.Save();
        Assert.True(saved.Saved, string.Join(" ", saved.Problems.Select(p => p.Message)));
        delay = Field(InstancePage(m, "ol_w1"), "fields", "Spawn after");
        Assert.Equal((true, false), (delay.Override, delay.Changed));

        Run(m, delay.Actions[0].Batch);
        Assert.Empty(m.Missions.Graph(Script).Instance("ol_w1")!.Overrides);
        Assert.False(Field(InstancePage(m, "ol_w1"), "fields", "Spawn after").Override);
    }

    [Fact]
    public void ApplyToPrefabOfAFieldMenuChangesTheDefaultOfThePrefab()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Run(m, Field(InstancePage(m, "ol_w1"), "fields", "Spawn after").Edit!.Make!(9));

        Run(m, Field(InstancePage(m, "ol_w1"), "fields", "Spawn after").Actions[1].Batch);

        Assert.Equal([PrefabRow.Of("9")], m.Missions.Prefab(GateRef)!.Template!.Field("delay")!.Default);
        Assert.Empty(m.Missions.Graph(Script).Instance("ol_w1")!.Overrides);
    }

    [Fact]
    public void AnOverrideOfABuiltInPrefabOffersAVariantInPlaceOfApply()
    {
        using var m = PrefabInstanceTests.Create();
        m.Apply(new PlacePrefabInstance(TestScript, PrefabRef.BuiltinOf("spawnWave")) { Id = "ol_b" });
        var spawnWhen = Field(InstancePage(m, "ol_b"), "fields", "Spawn when");
        var list = spawnWhen.List!;

        Assert.Equal(EditWidget.List, spawnWhen.Edit!.Widget);
        Assert.Equal(["Cnd_True"], list.Rows.Select(r => r.Key));
        var timer = list.Remove(0).Add(0, new ListFieldRow("Cnd_Timer", ["5"], null));
        One(new SetPrefabField(I("ol_b"), "spawnWhen", ["Cnd_Timer = 5"]), spawnWhen.Edit.MakeList!(timer));
        Run(m, spawnWhen.Edit.MakeList!(timer));

        var page = InstancePage(m, "ol_b");
        spawnWhen = Field(page, "fields", "Spawn when");
        Assert.True(spawnWhen.Override);
        Assert.Equal(["Revert", "Make a variant with this value", "Show in raw INI"], spawnWhen.Actions.Select(a => a.Label));
        Assert.DoesNotContain(page.PageActions, a => a.Label == "Apply all");
        One(new CreatePrefabAsset("spawnwave_variant")
        {
            VariantOf = PrefabRef.BuiltinOf("spawnWave"), Source = I("ol_b"), Fields = ["spawnWhen"],
        }, spawnWhen.Actions[1].Batch);
        Run(m, spawnWhen.Actions[1].Batch);
        var record = m.Missions.Graph(Script).Instance("ol_b")!;
        Assert.Equal(("mod:spawnwave_variant", 0), (record.Prefab, record.Overrides.Count));
        // The next variant gets the next free file name.
        Assert.Equal("spawnwave_variant2", m.Missions.FreePrefabId("spawnwave_variant"));
    }

    [Fact]
    public void ARemovedMemberShowsAsAnOverrideWithItsRevert()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        m.Apply(new RemoveTrigger(T("ol_w1_end")));

        var removed = Field(InstancePage(m, "ol_w1"), "members", "end");

        Assert.Equal((MissionPageModel.RemovedText, true, true), (removed.Value, removed.Override, removed.Changed));
        Assert.Equal(["Revert", "Apply to prefab 'Gate ambush'"], removed.Actions.Select(a => a.Label));
        Assert.Equal(new RevertOverride(I("ol_w1")) { Member = "end" }, Assert.Single(removed.Actions[0].Batch!.Commands));
        Run(m, removed.Actions[0].Batch);
        Assert.True(m.Has("ol_w1_end"));
        Assert.Equal("ol_w1_end", Field(InstancePage(m, "ol_w1"), "members", "end").Value);
    }

    [Fact]
    public void TheButtonsOfALinkedInstanceOpenThePrefabAndUnpackIt()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var page = InstancePage(m, "ol_w1");
        var buttons = page.Section("fields")!.Actions;

        Assert.Equal(["Open prefab", "Select prefab asset", "Unpack"], buttons.Select(a => a.Label));
        Assert.Equal(new PrefabModeLink(GateRef), buttons[0].Link);
        Assert.Equal(new PrefabAssetLink(GateRef), buttons[1].Link);
        Assert.Equal(new PrefabModeLink(GateRef), Assert.Single(page.Lead).Link);
        Run(m, buttons[2].Batch);
        Assert.Null(m.Missions.Graph(Script).Instance("ol_w1"));
    }

    [Fact]
    public void ALinkedInstanceOfAMissingPrefabIsReadOnlyWithUnpack()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Run(m, new MissionBatch("Delete", [new RemovePrefabAsset(GateRef)]) { Confirm = true });

        var page = InstancePage(m, "ol_w1");

        Assert.Equal(PrefabLinks.MissingMessage("ol_w1", "ol_gate"), page.ReadOnly);
        Assert.Equal(["Unpack"], page.Section("fields")!.Actions.Select(a => a.Label));
        Assert.Contains(page.Section("problems")!.Fields, f => f.Value.Contains("ol_w1"));
    }

    [Fact]
    public void AProblemP01OfAHandEditedInstanceGivesItsFixes()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        m.Session.Execute(MissionTestData.Raw(EditCommand.SetLinesFromText(T("ol_w1_spawn").Item.Ref, "Cnd_Timer", ["9"])));
        var analysis = m.Missions.Model(Script).Analysis;
        var problem = analysis.Problems.Single(p => p.Rule == "P01");

        var fixes = MissionPageModel.FixActions(analysis, problem.Subject!);

        Assert.Equal(2, fixes.Count);
        Assert.Contains(InstancePage(m, "ol_w1").Section("problems")!.Fields,
            f => f.Value == problem.Message && f.Actions.Select(a => a.Label).SequenceEqual(fixes.Select(x => x.Label)));
        Run(m, fixes[0].Batch);
        Assert.Contains("Cnd_Timer = 5", m.Lines("ol_w1_spawn"));
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule == "P01");
    }

    // ---- Matched instances ----

    internal const string MatchedText = """
        [Mission]

        [Trigger]
        nickname = ol_start
        InitState = ACTIVE
        Cnd_True = no_params
        Act_ActTrig = ol_win

        [Trigger]
        nickname = ol_win
        Cnd_True = no_params
        Act_AdjAcct = 1000
        Act_ChangeState = SUCCEED
        """;

    [Fact]
    public void AFieldOfAMatchedInstanceWritesThroughItsRuleAndSaveAsPrefabLinksIt()
    {
        using var m = MissionEdits.Create(MatchedText);
        var definition = MissionPrefabs.Retail.Definition("success")!;
        var reward = definition.Fields.Single(f => f.Id == "reward");
        var page = InstancePage(m, "success/ol_win");
        var field = Field(page, "fields", reward.Label);

        Assert.Equal((EditWidget.WholeNumber, "1000"), (field.Edit!.Widget, field.Edit.Value));
        One(new SetPrefabField(new InstanceRef(Script, "success/ol_win"), "reward", ["2500"]), field.Edit.Make!(2500));
        Run(m, field.Edit.Make!(2500));
        Assert.Contains("Act_AdjAcct = 2500", m.Lines("ol_win"));
        // A value without its line gets a text field, and the write adds the line.
        var scene = definition.Fields.Single(f => f.Id == "endScene");
        var endScene = Field(InstancePage(m, "success/ol_win"), "fields", scene.Label);
        Assert.Equal((EditWidget.Picker, "None"), (endScene.Edit!.Widget, endScene.Value));
        Run(m, endScene.Edit.Make!(@"missions\ol\ol_room.thn"));
        Assert.Contains(@"Act_AddRTC = missions\\ol\\ol_room.thn", m.Lines("ol_win"));

        var save = page.PageActions.Single(a => a.Label == "Save as prefab...");
        var link = Assert.IsType<SavePrefabLink>(save.Link);
        Assert.Equal((definition.Name, "success2"), (link.Name, link.Id));
        Run(m, SavePrefabDialog.Batch(link.Instance, "Win and pay", "ol_win_pay"));
        var record = Assert.Single(m.Missions.Graph(Script).Instances);
        Assert.Equal(("mod:ol_win_pay", 0), (record.Prefab, record.Overrides.Count));
        Assert.Equal("Win and pay", m.Missions.Prefab(PrefabRef.ModOf("ol_win_pay"))!.Name);
    }

    [Theory]
    [InlineData("ol_wave", null)]
    [InlineData("ol-wave_2", null)]
    [InlineData("", "Give a file name.")]
    [InlineData("ol wave", "'ol wave' is not a valid file name. Use lowercase letters, digits, '_' and '-'.")]
    public void TheDialogSaveAsPrefabChecksTheFileName(string id, string? problem)
    {
        Assert.Equal(problem, SavePrefabDialog.FileNameProblem(id));
    }

    // ---- Prefab mode ----

    [Fact]
    public void ThePrefabPageSetsTheDefaultsAndShowsThePortsAndTheSections()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var model = m.Missions.PrefabMode(GateRef)!;
        var page = MissionPageModel.For(Context(m, model.Analysis, model), MissionRef.Of(GateRef.Script));

        Assert.Equal(("Gate ambush", null), (page.Title, page.ReadOnly));
        // The steps of the prefab (UI rule 12): the file, the lines and the fields.
        Assert.Equal(["Prefab file", "Valid lines", "Fields in lines"], page.Completeness!.Steps.Select(s => s.Name));
        Assert.Equal(3, page.Completeness.Done);
        // Each field shows its kind and "Per instance" on its heading, then its default (section 8.6 of the M6b design).
        var fields = page.Section("fields")!.Fields.ToList();
        var heading = Field(page, "fields", "Spawn after");
        Assert.Equal((true, "Number"), (heading.Heading, heading.Value));
        Assert.Equal("Formation, per instance", Field(page, "fields", "Enemy formation").Value);
        var delay = fields[fields.IndexOf(heading) + 1];
        Assert.Equal("Default", delay.Label);
        Assert.Equal(["Remove field"], delay.Actions.Select(a => a.Label));
        Assert.Equal("When trigger 'end' runs", Field(page, "ports", "When all are destroyed").Value);
        Assert.Equal(["spawn", "end"], page.Section("sections")!.Fields.Select(f => f.Label));
        Assert.All(page.Section("sections")!.Fields, f => Assert.IsType<MissionRef>(f.Link));
        Assert.Equal("Changes here change 1 instance in 1 mission of mod act.", Field(page, "prefab", "Instances").Value);
        One(new SetPrefabFieldDefault(GateRef, "delay", ["8"]), delay.Edit!.Make!(8));

        Run(m, delay.Edit.Make!(8));
        Assert.Equal("Cnd_Timer = 8", m.Lines("ol_w1_spawn")[1]);
        Assert.Equal("fields", MissionPageModel.For(Context(m, model.Analysis, model),
            new MissionRef(GateRef.Script, MissionItemKind.Mission, Key: "field:delay")).FocusSection);
    }

    [Fact]
    public void ABuiltInPrefabPageIsReadOnlyWithMakeVariant()
    {
        using var m = PrefabInstanceTests.Create();
        var prefab = PrefabRef.BuiltinOf("spawnWave");
        var model = m.Missions.PrefabMode(prefab)!;

        var page = MissionPageModel.For(Context(m, model.Analysis, model), MissionRef.Of(prefab.Script));

        Assert.Equal(PrefabModeModel.BuiltInBanner, page.ReadOnly);
        var variant = Assert.Single(page.PageActions);
        Assert.Equal(("Make variant", MissionPageModel.MakeVariantTip), (variant.Label, variant.Tooltip));
        Assert.Equal(new CreatePrefabAsset("spawnwave_variant") { VariantOf = prefab }, Assert.Single(variant.Batch!.Commands));
        Assert.All(page.Section("fields")!.Fields.Where(f => !f.Heading), f => Assert.NotNull(f.Edit?.ReadOnly));
        Run(m, variant.Batch);
        Assert.Equal("builtin:spawnWave", m.Missions.Prefab(PrefabRef.ModOf("spawnwave_variant"))!.Template!.VariantOf);
    }

    [Fact]
    public void AValueOfATemplateLineHasTheChoiceFixedValueOrField()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        MissionPageModel TriggerPage()
        {
            var model = m.Missions.PrefabMode(GateRef)!;
            var spawn = model.Analysis!.Index.Trigger("spawn")!;
            return MissionPageModel.For(Context(m, model.Analysis, model),
                new MissionRef(GateRef.Script, MissionItemKind.Trigger, spawn.Section.Ref));
        }

        var bound = TriggerPage().Section("startsWhen")!.Fields.Single(f => f.Value == "Field: Spawn after");
        Assert.Equal(["Fixed value"], bound.Actions.Select(a => a.Label));
        Assert.Equal(MissionPageModel.FieldValueTip, bound.Help);
        Run(m, bound.Actions[0].Batch);
        Assert.Equal("Cnd_Timer = 5", m.Lines("ol_w1_spawn")[1]);

        var fixedValue = TriggerPage().Section("startsWhen")!.Fields.Single(f => f.Actions.Any(a => a.Label == "Field: Spawn after"));
        Assert.Equal("5", fixedValue.Edit!.Value);
        Assert.Equal(new BindValue(new RowRef(new ItemRef(GateRef.Script, new SectionIdentity("Trigger", "spawn")), 1) { ExpectKey = "Cnd_Timer" },
            "delay") { Value = "seconds" }, Assert.Single(fixedValue.Actions.Single(a => a.Label == "Field: Spawn after").Batch!.Commands));
    }

    [Fact]
    public void AConditionAndTheLinesOfATemplateHaveTheChoiceOfTheirFields()
    {
        using var m = PrefabInstanceTests.Create();
        var prefab = PrefabRef.BuiltinOf("spawnWave");
        Run(m, new MissionBatch("Variant", [new CreatePrefabAsset("ol_wave") { VariantOf = prefab }]));
        var wave = PrefabRef.ModOf("ol_wave");
        MissionPageModel TriggerPage()
        {
            var model = m.Missions.PrefabMode(wave)!;
            var spawn = model.Analysis!.Index.Trigger("spawn")!;
            return MissionPageModel.For(Context(m, model.Analysis, model), new MissionRef(wave.Script, MissionItemKind.Trigger, spawn.Section.Ref));
        }

        var page = TriggerPage();
        var spawnWhen = Field(page, "startsWhen", "Event");
        Assert.Equal("Field: Spawn when", spawnWhen.Value);
        Assert.Equal("Field: Sides", Assert.Single(page.Section("fieldLines")!.Fields).Value);
        Assert.DoesNotContain(page.Section("group.spawn")!.Fields.SelectMany(f => f.Actions), a => a.Label == "Show in raw INI");
        Run(m, spawnWhen.Actions.Single(a => a.Label == "Fixed value").Batch);

        var condition = Field(TriggerPage(), "startsWhen", "Event");
        Assert.Equal("Cnd_True", condition.Edit!.Value);
        var bind = condition.Actions.Single(a => a.Label == "Field: Spawn when");
        Assert.IsType<BindValue>(Assert.Single(bind.Batch!.Commands));
        Run(m, bind.Batch);
        Assert.Equal("Field: Spawn when", Field(TriggerPage(), "startsWhen", "Event").Value);
    }

    [Fact]
    public void AMemberTriggerOfALinkedInstanceIsReadOnlyWithItsReason()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var trigger = m.Missions.Model(Script).Index.Trigger("ol_w1_spawn")!;

        var page = MissionPageModel.For(Context(m), new MissionRef(Script, MissionItemKind.Trigger, trigger.Section.Ref));

        Assert.Equal(PrefabLinks.MemberReason("Gate ambush", "ol_w1"), page.ReadOnly);
        Assert.NotNull(Field(page, "startsWhen", "Event").Edit!.ReadOnly);
        Assert.Null(m.Missions.ReadOnlyReason(m.Missions.Model(Script).Index.Trigger("ol_start")!.Section.Ref));
    }

    [Fact]
    public void TheSlotsOfATemplateLineShowTheLabelsOfTheirFields()
    {
        var template = PrefabTemplate.Read(Gate, out _)!;

        Assert.Equal("Spawn formation: [Enemy formation]", MissionPageModel.SlotText("Spawn formation: {formation}", template));
        Assert.Equal("[Instance]_end starts [Port: When all are destroyed]",
            MissionPageModel.SlotText("{instance}_end starts {port:done}", template));
        Assert.Equal("No slot", MissionPageModel.SlotText("No slot", template));
        Assert.Equal("ol_gate > Gate ambush", MissionView.PrefabBarTitle("ol_gate", "Gate ambush"));
    }

    [Fact]
    public void TheTreeOfPrefabModeHasTheFieldsThePortsAndTheTriggers()
    {
        using var m = PrefabInstanceTests.Create();
        var model = m.Missions.PrefabMode(GateRef)!;

        var root = HierarchyModel.PrefabTree(model, showBaseGame: false);

        Assert.Equal("Gate ambush", root.Name);
        Assert.Equal(["Fields", "Ports", "Triggers"], root.Children.Select(c => c.Name));
        Assert.Equal(["Enemy formation", "Spawn after"], root.Children[0].Children.Select(c => c.Name));
        Assert.Equal(new MissionRef(GateRef.Script, MissionItemKind.Mission, Key: "field:delay"), root.Children[0].Children[1].Item);
        Assert.Equal(["spawn", "end"], root.Children[2].Children.Select(c => c.Name));
    }

    // ---- Library and graph ----

    [Fact]
    public void TheLibraryHasThePrefabsBuiltInFirstThenTheModWithTheLockOfALowerMod()
    {
        using var m = MissionEdits.Create(modFiles: new Dictionary<string, string> { [@"prefabs\missions\ol_gate.ini"] = Gate },
            lowerMod: new Dictionary<string, string> { [@"prefabs\missions\ol_low.ini"] = Gate.Replace("id = ol_gate", "id = ol_low") });

        var items = MissionLibrary.Prefabs(m.Missions.Prefabs()).Items.ToList();

        Assert.Equal(BuiltinPrefabs.Ids.Count + 2, items.Count);
        Assert.All(items.Take(BuiltinPrefabs.Ids.Count), i => Assert.True(i.Prefab!.Builtin));
        Assert.Equal(("Gate ambush", null), (items[^2].Name, items[^2].Lock));
        Assert.Equal(MissionLibrary.LockTip("low"), items[^1].Lock);
        Assert.All(items, i => Assert.Equal(LibraryKind.Prefab, i.Kind));
    }

    [Fact]
    public void APrefabOfTheLibraryPlacesOnAnEmptyPlaceAndNotOnATrigger()
    {
        using var m = PrefabInstanceTests.Create();
        var model = new GraphEditModel(MissionGraph.Build(m.Missions.Model(Script).Analysis, new GraphOptions(GraphLens.Logic)));
        var item = MissionLibrary.Prefab(m.Missions.Prefab(GateRef)!);
        var place = new GraphPoint(100, 200);

        Assert.Null(GraphEditModel.EmptyDropRefusal(item));
        Assert.Null(model.DropOnNode(item, model.Graph.TriggerNode(model.Graph.Index.Trigger("ol_wave")!)!));
        Assert.True(GraphEditModel.Fits(new QuickAddContext(null, null, QuickAddFilter.All), item));
        Assert.False(GraphEditModel.Fits(new QuickAddContext(null, null, QuickAddFilter.Triggers), item));
        Assert.Equal(new PlacePrefabInstance(TestScript, GateRef) { Place = place },
            Assert.Single(model.DropOnEmpty(item, place)!.Commands));
        Run(m, model.QuickAdd(new QuickAddContext(null, null, QuickAddFilter.All) { Place = place }, item));
        Assert.Single(m.Missions.Graph(Script).Instances);
    }

    /// <summary>A drag from an output pin to an empty place offers the prefabs with a trigger in quick add (section 8.2 of
    /// the M6b design). The pick is one batch: the new instance, and the link of the pin to its first trigger.</summary>
    [Fact]
    public void QuickAddFromAPinPlacesAPrefabAndLinksThePinToItsFirstTrigger()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_gate_1");
        var model = new GraphEditModel(MissionGraph.Build(m.Missions.Model(Script).Analysis, new GraphOptions(GraphLens.Logic)));
        var gate = MissionLibrary.Prefab(m.Missions.Prefab(GateRef)!);
        var escort = MissionLibrary.Prefab(m.Missions.Prefab(PrefabRef.BuiltinOf("escort"))!);
        var from = new GraphEnd(model.Graph.TriggerNode(model.Graph.Index.Trigger("ol_wave")!)!, null);
        var place = new GraphPoint(300, 40);
        var context = new QuickAddContext(from, null, QuickAddFilter.Triggers) { Place = place };

        Assert.True(GraphEditModel.Fits(context, gate));
        Assert.False(GraphEditModel.Fits(context, escort));
        var batch = model.QuickAdd(context, gate)!;
        Assert.Equal("Place prefab Gate ambush", batch.Label);
        Assert.Equal(new PlacePrefabInstance(TestScript, GateRef) { Id = "ol_gate_2", Place = place }, batch.Commands[0]);
        Assert.Equal(new LinkTriggers(T("ol_wave"), "ol_gate_2_spawn", LinkKind.Start), batch.Commands[1]);
        Run(m, batch);
        Assert.Equal("Act_ActTrig = ol_gate_2_spawn", m.Lines("ol_wave")[^1]);
        Assert.Equal(2, m.Missions.Graph(Script).Instances.Count);
        Assert.Equal("Place prefab Gate ambush", m.Session.UndoLabel);
    }

    /// <summary>The prefab frame of an instance in the graph of the test script.</summary>
    private static (GraphEditModel Model, GraphFrame Frame) FrameOf(MissionEdits m, string id)
    {
        var model = new GraphEditModel(MissionGraph.Build(m.Missions.Model(Script).Analysis, new GraphOptions(GraphLens.Logic)));
        return (model, model.Graph.Frames.Single(f => f.Instance?.Id == id));
    }

    [Fact]
    public void DeleteAndDuplicateOfALinkedFrameRemoveAndPlaceTheInstance()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["9"]));
        var (model, frame) = FrameOf(m, "ol_w1");

        Assert.Equal(new RemovePrefabInstance(I("ol_w1")), Assert.Single(model.Delete([frame])!.Commands));
        Assert.Equal(new RemovePrefabInstance(I("ol_w1")), Assert.Single(model.Delete([frame.Node])!.Commands));
        var copy = Assert.IsType<PlacePrefabInstance>(Assert.Single(model.DuplicateInstance(frame)!.Commands));
        Assert.Equal([new PrefabFieldInput("formation", ["ol_form"]), new PrefabFieldInput("delay", ["9"])], copy.Values);
        Run(m, model.DuplicateInstance(frame));
        Assert.Equal(2, m.Missions.Graph(Script).Instances.Count);
        Assert.Equal(["Open prefab", "Select prefab asset", "Unpack", "Delete instance"],
            model.FrameActions(frame.Instance!, m.Missions.FreePrefabId, editable: true).Select(a => a.Label));
        Assert.Equal(["Open prefab", "Select prefab asset"],
            model.FrameActions(frame.Instance!, m.Missions.FreePrefabId, editable: false).Select(a => a.Label));
    }

    [Fact]
    public void DeleteAndDuplicateOfAMatchedFrameRemoveAndCopyItsTriggers()
    {
        using var m = MissionEdits.Create(MatchedText);
        var (model, frame) = FrameOf(m, "success/ol_win");

        Assert.Equal([new RemoveTrigger(T("ol_win"))], model.Delete([frame])!.Commands);
        Run(m, model.DuplicateInstance(frame));
        Assert.Equal(["nickname = ol_win_2", "Cnd_True = no_params", "Act_AdjAcct = 1000", "Act_ChangeState = SUCCEED"],
            m.Lines("ol_win_2"));
        var actions = model.FrameActions(frame.Instance!, m.Missions.FreePrefabId, editable: true);
        Assert.Equal(["Open prefab", "Save as prefab...", "Delete instance"], actions.Select(a => a.Label));
        Assert.Equal(new PrefabModeLink(PrefabRef.BuiltinOf("success")), actions[0].Link);
    }

    /// <summary>Delete and Ctrl+D on the row of a prefab instance in the Hierarchy give the batches of the graph (section
    /// 8.9 of the M6b design).</summary>
    [Fact]
    public void DeleteAndDuplicateOfAnInstanceRowOfTheHierarchyGiveTheBatchesOfTheGraph()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var analysis = m.Missions.Model(Script).Analysis;
        var instance = analysis.Prefabs!.Instances.Single(i => i.Id == "ol_w1");
        var row = new MissionRef(Script, MissionItemKind.PrefabInstance, Key: instance.Key);
        var (model, frame) = FrameOf(m, "ol_w1");

        var delete = MissionTreeEdits.Delete(analysis, row, null)!;
        Assert.Equal(("Delete instance ol_w1", new RemovePrefabInstance(I("ol_w1"))), (delete.Label, Assert.Single(delete.Commands)));
        Assert.Equal(MissionJson.Write(model.DuplicateInstance(frame)!.Commands[0]),
            MissionJson.Write(Assert.Single(MissionTreeEdits.Duplicate(analysis, row)!.Commands)));
        Run(m, MissionTreeEdits.Duplicate(analysis, row));
        Assert.Equal(2, m.Missions.Graph(Script).Instances.Count);

        using var matched = MissionEdits.Create(MatchedText);
        var retail = matched.Missions.Model(Script).Analysis;
        var win = new MissionRef(Script, MissionItemKind.PrefabInstance,
            Key: retail.Prefabs!.Instances.Single(i => i.Id == "success/ol_win").Key);
        Assert.Equal([new RemoveTrigger(T("ol_win"))], MissionTreeEdits.Delete(retail, win, null)!.Commands);
        Run(matched, MissionTreeEdits.Duplicate(retail, win));
        Assert.True(matched.Has("ol_win_2"));
    }

    // ---- Outputs of linked instances (sections 5.3 and 5.5) ----

    [Fact]
    public void TheOutputsOfALinkedInstanceLinkItsPortToTriggersOfTheMission()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var output = Field(InstancePage(m, "ol_w1"), "outputs", "When all are destroyed");

        Assert.Equal(EditWidget.List, output.Edit!.Widget);
        Assert.Empty(output.List!.Rows);
        var rows = output.List.Add(0, new ListFieldRow("done", ["ol_end"], null));
        One(new SetPrefabPort(I("ol_w1"), "done", ["ol_end"]), output.Edit.MakeList!(rows));
        Run(m, output.Edit.MakeList!(rows));

        Assert.Contains("Act_ActTrig = ol_end", m.Lines("ol_w1_end"));
        Assert.Single(Field(InstancePage(m, "ol_w1"), "outputs", "When all are destroyed").List!.Rows);
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule == "P01");
    }

    /// <summary>The model of the graph of the test script with all frames open, at the lines level.</summary>
    private static GraphEditModel OpenGraph(MissionEdits m) =>
        new(MissionGraph.Build(m.Missions.Model(Script).Analysis, new GraphOptions(GraphLens.Logic, ShowLines: true)));

    private static GraphNode Node(GraphEditModel model, string trigger) => model.Graph.TriggerNode(model.Graph.Index.Trigger(trigger)!)!;

    [Fact]
    public void ALinkDragFromAMemberLinksTheOutputOfItsRole()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var model = OpenGraph(m);
        var start = new GraphEnd(Node(model, "ol_start"), Node(model, "ol_start").Pin(GraphPinKind.Start)!);

        var link = model.Link(new GraphEnd(Node(model, "ol_w1_end"), null), start);

        Assert.Equal(new SetPrefabPort(I("ol_w1"), "done", ["ol_start"]), Assert.Single(link.Batch!.Commands), Equal);
        Run(m, link.Batch);
        Assert.Contains("Act_ActTrig = ol_start", m.Lines("ol_w1_end"));

        // The member spawn has no output, so a link from it refuses with the reason.
        model = OpenGraph(m);
        var refused = model.Link(new GraphEnd(Node(model, "ol_w1_spawn"), null),
            new GraphEnd(Node(model, "ol_end"), Node(model, "ol_end").Pin(GraphPinKind.Start)!));
        Assert.Equal(GraphEditModel.MemberLinkRefusal("ol_w1_spawn", "ol_w1"), refused.Reason);

        // Remove link takes the trigger from the output.
        var output = model.Graph.Links.First(l => l.From == Node(model, "ol_w1_end") && l.To == Node(model, "ol_start"));
        Assert.Equal(new SetPrefabPort(I("ol_w1"), "done", []), Assert.Single(model.Unlink(output)!.Commands), Equal);
        Run(m, model.Unlink(output));
        Assert.DoesNotContain("Act_ActTrig = ol_start", m.Lines("ol_w1_end"));
    }

    /// <summary>Compares two commands by their JSON form, because a list of a command compares by reference.</summary>
    private static readonly IEqualityComparer<MissionCommand> Equal = new JsonEquality();

    private sealed class JsonEquality : IEqualityComparer<MissionCommand>
    {
        public bool Equals(MissionCommand? x, MissionCommand? y) =>
            x != null && y != null && MissionJson.Write(x) == MissionJson.Write(y);

        public int GetHashCode(MissionCommand obj) => MissionJson.Write(obj).GetHashCode();
    }

    [Fact]
    public void ADuplicateKeepsTheRemovedMembersAndTheOutputs()
    {
        using var m = PrefabInstanceTests.Create();
        m.Apply(new PlacePrefabInstance(TestScript, GateRef)
        {
            Id = "ol_w1", Values = [new PrefabFieldInput("formation", ["ol_form"])], Ports = [new InstancePort("done", "ol_start")],
        });
        m.Apply(new RemoveTrigger(T("ol_w1_spawn")));
        var (model, frame) = FrameOf(m, "ol_w1");

        var copy = Assert.IsType<PlacePrefabInstance>(Assert.Single(model.DuplicateInstance(frame)!.Commands));

        Assert.Equal(["spawn"], copy.Removed);
        Assert.Equal([new InstancePort("done", "ol_start")], copy.Ports);
        Run(m, model.DuplicateInstance(frame));
        var record = m.Missions.Graph(Script).Instances.Single(i => i.Id != "ol_w1");
        Assert.Equal(["spawn"], record.Removed);
    }

    // ---- The selection after a prefab command (section 8.10) ----

    [Fact]
    public void TheSelectionStaysOnAnInstanceThatGetsAnotherPrefab()
    {
        using var m = PrefabInstanceTests.Create();
        m.Apply(new PlacePrefabInstance(TestScript, PrefabRef.BuiltinOf("spawnWave")) { Id = "ol_b" });
        m.Apply(new SetPrefabField(I("ol_b"), "spawnWhen", ["Cnd_Timer = 5"]));
        string Key() => m.Missions.Model(Script).Prefabs!.Instances.Single(i => i.Id == "ol_b").Key;
        var key = Key();
        var variant = Field(InstancePage(m, "ol_b"), "fields", "Spawn when").Actions.Single(a => a.Label == "Make a variant with this value");

        var result = m.Missions.Apply(variant.Batch!);

        var selection = MissionWorkspace.SelectionOf(m.Missions.Model(Script).Analysis, result.Select!);
        Assert.Equal(new MissionRef(Script, MissionItemKind.PrefabInstance, Key: key), selection);
        Assert.Null(MissionWorkspace.NewPrefabOf(result));
        Assert.Equal("Spawn wave variant ol_b", MissionPageModel.For(Context(m), selection!).Title);

        var rename = m.Missions.Apply(new RenamePrefabAsset(PrefabRef.ModOf("spawnwave_variant"), "ol_wave_b"));
        Assert.Equal(key, Key());
        Assert.Equal(PrefabRef.ModOf("ol_wave_b"), MissionWorkspace.RenamedPrefab(PrefabRef.ModOf("spawnwave_variant"), rename.Renames));

        var unpack = m.Missions.Apply(new UnpackPrefabInstance(I("ol_b")));
        Assert.Equal(MissionItemKind.Trigger, MissionWorkspace.SelectionOf(m.Missions.Model(Script).Analysis, unpack.Select!)!.Kind);
    }

    [Fact]
    public void MakeVariantOfABuiltInPrefabSelectsTheVariantForPrefabMode()
    {
        using var m = PrefabInstanceTests.Create();
        var prefab = PrefabRef.BuiltinOf("spawnWave");
        var model = m.Missions.PrefabMode(prefab)!;
        var page = MissionPageModel.For(Context(m, model.Analysis, model), MissionRef.Of(prefab.Script));

        var result = m.Missions.Apply(page.PageActions.Single(a => a.Label == "Make variant").Batch!);

        Assert.Equal(PrefabRef.ModOf("spawnwave_variant"), MissionWorkspace.NewPrefabOf(result));
        Assert.Equal("Spawn wave variant", m.Missions.Prefab(PrefabRef.ModOf("spawnwave_variant"))!.Name);
        // A second variant gets another name in the library (UI rule 9).
        m.Apply(new CreatePrefabAsset("spawnwave_variant2") { VariantOf = prefab });
        Assert.Equal("Spawn wave variant 2", m.Missions.Prefab(PrefabRef.ModOf("spawnwave_variant2"))!.Name);
    }

    [Fact]
    public void APrefabOfALowerModOffersMakeVariantOnItsPage()
    {
        using var m = MissionEdits.Create(lowerMod: new Dictionary<string, string>
        {
            [@"prefabs\missions\ol_low.ini"] = Gate.Replace("id = ol_gate", "id = ol_low"),
        });
        var prefab = PrefabRef.ModOf("ol_low");
        var model = m.Missions.PrefabMode(prefab)!;

        var page = MissionPageModel.For(Context(m, model.Analysis, model), MissionRef.Of(prefab.Script));

        Assert.Equal(PrefabModeModel.LowerModBanner("low"), page.ReadOnly);
        Assert.Equal(["Make variant"], page.PageActions.Select(a => a.Label));
    }

    [Fact]
    public void ThePrefabPageRenamesThePrefabInTheLibrary()
    {
        using var m = PrefabInstanceTests.Create();
        var model = m.Missions.PrefabMode(GateRef)!;
        var name = Field(MissionPageModel.For(Context(m, model.Analysis, model), MissionRef.Of(GateRef.Script)), "prefab", "Name");

        One(new RenamePrefabAsset(GateRef, "ol_gate") { NewName = "Gate wave" }, name.Edit!.Make!("Gate wave"));
        Run(m, name.Edit.Make!("Gate wave"));

        Assert.Equal("Gate wave", m.Missions.Prefab(GateRef)!.Name);
        One(new RenamePrefabAsset(GateRef, "ol_gate") { NewName = "Gate b" }, OpenLancer.Editor.Assets.AssetsPanel.RenameBatch("ol_gate", "Gate b", file: false));
        One(new RenamePrefabAsset(GateRef, "ol_gate_b"), OpenLancer.Editor.Assets.AssetsPanel.RenameBatch("ol_gate", "ol_gate_b", file: true));
    }

    [Fact]
    public void ThePrefabPageOfAFileWithAProblemShowsTheProblemAndTheText()
    {
        using var m = PrefabInstanceTests.Create(new Dictionary<string, string>
        {
            [@"prefabs\missions\ol_bad.ini"] = Gate.Replace("id = ol_gate", "id = ol_bad").Replace("Cnd_Timer = {delay}", "Cnd_Timer = {nothing}"),
        });
        var prefab = PrefabRef.ModOf("ol_bad");
        var model = m.Missions.PrefabMode(prefab)!;

        var page = MissionPageModel.For(Context(m, model.PageAnalysis, model), MissionRef.Of(prefab.Script));

        var step = page.Completeness!.Steps.Single(s => s.Name == "Prefab file");
        Assert.Equal(CompletenessState.Missing, step.State);
        Assert.Equal(new MissionRef(prefab.Script, MissionItemKind.Mission, Key: "file"), step.GoTo);
        var file = page.PrefabFile!;
        Assert.Equal(@"prefabs\missions\ol_bad.ini", file.Path);
        var mark = Assert.Single(file.Marks);
        Assert.Contains("{nothing}", file.Text.Lines[mark.Line]);
        Assert.Equal(MissionPageModel.FileSection,
            MissionPageModel.For(Context(m, model.PageAnalysis, model), (MissionRef)step.GoTo!).FocusSection);
    }

    // ---- Prefab mode: new fields (section 5.7) ----

    [Fact]
    public void NewFieldFromAValueMakesAFieldAndBindsTheValue()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        var model = m.Missions.PrefabMode(GateRef)!;
        var end = model.Analysis!.Index.Trigger("end")!;
        var page = MissionPageModel.For(Context(m, model.Analysis, model), new MissionRef(GateRef.Script, MissionItemKind.Trigger, end.Section.Ref));
        var action = page.Section("startsWhen")!.Fields.SelectMany(f => f.Actions).Single(a => a.Label == MissionPageModel.NewFieldText);

        Run(m, action.Batch);

        var template = m.Missions.Prefab(GateRef)!.Template!;
        var field = Assert.Single(template.Fields, f => f.Id != "formation" && f.Id != "delay");
        Assert.Equal([PrefabRow.Of("-1")], field.Default);
        Assert.Contains(template.Section("end")!.Lines, l => l.Value == $"{{formation}}, {{{field.Id}}}");
        Assert.Contains("Cnd_Destroyed = ol_form, -1", m.Lines("ol_w1_end"));
    }

    [Fact]
    public void NewFieldFromALineMakesAConditionField()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        m.Apply(new UnbindValue(new RowRef(new TriggerRef(GateRef.Script, "spawn").Item, 1)) { Value = "seconds" });
        var model = m.Missions.PrefabMode(GateRef)!;
        var spawn = model.Analysis!.Index.Trigger("spawn")!;
        var page = MissionPageModel.For(Context(m, model.Analysis, model), new MissionRef(GateRef.Script, MissionItemKind.Trigger, spawn.Section.Ref));
        var action = Field(page, "startsWhen", "Event").Actions.Single(a => a.Label == MissionPageModel.NewLineFieldText);

        Run(m, action.Batch);

        var field = m.Missions.Prefab(GateRef)!.Template!.Field("condition")!;
        Assert.Equal((PrefabFieldKind.Condition, "Condition"), (field.Kind, field.Label));
        Assert.Equal([PrefabRow.Line("Cnd_Timer", "5")], field.Default);
        Assert.Contains("Cnd_Timer = 5", m.Lines("ol_w1_spawn"));
        // A line with a slot, or a line that names a member of the prefab, cannot be a default: each instance names its own
        // member. The other lines of the spawn trigger are such lines.
        model = m.Missions.PrefabMode(GateRef)!;
        page = MissionPageModel.For(Context(m, model.Analysis, model), MissionRef.OfSection(model.Analysis!.Index.Trigger("spawn")!.Section.Ref, -1));
        Assert.DoesNotContain(page.Sections.SelectMany(s => s.Fields).SelectMany(f => f.Actions), a => a.Label == MissionPageModel.NewLineFieldText);
    }

    // ---- Matched instances: the widgets of the write rules ----

    private const string EscortText = """
        [Mission]

        [ObjList]
        nickname = ol_follow
        Follow = Player, 300
        """;

    [Fact]
    public void AKeyFieldOfAMatchedInstanceHasTheWidgetOfItsValue()
    {
        using var m = MissionEdits.Create(EscortText);
        var page = InstancePage(m, "escort/ol_follow");
        var follows = Field(page, "fields", "Follows");
        var distance = Field(page, "fields", "Distance");

        Assert.Equal((EditWidget.Picker, "Player"), (follows.Edit!.Widget, follows.Edit.Value));
        Assert.Equal((EditWidget.WholeNumber, "300"), (distance.Edit!.Widget, distance.Edit.Value));
        Run(m, distance.Edit.Make!(500));

        Assert.Equal(["nickname = ol_follow", "Follow = Player, 500"], m.Lines(Item("ObjList", "ol_follow")));
    }

    [Fact]
    public void AConditionFieldOfAMatchedInstanceHasTheListOfOneCondition()
    {
        using var m = PrefabInstanceTests.Create();
        var analysis = m.Missions.Model(Script).Analysis;
        var instance = analysis.Prefabs!.Instances.First(i => i.Definition.Fields.Any(f => f.Write == PrefabWrite.Condition));
        var definition = instance.Definition.Fields.First(f => f.Write == PrefabWrite.Condition);
        var field = Field(InstancePage(m, instance.Id), "fields", definition.Label);

        Assert.Equal(EditWidget.List, field.Edit!.Widget);
        Assert.Single(field.List!.Rows);
    }

    // ---- Hierarchy ----

    [Fact]
    public void TheInstancesOfAPrefabOfTheModShareOneRowOfTheHierarchy()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2");
        var analysis = m.Missions.Model(Script).Analysis;

        var root = HierarchyModel.MissionTree(analysis, _ => "", showBaseGame: false);

        var rows = Flatten(root).Where(n => n.Id.StartsWith("kind:mod:", System.StringComparison.Ordinal)).ToList();
        var row = Assert.Single(rows);
        Assert.Equal(("kind:mod:ol_gate", 2), (row.Id, row.Children.Count));
        var page = MissionPageModel.For(Context(m), (MissionRef)row.Item!);
        Assert.Equal("Gate ambush", page.Title);
        Assert.Equal(2, page.Section("uses")!.Fields.Count);
    }

    private static IEnumerable<HierarchyNode> Flatten(HierarchyNode node) => node.Children.SelectMany(Flatten).Prepend(node);

    [Fact]
    public void AnInstanceOfAMissingPrefabShowsMissingPrefab()
    {
        using var m = PrefabInstanceTests.Create();
        Place(m, "ol_w1");
        Run(m, new MissionBatch("Delete", [new RemovePrefabAsset(GateRef)]) { Confirm = true });
        var analysis = m.Missions.Model(Script).Analysis;

        Assert.Equal(MissionGraph.MissingPrefab, InstancePage(m, "ol_w1").Kind);
        Assert.Contains(Flatten(HierarchyModel.MissionTree(analysis, _ => "", showBaseGame: false)),
            n => n.Id.StartsWith("inst:", System.StringComparison.Ordinal) && n.Detail == MissionGraph.MissingPrefab);
        Assert.StartsWith(MissionGraph.MissingPrefab, FrameOf(m, "ol_w1").Frame.Title);
    }

    [Fact]
    public void PrefabModeHasNoActItems()
    {
        using var m = PrefabInstanceTests.Create();
        var model = m.Missions.PrefabMode(GateRef)!;

        Assert.DoesNotContain(MissionTreeEdits.CreateKindsOf(model.Analysis!), k => k.Kind == "act");
        Assert.Contains(MissionTreeEdits.CreateKindsOf(m.Missions.Model(Script).Analysis), k => k.Kind == "act");
        var graph = new GraphEditModel(MissionGraph.Build(model.Analysis!, new GraphOptions(GraphLens.Logic)));
        Assert.False(graph.HasActs);
        Assert.Null(graph.NewAct(graph.Graph.Nodes));
    }

    // ---- Assets ----

    [Fact]
    public void TheAssetsHaveACategoryOfTheMissionPrefabsOfTheMods()
    {
        using var m = MissionEdits.Create(modFiles: new Dictionary<string, string> { [@"prefabs\missions\ol_gate.ini"] = Gate },
            lowerMod: new Dictionary<string, string> { [@"prefabs\missions\ol_low.ini"] = Gate.Replace("id = ol_gate", "id = ol_low") });
        var assets = m.Session.Assets;

        var gate = assets.Rows("prefabs").Single(r => r.Nickname == "ol_gate");
        Assert.Equal((AssetKind.MissionPrefab, "2 fields", ValueOrigin.ActiveMod, AssetMark.None),
            (gate.Asset.Kind, gate.Detail, gate.Origin, gate.Mark));
        Assert.Equal(@"prefabs\missions\ol_gate.ini", gate.Asset.File);
        Assert.Equal((ValueOrigin.LowerMod, "low"), (assets.Rows("prefabs").Single(r => r.Nickname == "ol_low").Origin,
            assets.Rows("prefabs").Single(r => r.Nickname == "ol_low").ModId));
        Assert.Equal(2, assets.Categories.Single(c => c.Id == "prefabs").Count);

        m.Apply(new CreatePrefabAsset("ol_copy") { VariantOf = GateRef });

        var copy = assets.Find(AssetKind.MissionPrefab, "ol_copy")!;
        Assert.Equal(("Variant of ol_gate", AssetMark.Added | AssetMark.Unsaved), (copy.Detail, copy.Mark));
        Assert.True(m.Session.Save().Saved);
        Assert.Equal(AssetMark.None, assets.Find(AssetKind.MissionPrefab, "ol_copy")!.Mark);
    }

    [Fact]
    public void AnOverrideHasTheInfoTextOfTheDesign()
    {
        Assert.Equal(("Override", "A field of this instance that differs from the prefab. Only this instance has the value.",
                "The lines of this instance. The prefab does not change until you apply the value."),
            (PrefabLinks.OverrideHelp.Title, PrefabLinks.OverrideHelp.WhatItIs, PrefabLinks.OverrideHelp.WhatItWrites));
    }

    [Fact]
    public void TheDotOfAnInstanceWithOverridesCountsThem()
    {
        Assert.Equal("This instance has 1 override.", MissionPageModel.OverridesDotTip(1));
        Assert.Equal("This instance has 3 overrides.", MissionPageModel.OverridesDotTip(3));
    }
}
