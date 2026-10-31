using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the mission layer of the Scene (sections 7.2 and 18.2 of the M6a design). They pin the count of
/// placeable items of each kind in Li01 over the 14 retail story scripts. The tests count items. They hold no retail
/// text.
/// </summary>
public sealed class MissionLayerRetailTests(ITestOutputHelper output)
{
    /// <summary>The positions of the objects of a system and the slots of the formation shapes, from the edit view of a
    /// retail session.</summary>
    private static (Dictionary<string, Vector3> Objects, Dictionary<string, IReadOnlyList<Vector3>> Slots) World(
        string system)
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6alayer");
        var session = workspace.Open();
        var objects = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in session.Sections(session.SystemPath(system)!))
        {
            if (session.Get(section) is { } view && section.Id.Type.Equals("Object", StringComparison.OrdinalIgnoreCase) &&
                Value(view, "nickname") is { } nickname && Vector(view, "pos") is { } position)
                objects[nickname] = position;
        }
        var slots = new Dictionary<string, IReadOnlyList<Vector3>>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in session.Sections(session.DataPath(@"MISSIONS\formations.ini")!))
        {
            if (session.Get(section) is { } view && Value(view, "nickname") is { } shape)
                slots.TryAdd(shape, view.Entries.Where(e => e.Name.Equals("pos", StringComparison.OrdinalIgnoreCase) &&
                                                            e.Values.Count >= 3)
                    .Select(e => new Vector3(Number(e, 0), Number(e, 1), Number(e, 2))).ToList());
        }
        return (objects, slots);
    }

    private static string? Value(SectionView view, string key) =>
        view.Entries.FirstOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Values.Count: > 0 } entry
            ? MissionIndex.Text(entry.Values[0])
            : null;

    private static Vector3? Vector(SectionView view, string key) =>
        view.Entries.FirstOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Values.Count: >= 3 } entry
            ? new Vector3(Number(entry, 0), Number(entry, 1), Number(entry, 2))
            : null;

    private static float Number(EntryView entry, int index) =>
        float.TryParse(MissionIndex.Text(entry.Values[index]), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>The objects of Li01 list the mission lines that name them in their Behaviors section (section 10.18 of
    /// the M6a design). Examples are the gates of the lock lines.</summary>
    [Fact]
    public void TheObjectsOfLi01ListTheirMissionRows()
    {
        var (objects, _) = World("li01");
        var run = RetailMissions.Run();
        var uses = objects.Keys.ToDictionary(k => k, k => MissionSummary.ObjectUses(run, k));
        var named = uses.Count(u => u.Value.Count > 0);
        var lines = uses.Values.SelectMany(u => u).ToList();
        var inTriggers = lines.Count(u => u.Line.Trigger != null);

        output.WriteLine($"{named} objects of Li01 have {lines.Count} mission lines, {inTriggers} of them in triggers.");
        // A line outside a trigger, for example the arrival object of a mission ship or a dock order, names its section.
        Assert.All(lines, u => Assert.NotNull(u.Line.Trigger?.Nickname ?? u.Line.Section.Nickname));
        Assert.All(lines, u => Assert.StartsWith($"{u.Script.Slot!.Slot}: ", u.Text));
        // The rep_inst objectives name their object too, since the type line of an objective has named values for each
        // form (final finding 15 of MCP1-3). They name objects that a trigger line names too, so the count of the
        // objects stays.
        Assert.Equal((47, 360, 275), (named, lines.Count, inTriggers));
    }

    [Fact]
    public void TheCountOfPlaceableItemsOfEachKindInLi01()
    {
        var (objects, slots) = World("li01");
        var run = RetailMissions.Run();
        var placements = run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!)
            .SelectMany(a => MissionPlacements.In(a.Index, "Li01", name => objects.TryGetValue(name, out var p) ? p : null,
                shape => slots.GetValueOrDefault(shape)))
            .ToList();
        var counts = Enum.GetValues<PlacementKind>().ToDictionary(k => k, k => placements.Count(p => p.Kind == k));
        var text = string.Join(" ", counts.Select(c => $"{c.Key}:{c.Value}"));

        output.WriteLine(text);
        Assert.True(objects.Count > 100, $"Li01 has {objects.Count} objects.");
        Assert.All(placements, p => Assert.Equal("Li01", p.System));
        Assert.Equal("ShipStart:47 FormationStart:34 FormationMember:34 SolarStart:9 LootStart:0 NearAnchor:0 " +
                     "Arrival:0 Spawn:71 Move:67 Route:2 DistanceVolume:73 RingPass:0 Plane:0 TrafficArea:23 " +
                     "ObjectiveMarker:51 OrderPath:118 FixedCamera:1 CutsceneAnchor:4", text);
    }
}
