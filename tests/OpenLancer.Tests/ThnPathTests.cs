using System;
using System.Collections.Generic;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>Paths of values in an element: record keys joined with '.', and the four slots of an event.</summary>
public sealed class ThnPathTests
{
    private static readonly ThnTable Camera = Record(F("entity_name", T("Cam")),
        F("cameraprops", Record(F("fovh", N(10)), F("hvaspect", N(1.5f)))), F("spatialprops", Record(F("pos", List(N(0), N(1), N(2))))));

    private static readonly ThnTable Psys = List(N(5), S("START_PSYS"), List(T("Fx")), Record(F("duration", N(2))));

    private static ThnPath P(string text) => new(text);

    [Fact]
    public void GetReadsRecordsAndEventSlots()
    {
        Assert.Equal(N(10), ThnElementPaths.Get(ThnElementKind.Entity, Camera, P("cameraprops.fovh")));
        Assert.Equal(List(N(0), N(1), N(2)), ThnElementPaths.Get(ThnElementKind.Entity, Camera, P("spatialprops.pos")));
        Assert.Null(ThnElementPaths.Get(ThnElementKind.Entity, Camera, P("cameraprops.nearplane")));
        Assert.Null(ThnElementPaths.Get(ThnElementKind.Entity, Camera, P("cameraprops.fovh.x")));
        Assert.Equal(N(5), ThnElementPaths.Get(ThnElementKind.Event, Psys, P("time")));
        Assert.Equal(N(2), ThnElementPaths.Get(ThnElementKind.Event, Psys, P("props.duration")));
        Assert.Null(ThnElementPaths.Get(ThnElementKind.Event, List(N(0), S("SET_CAMERA"), List()), P("props.duration")));
    }

    /// <summary>
    /// A path of a patch ends at a table with a list part. The engine functions go into the record part of such a
    /// table, as the engine reads it, and keep its list part. Such a table never counts as an empty record. The patch
    /// path of a change in such a table is the path of the outermost such table. A value that is not a table still
    /// refuses.
    /// </summary>
    [Fact]
    public void TheEngineFunctionsGoIntoATableWithAListPart()
    {
        var entity = Record(F("entity_name", T("Mk")), F("spatialprops", new ThnTable([N(1)], [F("pos", List(N(0), N(1), N(2)))])));
        var ev = List(N(0), S("START_PSYS"), List(T("Fx")), new ThnTable([N(1)], [F("duration", N(2))]));
        var pos = P("spatialprops.pos");
        var length = P("props.duration");

        Assert.Null(ThnElementPaths.Get(ThnElementKind.Entity, entity, pos));
        Assert.Equal(List(N(0), N(1), N(2)), ThnElementPaths.EngineGet(ThnElementKind.Entity, entity, pos));
        Assert.Equal(N(2), ThnElementPaths.EngineGet(ThnElementKind.Event, ev, length));
        Assert.Equal(new ThnTable([N(1)], [F("pos", List(N(3), N(3), N(3)))]),
            ThnElementPaths.EngineSet(ThnElementKind.Entity, entity, pos, List(N(3), N(3), N(3)))["spatialprops"]);
        Assert.Throws<ThnPathException>(() => ThnElementPaths.Set(ThnElementKind.Entity, entity, pos, N(1)));
        Assert.Equal(P("spatialprops"), ThnElementPaths.PatchPath(ThnElementKind.Entity, entity, pos));
        Assert.Equal(P("props"), ThnElementPaths.PatchPath(ThnElementKind.Event, ev, length));
        Assert.Equal(P("spatialprops.pos"), ThnElementPaths.PatchPath(ThnElementKind.Entity, Camera, pos));
        Assert.Equal(P("cameraprops.x.y"), ThnElementPaths.PatchPath(ThnElementKind.Entity, Camera, P("cameraprops.x.y")));
        Assert.Equal(List(N(0), S("START_PSYS"), List(T("Fx")), List(N(1))),
            ThnElementPaths.EngineRemoveWithMadeRecords(ThnElementKind.Event, ev, length, List(N(0), S("START_PSYS"), List(T("Fx")))));
        var e = Assert.Throws<ThnPathException>(() =>
            ThnElementPaths.EngineSet(ThnElementKind.Entity, entity, P("entity_name.x"), N(1)));
        Assert.Equal("'entity_name.x' goes through 'entity_name', which is not a table.", e.Message);
    }

    /// <summary>A change keeps the place of its key, and the other values stay the same objects.</summary>
    [Fact]
    public void ASetKeepsThePlaceOfTheKeyAndSharesTheOtherValues()
    {
        var changed = ThnElementPaths.Set(ThnElementKind.Entity, Camera, P("cameraprops.fovh"), N(20));

        Assert.Equal(Record(F("entity_name", T("Cam")), F("cameraprops", Record(F("fovh", N(20)), F("hvaspect", N(1.5f)))),
            F("spatialprops", Record(F("pos", List(N(0), N(1), N(2)))))), changed);
        Assert.Same(Camera["spatialprops"], changed["spatialprops"]);
    }

    [Fact]
    public void AnEventGetsItsSlotsInOrder()
    {
        var noTargets = List(N(0), S("SET_CAMERA"));

        var e = Assert.Throws<ThnPathException>(() => ThnElementPaths.Set(ThnElementKind.Event, noTargets, P("props.duration"), N(1)));
        Assert.Equal("The event has 2 items, so it has no targets before its props.", e.Message);
        Assert.Equal(List(N(0), S("SET_CAMERA"), List(T("M"))),
            ThnElementPaths.Set(ThnElementKind.Event, noTargets, P("targets"), List(T("M"))));
        var extra = List(N(5), S("START_PSYS"), List(T("Fx")), Record(), N(9));
        Assert.Throws<ThnPathException>(() => ThnElementPaths.Remove(ThnElementKind.Event, extra, P("props")));
        Assert.Null(ThnElementPaths.Remove(ThnElementKind.Event, noTargets, P("props")));
        Assert.Equal(List(N(5), S("START_PSYS"), List(T("Fx"))), ThnElementPaths.Remove(ThnElementKind.Event, Psys, P("props")));
    }

    /// <summary>A patch can name only the record of an entity and the four slots of an event. Make refuses other
    /// changes, because the merge could not give them.</summary>
    [Theory]
    [MemberData(nameof(ChangesWithoutAPath))]
    public void DiffRefusesAChangeThatNoPathNames(ThnElementKind kind, ThnTable lower, ThnTable working)
    {
        Assert.Throws<ArgumentException>(() => ThnElementPaths.Diff(kind, lower, working, [], []));
    }

    public static TheoryData<ThnElementKind, ThnTable, ThnTable> ChangesWithoutAPath() => new()
    {
        { ThnElementKind.Entity, new ThnTable([N(1)], [F("entity_name", T("a"))]), new ThnTable([N(2)], [F("entity_name", T("a"))]) },
        { ThnElementKind.Entity, Record(F("entity_name", T("a"))), Record(F("entity_name", T("a")), F("a.b", N(1))) },
        { ThnElementKind.Event, Psys, new ThnTable(Psys.List, [F("x", N(1))]) },
        { ThnElementKind.Event, new ThnTable(Psys.List.Add(N(1)), []), new ThnTable(Psys.List.Add(N(2)), []) },
        { ThnElementKind.Event, Psys, List(N(5), S("START_PSYS")) },
    };

    [Fact]
    public void DiffGivesTheChangedPathsAndTheRemoves()
    {
        var sets = new List<(ThnPath, ThnValue)>();
        var removes = new List<ThnPath>();
        var working = List(N(6), S("START_PSYS"), List(T("Fx")), Record(F("duration", N(2)), F("sparam", N(1))));

        ThnElementPaths.Diff(ThnElementKind.Event, Psys, working, sets, removes);
        Assert.Equal<(ThnPath, ThnValue)>([(P("time"), N(6)), (P("props.sparam"), N(1))], sets);
        Assert.Empty(removes);

        sets.Clear();
        ThnElementPaths.Diff(ThnElementKind.Event, Psys, List(N(5), S("START_PSYS"), List(T("Fx"))), sets, removes);
        Assert.Empty(sets);
        Assert.Equal([P("props")], removes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a..b")]
    public void APathHasNoEmptyKey(string text) => Assert.Throws<ArgumentException>(() => new ThnPath(text));
}
