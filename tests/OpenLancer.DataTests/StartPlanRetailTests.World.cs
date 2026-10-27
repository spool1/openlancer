using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Export;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the world of the start system (section 5.3, note of 2026-10-28 after step 7 of the M6c design, a
/// change of D748). The setup makes only the objects of the start system, and the ships that travel with the player
/// start next to the player. The tests name the retail triggers and objects of the design rows, and read their points
/// from the script lines.
/// </summary>
public sealed partial class StartPlanRetailTests
{
    private static readonly MissionValueKind[] ObjectKinds =
    [
        MissionValueKind.Object, MissionValueKind.Group, MissionValueKind.Ship, MissionValueKind.Formation,
        MissionValueKind.Solar, MissionValueKind.Loot,
    ];

    private static readonly string[] SpawnKeys =
        ["Act_SpawnShip", "Act_SpawnShipRel", "Act_SpawnFormation", "Act_SpawnSolar", "Act_SpawnLoot"];

    private static StartPlan Plan(MissionAnalysis analysis, string trigger) =>
        StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.At(trigger));

    /// <summary>The lines of the setup trigger of the world of a plan.</summary>
    private static List<(string Key, IReadOnlyList<string> Values)> World(StartPlan plan) =>
        plan.Setup.Count < 2 ? [] : plan.Setup[1].Lines.Skip(1).ToList();

    private static List<string> WorldText(StartPlan plan) =>
        World(plan).Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

    /// <summary>The world lines of a plan as the setup made them before the note of 2026-10-28: each kept world line of
    /// the path.</summary>
    private static List<string> OldWorldText(MissionAnalysis analysis, string trigger)
    {
        var index = analysis.Index;
        var replay = StartReplay.Run(index, MissionStartPoint.At(trigger));
        var forced = replay.Path.Where(t => t != replay.Chosen).ToList();
        var lines = replay.Effects.Select(e => e.Line).ToList();
        var keep = StartSetup.Keep(index, lines, forced);
        return lines.Where((l, i) => keep[i] && l.Name?.Start == StartRole.World)
            .Select(l => $"{l.Key.Trim()} = {string.Join(", ", l.Line.Select(RetailText.Of))}").ToList();
    }

    /// <summary>The names of the objects of a setup line: the values of the object kinds of its schema, and the target
    /// of <c>Act_NagDistTowards</c> with the form OBJ.</summary>
    private static IEnumerable<string> ObjectNames(MissionIndex index, string key, IReadOnlyList<string> values)
    {
        foreach (var spec in index.Schema.Name(key)?.Values.Where(s => ObjectKinds.Contains(s.Kind)) ?? [])
        {
            for (var i = spec.Index; i < Math.Min(spec.End, values.Count); i++)
            {
                if (values[i].Trim() is { Length: > 0 } name && !MissionIndex.Is(name, "Player"))
                    yield return name;
            }
        }
        if (MissionIndex.Is(key, "Act_NagDistTowards") && values.Count > 3 && MissionIndex.Is(values[0], "OBJ"))
            yield return values[3].Trim();
    }

    /// <summary>The start anchor of a space or a base start, or null.</summary>
    private static Vector3? Anchor(MissionIndex index, StartPlace place) => place.Kind switch
    {
        StartPlaceKind.Space => place.Position,
        StartPlaceKind.Base when index.World.DockOf(place.Base!) is { } dock => StartPlaces.InFront(dock).Point,
        _ => null,
    };

    private static Vector3 Point(IReadOnlyList<string> values, int from) =>
        new(Parse(values[from]), Parse(values[from + 1]), Parse(values[from + 2]));

    private static float Parse(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void Within(float distance, Vector3 anchor, Vector3? point, string what)
    {
        Assert.NotNull(point);
        var away = Vector3.Distance(anchor, point!.Value);
        Assert.True(away <= distance, $"{what} is {away:0} units from the start.");
    }

    /// <summary>The design rows of M10, M08, M12 and M13 in the base game and in HD Edition.</summary>
    [Fact]
    public void TheDesignRowsGetTheWorldOfTheStartSystem()
    {
        foreach (var (set, scripts) in new[] { ("vanilla", Scripts()), ("hd", Slots(Hde.Value)) })
        {
            var vanilla = set == "vanilla";
            var m10 = Slot(scripts, "M10");
            foreach (var trigger in new[] { "nse_jonnerpal_Rh04", "sye_Rh04again" })
            {
                var plan = Plan(m10, trigger);
                Assert.True(plan.CanStart, plan.Reason);
                var start = plan.Place.Position;
                Assert.Equal(["Hakkeras_Formation", "fischer"], plan.Companions.Select(c => c.Object));
                foreach (var companion in plan.Companions)
                    Within(StartWorld.NearDistance, start, companion.Position, $"{set} {trigger} {companion.Object}");
                var names = World(plan).SelectMany(l => ObjectNames(m10.Index, l.Key, l.Values)).ToHashSet();
                Assert.DoesNotContain("sigma_fleet_main_formation", names);
                Assert.DoesNotContain("sigma_fleet_main_formation_escort", names);
            }
            var rh04 = Plan(m10, "nse_jonnerpal_Rh04");
            var rh04Names = World(rh04).SelectMany(l => ObjectNames(m10.Index, l.Key, l.Values)).ToHashSet();
            foreach (var name in new[] { "new_berlin_rh_gunboats", "Battleship_Hel", "Battleship_Freya" })
                Assert.DoesNotContain(name, rh04Names);
            if (vanilla)
                Assert.Equal("frankfurt2", rh04.Companions.Single(c => c.Object == "fischer").PointTrigger);
            Assert.Contains("Act_SpawnSolar = Battleship_Thor", WorldText(rh04));
            Assert.DoesNotContain(WorldText(rh04), l => l.StartsWith("Act_RelocateShip = Battleship_Thor", StringComparison.Ordinal));
            // In Rh01, the Berlin fleet keeps the points of its lines.
            var rh01 = Plan(m10, "sye_Rh04again");
            Assert.Contains("Act_SpawnSolar = Battleship_Freya", WorldText(rh01));
            Assert.DoesNotContain(rh01.LeftOut, l => l.Object.StartsWith("new_berlin", StringComparison.OrdinalIgnoreCase));

            var m08 = Slot(scripts, "M08");
            var ku04 = Plan(m08, "ku04_init");
            Assert.Equal(["our_formation", "bd_1", "bd_2", "bd_3"], ku04.Companions.Select(c => c.Object));
            if (vanilla)
            {
                // The points of the jump-here trigger entering_ku04, because the start is at its point.
                var jump = m08.Index.Trigger("entering_ku04")!;
                foreach (var companion in ku04.Companions)
                {
                    var line = jump.Actions.Single(a => a.Value(0) == companion.Object);
                    var from = MissionIndex.Is(line.Key, "Act_SpawnShip") ? 2 : 1;
                    Assert.Equal(Point(line.Line.Select(RetailText.Of).ToList(), from), companion.Position);
                    Assert.Equal(CompanionPoint.JumpHere, companion.Point);
                }
            }
            foreach (var companion in ku04.Companions)
                Within(StartWorld.NearDistance, ku04.Place.Position, companion.Position, $"{set} ku04_init {companion.Object}");
            var old = OldWorldText(m08, "ku04_init");
            foreach (var kept in new[] { "Transport1", "bd_4" })
            {
                Assert.Equal(old.Where(l => l.Contains(kept, StringComparison.Ordinal)),
                    WorldText(ku04).Where(l => l.Contains(kept, StringComparison.Ordinal)));
            }
            Assert.DoesNotContain(World(ku04).SelectMany(l => ObjectNames(m08.Index, l.Key, l.Values)),
                n => m08.Index.World.ObjectOf(n) is { } item && MissionIndex.Is(item.System, "Ku01"));

            var m12 = Slot(scripts, "M12");
            var decloak = Plan(m12, "decloak_R_wings");
            Assert.DoesNotContain(WorldText(decloak), l => l.Contains("St02c_lair_core", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(decloak.LeftOut, l => l is { Object: "St02c_lair_core", Kind: StartObjectKind.Fuse });
            Assert.Equal([CompanionPoint.Near, CompanionPoint.Near], decloak.Companions.Select(c => c.Point));
            Assert.Equal(OldWorldText(m12, "decloak_R_wings").Where(l => !l.Contains("St02c_lair_core", StringComparison.Ordinal)),
                WorldText(decloak));

            var m13 = Slot(scripts, "M13");
            var moon = Plan(m13, "everybody_to_moon");
            var party = new[] { "juni", "king", "VanClaussn", "Orillion", "order_wing1", "BetaWing", "GammaWing", "AlphaWing" };
            Assert.Equal(party, moon.Companions.Select(c => c.Object));
            foreach (var companion in moon.Companions)
                Within(StartWorld.NearDistance, moon.Place.Position, companion.Position, $"{set} everybody_to_moon {companion.Object}");
            foreach (var name in party)
                Assert.Single(World(moon), l => SpawnKeys.Contains(l.Key) && l.Values[0] == name);
            Assert.Contains(moon.LeftOut, l => l.Object == "bad_wing1");
            Assert.Contains(moon.LeftOut, l => l.Object == "bad_wing2");
            // Each other object of the start system keeps the lines of the path, for example the dyson generators.
            var moved = moon.Companions.Select(c => c.Object).ToHashSet();
            var oldMoon = OldWorldText(m13, "everybody_to_moon");
            foreach (var spawn in WorldText(moon).Where(l => SpawnKeys.Any(k => l.StartsWith(k + " =", StringComparison.Ordinal))))
            {
                if (!moved.Contains(spawn.Split('=')[1].Split(',')[0].Trim()))
                    Assert.Contains(spawn, oldMoon);
            }
        }
    }

    /// <summary>The world of the design rows of M07, M02 and M05 does not change, but for one move: the jump-here
    /// trigger of the base game puts King of M02 at the point of its checkpoint.</summary>
    [Fact]
    public void TheDesignRowsOfOneSystemKeepTheirWorld()
    {
        foreach (var (set, scripts) in new[] { ("vanilla", Scripts()), ("hd", Slots(Hde.Value)) })
        {
            foreach (var (slot, trigger) in new[] { ("M07", "wait_for_us_to_attack_enemy"), ("M05", "chase_player") })
            {
                var plan = Plan(Slot(scripts, slot), trigger);
                Assert.True(plan.CanStart, plan.Reason);
                Assert.Equal(OldWorldText(Slot(scripts, slot), trigger), WorldText(plan));
                Assert.DoesNotContain(plan.Companions, c => c.Moved);
                Assert.Empty(plan.LeftOut);
            }

            var m02 = Slot(scripts, "M02");
            var range = Plan(m02, "in_range");
            var king = Assert.Single(range.Companions);
            Assert.Equal("King", king.Object);
            Within(StartWorld.NearDistance, range.Place.Position, king.Position, $"{set} in_range King");
            Assert.Equal(OldWorldText(m02, "in_range"),
                WorldText(range).Where(l => !l.StartsWith("Act_RelocateShip = King,", StringComparison.Ordinal)));
            if (set == "vanilla")
            {
                var spawn = m02.Index.Trigger("key_save_fly_to_debris")!.Actions.First(a => a.Value(0) == "King");
                Assert.Equal(Point(spawn.Line.Select(RetailText.Of).ToList(), 2), king.Position);
            }
        }
    }

    /// <summary>
    /// Each approximate plan of both games: a plan that starts without the setup check also starts with it. No world line
    /// names an object outside the start system, and no object has two spawn lines. Each companion of a space or a base
    /// start is within 5,000 units of the anchor. A plan without a start system keeps each world line. The test writes
    /// the counts to its output.
    /// </summary>
    [Fact]
    public void EachApproximatePlanMakesOnlyTheWorldOfItsStartSystem()
    {
        var watch = Stopwatch.StartNew();
        foreach (var (set, scripts) in new[] { ("vanilla", Scripts()), ("hd", Slots(Hde.Value)) })
        {
            int plans = 0, leftOut = 0, fuses = 0, companions = 0, moved = 0, withoutSystem = 0;
            foreach (var analysis in scripts)
            {
                var index = analysis.Index;
                foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate))
                {
                    var start = MissionStartPoint.At(trigger.Nickname);
                    var fast = StartPlan.Make(index, analysis.Script.Slot, start, checkSetup: false);
                    if (!fast.CanStart || fast.Kind != StartKind.Approximate)
                        continue;
                    var what = $"{set} {analysis.Script.Slot!.Slot} {trigger.Nickname}";
                    var plan = StartPlan.Make(index, analysis.Script.Slot, start);
                    Assert.True(plan.CanStart, $"{what}: {plan.Reason}");
                    plans++;
                    leftOut += plan.LeftOut.Count(l => l.Kind != StartObjectKind.Fuse);
                    fuses += plan.LeftOut.Count(l => l.Kind == StartObjectKind.Fuse);
                    companions += plan.Companions.Count;
                    moved += plan.Companions.Count(c => c.Moved);
                    var world = World(plan);
                    if (plan.StartSystem is not { } system)
                    {
                        withoutSystem++;
                        Assert.Equal(OldWorldText(analysis, trigger.Nickname), WorldText(plan));
                        continue;
                    }
                    var outside = plan.LeftOut.Where(l => l.Kind != StartObjectKind.Fuse)
                        .SelectMany(l => StartSetup.Members(index, l.Object).Append(l.Object))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, values) in world)
                    {
                        foreach (var name in ObjectNames(index, key, values))
                        {
                            Assert.False(outside.Contains(name), $"{what}: {key} names {name}, which the setup leaves out.");
                            Assert.False(!index.IsScriptObject(name) && index.World.ObjectOf(name) is { } item &&
                                         !MissionIndex.Is(item.System, system), $"{what}: {key} names {name} of another system.");
                        }
                    }
                    var twice = world.Where(l => SpawnKeys.Any(k => MissionIndex.Is(l.Key, k)))
                        .GroupBy(l => l.Values[0], StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                    Assert.True(twice == null, $"{what}: two spawn lines of {twice?.Key}.");
                    if (Anchor(index, plan.Place) is { } anchor)
                    {
                        foreach (var companion in plan.Companions.Where(c => c.Point != CompanionPoint.Kept))
                            Within(StartWorld.JumpHereDistance, anchor, companion.Position, $"{what} {companion.Object}");
                    }
                }
            }
            output.WriteLine($"{set}: {plans} approximate plans, {leftOut} left-out objects, {fuses} removed fuse lines, " +
                             $"{companions} companions ({moved} moved), {withoutSystem} plans without a start system.");
        }
        output.WriteLine($"Time: {watch.Elapsed.TotalSeconds:0} s.");
    }

    /// <summary>The world of the retail checks knows the dock object of a base of the system files.</summary>
    [Fact]
    public void TheWorldKnowsTheDockOfARetailBase()
    {
        var dock = Slot(Scripts(), "M02").Index.World.DockOf("li01_01_base");
        Assert.NotNull(dock);
        Assert.Equal("Li01", dock!.System, ignoreCase: true);
        Assert.Equal("li01_01_base", dock.DockWith, ignoreCase: true);
    }
}
