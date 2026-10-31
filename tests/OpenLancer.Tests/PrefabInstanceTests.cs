using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using Xunit;
using static OpenLancer.Tests.MissionEdits;

namespace OpenLancer.Tests;

/// <summary>
/// The linked prefab instances (sections 5.1 and 5.5 to 5.7 of the M6b design): place, fields, overrides, Revert, Apply
/// to prefab, unpack, remove, the removed-member override of decision D676 c, and the warnings P01, P02 and P03. All
/// names are invented.
/// </summary>
public sealed class PrefabInstanceTests
{
    /// <summary>A prefab asset of the active mod.</summary>
    internal const string Gate = PrefabTemplate.Header + """

        [Prefab]
        id = ol_gate
        name = Gate ambush
        kind = beat
        format = 1

        [Field]
        id = formation
        label = Enemy formation
        kind = formation
        per_instance = true
        default = none

        [Field]
        id = delay
        label = Spawn after
        kind = number
        unit = s
        default = 5

        [Port]
        id = done
        label = When all are destroyed
        role = end

        [Trigger]
        role = spawn
        nickname = {instance}_spawn
        Cnd_Timer = {delay}
        Act_SpawnFormation = {formation}
        Act_ActTrig = {instance}_end

        [Trigger]
        role = end
        nickname = {instance}_end
        Cnd_Destroyed = {formation}, -1
        Act_ActTrig = {port:done}

        """;

    internal const string Two = @"DATA\MISSIONS\OL_TWO\ol_two.ini";

    private const string TwoText = """
        [Mission]

        [MsnFormation]
        nickname = ol_form_b
        position = 0, 0, 0

        [Trigger]
        nickname = ol_two_start
        InitState = ACTIVE
        Cnd_True = no_params
        """;

    internal static readonly PrefabRef GateRef = PrefabRef.ModOf("ol_gate");

    /// <summary>The test profile with the asset ol_gate in the active mod and a second script.</summary>
    internal static MissionEdits Create(Dictionary<string, string>? more = null)
    {
        var files = new Dictionary<string, string> { [@"prefabs\missions\ol_gate.ini"] = Gate };
        foreach (var (path, text) in more ?? [])
            files[path] = text;
        return MissionEdits.Create(setup: ws => ws.WriteBase(Two, TwoText), modFiles: files);
    }

    internal static InstanceRef I(string id, string script = Script) => new(script, id);

    internal static MissionEditResult Place(MissionEdits m, string id, string script = Script, string formation = "ol_form") =>
        m.Apply(new PlacePrefabInstance(new ScriptRef(script), GateRef)
        {
            Id = id, Values = [new PrefabFieldInput("formation", [formation])],
        });

    private static InstanceRecord Record(MissionEdits m, string id, string script = Script) =>
        m.Missions.Graph(script).Instance(id) ?? throw new InvalidOperationException($"No record {id}.");

    private static MissionPrefabInstance Instance(MissionEdits m, string id, string script = Script) =>
        m.Missions.Model(script).Prefabs!.Instances.Single(i => i.Linked?.Record.Id == id);

    private static IEnumerable<Diagnostic> Rule(MissionEdits m, string rule, string script = Script) =>
        m.Missions.Model(script).Problems.Where(p => p.Rule == rule);

    /// <summary>The engine form of both scripts, their graph files, the asset files and the text.</summary>
    internal static string State(MissionEdits m) => m.State() + "\n---\n" +
        string.Join("\n", m.Session.OpenDocument(Two)!.Working.Select(s => $"[{s.Name}]\n" + string.Join("\n", s.Select(Line)))) +
        "\ngraph two\n" + m.Session.ModFileText(GraphFile.PathOf(Two)) +
        "\nassets\n" + string.Join("\n", m.Missions.Prefabs().Where(p => p.Active).Select(p => p.Ref + "\n" + p.Text));

    [Fact]
    public void APlaceWritesTheCompiledSectionsAndTheRecord()
    {
        using var m = Create();

        var result = Place(m, "ol_w1");

        Assert.Equal(["nickname = ol_w1_spawn", "Cnd_Timer = 5", "Act_SpawnFormation = ol_form", "Act_ActTrig = ol_w1_end"],
            m.Lines("ol_w1_spawn"));
        Assert.Equal(["nickname = ol_w1_end", "Cnd_Destroyed = ol_form, -1"], m.Lines("ol_w1_end"));
        var record = Record(m, "ol_w1");
        Assert.Equal(("mod:ol_gate", 2), (record.Prefab, record.Members.Count));
        Assert.Equal([new InstanceMember("spawn", "ol_w1_spawn"), new InstanceMember("end", "ol_w1_end")], record.Members);
        Assert.Equal([new InstanceValue("formation", ["ol_form"])], record.Values);
        Assert.Empty(record.Overrides);
        Assert.Equal(m.Missions.Prefab(GateRef)!.Template!.ContentHash, record.PrefabHash);
        Assert.NotNull(record.LinesHash);
        Assert.Equal(I("ol_w1").ToString(), result.Select);
        var instance = Instance(m, "ol_w1");
        Assert.Equal(("ol_w1", "Gate ambush", 2), (instance.Id, instance.Definition.Name, instance.Claimed.Count));
        Assert.DoesNotContain(m.Missions.Model(Script).Problems, p => p.Rule is "P01" or "P02" or "P03");
    }

    [Fact]
    public void APlaceWithAPortAndAPlaceWritesTheLinksAndTheNodes()
    {
        using var m = Create();

        m.Apply(new PlacePrefabInstance(TestScript, GateRef)
        {
            Id = "ol_w1", Values = [new PrefabFieldInput("formation", ["ol_form"])], Ports = [new InstancePort("done", "ol_end")],
            Place = new GraphPoint(100, 200),
        });

        Assert.Contains("Act_ActTrig = ol_end", m.Lines("ol_w1_end"));
        var graph = m.Missions.Graph(Script);
        Assert.Equal(new GraphNodePlace("ol_w1_spawn", 100, 200), graph.Place("ol_w1_spawn"));
        Assert.NotNull(graph.Place("ol_w1_end"));
    }

    [Fact]
    public void ATakenNameAndABadValueRefuse()
    {
        using var m = Create();
        Place(m, "ol_w1");

        m.Refuse(new PlacePrefabInstance(TestScript, GateRef) { Id = "ol_w1" }, MissionErrors.NameTaken);
        m.Refuse(new PlacePrefabInstance(TestScript, GateRef) { Id = "ol_w2", Values = [new PrefabFieldInput("delay", ["soon"])] },
            MissionErrors.FieldValue);
        m.Refuse(new PlacePrefabInstance(TestScript, GateRef) { Id = "ol_w3", Values = [new PrefabFieldInput("speed", ["5"])] },
            MissionErrors.FieldNotFound);
        m.Refuse(new PlacePrefabInstance(TestScript, PrefabRef.ModOf("ol_none")), MissionErrors.PrefabNotFound);
    }

    /// <summary>A prefab does not go into a prefab: the template of a prefab of the mod and of a built-in prefab refuse
    /// a place, and nothing changes. A template has no graph file for the record of an instance.</summary>
    [Fact]
    public void APrefabDoesNotGoIntoAPrefab()
    {
        using var m = Create();

        var error = m.Refuse(new PlacePrefabInstance(new ScriptRef("prefab:mod:ol_gate"), GateRef) { Id = "ol_w1" },
            MissionErrors.PrefabInPrefab);
        m.Refuse(new PlacePrefabInstance(new ScriptRef("prefab:builtin:spawnWave"), GateRef), MissionErrors.PrefabInPrefab);

        Assert.Equal(EditErrorKind.Refused, error.Kind);
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void EachBuiltInPrefabPlacesWithItsDefaults()
    {
        using var m = Create();

        foreach (var id in BuiltinPrefabs.Ids)
        {
            var result = m.Missions.Apply(new PlacePrefabInstance(TestScript, PrefabRef.BuiltinOf(id)));
            Assert.True(result.Status == EditStatus.Applied, $"{id}: {string.Join(" ", result.Errors.Select(e => e.Message))}");
        }

        var model = m.Missions.Model(Script);
        Assert.Equal(BuiltinPrefabs.Ids.Count, model.Prefabs!.Instances.Count(i => i.Linked != null));
        Assert.All(model.Prefabs.Instances.Where(i => i.Linked != null),
            i => Assert.Equal(i.Linked!.Record.Prefab, "builtin:" + i.Definition.Id));
    }

    [Fact]
    public void AFieldEditIsAnOverrideAndTheDefaultValueRemovesIt()
    {
        using var m = Create();
        Place(m, "ol_w1");

        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));

        Assert.Contains("Cnd_Timer = 8", m.Lines("ol_w1_spawn"));
        Assert.Equal([new InstanceValue("delay", ["8"])], Record(m, "ol_w1").Overrides);
        var linked = Instance(m, "ol_w1").Linked!;
        Assert.True(linked.Field("delay")!.Override);
        Assert.Equal(1, linked.OverrideCount);
        Assert.Equal(["delay"], m.Missions.Queries.GetPrefabInstance(I("ol_w1").ToString()).Overrides);
        Assert.Empty(Rule(m, "P01"));

        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["5"]));

        Assert.Empty(Record(m, "ol_w1").Overrides);
        Assert.Contains("Cnd_Timer = 5", m.Lines("ol_w1_spawn"));
    }

    [Fact]
    public void APerInstanceFieldNeverShowsAsAnOverride()
    {
        using var m = Create();
        Place(m, "ol_w1");

        m.Apply(new SetPrefabField(I("ol_w1"), "formation", ["ol_ship_1"]));

        Assert.Contains("Act_SpawnFormation = ol_ship_1", m.Lines("ol_w1_spawn"));
        Assert.Equal([new InstanceValue("formation", ["ol_ship_1"])], Record(m, "ol_w1").Values);
        Assert.False(Instance(m, "ol_w1").Linked!.Field("formation")!.Override);
        Assert.Equal(0, Instance(m, "ol_w1").Linked!.OverrideCount);
    }

    [Fact]
    public void RevertGivesTheValueOfThePrefabBack()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));

        m.Apply(new RevertOverride(I("ol_w1")) { Field = "delay" });

        Assert.Contains("Cnd_Timer = 5", m.Lines("ol_w1_spawn"));
        Assert.Empty(Record(m, "ol_w1").Overrides);
        Assert.Equal(MissionErrors.NoOverride, m.Refuse(new RevertOverride(I("ol_w1")) { Field = "delay" }, MissionErrors.NoOverride).Detail);
    }

    [Fact]
    public void ApplyToPrefabChangesThreeInstancesInTwoScriptsInOneUndoEntry()
    {
        using var m = Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2");
        Place(m, "ol_w3", Two, "ol_form_b");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));
        var before = State(m);

        var result = m.Apply(new ApplyOverrideToPrefab(I("ol_w1")) { Field = "delay" });

        Assert.Contains("Cnd_Timer = 8", m.Lines("ol_w1_spawn"));
        Assert.Contains("Cnd_Timer = 8", m.Lines("ol_w2_spawn"));
        Assert.Contains("Cnd_Timer = 8", m.Lines(T("ol_w3_spawn", Two).Item));
        Assert.Empty(Record(m, "ol_w1").Overrides);
        Assert.Equal([PrefabRow.Of("8")], m.Missions.Prefab(GateRef)!.Template!.Field("delay")!.Default);
        Assert.Contains("Prefab 'Gate ambush' changed 3 instances in 2 missions.", result.Notes);
        Assert.Empty(Rule(m, "P03"));
        Assert.Empty(Rule(m, "P03", Two));

        m.Session.Undo();

        Assert.Equal(before, State(m));
    }

    [Fact]
    public void AHandEditGivesP01AndThePropagationSkipsIt()
    {
        using var m = Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));
        m.Session.Execute(MissionTestData.Raw(EditCommand.SetLinesFromText(T("ol_w2_spawn").Item.Ref, "Cnd_Timer", ["9"])));

        var problem = Assert.Single(Rule(m, "P01"));
        Assert.Equal(PrefabLinks.HandEditMessage("ol_w2"), problem.Message);
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.True(Instance(m, "ol_w2").Linked!.HandEdited);

        var result = m.Apply(new ApplyOverrideToPrefab(I("ol_w1")) { Field = "delay" });

        Assert.Contains("Cnd_Timer = 9", m.Lines("ol_w2_spawn"));
        Assert.Contains(result.Notes, n => n.StartsWith("Prefab instance 'ol_w2' in"));
        Assert.Contains(result.Warnings, w => w.Rule == "P01");
    }

    [Fact]
    public void TheFixesOfP01CompileAgainOrUnpack()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Session.Execute(MissionTestData.Raw(EditCommand.SetLinesFromText(T("ol_w1_spawn").Item.Ref, "Cnd_Timer", ["9"])));
        var problem = Assert.Single(Rule(m, "P01"));
        var fixes = MissionFixes.Of(m.Missions.Model(Script).Analysis, problem);

        Assert.Equal(["compile", "unpack"], fixes.Select(f => f.Id));
        m.Apply(new ApplyFix(TestScript, problem.Subject!, "compile"));

        Assert.Contains("Cnd_Timer = 5", m.Lines("ol_w1_spawn"));
        Assert.Empty(Rule(m, "P01"));
    }

    [Fact]
    public void ABuiltInPrefabRefusesApply()
    {
        using var m = Create();
        m.Apply(new PlacePrefabInstance(TestScript, PrefabRef.BuiltinOf("spawnWave")) { Id = "ol_b" });
        m.Apply(new SetPrefabField(I("ol_b"), "spawnWhen", ["Cnd_Timer = 5"]));

        var error = m.Refuse(new ApplyOverrideToPrefab(I("ol_b")) { Field = "spawnWhen" }, MissionErrors.BuiltInPrefab);

        Assert.Equal("'Spawn wave' is a built-in prefab, so it cannot change. Make a variant of it in the mod, then apply to " +
                     "the variant.", error.Message);
    }

    [Fact]
    public void AVariantWithTheValueOfAnOverrideTakesTheInstance()
    {
        using var m = Create();
        m.Apply(new PlacePrefabInstance(TestScript, PrefabRef.BuiltinOf("spawnWave")) { Id = "ol_b" });
        m.Apply(new SetPrefabField(I("ol_b"), "spawnWhen", ["Cnd_Timer = 5"]));
        var lines = m.Lines("ol_b_spawn");

        m.Apply(new CreatePrefabAsset("ol_wave") { Name = "Gate wave", Source = I("ol_b"), Fields = ["spawnWhen"] });

        var record = Record(m, "ol_b");
        Assert.Equal(("mod:ol_wave", 0), (record.Prefab, record.Overrides.Count));
        var template = m.Missions.Prefab(PrefabRef.ModOf("ol_wave"))!.Template!;
        Assert.Equal(("Gate wave", "builtin:spawnWave"), (template.Name, template.VariantOf));
        Assert.Equal([PrefabRow.Line("Cnd_Timer", "5")], template.Field("spawnWhen")!.Default);
        Assert.Equal(lines, m.Lines("ol_b_spawn"));
        Assert.Empty(Rule(m, "P01"));
        Assert.Empty(Rule(m, "P03"));
    }

    [Fact]
    public void UnpackRemovesTheRecordAndTheTriggersStay()
    {
        using var m = Create();
        Place(m, "ol_w1");
        var lines = m.Lines("ol_w1_spawn");

        m.Apply(new UnpackPrefabInstance(I("ol_w1")));

        Assert.Empty(m.Missions.Graph(Script).Instances);
        Assert.Equal(lines, m.Lines("ol_w1_spawn"));
        Assert.DoesNotContain(m.Missions.Model(Script).Prefabs!.Instances, i => i.Linked != null);
    }

    [Fact]
    public void RemoveRemovesTheMembersAndTheRecordAndKeepsTheLinesThatNameThem()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Apply(new LinkTriggers(T("ol_start"), "ol_w1_spawn", LinkKind.Start));

        var result = m.Apply(new RemovePrefabInstance(I("ol_w1")));

        Assert.False(m.Has("ol_w1_spawn"));
        Assert.False(m.Has("ol_w1_end"));
        Assert.Empty(m.Missions.Graph(Script).Instances);
        Assert.Contains("Act_ActTrig = ol_w1_spawn", m.Lines("ol_start"));
        Assert.Single(result.Missing);
        Assert.Contains(result.Deletes, d => d.Ref == I("ol_w1").ToString());
    }

    [Fact]
    public void ALineEditOfAMemberRefusesAndADeleteIsARemovedMemberOverride()
    {
        using var m = Create();
        Place(m, "ol_w1");

        var error = m.Refuse(new AddAction(T("ol_w1_spawn"), LineSpec.Raw("Act_ActTrig", "ol_start")), MissionErrors.PrefabMember);
        Assert.Equal("Trigger 'ol_w1_spawn' is a part of prefab instance 'ol_w1'. Change a field of the instance, open the " +
                     "prefab, or unpack the instance first.", error.Message);
        m.Refuse(new RenameTrigger(T("ol_w1_spawn"), "ol_other"), MissionErrors.PrefabMember);
        m.Refuse(new DuplicateTrigger(T("ol_w1_spawn")), MissionErrors.PrefabMember);

        m.Apply(new RemoveTrigger(T("ol_w1_end")));

        Assert.Equal(["end"], Record(m, "ol_w1").Removed);
        Assert.Equal(1, Instance(m, "ol_w1").Linked!.OverrideCount);
        Assert.Empty(Rule(m, "P01"));
        Assert.Equal(["end"], m.Missions.Queries.GetPrefabInstance(I("ol_w1").ToString()).Removed);

        m.Apply(new RevertOverride(I("ol_w1")) { Member = "end" });

        Assert.Equal(["nickname = ol_w1_end", "Cnd_Destroyed = ol_form, -1"], m.Lines("ol_w1_end"));
        Assert.Empty(Record(m, "ol_w1").Removed);
        Assert.Empty(Rule(m, "P01"));
    }

    [Fact]
    public void ApplyOfARemovedMemberRemovesItFromThePrefabAndFromTheOtherInstances()
    {
        using var m = Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2");
        m.Apply(new RemoveTrigger(T("ol_w1_end")));

        m.Apply(new ApplyOverrideToPrefab(I("ol_w1")) { Member = "end" });

        Assert.Null(m.Missions.Prefab(GateRef)!.Template!.Section("end"));
        Assert.Empty(m.Missions.Prefab(GateRef)!.Template!.Ports);
        Assert.False(m.Has("ol_w2_end"));
        Assert.Empty(Record(m, "ol_w1").Removed);
    }

    [Fact]
    public void AMissingPrefabGivesP02()
    {
        using var m = Create(new Dictionary<string, string>
        {
            [@"graphs\DATA\MISSIONS\OL_TEST\ol_test.ini"] = GraphFile.Header + "\n[Instance]\nid = ol_lost\nprefab = mod:ol_none\n" +
                                                           "member = spawn, ol_wave\n",
        });

        var problem = Assert.Single(Rule(m, "P02"));

        Assert.Equal(PrefabLinks.MissingMessage("ol_lost", "ol_none"), problem.Message);
        Assert.Equal(T("ol_wave").Item.Ref, problem.Target);
        var instance = Instance(m, "ol_lost");
        Assert.True(instance.Linked!.Missing);
        Assert.Equal("ol_none", instance.Definition.Name);
    }

    [Fact]
    public void ADeleteOfAPrefabGivesP02OnItsInstancesAndOneUndoRestoresIt()
    {
        using var m = Create();
        Place(m, "ol_w1");
        var lines = m.Lines("ol_w1_spawn");

        var refused = m.Missions.Apply(new RemovePrefabAsset(GateRef));
        Assert.Equal((EditStatus.Refused, true), (refused.Status, refused.NeedsConfirmation));
        Assert.Equal(MissionErrors.PrefabInUse, Assert.Single(refused.Errors).Detail);

        var result = m.Missions.Apply(new MissionBatch("Delete", [new RemovePrefabAsset(GateRef)]) { Confirm = true });

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Null(m.Missions.Prefab(GateRef));
        Assert.Single(Rule(m, "P02"));
        Assert.Equal(lines, m.Lines("ol_w1_spawn"));
        Assert.Contains(result.NewProblems, p => p.Rule == "P02");

        m.Session.Undo();

        Assert.NotNull(m.Missions.Prefab(GateRef));
        Assert.Empty(Rule(m, "P02"));
    }

    [Fact]
    public void APrefabThatChangedOutsideTheSessionGivesP03AndTheOpenDoesNotCompile()
    {
        var record = GraphFile.Header + """

            [Instance]
            id = ol_w1
            prefab = mod:ol_gate
            prefab_hash = 0000000000000000
            member = spawn, ol_wave
            """;
        using var m = Create(new Dictionary<string, string> { [@"graphs\DATA\MISSIONS\OL_TEST\ol_test.ini"] = record });
        var lines = m.Lines("ol_wave");

        var problem = Assert.Single(Rule(m, "P03"));

        Assert.Equal(PrefabLinks.OutdatedMessage("Gate ambush", "ol_w1"), problem.Message);
        Assert.Equal(lines, m.Lines("ol_wave"));
        Assert.False(m.Session.CanUndo);
        Assert.Equal(["compile"], MissionFixes.Of(m.Missions.Model(Script).Analysis, problem).Select(f => f.Id));
    }

    [Fact]
    public void TheRenameOfATriggerThatAPortStartsMakesNoHandEdit()
    {
        using var m = Create();
        m.Apply(new PlacePrefabInstance(TestScript, GateRef)
        {
            Id = "ol_w1", Values = [new PrefabFieldInput("formation", ["ol_form"])], Ports = [new InstancePort("done", "ol_end")],
        });

        m.Apply(new RenameTrigger(T("ol_end"), "ol_finish"));

        Assert.Contains("Act_ActTrig = ol_finish", m.Lines("ol_w1_end"));
        Assert.Equal([new InstancePort("done", "ol_finish")], Record(m, "ol_w1").Ports);
        Assert.Empty(Rule(m, "P01"));
    }

    [Fact]
    public void TheRenameOfAPrefabFollowsInTheRecords()
    {
        using var m = Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2", Two, "ol_form_b");

        var result = m.Apply(new RenamePrefabAsset(GateRef, "ol_gate_b") { NewName = "Gate wave" });

        Assert.Null(m.Missions.Prefab(GateRef));
        Assert.Equal("Gate wave", m.Missions.Prefab(PrefabRef.ModOf("ol_gate_b"))!.Name);
        Assert.Equal("mod:ol_gate_b", Record(m, "ol_w1").Prefab);
        Assert.Equal("mod:ol_gate_b", Record(m, "ol_w2", Two).Prefab);
        Assert.Empty(Rule(m, "P03"));
        Assert.Equal(new ResultRename("prefab:mod:ol_gate", "prefab:mod:ol_gate_b"), Assert.Single(result.Renames));
    }

    [Fact]
    public void ThePrefabsOfTheLibraryHaveTheBuiltInPrefabsAndTheAssetsOfTheMod()
    {
        using var m = Create();

        var prefabs = m.Missions.Queries.ListPrefabs();

        Assert.Equal(BuiltinPrefabs.Ids.Count + 1, prefabs.Count);
        var gate = prefabs.Single(p => p.Ref == "prefab:mod:ol_gate");
        Assert.Equal(("act", false), (gate.ModId, gate.ReadOnly));
        Assert.Equal(["done"], gate.Ports);
        Assert.Equal(["formation", "delay"], gate.Fields.Select(f => f.Id));
        Assert.True(prefabs.Single(p => p.Ref == "prefab:builtin:spawnWave").ReadOnly);
    }

    /// <summary>A command of each prefab kind on a script with the instance ol_w1, whose delay overrides the
    /// prefab.</summary>
    internal static readonly Dictionary<string, Func<MissionCommand>> Commands = new()
    {
        ["place_prefab_instance"] = () => new PlacePrefabInstance(TestScript, GateRef)
        {
            Id = "ol_w9", Values = [new PrefabFieldInput("formation", ["ol_form"])], Place = new GraphPoint(10, 20),
        },
        ["set_prefab_field"] = () => new SetPrefabField(I("ol_w1"), "delay", ["12"]),
        ["set_prefab_port"] = () => new SetPrefabPort(I("ol_w1"), "done", ["ol_end", "ol_start"]),
        ["revert_override"] = () => new RevertOverride(I("ol_w1")) { Field = "delay" },
        ["apply_override_to_prefab"] = () => new ApplyOverrideToPrefab(I("ol_w1")) { Field = "delay" },
        ["compile_prefab_instance"] = () => new CommandGroup(
        [
            new RestoreInstanceRecord(TestScript, new InstanceRecord("ol_w1", "mod:ol_gate")
            {
                Members = [new InstanceMember("spawn", "ol_w1_spawn"), new InstanceMember("end", "ol_w1_end")],
                Values = [new InstanceValue("formation", ["ol_ship_1"])],
            }),
            new CompilePrefabInstance(I("ol_w1")),
        ]),
        ["unpack_prefab_instance"] = () => new UnpackPrefabInstance(I("ol_w1")),
        ["remove_prefab_instance"] = () => new RemovePrefabInstance(I("ol_w1")),
        ["remove_prefab_instance_lines"] = () => new RemovePrefabInstance(I("ol_w1")) { References = ReferencesMode.Remove },
        ["restore_instance_record"] = () => new RestoreInstanceRecord(TestScript, new InstanceRecord("ol_x", "mod:ol_gate")),
        ["create_prefab_asset"] = () => new CreatePrefabAsset("ol_copy") { VariantOf = GateRef },
        ["create_prefab_asset_source"] = () => new CreatePrefabAsset("ol_copy") { Source = I("ol_w1") },
        ["remove_prefab_asset"] = () => new RemovePrefabAsset(PrefabRef.ModOf("ol_spare")),
        ["rename_prefab_asset"] = () => new RenamePrefabAsset(GateRef, "ol_gate_b") { NewName = "Gate wave" },
        ["restore_prefab_asset"] = () => new RestorePrefabAsset(PrefabRef.ModOf("ol_spare")) { Text = Gate.Replace("ol_gate", "ol_spare").Replace("default = 5", "default = 6") },
        ["remove_trigger_member"] = () => new RemoveTrigger(T("ol_w1_end")),
    };

    public static IEnumerable<object[]> Kinds() => Commands.Keys.Select(k => new object[] { k });

    /// <summary>The script of the inverse tests: the instance ol_w1 with an override, an outside line that starts its
    /// spawn, and the asset ol_spare.</summary>
    private static MissionEdits Setup()
    {
        var m = Create(new Dictionary<string, string> { [@"prefabs\missions\ol_spare.ini"] = Gate.Replace("ol_gate", "ol_spare") });
        Place(m, "ol_w1");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));
        m.Apply(new LinkTriggers(T("ol_start"), "ol_w1_spawn", LinkKind.Start));
        return m;
    }

    /// <summary>Each prefab command and its inverse give the state before, with the same editor files. Undo gives the
    /// same state as the inverse, and Redo gives the same state as the command (section 4.8 of the M6b design).</summary>
    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachPrefabCommandAndItsInverseGiveTheStateBefore(string kind)
    {
        using var m = Setup();
        var before = State(m);

        var result = m.Missions.Apply(new MissionBatch("Test", [Commands[kind]()]) { Confirm = true });
        Assert.True(result.Status == EditStatus.Applied, string.Join("\n", result.Errors.Select(e => $"{e.Detail}: {e.Message}")));
        var after = State(m);
        Assert.NotEqual(before, after);
        var json = MissionJson.Write(result.Inverse!);
        var inverse = m.Missions.Apply(new MissionBatch("Back", [MissionJson.ReadCommand(json)]) { Confirm = true });

        Assert.True(inverse.Status == EditStatus.Applied, string.Join("\n", inverse.Errors.Select(e => $"{e.Detail}: {e.Message}")));
        Assert.Equal(before, State(m));
        m.Session.Undo();
        Assert.Equal(after, State(m));
        m.Session.Undo();
        Assert.Equal(before, State(m));
        m.Session.Redo();
        Assert.Equal(after, State(m));
    }

    // ---- The names of the members (section 4.9 of the M6b design) ----

    [Fact]
    public void APlaceWhoseMadeNameIsASectionOfTheScriptRefusesAndTheSectionStays()
    {
        using var m = Create();

        // The made name of the role "end" is ol_end, a trigger of the script that is not a part of the instance.
        var error = m.Refuse(new PlacePrefabInstance(TestScript, GateRef)
        {
            Id = "ol", Values = [new PrefabFieldInput("formation", ["ol_form"])],
        }, MissionErrors.NameTaken);

        Assert.Contains("'ol_end'", error.Message);
        Assert.Contains("Act_ChangeState = SUCCEED", m.Lines("ol_end"));
    }

    [Fact]
    public void ARevertOfARemovedMemberWhoseNameATriggerOfTheUserTookRefuses()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Apply(new RemoveTrigger(T("ol_w1_end")));
        m.Apply(new AddTrigger(TestScript)
        {
            Nickname = "ol_w1_end", Condition = LineSpec.Raw("Cnd_True", "no_params"), Actions = [LineSpec.Raw("Act_ChangeState", "SUCCEED")],
        });

        m.Refuse(new RevertOverride(I("ol_w1")) { Member = "end" }, MissionErrors.NameTaken);

        Assert.Equal(["nickname = ol_w1_end", "Cnd_True = no_params", "Act_ChangeState = SUCCEED"], m.Lines("ol_w1_end"));
        Assert.Equal(["end"], Record(m, "ol_w1").Removed);
    }

    // ---- Removed members (decision D676 c) ----

    [Fact]
    public void ADeleteOfAMemberRemovesTheLinesOfTheInstanceThatNameItAndTheSaveWorks()
    {
        using var m = Create();
        Place(m, "ol_w1");
        var before = State(m);

        var result = m.Apply(new RemoveTrigger(T("ol_w1_end")));

        // Unity sets a reference to a removed child to None. The compile leaves out the line that started the member.
        Assert.Equal(["nickname = ol_w1_spawn", "Cnd_Timer = 5", "Act_SpawnFormation = ol_form"], m.Lines("ol_w1_spawn"));
        Assert.Empty(result.Missing);
        Assert.Empty(Rule(m, "P01"));
        Assert.Empty(Rule(m, "M05"));
        Assert.True(m.Session.Save().Saved);

        m.Session.Undo();

        Assert.Equal(before, State(m));
    }

    [Fact]
    public void ADeleteOfAMemberKeepsAnOutsideLineThatNamesItAndItsFixRemovesTheLine()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Apply(new LinkTriggers(T("ol_start"), "ol_w1_end", LinkKind.Start));

        var result = m.Apply(new RemoveTrigger(T("ol_w1_end")));

        var missing = Assert.Single(result.Missing);
        Assert.Equal("Act_ActTrig", missing.Key);
        Assert.Contains("ol_start", missing.Ref);
        var problem = Assert.Single(Rule(m, "M05"));
        var fix = MissionFixes.Of(m.Missions.Model(Script).Analysis, problem).First();
        m.Apply(new ApplyFix(TestScript, problem.Subject!, fix.Id));
        Assert.Empty(Rule(m, "M05"));
        Assert.DoesNotContain("Act_ActTrig = ol_w1_end", m.Lines("ol_start"));
    }

    [Fact]
    public void ADeleteOfAMemberWithItsLinesGivesNoHandEdit()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Apply(new LinkTriggers(T("ol_start"), "ol_w1_end", LinkKind.Start));

        m.Apply(new RemoveTrigger(T("ol_w1_end")) { References = ReferencesMode.Remove });

        Assert.DoesNotContain("Act_ActTrig = ol_w1_end", m.Lines("ol_start"));
        Assert.DoesNotContain("Act_ActTrig = ol_w1_end", m.Lines("ol_w1_spawn"));
        Assert.Equal(["end"], Record(m, "ol_w1").Removed);
        Assert.Empty(Rule(m, "P01"));
    }

    [Fact]
    public void ApplyOfARemovedMemberRemovesTheLinesThatNameItFromThePrefab()
    {
        using var m = Create();
        Place(m, "ol_w1");
        Place(m, "ol_w2");
        m.Apply(new RemoveTrigger(T("ol_w1_end")));
        var before = State(m);

        m.Apply(new ApplyOverrideToPrefab(I("ol_w1")) { Member = "end" });

        Assert.DoesNotContain(m.Missions.Prefab(GateRef)!.Template!.Section("spawn")!.Lines, l => l.Key == "Act_ActTrig");
        Assert.Equal(["nickname = ol_w2_spawn", "Cnd_Timer = 5", "Act_SpawnFormation = ol_form"], m.Lines("ol_w2_spawn"));
        Assert.Empty(Rule(m, "M05"));
        Assert.Empty(Rule(m, "P01"));
        Assert.True(m.Session.Save().Saved);

        m.Session.Undo();

        Assert.Equal(before, State(m));
    }

    [Fact]
    public void ADuplicateKeepsTheRemovedMembersAndThePortLinks()
    {
        using var m = Create();

        m.Apply(new PlacePrefabInstance(TestScript, GateRef)
        {
            Id = "ol_w1", Values = [new PrefabFieldInput("formation", ["ol_form"])], Ports = [new InstancePort("done", "ol_end")],
            Removed = ["end"],
        });

        var record = Record(m, "ol_w1");
        Assert.Equal(["end"], record.Removed);
        Assert.Equal([new InstancePort("done", "ol_end")], record.Ports);
        Assert.False(m.Has("ol_w1_end"));
        Assert.DoesNotContain("Act_ActTrig = ol_w1_end", m.Lines("ol_w1_spawn"));
        m.Refuse(new PlacePrefabInstance(TestScript, GateRef) { Id = "ol_w2", Removed = ["alarm"] }, MissionErrors.RoleNotFound);

        m.Apply(new RevertOverride(I("ol_w1")) { Member = "end" });

        Assert.Equal(["nickname = ol_w1_end", "Cnd_Destroyed = ol_form, -1", "Act_ActTrig = ol_end"], m.Lines("ol_w1_end"));
    }

    // ---- Renames (section 4.9 of the M6b design) ----

    [Fact]
    public void ARenameOfAnItemThatAFieldNamesChangesTheRecordAndGivesNoHandEdit()
    {
        using var m = Create();
        Place(m, "ol_w1");

        m.Apply(new RenameItem(Item("MsnFormation", "ol_form"), "ol_form_x"));

        Assert.Contains("Act_SpawnFormation = ol_form_x", m.Lines("ol_w1_spawn"));
        Assert.Equal([new InstanceValue("formation", ["ol_form_x"])], Record(m, "ol_w1").Values);
        Assert.Empty(Rule(m, "P01"));

        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["7"]));

        Assert.Contains("Act_SpawnFormation = ol_form_x", m.Lines("ol_w1_spawn"));
        Assert.Empty(Rule(m, "M06"));
    }

    [Fact]
    public void ARenameOfATriggerThatATriggersFieldNamesChangesTheRecord()
    {
        using var m = Create();
        m.Apply(new PlacePrefabInstance(TestScript, PrefabRef.BuiltinOf("branch")) { Id = "ol_b", Values = [new PrefabFieldInput("tests", ["ol_wave"])] });

        m.Apply(new RenameTrigger(T("ol_wave"), "ol_wave_x"));

        Assert.Equal(["ol_wave_x"], Record(m, "ol_b").Values.Concat(Record(m, "ol_b").Overrides).Single(v => v.Field == "tests").Rows);
        Assert.Empty(Rule(m, "P01"));
    }

    // ---- Prefab assets ----

    [Fact]
    public void ANewPrefabWithTheIdOfAPrefabOfALowerModRefuses()
    {
        using var m = MissionEdits.Create(lowerMod: new Dictionary<string, string>
        {
            [@"prefabs\missions\ol_low.ini"] = Gate.Replace("ol_gate", "ol_low"),
        }, modFiles: new Dictionary<string, string> { [@"prefabs\missions\ol_gate.ini"] = Gate });

        var error = m.Refuse(new CreatePrefabAsset("ol_low") { VariantOf = PrefabRef.BuiltinOf("spawnWave") }, MissionErrors.NameTaken);
        m.Refuse(new RenamePrefabAsset(GateRef, "ol_low"), MissionErrors.NameTaken);

        Assert.Equal("Mod 'low' already has a prefab 'ol_low'. A prefab of the active mod with this file name hides it, and " +
                     "its instances then use the new prefab. Pick another file name.", error.Message);
    }

    [Theory]
    [InlineData("OL_GATE", "'OL_GATE' differs from 'ol_gate' only in letter case. Pick another file name.")]
    [InlineData("Ol_Gate_B", "'Ol_Gate_B' is not a valid file name. Use lowercase letters, digits, '_' and '-'.")]
    public void ANewFileNameOfAPrefabHasOnlyLowercaseLetters(string id, string message)
    {
        using var m = Create();

        var error = m.Refuse(new RenamePrefabAsset(GateRef, id), MissionErrors.NameInvalid);

        Assert.Equal(message, error.Message);
        Assert.Equal(MissionErrors.NameInvalid, m.Refuse(new CreatePrefabAsset("OL_COPY") { VariantOf = GateRef }, MissionErrors.NameInvalid).Detail);
    }

    [Fact]
    public void ARenameOfTheNameOnlyKeepsTheFileAndTheRecords()
    {
        using var m = Create();
        Place(m, "ol_w1");
        var before = State(m);

        m.Apply(new RenamePrefabAsset(GateRef, "ol_gate") { NewName = "Gate wave" });

        Assert.Equal("Gate wave", m.Missions.Prefab(GateRef)!.Name);
        Assert.Equal("mod:ol_gate", Record(m, "ol_w1").Prefab);
        m.Session.Undo();
        Assert.Equal(before, State(m));
    }

    [Fact]
    public void TwoVariantsOfOnePrefabGetTwoNames()
    {
        using var m = Create();

        m.Apply(new CreatePrefabAsset("ol_v1") { VariantOf = GateRef });
        m.Apply(new CreatePrefabAsset("ol_v2") { VariantOf = GateRef });

        Assert.Equal("Gate ambush variant", m.Missions.Prefab(PrefabRef.ModOf("ol_v1"))!.Name);
        Assert.Equal("Gate ambush variant 2", m.Missions.Prefab(PrefabRef.ModOf("ol_v2"))!.Name);
    }

    // ---- Hand edits (rule P01) ----

    [Fact]
    public void AFieldEditOfAHandEditedInstanceSaysThatTheCompileReplacedTheHandEdit()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Session.Execute(MissionTestData.Raw(EditCommand.SetLinesFromText(T("ol_w1_spawn").Item.Ref, "Cnd_Timer", ["9"])));

        var result = m.Apply(new SetPrefabField(I("ol_w1"), "formation", ["ol_ship_1"]));

        Assert.Contains(PrefabLinks.ReplacedHandEditNote("ol_w1", Script), result.Notes);
        Assert.Contains("Cnd_Timer = 5", m.Lines("ol_w1_spawn"));
    }

    [Fact]
    public void ABodyGuardOfAnotherSectionDoesNotOpenTheMembers()
    {
        using var m = Create();
        Place(m, "ol_w1");
        var start = m.Session.OpenDocument(Script)!.Working.First(s => s.Name == "Trigger");

        // The guard names ol_start, which is not a member. The line edit of the member still refuses.
        var error = m.Refuse(new CommandGroup([new SetLine(new RowRef(T("ol_w1_spawn").Item, 1), LineSpec.Raw("Cnd_Timer", "9"))])
        {
            ExpectBodies = new Dictionary<string, string> { [T("ol_start").Item.ToString()] = SectionHash.Of(start) },
        }, MissionErrors.PrefabMember);

        Assert.Contains("ol_w1", error.Message);
    }

    [Fact]
    public void ASaveWritesTheGraphFileAndTheAssetAndTheInstanceReadsBack()
    {
        using var m = Create();
        Place(m, "ol_w1");
        m.Apply(new SetPrefabField(I("ol_w1"), "delay", ["8"]));
        m.Apply(new CreatePrefabAsset("ol_copy") { VariantOf = GateRef });

        m.Session.Save();
        m.Session.Dispose();
        var reopened = m.Ws.Session("act");

        var graph = reopened.Missions.Graph(Script);
        Assert.Equal(Record(m, "ol_w1"), graph.Instance("ol_w1"));
        Assert.NotNull(reopened.Missions.Prefab(PrefabRef.ModOf("ol_copy")));
        Assert.DoesNotContain(reopened.Missions.Model(Script).Problems, p => p.Rule is "P01" or "P02" or "P03");
    }
}
