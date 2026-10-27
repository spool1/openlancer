using System;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.SceneWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The commands of the Scene (section 4.0.8 of the M6b design). The command runner runs each command as the editor
/// does. The command gives the same W, adds, deletes and selection as the plan of today that it wraps.
/// </summary>
public sealed class SceneCommandsTests
{
    [Fact]
    public void CreateDuplicateDeleteAndRenameGiveTheWOfTheirPlans()
    {
        var position = new Vector3(1.4f, 2.6f, -3.6f);
        AssertParity(() => Create(), s => ScenePlans.Create(s, "Li01", SceneTemplate.Station, "station1", position, null,
                "Li01_01_Base"),
            s => SceneCommands.Create(s, "Li01", SceneTemplate.Station, "station1", position, dockWith: "Li01_01_Base"));
        AssertParity(() => Create(), s => ScenePlans.Create(s, "Li01", SceneTemplate.Zone, null, position, null,
                shape: LibreLancer.Data.Schema.Universe.ZoneShape.RING),
            s => SceneCommands.Create(s, "Li01", SceneTemplate.Zone, null, position,
                shape: LibreLancer.Data.Schema.Universe.ZoneShape.RING));
        AssertParity(() => Create(), s => ScenePlans.Duplicate(s, [Object("Li01_01")]),
            s => SceneCommands.Duplicate(s, [Object("Li01_01")]));
        AssertParity(() => Create(), s => ScenePlans.Delete(s, [Object("Li01_01")]),
            s => SceneCommands.Delete(s, [Object("Li01_01")]));
        AssertParity(() => Create(), s => ScenePlans.Rename(s, Object("Li01_child"), "Li01_kid"),
            s => SceneCommands.Rename(s, Object("Li01_child"), "Li01_kid"));
    }

    /// <summary>
    /// The spawn of an asset (section 8 of the M23 design). A solar archetype takes the template of its type. A station
    /// archetype with a docking sphere asks for a base. A ship or a model makes a new static prop archetype with its
    /// object. An asset that the Scene does not place gives a plan that refuses.
    /// </summary>
    [Fact]
    public void ASpawnTakesTheTemplateOfTheArchetypeOrMakesAPropArchetype()
    {
        using (var ws = AssetWorkspace.Create())
        {
            var session = ws.Session("act");
            Assert.Equal(SceneTemplate.Solar, SceneCommands.SpawnTemplate(session, "depot"));
            Assert.Equal(SceneTemplate.Station, SceneCommands.SpawnTemplate(session, "station_nodock"));
            Assert.True(SceneCommands.SpawnAsksForBase(session, "station1"));
            Assert.False(SceneCommands.SpawnAsksForBase(session, "station_nodock"));
            Assert.False(SceneCommands.SpawnAsksForBase(session, "planet1"));
            Assert.Contains("milestone M27", Assert.Single(SceneCommands.SpawnAsset(session, "Li01",
                new AssetRef(AssetKind.SolarArchetype, "jumpgate"), Vector3.Zero).Refusals).Message);
            Assert.Equal("no_ship has no model.", Assert.Single(SceneCommands.SpawnAsset(session, "Li01",
                new AssetRef(AssetKind.Ship, "no_ship"), Vector3.Zero).Refusals).Message);
            Assert.False(SceneCommands.SpawnAsset(session, "Li01", new AssetRef(AssetKind.Effect, "fx_smoke"), Vector3.Zero)
                .CanRun);
            Assert.False(SceneCommands.SpawnAsset(session, "Li01", new AssetRef(AssetKind.PropGroup, "group"), Vector3.Zero)
                .CanRun);
        }

        var at = new Vector3(10.4f, 0, -5.6f);
        AssertParity(() => AssetWorkspace.Create(),
            s => ScenePlans.Create(s, "Li01", SceneTemplate.Station, "station1", at, Object("Li01_01"), "Li01_01_Base"),
            s => SceneCommands.SpawnAsset(s, "Li01", new AssetRef(AssetKind.SolarArchetype, "station1"), at,
                Object("Li01_01"), "Li01_01_Base"));
        AssertParity(() => AssetWorkspace.Create(),
            s => ScenePlans.CreateProp(s, "Li01", ModelSource.Of(s, AssetWorkspace.Ship("br_gunboat"))!, "br_gunboat", at,
                null),
            s => SceneCommands.SpawnAsset(s, "Li01",
                new AssetRef(AssetKind.Ship, "br_gunboat", AssetWorkspace.Ship("br_gunboat")), at),
            ListWorkspace.SolarPath);
        AssertParity(() => AssetWorkspace.Create(),
            s => ScenePlans.CreateProp(s, "Li01", s.Assets.ModelOf(new AssetRef(AssetKind.Model, AssetWorkspace.GunboatModel))!,
                "br_gunboat", at, null),
            s => SceneCommands.SpawnAsset(s, "Li01", new AssetRef(AssetKind.Model, AssetWorkspace.GunboatModel), at),
            ListWorkspace.SolarPath);
    }

    /// <summary>
    /// A transform of more items is one entry. Each item gets the line that the number rules give, as the gizmo writes
    /// it, and a child does not move with its parent (milestone M3b).
    /// </summary>
    [Fact]
    public void ATransformOfMoreItemsIsOneEntryWithTheLinesOfTheGizmo()
    {
        var position = new Vector3(100.5f, 0, -20);
        using var today = Create();
        var expected = today.Session("act");
        var start = expected.Get(Object("Li01_01"))!.Lines("pos").LastOrDefault()?.Values;
        expected.Execute(EditCommand.SetLines(Object("Li01_01"), "pos", [TransformValues.Line(start, position)]));
        var size = expected.Get(Zone("Zone_ring"))!.Lines("size")[^1].Values;
        expected.Execute(EditCommand.SetLines(Zone("Zone_ring"), "size", [TransformValues.Line(size, [2000, 1000, 250])]));

        using var ws = Create();
        var session = ws.Session("act");
        var child = ScenePlansDeleteTests.Lines(session, Object("Li01_child"));
        var plan = SceneCommands.Transform(session,
        [
            new TransformMove(Object("Li01_01"), new TransformChange(position)),
            new TransformMove(Zone("Zone_ring"), new TransformChange(Size: [2000, 1000, 250])),
        ], TransformMode.Absolute);
        var result = FakeCommandHost.Runner(session).Run(plan);

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Transform Li01_01 and 1 more", session.UndoLabel);
        Assert.Equal([Object("Li01_01"), Zone("Zone_ring")], plan.Changes);
        Assert.Equal(W(expected, Li01Path), W(session, Li01Path));
        Assert.Equal(child, ScenePlansDeleteTests.Lines(session, Object("Li01_child")));
        session.Undo();
        Assert.False(session.CanUndo);
    }

    /// <summary>A transform names each item one time, and names at least one. When one item refuses, the plan refuses
    /// with the reason of each item that refuses, and nothing changes.</summary>
    [Fact]
    public void ATransformRefusesAsAWhole()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var move = new TransformMove(Object("Li01_01"), new TransformChange(Vector3.One));

        Assert.False(SceneCommands.Transform(session, [], TransformMode.Absolute).CanRun);
        Assert.Contains("more than one time", Assert.Single(SceneCommands.Transform(session, [move, move],
            TransformMode.Absolute).Refusals).Message);
        var refused = SceneCommands.Transform(session,
        [
            move,
            new TransformMove(Object("Li01_dock"), new TransformChange(Size: [1, 2, 3])),
            new TransformMove(Li01("LightSource", "light1"), new TransformChange(Rotation: Vector3.One)),
        ], TransformMode.Absolute);

        Assert.False(refused.CanRun);
        Assert.Equal(["The engine reads no size for an object.", "The engine reads no rotation for a light."],
            refused.Refusals.Select(r => r.Message));
        Assert.Equal(EditStatus.Refused, FakeCommandHost.Runner(session).Run(refused).Status);
        Assert.False(session.CanUndo);
    }

    /// <summary>The components of an object (decisions D243, D244 and D258): an add and a remove give the W of the edits
    /// of today with their labels, and the remove of Solar refuses, as RequireComponent does.</summary>
    [Fact]
    public void AComponentAddsAndRemovesAsTheEditsOfToday()
    {
        AssertParity(() => Create(), s => new CommandPlan(ObjectComponents.Add(Object("Li01_dock"), ObjectComponent.LoadoutUser,
                "lo_station")),
            s => SceneCommands.SetComponent(s, Object("Li01_dock"), ObjectComponent.LoadoutUser, "lo_station"));
        AssertParity(() => Create(), s => new CommandPlan(ObjectComponents.Remove(Object("Li01_01"), ObjectComponent.DockableStation)),
            s => SceneCommands.SetComponent(s, Object("Li01_01"), ObjectComponent.DockableStation, null));

        using var ws = Create();
        var session = ws.Session("act");
        Assert.Equal("Add Loadout user",
            SceneCommands.SetComponent(session, Object("Li01_dock"), ObjectComponent.LoadoutUser, "lo_station").Label);
        var solar = SceneCommands.SetComponent(session, Object("Li01_01"), ObjectComponent.Solar, null);
        Assert.Equal("Solar: Each object needs an archetype.", Assert.Single(solar.Refusals).Message);
        Assert.Equal("Solar cannot be added in the Inspector.", Assert.Single(SceneCommands.SetComponent(session,
            Object("Li01_01"), ObjectComponent.Solar, "x").Refusals).Message);
        Assert.Contains("has no section", Assert.Single(SceneCommands.SetComponent(session, Object("no_object"),
            ObjectComponent.LoadoutUser, "lo_station").Refusals).Message);
    }

    /// <summary>A loadout or a faction tile on an object sets its line as one entry "Set loadout of &lt;object&gt;", also
    /// in place of a present line (decision D281 of the M23 design).</summary>
    [Fact]
    public void ADropOfALoadoutOrAFactionSetsItsLine()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var runner = FakeCommandHost.Runner(session);

        runner.Run(SceneCommands.DropAsset(session, Object("Li01_01"), new AssetRef(AssetKind.Loadout, "lo_other")));
        Assert.Equal("Set loadout of Li01_01", session.UndoLabel);
        Assert.Equal(["lo_other"], Lines(session, Object("Li01_01"), "loadout"));
        runner.Run(SceneCommands.DropAsset(session, Object("Li01_dock"), new AssetRef(AssetKind.Faction, "li_p_grp")));
        Assert.Equal("Set faction of Li01_dock", session.UndoLabel);
        Assert.Equal(["li_p_grp"], Lines(session, Object("Li01_dock"), "reputation"));
        Assert.False(SceneCommands.DropAsset(session, Object("Li01_dock"), new AssetRef(AssetKind.Ship, "x")).CanRun);
    }

    /// <summary>Runs the plan of today with the session and the plan of the command through the runner in two copies of
    /// the profile. Both give the same W, adds, deletes, selection and label, and the entry has the origin User.</summary>
    private static void AssertParity(Func<TestWorkspace> create, Func<EditSession, EditPlan> today,
        Func<EditSession, EditPlan> command, params string[] paths)
    {
        using var a = create();
        var expected = a.Session("act");
        var plan = today(expected);
        expected.Execute(plan.Command!);

        using var b = create();
        var session = b.Session("act");
        var host = new FakeCommandHost(session);
        var commanded = command(session);
        var result = new CommandRunner(host).Run(commanded);

        Assert.Equal(EditStatus.Applied, result.Status);
        foreach (var path in paths.Append(Li01Path))
            Assert.Equal(W(expected, path), W(session, path));
        Assert.Equal(plan.Adds, commanded.Adds);
        Assert.Equal(plan.Deletes, commanded.Deletes);
        Assert.Equal(plan.SelectAfter, commanded.SelectAfter);
        Assert.Equal(expected.UndoLabel, session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.DoesNotContain(host.Log.Entries, e => e.Severity == Editor.Logs.ConsoleSeverity.Error);
    }

    /// <summary>Each section of a file of W with its lines.</summary>
    private static string[] W(EditSession session, string path) =>
        session.Sections(path).SelectMany(s => ScenePlansDeleteTests.Lines(session, s).Prepend($"[{s.Id}]")).ToArray();

    private static string[] Lines(EditSession session, SectionRef section, string key) =>
        session.Get(section)!.Lines(key).Select(l => string.Join(", ", l.Values.Select(v => v.ToString()))).ToArray();
}

/// <summary>
/// The commands of the prop groups (section 4.0.8 of the M6b design). A placement is a plan. A save, a rename and a
/// delete are asset writes. They refuse in an open batch, for a group of another mod, and for a session that takes no
/// edits.
/// </summary>
public sealed class PropGroupCommandsTests
{
    [Fact]
    public void ASaveARenameAndADeleteWriteTheFilesOfTheGroup()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var folder = GroupWorkspace.PropsFolder(ws);
        var draft = PropGroups.Draft(session, [Object("Li01_dock")]);

        var saved = PropGroupCommands.Save(session, draft, "dock", "Dock");
        Assert.True(saved.Written);
        Assert.Equal("dock", saved.Group!.Nickname);
        Assert.Equal(EditErrorKind.NameTaken, PropGroupCommands.Save(session, draft, "dock", "Dock").Error!.Kind);
        Assert.Equal(EditErrorKind.InvalidValue, PropGroupCommands.Save(session, draft, "Dock", "Dock").Error!.Kind);
        Assert.Equal(EditErrorKind.InvalidValue, PropGroupCommands.Save(session, draft, "quay", "a;b").Error!.Kind);

        var renamed = PropGroupCommands.Rename(session, saved.Group, "quay");
        Assert.Equal(["quay.ini"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.True(PropGroupCommands.Delete(session, renamed.Group!).Written);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.False(session.CanUndo);
    }

    /// <summary>An asset write changes files at once, so it refuses while a batch is open (section 4.0.4, item 9 of the
    /// M6b design). It also refuses a group of another mod and a session that takes no edits. Nothing changes.</summary>
    [Fact]
    public void AnAssetWriteRefusesInABatchForAnotherModAndWithoutEdits()
    {
        using var ws = GroupWorkspace.Create();
        GroupWorkspace.WriteGroup(ws, "low_group", "[PropGroup]\nnickname = low_group\nformat = 1\n[Object]\nmember = a\n" +
                                                   "archetype = station1\n", "lower");
        var session = ws.Session("act");
        var draft = PropGroups.Draft(session, [Object("Li01_dock")]);
        var own = PropGroupCommands.Save(session, draft, "dock", "Dock").Group!;
        var lower = GroupWorkspace.Group(session, "low_group");

        using (session.BeginBatch())
        {
            foreach (var write in new[]
                     {
                         PropGroupCommands.Save(session, draft, "quay", "Quay"), PropGroupCommands.Rename(session, own, "quay"),
                         PropGroupCommands.Delete(session, own),
                     })
            {
                Assert.Equal((EditErrorKind.Refused, PropGroupCommands.AssetWriteInBatch),
                    (write.Error!.Kind, write.Error.Detail));
            }
        }

        Assert.Equal(PropGroupCommands.OtherMod, PropGroupCommands.Rename(session, lower, "low_2").Error!.Detail);
        Assert.Equal(PropGroupCommands.OtherMod, PropGroupCommands.Delete(session, lower).Error!.Detail);
        Assert.Equal(["dock", "low_group"], PropGroups.Of(session).Select(g => g.Nickname).Order());

        var readOnly = ws.Session(null);
        Assert.Equal(EditErrorKind.ReadOnly,
            PropGroupCommands.Save(readOnly, PropGroups.Draft(readOnly, [Object("Li01_dock")]), "quay", "Quay").Error!.Kind);
    }

    /// <summary>A placement through the runner gives the W of the plan of today, as one entry.</summary>
    [Fact]
    public void APlacementIsOneEntry()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var group = PropGroupCommands.Save(session, PropGroups.Draft(session, [Object("Li01_14")]), "trio", "Trio").Group!;
        var expected = PropGroups.Place(session, "Li01", group, new Vector3(0, 0, 5000), null);

        var result = FakeCommandHost.Runner(session).Run(PropGroupCommands.Place(session, "Li01", group,
            new Vector3(0, 0, 5000)));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Place Trio", session.UndoLabel);
        Assert.All(expected.Adds, a => Assert.NotNull(session.Get(a)));
        session.Undo();
        Assert.All(expected.Adds, a => Assert.Null(session.Get(a)));
    }
}
