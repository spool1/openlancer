using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using Xunit;
using ZoneShape = LibreLancer.Data.Schema.Universe.ZoneShape;

namespace OpenLancer.Tests;

/// <summary>
/// The Jump connection and Trade lane ring components of the Inspector and their completeness panels (AGENTS.md UI rule
/// 12): each step done and missing, with the text of its check and its "Go to" target, the jump count, the place of a
/// ring in its lane, a lane without a zone and the current lock of a gate. The data is invented (see
/// <see cref="ConnectionWorkspace"/> and <see cref="LaneWorkspace"/>).
/// </summary>
public sealed class ComponentCompletenessTests
{
    private static readonly SectionRef Gate = SceneWorkspace.Object("Li01_to_Li02");
    private static readonly SectionRef Partner = SceneWorkspace.Li02("Object", "Li02_to_Li01");

    private static SectionRef Ring(int n) => LaneWorkspace.Ring(n);

    private static void Run(EditSession session, EditPlan plan)
    {
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
    }

    private static CompletenessStep Step(EditSession session, SectionRef section, int index) =>
        ComponentCompleteness.Of(session, section)!.Steps[index];

    /// <summary>The message of the check of a section whose text starts with a text.</summary>
    private static string Check(EditSession session, SectionRef section, string start) =>
        ObjectComponents.Checks(session, section).Single(p => p.Message.StartsWith(start, System.StringComparison.Ordinal))
            .Message;

    /// <summary>
    /// A pair that goes both ways is complete: the partner goes back, both ends are jump gates, and Li01 has 1 of 10 jump
    /// objects. The component names the target system, the exit object and its section, and the lock of a new game.
    /// The fixture locks Li01_to_Li02 for the player and for NPCs, and Li02_to_Li01 for NPCs only.
    /// </summary>
    [Fact]
    public void APairThatGoesBothWaysIsCompleteAndGivesItsPartnerAndItsLock()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");

        var jump = ConnectionComponents.Jump(session, Gate)!;
        Assert.Equal("jump gate", jump.Kind);
        Assert.Equal("Li02", jump.System);
        Assert.Equal(session.SystemSection("Li02"), jump.SystemSection);
        Assert.Equal("Li02_to_Li01", jump.Exit);
        Assert.Equal(Partner, jump.Partner);
        Assert.Equal("jump gate", jump.PartnerKind);
        Assert.True(jump.GoesBack);
        Assert.True(jump.IsPair);
        Assert.Empty(jump.TrafficZones);
        Assert.Equal(JumpLock.Both, jump.Lock);
        Assert.Null(jump.LockUnavailable);
        Assert.Empty(jump.LockRefusals);
        Assert.Equal(JumpLock.Npc, ConnectionComponents.Jump(session, Partner)!.Lock);
        Assert.Equal(JumpLock.None, ConnectionComponents.Jump(session, ConnectionWorkspace.Li03("Object", "Li03_hole"))!.Lock);

        var report = ComponentCompleteness.Of(session, Gate)!;
        Assert.Equal("Jump connection", report.Title);
        Assert.Equal(["Partner goes back", "Kinds match", "Jump objects of Li01: 1 of 10"], report.Steps.Select(s => s.Name));
        Assert.True(report.Complete);
        Assert.Equal("All 3 steps complete.", report.Summary);
        Assert.All(report.Steps, s => Assert.Null(s.GoTo));
        // A station has no connection, and a ring has no jump connection.
        Assert.Null(ComponentCompleteness.Of(session, SceneWorkspace.Object("Li01_01")));
        Assert.Null(ConnectionComponents.Jump(session, SceneWorkspace.Object("ring1")));
    }

    /// <summary>A new pair shows at once from W: its partner in the other file, its traffic zone and its jump count.</summary>
    [Fact]
    public void ANewPairShowsItsTrafficZoneAndItsJumpCountAtOnce()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));
        var gate = SceneWorkspace.Object("Li01_to_Li03");

        var jump = ConnectionComponents.Jump(session, gate)!;

        Assert.Equal(ConnectionWorkspace.Li03("Object", "Li03_to_Li01"), jump.Partner);
        Assert.Equal([SceneWorkspace.Zone("Zone_Li01_to_Li03_traffic")], jump.TrafficZones);
        Assert.Equal(JumpLock.None, jump.Lock);
        var report = ComponentCompleteness.Of(session, gate)!;
        Assert.True(report.Complete);
        Assert.Equal("Jump objects of Li01: 2 of 10", report.Steps[2].Name);
    }

    /// <summary>
    /// A partner that does not go back misses the first step, with the text of the check and "Go to" the partner. A
    /// partner of another kind misses the second step in the same way. A jump gate without a goto line misses both, and
    /// a goto line to an object that does not exist goes to the system of the line.
    /// </summary>
    [Fact]
    public void EachMissingStepOfAJumpConnectionGivesTheTextOfItsCheckAndItsTarget()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Partner, "goto", ["Li03, Li03_gate, gate_tunnel_bretonia"]));
        var back = Step(session, Gate, 0);
        Assert.Equal(CompletenessState.Missing, back.State);
        Assert.Equal("The player can jump back only when the partner names this object in its goto line.", back.Reason);
        Assert.Equal(Check(session, Gate, "Li01_to_Li02 goes to Li02_to_Li01 in Li02, but"), back.Detail);
        Assert.Equal(Partner, back.GoTo);
        Assert.False(back.IsError);
        Assert.Equal("2 of 3 steps complete", ComponentCompleteness.Of(session, Gate)!.Summary);
        session.Undo();

        session.Execute(EditCommand.SetLinesFromText(Partner, "archetype", ["jumphole"]));
        var kinds = Step(session, Gate, 1);
        Assert.Equal(CompletenessState.Missing, kinds.State);
        Assert.Equal(Check(session, Gate, "Li01_to_Li02 is a jump gate and its partner Li02_to_Li01 is a jump hole."),
            kinds.Detail);
        Assert.Equal(Partner, kinds.GoTo);
        session.Undo();

        var lone = ComponentCompleteness.Of(session, ConnectionWorkspace.Li03("Object", "Li03_gate"))!;
        Assert.Equal([CompletenessState.Missing, CompletenessState.Missing, CompletenessState.Done],
            lone.Steps.Select(s => s.State));
        Assert.Equal("Li03_gate has no goto line.", lone.Steps[0].Detail);
        Assert.Equal("The connection has no partner to compare.", lone.Steps[1].Detail);
        Assert.Null(lone.Steps[0].GoTo);

        session.Execute(EditCommand.SetLinesFromText(Gate, "goto", ["Li02, Li02_nothing, gate_tunnel_bretonia"]));
        var missing = Step(session, Gate, 0);
        Assert.Equal(CompletenessState.Missing, missing.State);
        Assert.NotNull(missing.Detail);
        Assert.Equal(session.SystemSection("Li02"), missing.GoTo);
    }

    /// <summary>Eleven jump objects in Li01 miss the count step with the text of the count check, and the step goes to
    /// the system. The check is an error of the active mod, so the step is an error too.</summary>
    [Fact]
    public void ElevenJumpObjectsMissTheCountStep()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        for (var i = 0; i < 10; i++)
        {
            session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "Object",
            [
                ListWorkspace.Line("nickname", $"Li01_extra_{i}"), ListWorkspace.Line("archetype", "jumpgate"),
                ListWorkspace.Line("goto", "Li02", "Li02_to_Li01", "gate_tunnel_bretonia"),
            ]));
        }

        var count = Step(session, Gate, 2);

        Assert.Equal("Jump objects of Li01: 11 of 10", count.Name);
        Assert.Equal(CompletenessState.Missing, count.State);
        var problem = ObjectComponents.Checks(session, Gate).Single(p => p.Message.StartsWith("Li01 has 11 jump objects."));
        Assert.Equal(problem.Message, count.Detail);
        Assert.Equal(problem.Severity == DiagnosticSeverity.Error, count.IsError);
        Assert.Equal(session.SystemSection("Li01"), count.GoTo);
        Assert.Equal("The game crashes with more than 10 jump objects in one system.", count.Reason);
    }

    /// <summary>
    /// A lane with good links, two end names and a lane zone is complete. Each ring knows its place, its neighbors and
    /// the lane zone. Only the two end rings are end rings. A repair has nothing to change, so its button tells why.
    /// </summary>
    [Fact]
    public void AGoodLaneIsCompleteAndEachRingKnowsItsPlace()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        var second = ConnectionComponents.Ring(session, Ring(2))!;
        Assert.Equal((2, 4, false), (second.Index, second.Count, second.Loops));
        Assert.Equal("Ring 2 of 4", second.Place);
        Assert.Equal(Ring(1), second.Previous);
        Assert.Equal(Ring(3), second.Next);
        Assert.Equal(LaneWorkspace.Zone, second.Zone);
        Assert.Equal([true, false, false, true],
            Enumerable.Range(1, 4).Select(n => ConnectionComponents.Ring(session, Ring(n))!.IsEnd));
        Assert.Null(ConnectionComponents.Ring(session, Ring(1))!.Previous);
        Assert.Null(ConnectionComponents.Ring(session, Ring(4))!.Next);
        Assert.Null(second.InsertUnavailable);
        Assert.Equal(ConnectionComponents.NothingToRepair, second.RepairUnavailable);
        Assert.Null(second.DeleteUnavailable);

        var report = ComponentCompleteness.Of(session, Ring(2))!;
        Assert.Equal("Trade lane", report.Title);
        Assert.Equal(["Links go both ways", "Names at both ends", "Zone covers the lane"], report.Steps.Select(s => s.Name));
        Assert.True(report.Complete);
        // A ring has no jump connection, and a lane zone has no ring component.
        Assert.Null(ConnectionComponents.Jump(session, Ring(2)));
        Assert.Null(ConnectionComponents.Ring(session, LaneWorkspace.Zone));
    }

    /// <summary>A lane of 7 rings gives "Ring 3 of 7" for its third ring, with its neighbors and its new lane zone.</summary>
    [Fact]
    public void TheThirdRingOfALaneOfSevenRingsIsRingThreeOfSeven()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        var plan = LanePlans.Create(session, "li01", new TradeLaneEnd { Point = new(30000, 0, 30000), Name = "A" },
            new TradeLaneEnd { Point = new(30000, 0, 75000), Name = "B" });
        Run(session, plan);
        Assert.Equal(7, plan.Rings.Count);

        var third = ConnectionComponents.Ring(session, plan.Rings[2])!;

        Assert.Equal("Ring 3 of 7", third.Place);
        Assert.Equal(plan.Rings[1], third.Previous);
        Assert.Equal(plan.Rings[3], third.Next);
        Assert.Equal(plan.Zone, third.Zone);
        Assert.False(third.IsEnd);
        Assert.True(ComponentCompleteness.Of(session, plan.Rings[2])!.Complete);
    }

    /// <summary>A lane without a zone misses the zone step, which tells how to add one and goes to the first ring. A
    /// lane zone that misses the lane after a move of an end ring gives the text of the zone check and goes to the
    /// zone.</summary>
    [Fact]
    public void ALaneWithoutAZoneAndAZoneThatMissesTheLaneMissTheZoneStep()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.DeleteSection(LaneWorkspace.Zone));
        var none = Step(session, Ring(2), 2);
        Assert.Equal(CompletenessState.Missing, none.State);
        Assert.Equal("Lane traffic spawns only in a lane zone that covers the lane.", none.Reason);
        Assert.StartsWith("No zone of the file covers the lane from Li01_Trade_Lane_Ring_1 to Li01_Trade_Lane_Ring_4.",
            none.Detail);
        Assert.Equal(Ring(1), none.GoTo);
        Assert.Null(ConnectionComponents.Ring(session, Ring(2))!.Zone);
        Assert.Equal("2 of 3 steps complete", ComponentCompleteness.Of(session, Ring(2))!.Summary);
        session.Undo();

        session.Execute(EditCommand.SetLinesFromText(Ring(4), "pos", ["40000, 0, -20000"]));
        var off = Step(session, Ring(2), 2);
        Assert.Equal(CompletenessState.Missing, off.State);
        Assert.Equal(Check(session, LaneWorkspace.Zone, "The lane zone Zone_Li01_Tradelane_1 does not cover"), off.Detail);
        Assert.Equal(LaneWorkspace.Zone, off.GoTo);
    }

    /// <summary>
    /// An end whose archetype has a type that the route tables do not count gets the text of its own type in the kinds
    /// step, also when its partner is a jump hole. Such an end takes no lock. A partner that is an airlock gate gets the
    /// kinds text with the right article.
    /// </summary>
    [Fact]
    public void TheKindsStepNamesTheEndWhoseTypeTheRouteTablesDoNotCount()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ListWorkspace.SolarPath, SceneWorkspace.Solars + ConnectionWorkspace.Solars + """

            [Solar]
            nickname = old_hole
            type = JUMPHOLE
            """);
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Gate, "archetype", ["old_hole"]));
        session.Execute(EditCommand.SetLinesFromText(Partner, "archetype", ["jumphole"]));

        var kinds = Step(session, Gate, 1);

        Assert.Equal(CompletenessState.Missing, kinds.State);
        Assert.Equal("Li01_to_Li02 has an archetype of the type JUMPHOLE. The route tables count only the types " +
                     "JUMP_GATE and JUMP_HOLE. Give Li01_to_Li02 an archetype of one of these types.", kinds.Detail);
        Assert.Null(kinds.GoTo);
        Assert.Null(ConnectionComponents.Jump(session, Gate)!.Lock);
        Assert.Empty(SteLint.Problems(kinds.Detail!));
        session.Undo();
        session.Undo();

        session.Execute(EditCommand.SetLinesFromText(Partner, "archetype", ["airlock"]));
        Assert.StartsWith("Li01_to_Li02 is a jump gate and its partner Li02_to_Li01 is an airlock gate.",
            Step(session, Gate, 1).Detail);
    }

    /// <summary>
    /// A gate lock file that the editor cannot change makes the lock plan refuse each other lock. Each refused lock
    /// gives the refusal of the plan, and the whole dropdown takes the first one. The profile takes edits, so the reason
    /// is not the reason of the profile.
    /// </summary>
    [Fact]
    public void ALockThatThePlanRefusesGivesTheRefusalOfThePlan()
    {
        using var ws = ConnectionWorkspace.Create();
        // The active mod replaces the whole gate lock file outside its folder, so the editor cannot change the file.
        ws.Mod("act", new()
        {
            [@"files\" + ListWorkspace.GroupsPath] = File.ReadAllText(Path.Combine(ws.Base, ListWorkspace.GroupsPath)),
        });
        var session = ws.Session("act");
        Assert.Null(session.ReadOnlyReason);
        Assert.NotNull(session.Get(ConnectionWorkspace.Locks)!.ReadOnlyReason);

        var jump = ConnectionComponents.Jump(session, Gate)!;

        Assert.Equal(JumpLock.Both, jump.Lock);
        Assert.Equal([JumpLock.None, JumpLock.Player, JumpLock.Npc], jump.LockRefusals.Keys.Order());
        foreach (var (choice, refusal) in jump.LockRefusals)
        {
            var plan = ConnectionPlans.SetLock(session, Gate, choice);
            Assert.False(plan.CanRun);
            Assert.Equal(plan.Refusals[0].Message, refusal);
        }

        Assert.Equal(jump.LockRefusals[JumpLock.None], jump.LockUnavailable);
    }

    /// <summary>
    /// The zone step of a lane without a zone gives the steps of Create, the lane zone rule and the values of a new
    /// lane zone. A box zone that follows the text completes the step only after its rename to a nickname with the word
    /// Tradelane in this letter case.
    /// </summary>
    [Fact]
    public void ABoxZoneThatFollowsTheTextOfTheZoneStepCompletesTheLane()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(LaneWorkspace.Zone));
        var detail = Step(session, Ring(2), 2).Detail!;
        Assert.Contains("In the Hierarchy, use Create > Zone > Box.", detail);
        Assert.Contains("a nickname with the word Tradelane, and the letter case counts.", detail);
        Assert.Contains("Its z size must stay more than 10 times its x size and its y size.", detail);
        Assert.Contains("scene_create_zone", detail);
        var values = Regex.Matches(detail, @"\(([^)]*)\)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["11250, 0, -20000", "0, -90, 0", "500, 500, 23500"], values);

        // Create > Zone > Box puts a zone with a default nickname and a default size at the pivot of the Scene camera.
        var create = ScenePlans.Create(session, "Li01", SceneTemplate.Zone, null, Vector3.Zero, null, shape: ZoneShape.BOX);
        Run(session, create);
        var zone = create.Adds[0];
        foreach (var (key, value) in new[] { ("pos", values[0]), ("rotate", values[1]), ("size", values[2]) })
        {
            session.Execute(EditCommand.SetLinesFromText(zone, key, [value]));
        }

        Assert.Equal(CompletenessState.Missing, Step(session, Ring(2), 2).State);
        Run(session, ScenePlans.Rename(session, zone, "Zone_Li01_tradelane_2"));
        Assert.Equal(CompletenessState.Missing, Step(session, Ring(2), 2).State);
        session.Undo();
        Run(session, ScenePlans.Rename(session, zone, "Zone_Li01_Tradelane_2"));

        Assert.Equal(CompletenessState.Done, Step(session, Ring(2), 2).State);
        Assert.Equal(SceneWorkspace.Zone("Zone_Li01_Tradelane_2"), ConnectionComponents.Ring(session, Ring(2))!.Zone);
    }

    /// <summary>The reason of "Insert ring after" comes from the insert plan. A ring without a position gets no ring
    /// after it, and the plan tells why.</summary>
    [Fact]
    public void TheReasonOfInsertRingAfterComesFromItsPlan()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(2), "pos"));

        var reason = ConnectionComponents.Ring(session, Ring(2))!.InsertUnavailable;

        var plan = LanePlans.InsertRing(session, Ring(2), LanePlace.After);
        Assert.False(plan.CanRun);
        Assert.Equal(plan.Refusals[0].Message, reason);
        Assert.StartsWith("Li01_Trade_Lane_Ring_2 has no position.", reason);
    }

    /// <summary>A link that does not go both ways misses the link step with the text of the check, goes to the ring of
    /// the problem, and has the fix "Repair lane". The repair makes the lane complete.</summary>
    [Fact]
    public void AOneWayLinkMissesTheLinkStepWithTheRepairFix()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(3), "prev_ring"));

        var links = Step(session, Ring(1), 0);

        Assert.Equal(CompletenessState.Missing, links.State);
        Assert.Equal(Check(session, Ring(2), "The rings Li01_Trade_Lane_Ring_2 and Li01_Trade_Lane_Ring_3"), links.Detail);
        Assert.Equal(Ring(2), links.GoTo);
        Assert.Equal(ComponentCompleteness.RepairFix, links.Fix);
        Assert.Null(ConnectionComponents.Ring(session, Ring(1))!.RepairUnavailable);
        Run(session, LanePlans.Repair(session, Ring(1)));
        Assert.True(ComponentCompleteness.Of(session, Ring(1))!.Complete);
    }

    /// <summary>
    /// A lost next_ring line in the middle of a lane breaks the lane at that ring. The link step counts the next ring
    /// that still names it. So the link step and the name step both miss with the text of the break, go to the ring, and
    /// have the fix "Repair lane". The repair makes the lane complete.
    /// </summary>
    [Fact]
    public void ALostLinkInTheMiddleMissesTheLinkStepAndTheNameStepWithTheRepairFix()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(2), "next_ring"));

        var links = Step(session, Ring(1), 0);
        var names = Step(session, Ring(1), 1);

        var text = Check(session, Ring(2), "Li01_Trade_Lane_Ring_3 links to Li01_Trade_Lane_Ring_2 in its prev_ring line");
        Assert.Equal((CompletenessState.Missing, text, (object?)Ring(2), ComponentCompleteness.RepairFix),
            (links.State, links.Detail, links.GoTo, links.Fix));
        Assert.Equal((CompletenessState.Missing, text, (object?)Ring(2), ComponentCompleteness.RepairFix),
            (names.State, names.Detail, names.GoTo, names.Fix));
        Assert.Equal("Ring 2 of 2", ConnectionComponents.Ring(session, Ring(2))!.Place);
        Run(session, LanePlans.Repair(session, Ring(2)));
        Assert.True(ComponentCompleteness.Of(session, Ring(1))!.Complete);
    }

    /// <summary>An end without a name misses the name step and goes to that end ring.</summary>
    [Fact]
    public void AnEndWithoutANameGoesToItsRing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(4), "tradelane_space_name"));

        var names = Step(session, Ring(2), 1);

        Assert.Equal(CompletenessState.Missing, names.State);
        Assert.Equal(Check(session, Ring(4), "The trade lane of Li01_Trade_Lane_Ring_4 has no name at its end."), names.Detail);
        Assert.Equal(Ring(4), names.GoTo);
        Assert.Null(names.Fix);
    }

    /// <summary>A circle of the active mod is an error of the link step, so the panel opens its list. A lane in a circle
    /// has no ends, so no ring is an end ring.</summary>
    [Fact]
    public void ACircleOfTheActiveModIsAnErrorAndTheLaneHasNoEnds()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_1"]));
        session.Execute(EditCommand.SetLinesFromText(Ring(1), "prev_ring", ["Li01_Trade_Lane_Ring_4"]));

        var report = ComponentCompleteness.Of(session, Ring(2))!;
        var ring = ConnectionComponents.Ring(session, Ring(2))!;

        Assert.True(report.Steps[0].IsError);
        Assert.True(report.HasError);
        Assert.Equal("The lane goes in a circle, so it has no ends.", report.Steps[1].Detail);
        Assert.True(ring.Loops);
        Assert.False(ring.IsEnd);
        Assert.EndsWith("of 4, in a circle", ring.Place);
        // Each ring of the circle has the ring before it and the ring after it by the links, also across the start.
        foreach (var n in Enumerable.Range(1, 4))
        {
            var info = ConnectionComponents.Ring(session, Ring(n))!;
            Assert.Equal((Ring((n + 2) % 4 + 1), Ring(n % 4 + 1)), (info.Previous, info.Next));
        }
    }

    /// <summary>A ring without links is in no lane: all three steps miss. A repair has no lane to join, so the step has
    /// no fix. An object of another type with a link line has no ring component and no report.</summary>
    [Fact]
    public void ARingWithoutLinksIsInNoLane()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "Object",
        [
            ListWorkspace.Line("nickname", "Li01_lone_ring"), ListWorkspace.Line("pos", "0", "0", "90000"),
            ListWorkspace.Line("archetype", "Trade_Lane_Ring"),
        ]));
        var lone = SceneWorkspace.Object("Li01_lone_ring");

        var report = ComponentCompleteness.Of(session, lone)!;
        var ring = ConnectionComponents.Ring(session, lone)!;

        Assert.Equal("0 of 3 steps complete", report.Summary);
        Assert.Equal("Li01_lone_ring links to no other ring, so it is in no lane.", report.Steps[0].Detail);
        Assert.Null(report.Steps[0].Fix);
        Assert.Equal(("Ring 1 of 1", true), (ring.Place, ring.IsEnd));

        var sun = SceneWorkspace.Object("Li01_sun");
        session.Execute(EditCommand.SetLinesFromText(sun, "next_ring", ["Li01_Trade_Lane_Ring_1"]));
        Assert.Null(ConnectionComponents.Ring(session, sun));
        Assert.Null(ComponentCompleteness.Of(session, sun));
    }

    /// <summary>The texts of the info icons, of the steps and of the repair reason pass the STE lint.</summary>
    [Fact]
    public void TheTextsPassTheSteLint()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(LaneWorkspace.Zone));
        session.Execute(EditCommand.RemoveName(Ring(4), "tradelane_space_name"));
        var lane = ComponentCompleteness.Of(session, Ring(2))!;
        using var connections = ConnectionWorkspace.Create();
        var other = connections.Session("act");
        var jump = ComponentCompleteness.Of(other, ConnectionWorkspace.Li03("Object", "Li03_gate"))!;

        var texts = new[] { ConnectionComponents.JumpHelp, ConnectionComponents.RingHelp }
            .SelectMany(h => new[] { h.WhatItIs, h.WhatItWrites })
            .Concat(lane.Steps.Concat(jump.Steps).SelectMany(s => new[] { s.Reason, s.Detail }).OfType<string>())
            .Append(ConnectionComponents.NothingToRepair);

        Assert.All(texts, t => Assert.Empty(SteLint.Problems(t)));
    }

    /// <summary>A ring that the editor cannot change gives the reason for each ring control. A profile without an active
    /// mod gives its reason for each control, also the lock.</summary>
    [Fact]
    public void AReadOnlyRingAndAReadOnlyProfileGiveTheReasonForEachControl()
    {
        using (var ws = LaneWorkspace.Create())
        {
            // The active mod replaces the whole file of Li01 outside its folder, so the editor cannot change the file.
            ws.Mod("act", new() { [@"files\" + SceneWorkspace.Li01Path] = LaneWorkspace.Li01Text });
            var session = ws.Session("act");
            var reason = session.Get(Ring(2))!.ReadOnlyReason;
            Assert.NotNull(reason);

            var ring = ConnectionComponents.Ring(session, Ring(2))!;

            var expected = $"Li01_Trade_Lane_Ring_2: {reason}";
            Assert.Equal([expected, expected, expected], [ring.InsertUnavailable, ring.RepairUnavailable, ring.DeleteUnavailable]);
        }

        using (var ws = ConnectionWorkspace.Create())
        {
            var session = ws.Session(null);
            Assert.NotNull(session.ReadOnlyReason);
            Assert.Equal(session.ReadOnlyReason, ConnectionComponents.Jump(session, Gate)!.LockUnavailable);
            Assert.Equal(JumpLock.Both, ConnectionComponents.Jump(session, Gate)!.Lock);
        }
    }
}
