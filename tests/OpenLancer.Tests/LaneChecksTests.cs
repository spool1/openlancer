using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor.Inspector;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The checks of the trade lanes: a lane that goes in a circle is an error of the active mod and stops the save. A
/// one-way link, an end without a name, a link to an object that is no ring and a ring without a lane give warnings. A
/// placed ring inside an object or a zone with damage and a lane zone away from its lane warn too.
/// Each text names a fix that works. The data is invented (see <see cref="LaneWorkspace"/>).
/// </summary>
public sealed class LaneChecksTests
{
    private const string RepairRing3 = "Repair the lane of Li01_Trade_Lane_Ring_3 with the button Repair lane in the " +
                                       "Inspector. An agent uses trade_lane_repair.";

    private static SectionRef Ring(int n) => LaneWorkspace.Ring(n);

    /// <summary>The lane checks of a section, without the other component checks.</summary>
    private static Diagnostic[] Lane(EditSession session, SectionRef section) =>
        LaneChecks.Check(session, section).ToArray();

    /// <summary>The circle problems of a ring.</summary>
    private static Diagnostic[] Circle(EditSession session, SectionRef section) =>
        Lane(session, section).Where(p => p.Message.Contains("goes in a circle")).ToArray();

    private static void AddRing(EditSession session, string nickname, string pos, params (string Key, string Value)[] lines) =>
        session.Execute(EditCommand.AddSection(SceneWorkspace.Li01Path, "Object",
        [
            ListWorkspace.Line("nickname", nickname), ListWorkspace.Line("pos", pos.Split(", ")),
            ListWorkspace.Line("archetype", "Trade_Lane_Ring"),
            .. lines.Select(l => ListWorkspace.Line(l.Key, l.Value)),
        ]));

    private static void Run(EditSession session, EditPlan plan)
    {
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
    }

    private static void AssertSte(Diagnostic problem) => Assert.Empty(SteLint.Problems(problem.Message));

    /// <summary>A lane with good links, two end names and a lane zone that covers it gives no problem. So does a lane
    /// zone with no ring near it.</summary>
    [Fact]
    public void AGoodLaneGivesNoProblem()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        Assert.All(Enumerable.Range(1, 4), n => Assert.Empty(ObjectComponents.Checks(session, Ring(n))));
        Assert.Empty(ObjectComponents.Checks(session, LaneWorkspace.Zone));
        Assert.Empty(Lane(session, SceneWorkspace.Zone("Zone_Li01_sun_death")));
        Assert.Empty(Lane(session, SceneWorkspace.Object("Li01_to_Li02")));
    }

    /// <summary>
    /// A hand-made circle of the active mod is an error on each ring of the circle. The save gate gives one error and
    /// refuses the save. The error names the first ring of the circle in the file whose link line the active mod writes.
    /// The text names the repair and the delete of that ring. The Trade lane ring component, the raw INI block and the
    /// agent check show it. A repair of the lane ends the circle, and the save then runs. A delete of the ring that the
    /// text names ends the circle too.
    /// </summary>
    [Fact]
    public void ACircleOfTheActiveModStopsTheSaveAndNamesTheRing()
    {
        using var ws = LaneWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));
        var session = host.Session!;
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_1"]));
        session.Execute(EditCommand.SetLinesFromText(Ring(1), "prev_ring", ["Li01_Trade_Lane_Ring_4"]));
        const string circle = "The trade lane of Li01_Trade_Lane_Ring_1 goes in a circle. The game hangs when the HUD " +
                              "reads the lane. Repair the lane with the button Repair lane in the Inspector, or delete " +
                              "Li01_Trade_Lane_Ring_1. An agent uses trade_lane_repair or delete_items.";

        var problem = Assert.Single(Lane(session, Ring(1)));
        var error = Assert.Single(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));

        // Each circle gives one error, not one error for each ring of the circle.
        var saveError = Assert.Single(LaneChecks.SaveErrors(session, SceneWorkspace.Li01Path));
        Assert.Equal((circle, Ring(1)), (saveError.Message, saveError.Target));
        Assert.Equal(circle, problem.Message);
        AssertSte(problem);
        Assert.Equal((DiagnosticSeverity.Error, "prev_ring"), (problem.Severity, problem.Key));
        Assert.Equal((circle, Ring(1), "prev_ring"), (error.Message, error.Target, error.Key));
        Assert.All(new[] { 2, 3, 4 }, n => Assert.Equal(DiagnosticSeverity.Error, Assert.Single(Lane(session, Ring(n))).Severity));
        Assert.Equal("next_ring", Lane(session, Ring(4))[0].Key);
        Assert.False(session.Save().Saved);

        var component = ObjectComponents.Of(session, Ring(1)).Single(c => c.Kind == ObjectComponent.TradeLaneRing);
        Assert.Equal(circle, Assert.Single(component.Problems).Message);
        var view = session.Get(Ring(1))!;
        var line = view.Entries.ToList().FindIndex(e => e.Name.Equals("prev_ring", StringComparison.OrdinalIgnoreCase)) + 2;
        Assert.Contains(SectionExtras.SectionProblems(session, Ring(1), view), p => p.Problem.Message == circle && p.Line == line);
        var check = Ok(host, "check", """{ "scope": "item", "ref": "obj:Li01_Trade_Lane_Ring_1" }""");
        Assert.Equal("error", (string?)Assert.Single(check["problems"]!.AsArray(), p => (string?)p!["message"] == circle)!["severity"]);

        Run(session, LanePlans.Repair(session, Ring(1)));
        Assert.All(Enumerable.Range(1, 4), n => Assert.Empty(Lane(session, Ring(n))));
        Assert.True(session.Save().Saved);

        session.Undo();
        Assert.NotEmpty(Circle(session, Ring(2)));
        Run(session, ScenePlans.Delete(session, [Ring(1)]));
        Assert.All(Enumerable.Range(2, 3), n => Assert.Empty(Circle(session, Ring(n))));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));
    }

    /// <summary>
    /// The game walks each direction of a lane alone. A walk to next_ring that comes to a ring of the walk to prev_ring
    /// is no circle, so it gives no circle problem and the save runs. The one-way links still warn.
    /// </summary>
    [Fact]
    public void AWalkThatComesToTheOtherWalkIsNoCircle()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(1), "next_ring"));
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_1"]));

        var walk = LanePlans.LaneOf(session, Ring(2), strays: true)!;

        Assert.False(walk.Loops);
        Assert.Equal([Ring(1), Ring(2), Ring(3), Ring(4)], walk.Rings);
        Assert.All(Enumerable.Range(1, 4), n => Assert.Null(LanePlans.CircleOf(session, Ring(n))));
        Assert.All(Enumerable.Range(1, 4), n => Assert.Empty(Circle(session, Ring(n))));
        Assert.Contains(Lane(session, Ring(4)), p => p.Message.Contains("do not link both ways") && p.Key == "next_ring");
        Assert.Contains(Lane(session, Ring(2)), p => p.Message.Contains("do not link both ways") && p.Key == "prev_ring");
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));
        Assert.True(session.Save().Saved);
    }

    /// <summary>
    /// The circle error of the save gate goes on a ring of the circle, at a link line that the active mod writes. A ring
    /// before the circle comes first in the file. Its removed link line does not count, and its changed link line does
    /// not take the error either. The ring before the circle gets the problem only when its own walk goes round the
    /// circle.
    /// </summary>
    [Fact]
    public void TheCircleErrorGoesOnARingOfTheCircle()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_2"]));
        session.Execute(EditCommand.RemoveName(Ring(1), "next_ring"));
        const string circle = "The trade lane of Li01_Trade_Lane_Ring_4 goes in a circle. The game hangs when the HUD " +
                              "reads the lane. Repair the lane with the button Repair lane in the Inspector, or delete " +
                              "Li01_Trade_Lane_Ring_4. An agent uses trade_lane_repair or delete_items.";

        var error = Assert.Single(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));

        Assert.Equal((circle, Ring(4), "next_ring"), (error.Message, error.Target, error.Key));
        Assert.Equal(circle, Assert.Single(Circle(session, Ring(4))).Message);
        Assert.Empty(Circle(session, Ring(1)));
        Assert.All(new[] { 2, 3 }, n => Assert.Equal((DiagnosticSeverity.Error, "next_ring"),
            (Assert.Single(Circle(session, Ring(n))).Severity, Assert.Single(Circle(session, Ring(n))).Key)));

        session.Execute(EditCommand.SetLinesFromText(Ring(1), "next_ring", ["Li01_Trade_Lane_Ring_3"]));
        var tail = Assert.Single(Circle(session, Ring(1)));
        error = Assert.Single(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));

        Assert.Equal((DiagnosticSeverity.Error, "next_ring"), (tail.Severity, tail.Key));
        Assert.Equal((circle, Ring(4)), (error.Message, error.Target));
        Assert.False(session.Save().Saved);

        Run(session, ScenePlans.Delete(session, [Ring(4)]));
        Assert.All(Enumerable.Range(1, 3), n => Assert.Empty(Circle(session, Ring(n))));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));
    }

    /// <summary>
    /// A stray link of the active mod can make a circle that only the walk of the HUD sees. It is an error and stops the
    /// save. The relink of a delete does not follow the stray link, so a delete of another ring of the circle keeps the
    /// circle. The text names the ring with the stray link, and its delete ends the circle. When a ring off the line
    /// stops the repair, the text names only that delete.
    /// </summary>
    [Fact]
    public void AStrayLinkOfTheActiveModMakesACircleThatStopsTheSave()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_2"]));

        var error = Assert.Single(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));

        Assert.False(LanePlans.LaneOf(session, Ring(3))!.Loops);
        Assert.Equal("The trade lane of Li01_Trade_Lane_Ring_4 goes in a circle. The game hangs when the HUD reads the " +
                     "lane. Repair the lane with the button Repair lane in the Inspector, or delete Li01_Trade_Lane_Ring_4. " +
                     "An agent uses trade_lane_repair or delete_items.", error.Message);
        AssertSte(error);
        Assert.Equal((Ring(4), "next_ring", DiagnosticSeverity.Error), (error.Target, error.Key, error.Severity));
        Assert.All(new[] { 2, 3, 4 }, n => Assert.Equal(DiagnosticSeverity.Error, Assert.Single(Circle(session, Ring(n))).Severity));
        // The walk of the HUD from the first ring goes round the circle too.
        Assert.Equal((DiagnosticSeverity.Error, "next_ring"),
            (Assert.Single(Circle(session, Ring(1))).Severity, Assert.Single(Circle(session, Ring(1))).Key));
        Assert.False(session.Save().Saved);

        Run(session, ScenePlans.Delete(session, [Ring(3)]));
        Assert.NotEmpty(Circle(session, Ring(2)));
        session.Undo();

        AddRing(session, "Li01_Trade_Lane_Ring_5", "11250, 0, -10000", ("next_ring", "Li01_Trade_Lane_Ring_3"));
        var stopped = Assert.Single(Circle(session, Ring(2)));
        Assert.False(LanePlans.Repair(session, Ring(2)).CanRun);
        Assert.Equal("The trade lane of Li01_Trade_Lane_Ring_2 goes in a circle. The game hangs when the HUD reads the " +
                     "lane. Delete Li01_Trade_Lane_Ring_4. An agent uses delete_items.", stopped.Message);
        AssertSte(stopped);

        Run(session, ScenePlans.Delete(session, [Ring(4)]));
        Assert.All(new[] { 1, 2, 3, 5 }, n => Assert.Empty(Circle(session, Ring(n))));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));
        Assert.True(session.Save().Saved);
    }

    /// <summary>A circle of the game data is a warning. A save of another edit in the same file runs.</summary>
    [Fact]
    public void ACircleOfTheGameIsAWarning()
    {
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text
            .Replace("tradelane_space_name = 505", "tradelane_space_name = 505\nnext_ring = Li01_Trade_Lane_Ring_1",
                StringComparison.Ordinal)
            .Replace("tradelane_space_name = 504", "tradelane_space_name = 504\nprev_ring = Li01_Trade_Lane_Ring_4",
                StringComparison.Ordinal));
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_planet"), "pos", ["0, 0, 41000"]));

        var problem = Assert.Single(Lane(session, Ring(2)));

        Assert.StartsWith("The trade lane of Li01_Trade_Lane_Ring_2 goes in a circle.", problem.Message);
        Assert.Equal((DiagnosticSeverity.Warning, "next_ring"), (problem.Severity, problem.Key));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.Contains("goes in a circle"));
        Assert.True(session.Save().Saved);
    }

    /// <summary>
    /// A link that does not go both ways warns on the ring with the line, at its key. The text names the repair when it
    /// works. A repair writes the links again, and the warnings go. For a ring off the line of its lane, the repair
    /// stops, so the text names the move and the delete of that ring.
    /// </summary>
    [Fact]
    public void AOneWayLinkWarnsAndNamesAFixThatWorks()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(3), "prev_ring", ["Li01_Trade_Lane_Ring_1"]));

        var ring3 = Assert.Single(Lane(session, Ring(3)));
        var ring2 = Assert.Single(Lane(session, Ring(2)));

        Assert.Equal("The rings Li01_Trade_Lane_Ring_3 and Li01_Trade_Lane_Ring_1 do not link both ways. Ships on the " +
                     "lane can stop or go the wrong way. " + RepairRing3, ring3.Message);
        AssertSte(ring3);
        Assert.Equal((DiagnosticSeverity.Warning, "prev_ring"), (ring3.Severity, ring3.Key));
        Assert.StartsWith("The rings Li01_Trade_Lane_Ring_2 and Li01_Trade_Lane_Ring_3 do not link both ways.", ring2.Message);
        Assert.Equal("next_ring", ring2.Key);
        Assert.Empty(Lane(session, Ring(1)));
        Assert.True(session.Save().Saved);

        Run(session, LanePlans.Repair(session, Ring(3)));
        Assert.All(Enumerable.Range(1, 4), n => Assert.Empty(Lane(session, Ring(n))));

        AddRing(session, "Li01_Trade_Lane_Ring_5", "11250, 0, -10000", ("next_ring", "Li01_Trade_Lane_Ring_3"));
        var off = Assert.Single(Lane(session, Ring(5)), p => p.Key == "next_ring");
        Assert.Equal("The rings Li01_Trade_Lane_Ring_5 and Li01_Trade_Lane_Ring_3 do not link both ways. Ships on the " +
                     "lane can stop or go the wrong way. Li01_Trade_Lane_Ring_5 does not stand on the line of the lane, so " +
                     "a repair stops. Move Li01_Trade_Lane_Ring_5 onto the line, or delete it. An agent uses set_fields or " +
                     "delete_items.", off.Message);
        AssertSte(off);
        Assert.False(LanePlans.Repair(session, Ring(5)).CanRun);
    }

    /// <summary>An end ring without a lane name warns at its start or its end. set_text gives the name, and the warning
    /// goes.</summary>
    [Fact]
    public void AnEndRingWithoutANameWarns()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(4), "tradelane_space_name"));
        session.Execute(EditCommand.RemoveName(Ring(1), "tradelane_space_name"));

        var end = Assert.Single(Lane(session, Ring(4)));
        var start = Assert.Single(Lane(session, Ring(1)));

        Assert.Equal("The trade lane of Li01_Trade_Lane_Ring_4 has no name at its end. The HUD shows no lane label. Set " +
                     "the name in the Lane name row of Li01_Trade_Lane_Ring_4. An agent uses set_text with the field " +
                     "tradelane_space_name.", end.Message);
        AssertSte(end);
        Assert.Equal((DiagnosticSeverity.Warning, "tradelane_space_name"), (end.Severity, end.Key));
        Assert.StartsWith("The trade lane of Li01_Trade_Lane_Ring_1 has no name at its start.", start.Message);
        Assert.Empty(Lane(session, Ring(2)));

        session.Execute(ItemEdits.SetText(session, Ring(4), "tradelane_space_name", "Far Point"));
        Assert.Empty(Lane(session, Ring(4)));
    }

    /// <summary>
    /// A raw edit can remove the next_ring line of a middle ring. The walk from the first ring then stops at that ring,
    /// but the ring is no real end, because the next ring still names it. So the ring gets the repair as its fix, not
    /// the end name. A name does not end the warning. A repair does.
    /// </summary>
    [Fact]
    public void ALostLinkInTheMiddleOfALaneNamesTheRepair()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.RemoveName(Ring(2), "next_ring"));

        var ring2 = Assert.Single(Lane(session, Ring(2)));

        Assert.Equal("Li01_Trade_Lane_Ring_3 links to Li01_Trade_Lane_Ring_2 in its prev_ring line, but " +
                     "Li01_Trade_Lane_Ring_2 does not link back to Li01_Trade_Lane_Ring_3. Ships on the lane can stop at " +
                     "Li01_Trade_Lane_Ring_2, and the HUD shows no lane label. Repair the lane of Li01_Trade_Lane_Ring_2 " +
                     "with the button Repair lane in the Inspector. An agent uses trade_lane_repair.", ring2.Message);
        AssertSte(ring2);
        Assert.Equal((DiagnosticSeverity.Warning, "next_ring"), (ring2.Severity, ring2.Key));
        Assert.Contains(Lane(session, Ring(3)), p => p.Message.Contains("do not link both ways") && p.Key == "prev_ring");
        Assert.Equal([ring2.Message],
            LaneChecks.Lane(session, LanePlans.LaneOf(session, Ring(1))!).Select(p => p.Message));

        session.Execute(ItemEdits.SetText(session, Ring(2), "tradelane_space_name", "Middle"));
        Assert.StartsWith("Li01_Trade_Lane_Ring_3 links to Li01_Trade_Lane_Ring_2 in its prev_ring line, but " +
                          "Li01_Trade_Lane_Ring_2 does not link back to Li01_Trade_Lane_Ring_3. Ships on the lane can stop " +
                          "at Li01_Trade_Lane_Ring_2. Repair the lane", Assert.Single(Lane(session, Ring(2))).Message);
        session.Undo();

        Run(session, LanePlans.Repair(session, Ring(1)));
        Assert.All(Enumerable.Range(1, 4), n => Assert.Empty(Lane(session, Ring(n))));
    }

    /// <summary>A link to an object that is no ring warns with the archetype of the object. A repair removes the link,
    /// and the warning goes.</summary>
    [Fact]
    public void ALinkToAnObjectThatIsNoRingWarns()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_to_Li02"]));

        var problem = Assert.Single(Lane(session, Ring(4)));

        Assert.Equal("Li01_Trade_Lane_Ring_4 links to Li01_to_Li02, but Li01_to_Li02 is not a trade lane ring. Its " +
                     "archetype is lane_gate. The game does not count Li01_to_Li02 in the lane. Repair the lane of " +
                     "Li01_Trade_Lane_Ring_4 with the button Repair lane in the Inspector. An agent uses trade_lane_repair.",
            problem.Message);
        AssertSte(problem);
        Assert.Equal((DiagnosticSeverity.Warning, "next_ring"), (problem.Severity, problem.Key));
        Assert.Empty(ObjectComponents.Checks(session, SceneWorkspace.Object("Li01_to_Li02")));

        Run(session, LanePlans.Repair(session, Ring(4)));
        Assert.Null(LaneWorkspace.Line(session, Ring(4), "next_ring"));
        Assert.Empty(Lane(session, Ring(4)));
    }

    /// <summary>
    /// A ring with a lane name and no links warns. On the line of a lane, the text names the repair that joins it. Far
    /// from each lane, the text names the delete and the move. A link of an object that is no ring does not make a lane,
    /// so the warning stays. A ring without a name and without links, and a ring that another ring links to, give no such
    /// warning.
    /// </summary>
    [Fact]
    public void ARingWithANameAndNoLinksWarns()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        AddRing(session, "Li01_lone", "11250, 0, -20000", ("tradelane_space_name", "506"));
        AddRing(session, "Li01_far", "0, 0, 70000", ("tradelane_space_name", "507"));
        AddRing(session, "Li01_quiet", "0, 0, 90000");

        var lone = Assert.Single(Lane(session, SceneWorkspace.Object("Li01_lone")));
        var far = Assert.Single(Lane(session, SceneWorkspace.Object("Li01_far")));

        Assert.Equal("Li01_lone is a trade lane ring without a lane. Join it to the lane on its line with the button " +
                     "Repair lane in the Inspector, or delete it. An agent uses trade_lane_repair or delete_items.", lone.Message);
        Assert.Equal((DiagnosticSeverity.Warning, "tradelane_space_name"), (lone.Severity, lone.Key));
        Assert.Equal("Li01_far is a trade lane ring without a lane. Delete it, or move it onto the line of a lane and " +
                     "repair that lane. An agent uses delete_items, set_fields and trade_lane_repair.", far.Message);
        AssertSte(lone);
        AssertSte(far);
        Assert.Empty(Lane(session, SceneWorkspace.Object("Li01_quiet")));

        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_to_Li02"), "next_ring", ["Li01_lone"]));
        Assert.Equal(lone.Message, Assert.Single(Lane(session, SceneWorkspace.Object("Li01_lone"))).Message);

        session.Execute(EditCommand.SetLinesFromText(Ring(4), "next_ring", ["Li01_far"]));
        Assert.DoesNotContain(Lane(session, SceneWorkspace.Object("Li01_far")), p => p.Message.Contains("without a lane"));
    }

    /// <summary>A ring that the active mod places inside a zone with damage or inside an object warns at its position.
    /// A ring of the game at such a place gives no warning.</summary>
    [Fact]
    public void ARingThatTheActiveModPlacesInsideAZoneWithDamageOrAnObjectWarns()
    {
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text + """

            [zone]
            nickname = Zone_Li01_mine
            pos = 0, 0, -20000
            shape = SPHERE
            size = 1000
            damage = 100
            """);
        var session = ws.Session("act");
        Assert.Empty(Lane(session, Ring(1)));

        session.Execute(EditCommand.SetLinesFromText(Ring(1), "pos", ["100, 0, -20000"]));
        session.Execute(EditCommand.SetLinesFromText(Ring(2), "pos", ["500, 0, 39500"]));

        var mine = Assert.Single(Lane(session, Ring(1)), p => p.Key == "pos");
        var planet = Assert.Single(Lane(session, Ring(2)), p => p.Key == "pos");

        Assert.Equal("Li01_Trade_Lane_Ring_1 stands inside Zone_Li01_mine. Ships on the lane can take damage or hit it. Move " +
                     "the ring.", mine.Message);
        AssertSte(mine);
        Assert.Equal(DiagnosticSeverity.Warning, mine.Severity);
        Assert.StartsWith("Li01_Trade_Lane_Ring_2 stands inside Li01_planet.", planet.Message);
    }

    /// <summary>
    /// A move of an end ring away from the lane zone warns on the zone, at its size, and names the first ring. The ring
    /// gets no warning for it. Undo takes the warning away. A lane zone that covers its lane and a zone far from each
    /// lane give no warning.
    /// </summary>
    [Fact]
    public void ALaneZoneWarnsAfterAMoveOfAnEndRing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Ring(4), "pos", ["40000, 0, -20000"]));
        var problem = Assert.Single(ObjectComponents.Checks(session, LaneWorkspace.Zone));

        Assert.Equal("The lane zone Zone_Li01_Tradelane_1 does not cover the trade lane of Li01_Trade_Lane_Ring_1. Lane " +
                     "traffic spawns away from the lane. Change the position, the rotation or the size of the zone so that it " +
                     "covers each ring of the lane. An agent uses set_fields.", problem.Message);
        AssertSte(problem);
        Assert.Equal((DiagnosticSeverity.Warning, "size", LaneWorkspace.Zone), (problem.Severity, problem.Key, problem.Target));
        Assert.Empty(Lane(session, Ring(4)));

        session.Undo();
        Assert.Empty(ObjectComponents.Checks(session, LaneWorkspace.Zone));
    }

    /// <summary>A lane zone whose axis passes no ring gives no warning, also when a lane without a zone is in the
    /// system.</summary>
    [Fact]
    public void ALaneZoneFarFromALaneWithoutAZoneGivesNoWarning()
    {
        using var ws = LaneWorkspace.Create(LaneWorkspace.Li01Text + """

            [zone]
            nickname = Zone_Li01_Tradelane_2
            pos = -50000, 0, 20000
            rotate = 0, -90, 0
            shape = BOX
            size = 500, 500, 23500
            pop_type = major_tradelane
            """);
        var session = ws.Session("act");
        AddRing(session, "Li01_far_1", "0, 0, 70000", ("next_ring", "Li01_far_2"), ("tradelane_space_name", "506"));
        AddRing(session, "Li01_far_2", "7500, 0, 70000", ("prev_ring", "Li01_far_1"), ("tradelane_space_name", "507"));
        var far = SceneWorkspace.Zone("Zone_Li01_Tradelane_2");

        Assert.NotNull(LaneZones.Read(session, far));
        Assert.Null(LanePlans.ZoneOf(session, LanePlans.LaneOf(session, SceneWorkspace.Object("Li01_far_1"))!));
        Assert.Empty(Lane(session, far));
        Assert.Empty(Lane(session, LaneWorkspace.Zone));
    }

    /// <summary>A lane zone that covers its own lane gives no warning for a crossing lane without a zone, also when a ring
    /// of that lane stands on the axis of the zone.</summary>
    [Fact]
    public void TheZoneOfALaneGivesNoWarningForACrossingLane()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        AddRing(session, "Li01_cross_1", "11250, 0, -19000", ("next_ring", "Li01_cross_2"), ("tradelane_space_name", "506"));
        AddRing(session, "Li01_cross_2", "11250, 0, -5000", ("prev_ring", "Li01_cross_1"), ("tradelane_space_name", "507"));
        var cross = LanePlans.LaneOf(session, SceneWorkspace.Object("Li01_cross_1"))!;

        Assert.Equal(2, cross.Rings.Count);
        Assert.Null(LanePlans.ZoneOf(session, cross));
        Assert.NotNull(LanePlans.AxisDistance(LaneZones.Read(session, LaneWorkspace.Zone)!,
            LanePlans.PositionOf(session, cross.Rings[0])!.Value));
        Assert.Equal(LaneWorkspace.Zone, LanePlans.ZoneOf(session, LanePlans.LaneOf(session, Ring(1))!));
        Assert.Empty(Lane(session, LaneWorkspace.Zone));
    }

    /// <summary>An object that is no ring gets no lane problem, also with a next_ring line. The ring that it names gets
    /// none either.</summary>
    [Fact]
    public void TheLaneChecksSkipOtherObjects()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_to_Li02"), "next_ring",
            ["Li01_Trade_Lane_Ring_1"]));

        Assert.Empty(Lane(session, SceneWorkspace.Object("Li01_to_Li02")));
        Assert.Empty(Lane(session, Ring(1)));
    }
}
