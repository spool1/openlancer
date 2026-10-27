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

    /// <summary>The world lines of the setup trigger of the world of a plan, without the starts of the active set at its
    /// end.</summary>
    private static List<(string Key, IReadOnlyList<string> Values)> World(StartPlan plan) =>
        plan.Setup.Count < 2 ? [] : plan.Setup[1].Lines.Skip(1).Where(l => l.Key != "Act_ActTrig").ToList();

    /// <summary>The triggers that a setup trigger of a plan starts.</summary>
    private static List<string> Starts(StartPlan plan, int at) =>
        plan.Setup[at].Lines.Where(l => l.Key == "Act_ActTrig").Select(l => l.Values[0]).ToList();

    private static List<string> WorldText(StartPlan plan) =>
        World(plan).Select(l => $"{l.Key} = {string.Join(", ", l.Values)}").ToList();

    /// <summary>The world lines of a plan as the setup made them before the note of 2026-10-28: each kept world line of
    /// the path.</summary>
    private static List<string> OldWorldText(MissionAnalysis analysis, string trigger)
    {
        var index = analysis.Index;
        var replay = StartReplay.Run(index, MissionStartPoint.At(trigger));
        var forced = replay.Path.Concat(replay.Changes.Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger))
            .Where(t => t != replay.Chosen).Distinct().ToList();
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


    private static Vector3 Point(IReadOnlyList<string> values, int from) =>
        new(Parse(values[from]), Parse(values[from + 1]), Parse(values[from + 2]));

    private static float Parse(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static void Within(float distance, Vector3 anchor, Vector3? point, string what)
    {
        Assert.NotNull(point);
        var away = Vector3.Distance(anchor, point!.Value);
        Assert.True(away <= distance, $"{what} is {away:0} units from the start.");
    }

    /// <summary>Each companion with a point is at least <see cref="StartWorld.Spacing"/> units from the anchor and from
    /// each other companion.</summary>
    private static void Apart(Vector3 anchor, IReadOnlyList<StartCompanion> companions, string what)
    {
        var placed = companions.Where(c => c is { Point: not CompanionPoint.Kept, Position: not null }).ToList();
        foreach (var companion in placed)
        {
            var away = Vector3.Distance(anchor, companion.Position!.Value);
            Assert.True(away >= StartWorld.Spacing - 0.05f, $"{what}: {companion.Object} is {away:0} units from the start.");
            foreach (var other in placed.TakeWhile(c => c != companion))
            {
                var apart = Vector3.Distance(other.Position!.Value, companion.Position.Value);
                Assert.True(apart >= StartWorld.Spacing - 0.05f,
                    $"{what}: {companion.Object} is {apart:0} units from {other.Object}.");
            }
        }
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
                // The point of the jump-here trigger entering_ku04, because the start is at its point. That trigger puts
                // bd_1 to bd_3 0 to 40 units from the player, so they take slots.
                var jump = m08.Index.Trigger("entering_ku04")!;
                var line = jump.Actions.Single(a => a.Value(0) == "our_formation");
                var formation = ku04.Companions.Single(c => c.Object == "our_formation");
                Assert.Equal(Point(line.Line.Select(RetailText.Of).ToList(), 1), formation.Position);
                Assert.Equal(CompanionPoint.JumpHere, formation.Point);
                Assert.All(ku04.Companions.Where(c => c.Object.StartsWith("bd_", StringComparison.Ordinal)),
                    c => Assert.Equal(CompanionPoint.Slot, c.Point));
            }
            foreach (var companion in ku04.Companions)
                Within(StartWorld.NearDistance, ku04.Place.Position, companion.Position, $"{set} ku04_init {companion.Object}");
            Apart(ku04.Place.Position, ku04.Companions, $"{set} ku04_init");
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
                    if (StartAnchor.Of(index, plan.Place, null)?.Point is { } anchor)
                    {
                        foreach (var companion in plan.Companions.Where(c => c.Point != CompanionPoint.Kept))
                            Within(StartWorld.JumpHereDistance, anchor, companion.Position, $"{what} {companion.Object}");
                        Apart(anchor, plan.Companions, what);
                    }
                    // The old and the new ship of one character never start together, for example juni and juni2.
                    var ships = plan.Companions.SelectMany(c => StartSetup.Members(index, c.Object))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var twin = ships.FirstOrDefault(s => s.TrimEnd("0123456789".ToCharArray()) is var stem && stem != s &&
                                                         ships.Contains(stem));
                    Assert.True(twin == null, $"{what}: {twin} and the old ship of its character start together.");
                    // Two ships of one character with two NPC records never start together either, for example juni
                    // and Juni_mactan. Wing pilots can share a name, but not also the voice and the costume.
                    var character = ships.Select(s => (Ship: s, Npc: Npc(index, s))).Where(s => s.Npc != null)
                        .GroupBy(s => Character(index, s.Npc!)).FirstOrDefault(g => g.Key != null &&
                            g.Select(s => s.Npc).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
                    Assert.True(character == null,
                        $"{what}: {string.Join(" and ", character?.Select(s => s.Ship) ?? [])} are one character.");
                }
            }
            output.WriteLine($"{set}: {plans} approximate plans, {leftOut} left-out objects, {fuses} removed fuse lines, " +
                             $"{companions} companions ({moved} moved), {withoutSystem} plans without a start system.");
        }
        output.WriteLine($"Time: {watch.Elapsed.TotalSeconds:0} s.");
    }

    /// <summary>The NPC record of a ship of the script, or null.</summary>
    private static string? Npc(MissionIndex index, string ship) => Value(index.Item(MissionSectionKind.Ship, ship), "npc");

    /// <summary>The character of an NPC record: its name, its voice and its costume, or null without a name.</summary>
    private static string? Character(MissionIndex index, string npc)
    {
        var record = index.Item(MissionSectionKind.Npc, npc);
        if (Value(record, "individual_name") is not { } name)
            return null;
        var costume = record!.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "space_costume"))?.Select(MissionIndex.Text);
        return $"{name}|{Value(record, "voice")}|{string.Join(",", costume ?? [])}";
    }

    private static string? Value(MissionSection? section, string key) =>
        section?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, key) && e.Count > 0) is { } entry
            ? MissionIndex.Text(entry[0])
            : null;

    /// <summary>The rows of the second review of 2026-10-28 in both games. M11 first_player_launch starts in space, and
    /// the world trigger waits for the launch, so the chosen trigger starts after the world and still gets the launch.
    /// M04 juni_mactan_tether: destroy_juni_hack removes the old Juni soon after the start, so only Juni_mactan travels
    /// with the player. M01a com_0420: an old timer leads to spawn_lsf_help, which ran at the restore of the
    /// checkpoint, so the setup keeps the reinforcements.</summary>
    [Fact]
    public void TheSecondReviewRowsGetTheirStart()
    {
        foreach (var (set, scripts) in new[] { ("vanilla", Scripts()), ("hd", Slots(Hde.Value)) })
        {
            var launch = Plan(Slot(scripts, "M11"), "first_player_launch");
            Assert.True(launch.CanStart, $"{set}: {launch.Reason}");
            Assert.Equal(StartPlaceKind.Space, launch.Place.Kind);
            Assert.Equal(["ol_play_world"], Starts(launch, 0));
            Assert.Equal(("Cnd_PlayerLaunch", "no_params"), (launch.Setup[1].Lines[0].Key, launch.Setup[1].Lines[0].Values[0]));
            Assert.Contains("first_player_launch", Starts(launch, 1));

            var tether = Plan(Slot(scripts, "M04"), "juni_mactan_tether");
            Assert.True(tether.CanStart, $"{set}: {tether.Reason}");
            Assert.Equal(["Juni_mactan"], tether.Companions.Select(c => c.Object));
            Assert.Contains("destroy_juni_hack", tether.ActiveSet);
            Assert.Contains(World(tether), l => l.Key == "Act_SpawnShip" && l.Values[0] == "juni");

            var help = Plan(Slot(scripts, "M01a"), "com_0420");
            Assert.True(help.CanStart, $"{set}: {help.Reason}");
            Assert.Contains("tm_o_attack_cam", help.ActiveSet);
            foreach (var ship in new[] { "lsf_reinforce1", "lsf_reinforce2", "lsf_reinforce3", "lsf_reinforce4" })
            {
                Assert.DoesNotContain(help.LeftOut, l => l.Object == ship);
                Assert.Contains(World(help), l => l.Key == "Act_SpawnShip" && l.Values[0] == ship);
            }
        }
    }

    /// <summary>The row of issue 45 in both games. M04 juni_mactan_tether starts in Iw03, after the jump from Li01. The
    /// game sent the exit from Li01 at the jump, so leaving_liberty_01 stopped the fail watcher too_far_from_magellan_dock
    /// of a point in Li01. arrive_at_jumpgate starts leaving_liberty_01, so it ran before the jump. A start at
    /// miner_encounter_sysen waits for that jump, so the watcher still waits there.</summary>
    [Fact]
    public void TheExitOfLi01StopsItsWatcherInM04()
    {
        foreach (var (set, scripts) in new[] { ("vanilla", Scripts()), ("hd", Slots(Hde.Value)) })
        {
            var m04 = Slot(scripts, "M04");
            var tether = Plan(m04, "juni_mactan_tether");
            Assert.True(tether.CanStart, $"{set}: {tether.Reason}");
            Assert.Equal("Iw03", tether.StartSystem);
            foreach (var trigger in new[] { "too_far_from_magellan_dock", "arrive_at_jumpgate", "write_log_Li01_sys_exit" })
                Assert.DoesNotContain(trigger, tether.ActiveSet);

            var ran = StartReplay.Run(m04.Index, MissionStartPoint.At("juni_mactan_tether")).Changes
                .Where(c => c.Kind == MachineChangeKind.Ran).Select(c => c.Trigger.Nickname).ToList();
            Assert.True(ran.IndexOf("arrive_at_jumpgate") >= 0 &&
                        ran.IndexOf("arrive_at_jumpgate") < ran.IndexOf("leaving_liberty_01") &&
                        ran.IndexOf("leaving_liberty_01") < ran.IndexOf("miner_encounter_sysen"), $"{set}: {string.Join(" ", ran)}");
            Assert.DoesNotContain("too_far_from_magellan_dock", ran);

            var before = Plan(m04, "miner_encounter_sysen");
            Assert.True(before.CanStart, $"{set}: {before.Reason}");
            Assert.Contains("too_far_from_magellan_dock", before.ActiveSet);
        }
    }

    /// <summary>The rows of the review of 2026-10-28 in both games. M11 com_1500: the newest list of the player names
    /// juni2 and king2, so the old juni and king are no companions. M05 chase_player and M08 ku04_init start the active
    /// set after the world. M11 bse_Li0112: postBuff_init makes King and Juni again after the launch, so the setup leaves
    /// out the old ships.</summary>
    [Fact]
    public void TheReviewRowsGetTheirCompanions()
    {
        foreach (var (set, scripts) in new[] { ("vanilla", Scripts()), ("hd", Slots(Hde.Value)) })
        {
            var m11 = Slot(scripts, "M11");
            var comsat = Plan(m11, "com_1500");
            Assert.True(comsat.CanStart, $"{set}: {comsat.Reason}");
            Assert.Equal(["juni2", "king2"], comsat.Companions.Select(c => c.Object));

            // The triggers of the active set start after the world, so their orders find the ships of the world.
            foreach (var (slot, trigger) in new[] { ("M05", "chase_player"), ("M08", "ku04_init") })
            {
                var space = Plan(Slot(scripts, slot), trigger);
                Assert.Equal(["ol_play_world"], Starts(space, 0));
                Assert.Equal(space.ActiveSet, Starts(space, 1));
            }

            var buffalo = Plan(m11, "bse_Li0112");
            Assert.True(buffalo.CanStart, $"{set}: {buffalo.Reason}");
            Assert.Empty(buffalo.Companions);
            foreach (var ship in new[] { "king", "juni" })
            {
                var row = Assert.Single(buffalo.LeftOut, l => l.Object == ship);
                Assert.Equal("postBuff_init", row.MadeAgainBy);
            }
            Assert.DoesNotContain(World(buffalo), l => l.Values.Count > 0 && l.Values[0] is "king" or "juni");
            // A base start starts the base trigger at the load, so it runs while the player is docked.
            Assert.Contains("bse_Li0112", Starts(buffalo, 0));
        }
    }

    /// <summary>The objective 3 seconds after the spawn of the play: the last objective line of the setup, then the
    /// objective lines of the triggers that the start runs in that time. These are the triggers of the active set with
    /// Cnd_True, Cnd_InSpace = true or a timer of at most 3 seconds, and the triggers that they start with such a
    /// condition.</summary>
    private static string Objective(MissionIndex index, StartPlan plan)
    {
        static bool IsObjective(string key) => MissionIndex.Is(key, "Act_SetNNObj") || MissionIndex.Is(key, "Act_NNPath");
        static bool Soon(MissionTrigger trigger) =>
            trigger.ConditionIs("Cnd_True") ||
            trigger.ConditionIs("Cnd_InSpace") && !MissionIndex.Is(trigger.Condition!.Value(0), "false") ||
            trigger.ConditionIs("Cnd_Timer") && float.TryParse(trigger.Condition!.Value(0), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var time) && time <= 3;
        var objective = plan.Setup[0].Lines.LastOrDefault(l => IsObjective(l.Key)) is var (key, values) && key != null
            ? $"{key} = {string.Join(", ", values)}"
            : "none";
        var queue = new Queue<MissionTrigger>(plan.ActiveSet.Select(n => index.Trigger(n)!).Where(Soon));
        var seen = new HashSet<MissionTrigger>();
        while (queue.Count > 0)
        {
            var trigger = queue.Dequeue();
            if (!seen.Add(trigger))
                continue;
            foreach (var line in trigger.Actions)
            {
                if (IsObjective(line.Key))
                    objective = $"{line.Key.Trim()} = {string.Join(", ", line.Line.Select(RetailText.Of))}";
                else if (MissionIndex.Is(line.Key, "Act_ActTrig") && line.Value(0) is { } name &&
                         index.Trigger(name) is { } next && Soon(next))
                    queue.Enqueue(next);
            }
        }
        return objective;
    }

    /// <summary>The objective of the user's report of far waypoints. M10 sye_Rh04again: the replay runs the timer and
    /// the trigger that set the objective of the dock ring, because the path completes that objective. M08 ku04_init: the
    /// replay runs the Cnd_InSpace trigger of the path to Ku01_05 at the launch in Ku01. So 3 seconds after the spawn,
    /// the objective is that of the path, not a waypoint far away.</summary>
    [Fact]
    public void TheObjectiveAfterTheSpawnIsThatOfThePath()
    {
        foreach (var (set, scripts) in new[] { ("vanilla", Scripts()), ("hd", Slots(Hde.Value)) })
        {
            var m10 = Slot(scripts, "M10");
            var rh04 = Plan(m10, "sye_Rh04again");
            Assert.DoesNotContain("delay_switch_23840_to_23845", rh04.ActiveSet);
            Assert.DoesNotContain("switch_23840_to_23845", rh04.ActiveSet);
            Assert.Equal("Act_SetNNObj = nn_blank, OBJECTIVE_HISTORY", Objective(m10.Index, rh04));

            var m08 = Slot(scripts, "M08");
            var ku04 = Plan(m08, "ku04_init");
            Assert.DoesNotContain("draw_path_to_newtoyko", ku04.ActiveSet);
            Assert.Equal("Act_SetNNObj = search", Objective(m08.Index, ku04));
        }
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
