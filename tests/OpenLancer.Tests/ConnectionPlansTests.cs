using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// A synthetic install for the jump connections, on top of <see cref="NewSystemWorkspace"/>. It adds the system Li03
/// with a faction of another house and a station far from the center. Li03 also has a jump gate and a jump hole
/// without a goto line. The install has the archetypes of a jump hole and an airlock gate. The gate of Li02 has a
/// faction, a loadout and a pilot, and the lists have the loadouts and pilots of the houses. The Li01 gate to Li02 has a
/// player lock, and both gates have an NPC lock. All names and values are invented.
/// </summary>
internal static class ConnectionWorkspace
{
    public const string Li03Path = @"DATA\UNIVERSE\SYSTEMS\LI03\li03.ini";
    public const string PilotsPath = @"DATA\MISSIONS\pilots_population.ini";

    public const string Li03Text = """
        [SystemInfo]
        local_faction = br_n_grp
        [Object]
        nickname = Li03_station
        pos = 60000, 0, 0
        archetype = station1
        [Object]
        nickname = Li03_gate
        pos = 0, 0, -30000
        archetype = jumpgate
        [Object]
        nickname = Li03_hole
        pos = 0, 0, 30000
        archetype = jumphole
        """;

    /// <summary>The solar archetypes of a jump hole and an airlock gate, after the archetypes of
    /// <see cref="SceneWorkspace.Solars"/>.</summary>
    public const string Solars = """

        [Solar]
        nickname = jumphole
        type = JUMP_HOLE
        [Solar]
        nickname = airlock
        type = AIRLOCK_GATE
        """;

    public static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null)
    {
        var ws = NewSystemWorkspace.Create(lowerFiles);
        ws.WriteBase(ListWorkspace.SolarPath, SceneWorkspace.Solars + Solars);
        ws.WriteBase(ListWorkspace.UniversePath, """
            [Base]
            nickname = Li01_01_Base
            system = Li01
            [Base]
            nickname = Li02_01_Base
            system = Li02
            [system]
            nickname = Li01
            file = systems\Li01\li01.ini
            pos = 7, 8
            [system]
            nickname = Li02
            file = systems\Li02\li02.ini
            pos = 6, 9
            NavMapScale = 2.5
            [system]
            nickname = Li03
            file = systems\Li03\li03.ini
            pos = 9, 8
            """);
        ws.WriteBase(ListWorkspace.Li02Path, NewSystemWorkspace.Li02Text.Replace("""
            goto = Li01, Li01_to_Li02, gate_tunnel_bretonia
            """, """
            goto = Li01, Li01_to_Li02, gate_tunnel_bretonia
            reputation = li_n_grp
            loadout = jumpgate_li_test
            pilot = pilot_test
            """, StringComparison.Ordinal));
        ws.WriteBase(Li03Path, Li03Text);
        ws.WriteBase(ListWorkspace.SolarLoadoutsPath, """
            [Loadout]
            nickname = lo_shared
            [Loadout]
            nickname = lo_solar
            [Loadout]
            nickname = jumpgate_li_test
            [Loadout]
            nickname = jumpgate_br_02
            """);
        ws.WriteBase(PilotsPath, """
            [Pilot]
            nickname = pilot_test
            [Pilot]
            nickname = pilot_solar_easiest
            """);
        ws.WriteBase(ListWorkspace.GroupsPath, $"""
            [Group]
            nickname = li_n_grp
            rep = 0.5, li_p_grp
            [Group]
            nickname = li_p_grp
            [Group]
            nickname = br_n_grp
            [locked_gates]
            locked_gate = {FLHash.CreateID("Li01_to_Li02")}
            npc_locked_gate = {FLHash.CreateID("Li02_to_Li01")}
            npc_locked_gate = {FLHash.CreateID("Li01_to_Li02")}
            """);
        return ws;
    }

    public static SectionRef Li03(string type, string key) => new(Li03Path, new SectionIdentity(type, key));

    public static SectionRef Locks => new(ListWorkspace.GroupsPath, new SectionIdentity("locked_gates", null));

    /// <summary>The lines of a section as name and value text, in order.</summary>
    public static List<(string Name, string Text)> Lines(EditSession session, SectionRef section) =>
        session.Get(section)!.Entries.Select(e => (e.Name, string.Join(", ", e.Values.Select(v => v.ToString())))).ToList();

    /// <summary>The text of the active mod that a placeholder value names, or null.</summary>
    public static string? TextOf(EditSession session, string placeholder) =>
        StringPlaceholder.TryParse(placeholder, out var parsed) ? session.Text(parsed) : null;

    /// <summary>The hashes of the lines of a key of the gate locks.</summary>
    public static List<uint> LockLines(EditSession session, string key) =>
        session.Get(Locks)!.Lines(key).Select(l => (uint)l.Values[0].ToInt32()).ToList();
}

/// <summary>
/// The plans of the jump connections: a new pair with its traffic zones, an existing end, the refusals and the gate
/// locks. Each plan is one entry of the edit history. The data is invented.
/// </summary>
public sealed class ConnectionPlansTests
{
    private static ConnectionPlan Run(EditSession session, ConnectionPlan plan)
    {
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        return plan;
    }

    private static Diagnostic Refusal(ConnectionPlan plan, string detail)
    {
        Assert.False(plan.CanRun);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal(detail, refusal.Rule);
        return refusal;
    }

    /// <summary>
    /// A jump gate pair between two systems. Each end has the lines in the retail order and a name from the pattern of
    /// the rule data. It has the generic infocard and a goto line to its partner. Each end gets a traffic zone without
    /// encounters.
    /// The ends go toward the other system on the nav map, and their front looks at the center. One undo takes back
    /// both files and the texts.
    /// </summary>
    [Fact]
    public void AGatePairAcrossTwoFilesIsOneUndoStep()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var options = new ConnectionOptions { SystemName = n => n == "Li03" ? "Leeds" : null };

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("li03"), options));

        var li01 = SceneWorkspace.Object("Li01_to_Li03");
        var li03 = ConnectionWorkspace.Li03("Object", "Li03_to_Li01");
        Assert.Equal([li01, li03], plan.Ends.Select(e => e.Object));
        var lines = ConnectionWorkspace.Lines(session, li01);
        Assert.Equal(["nickname", "ids_name", "pos", "rotate", "archetype", "ids_info", "jump_effect", "behavior",
            "difficulty_level", "goto"], lines.Select(l => l.Name));
        Assert.Equal(("pos", "50000, 0, 0"), lines[2]);
        Assert.Equal(("rotate", "0, 90, 0"), lines[3]);
        Assert.Equal(("goto", "Li03, Li03_to_Li01, gate_tunnel_bretonia"), lines.Single(l => l.Name == "goto"));
        Assert.Equal(("ids_info", "66145"), lines.Single(l => l.Name == "ids_info"));
        Assert.Equal("Leeds Jump Gate", ConnectionWorkspace.TextOf(session, (lines[1].Text)));
        var far = ConnectionWorkspace.Lines(session, li03);
        Assert.Equal(("pos", "-60000, 0, 0"), far[2]);
        Assert.Equal(("rotate", "0, -90, 0"), far[3]);
        Assert.Equal(("goto", "Li01, Li01_to_Li03, gate_tunnel_bretonia"), far.Single(l => l.Name == "goto"));
        Assert.Equal("Li01 Jump Gate", ConnectionWorkspace.TextOf(session, (far[1].Text)));
        Assert.Equal([SceneWorkspace.Zone("Zone_Li01_to_Li03_traffic"), ConnectionWorkspace.Li03("zone", "Zone_Li03_to_Li01_traffic")],
            plan.TrafficZones);
        Assert.Equal([("nickname", "Zone_Li01_to_Li03_traffic"), ("pos", "50000, 0, 0"), ("shape", "SPHERE"), ("size", "4000"),
            ("density", "10"), ("repop_time", "10"), ("max_battle_size", "4"), ("pop_type", "jumpgate"), ("relief_time", "15")],
            ConnectionWorkspace.Lines(session, plan.TrafficZones[0]));
        Assert.Equal([new JumpObjectCount("Li01", 2, 10), new JumpObjectCount("Li03", 1, 10)], plan.JumpObjects);
        // Li01 to Li02 has a player lock, so before the plan only Li02 reaches Li01. After it, Li01 and Li03 reach each
        // other, and Li02 reaches Li03 through Li01.
        Assert.Equal(new RouteDiff(3, 0, 0, 0), plan.Routes);
        Assert.Equal(new RouteDiff(3, 0, 0, 0), RouteTable.Diff(session.GameRoutes.Tables.All, session.Routes.Tables.All));
        Assert.Empty(plan.Warnings);
        Assert.False(plan.NeedsConfirmation);

        session.Undo();
        Assert.Null(session.Get(li01));
        Assert.Null(session.Get(li03));
        Assert.Null(session.Get(plan.TrafficZones[1]));
        Assert.Null(ConnectionWorkspace.TextOf(session, (lines[1].Text)));
    }

    /// <summary>A jump hole pair can stay in one system. Its two ends have no direction on the nav map. So they go to
    /// the two sides of the center, at the distance of the farthest object. The second end takes the next free
    /// nickname.</summary>
    [Fact]
    public void AHolePairInOneSystemGoesToBothSidesOfTheCenter()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li03"),
            new ConnectionEnd("Li03")));

        var a = ConnectionWorkspace.Lines(session, ConnectionWorkspace.Li03("Object", "Li03_to_Li03_hole"));
        var b = ConnectionWorkspace.Lines(session, ConnectionWorkspace.Li03("Object", "Li03_to_Li03_hole_1"));
        Assert.Equal(["nickname", "ids_name", "pos", "rotate", "archetype", "ids_info", "jump_effect", "goto", "visit"],
            a.Select(l => l.Name));
        Assert.Equal([("pos", "60000, 0, 0"), ("rotate", "0, 90, 0"), ("archetype", "jumphole"), ("ids_info", "66146"),
            ("jump_effect", "jump_effect_hole"), ("goto", "Li03, Li03_to_Li03_hole_1, gate_tunnel_bretonia"), ("visit", "0")],
            a.Skip(2));
        Assert.Equal(("pos", "-60000, 0, 0"), b[2]);
        Assert.Equal(("goto", "Li03, Li03_to_Li03_hole, gate_tunnel_bretonia"), b.Single(l => l.Name == "goto"));
        Assert.Equal("wormhole", ConnectionWorkspace.Lines(session, plan.TrafficZones[1]).Single(l => l.Name == "pop_type").Text);
        Assert.Equal("9", ConnectionWorkspace.Lines(session, plan.TrafficZones[1]).Single(l => l.Name == "density").Text);
        Assert.All(plan.Ends, e => Assert.Null(e.Loadout));
        Assert.Equal([new JumpObjectCount("Li03", 2, 10), new JumpObjectCount("Li03", 2, 10)], plan.JumpObjects);
    }

    /// <summary>
    /// Two systems at one place of the nav map have no direction either. Two systems on a slant give each end the
    /// direction to the other system. The x of the nav map goes on the x axis, and its y on the z axis.
    /// </summary>
    [Fact]
    public void TheDefaultPlaceFollowsTheNavMap()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(NewSystemWorkspace.System("Li03"), "pos", ["7, 8"]));

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));
        Assert.Equal("50000, 0, 0", ConnectionWorkspace.Lines(session, plan.Ends[0].Object)[2].Text);
        Assert.Equal("-60000, 0, 0", ConnectionWorkspace.Lines(session, plan.Ends[1].Object)[2].Text);

        session.Undo();
        session.Execute(EditCommand.SetLinesFromText(NewSystemWorkspace.System("Li03"), "pos", ["10, 4"]));
        plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));
        // From Li01 at 7, 8 to Li03 at 10, 4: the direction is 0.6, -0.8.
        Assert.Equal([("pos", "30000, 0, -40000"), ("rotate", "0, 143, 0")],
            ConnectionWorkspace.Lines(session, plan.Ends[0].Object).Skip(2).Take(2));
        Assert.Equal([("pos", "-36000, 0, 48000"), ("rotate", "0, -37, 0")],
            ConnectionWorkspace.Lines(session, plan.Ends[1].Object).Skip(2).Take(2));
    }

    /// <summary>
    /// A second pair between the same two systems without a position does not go onto the first pair. A new end that
    /// is closer than 8000 m to a jump object of its system goes out from the center by 8000 m until it is free, so no
    /// end gets the spacing warning. An end that the request places near a jump object gets the warning on its
    /// position.
    /// </summary>
    [Fact]
    public void ASecondPairToTheSameSystemGoesPastTheFirstPair()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        string Pos(SectionRef end) => ConnectionWorkspace.Lines(session, end).Single(l => l.Name == "pos").Text;
        static bool Spacing(Diagnostic d) => d.Message.Contains("Keep jump objects", StringComparison.Ordinal);

        var hole = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));
        var gate = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));

        Assert.Equal(["50000, 0, 0", "-60000, 0, 0"], hole.Ends.Select(e => Pos(e.Object)));
        Assert.Equal(["58000, 0, 0", "-68000, 0, 0"], gate.Ends.Select(e => Pos(e.Object)));
        Assert.All([.. hole.Ends, .. gate.Ends],
            e => Assert.DoesNotContain(ConnectionChecks.Check(session, e.Object), Spacing));

        var near = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Hole,
            new ConnectionEnd("Li01") { Position = new Vector3(58000, 0, 1000) }, new ConnectionEnd("Li03")));
        var warning = Assert.Single(ConnectionChecks.Check(session, near.Ends[0].Object), Spacing);
        Assert.Equal((DiagnosticSeverity.Warning, "pos"), (warning.Severity, warning.Key));
        Assert.Equal("Li01_to_Li03_hole_1 is 1000 m from the jump object Li01_to_Li03. Keep jump objects at least 8000 m " +
                     "apart, so that their traffic zones do not overlap.", warning.Message);
        Assert.Empty(SteLint.Problems(warning.Message));
        Assert.Equal("-76000, 0, 0", Pos(near.Ends[1].Object));
    }

    /// <summary>The count of the jump objects of a system includes the objects that the plan adds: two for a hole pair
    /// in one system. A system with 9 jump objects takes a gate, but not a hole pair inside it.</summary>
    [Fact]
    public void TheJumpLimitCountsTheObjectsThatThePlanAdds()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ConnectionWorkspace.Li03Path, ConnectionWorkspace.Li03Text + string.Concat(Enumerable.Range(1, 9).Select(i =>
            $"\n[Object]\nnickname = Li03_jump_{i}\narchetype = jumphole\ngoto = Li01, Li01_to_Li02, gate_tunnel_bretonia\n")));
        var session = ws.Session("act");

        var hole = ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li03"), new ConnectionEnd("Li03"));
        var gate = ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li03"), new ConnectionEnd("Li01"));

        Assert.Equal("Li03 would have 11 jump objects. The game crashes with more than 10 in one system. Delete a jump " +
                     "gate or a jump hole of Li03 first.", Refusal(hole, ConnectionPlans.JumpLimit).Message);
        Assert.True(gate.CanRun);
        Assert.Equal(new JumpObjectCount("Li03", 10, 10), gate.JumpObjects[0]);
    }

    /// <summary>The plan refuses a gate pair inside one system and an archetype or an existing object of another kind.
    /// It also refuses an airlock gate and an unknown system.</summary>
    [Fact]
    public void ThePlanRefusesAWrongKindAndAGatePairInOneSystem()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal("A jump gate pair needs two systems. Use the kind jump_hole for a hole inside one system.",
            Refusal(ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li03"), new ConnectionEnd("LI03")),
                ConnectionPlans.GateSameSystem).Message);
        Assert.Equal("The archetype jumphole is a JUMP_HOLE. The kind jump_gate needs an archetype of the type JUMP_GATE.",
            Refusal(ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"), new ConnectionEnd("Li03"),
                new ConnectionOptions { Archetype = "jumphole" }), ConnectionPlans.KindMismatch).Message);
        Assert.Equal("Li03_gate is a JUMP_GATE. The kind jump_hole needs an object of the type JUMP_HOLE.",
            Refusal(ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li01"),
                new ConnectionEnd("Li03") { Object = "Li03_gate" }), ConnectionPlans.KindMismatch).Message);
        Assert.Equal(ConnectionPlans.KindMismatch, Assert.Single(ConnectionPlans.CreatePair(session, ConnectionKind.Airlock,
            new ConnectionEnd("Li01"), new ConnectionEnd("Li03")).Refusals).Rule);
        Assert.Equal("No [system] has the nickname Li09.", Refusal(ConnectionPlans.CreatePair(session, ConnectionKind.Gate,
            new ConnectionEnd("Li01"), new ConnectionEnd("Li09")), MissionErrors.ItemNotFound).Message);
    }

    /// <summary>A new end takes the plain retail nickname first. When an object or a zone has it, the next free
    /// number follows. A given nickname must be free and at most 47 characters long.</summary>
    [Fact]
    public void TheNicknameIsThePlainRetailFormOrTheNextFreeNumber()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"), new ConnectionEnd("Li03")));

        var second = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));

        Assert.Equal(["Li01_to_Li03_1", "Li03_to_Li01_1"], second.Ends.Select(e => e.Object.Id.Key));
        Assert.Equal("Zone_Li01_to_Li03_1_traffic", second.TrafficZones[0].Id.Key);
        var taken = Refusal(ConnectionPlans.CreatePair(session, ConnectionKind.Gate,
            new ConnectionEnd("Li01") { Nickname = "li03_station" }, new ConnectionEnd("Li03")), MissionErrors.NameTaken);
        Assert.Equal("The nickname li03_station is taken: Li03_station has the same nickname or the same FLHash.", taken.Message);
        Assert.Equal(ConnectionWorkspace.Li03("Object", "Li03_station"), taken.Target);
        Refusal(ConnectionPlans.CreatePair(session, ConnectionKind.Gate,
            new ConnectionEnd("Li01") { Nickname = new string('a', 48) }, new ConnectionEnd("Li03")), MissionErrors.NameInvalid);
        var named = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate,
            new ConnectionEnd("Li01") { Nickname = "Li01_north_gate", Name = "North Gate" }, new ConnectionEnd("Li03")));
        Assert.Equal("Li03, Li03_to_Li01_2, gate_tunnel_bretonia",
            ConnectionWorkspace.Lines(session, named.Ends[0].Object).Single(l => l.Name == "goto").Text);
        Assert.Equal("North Gate", ConnectionWorkspace.TextOf(session, (
            ConnectionWorkspace.Lines(session, named.Ends[0].Object).Single(l => l.Name == "ids_name").Text)));
    }

    /// <summary>
    /// A gate takes the local faction of its system. Its loadout and its pilot come from the gates of W with that
    /// faction, else from the house of the faction in the rule data. A request and a template object take their place.
    /// </summary>
    [Fact]
    public void TheLoadoutFollowsTheGatesOfTheFactionThenTheHouse()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li02"),
            new ConnectionEnd("Li03")));

        Assert.Equal(new ConnectionEndPlan(plan.Ends[0].Object, true, "li_n_grp", "jumpgate_li_test", "pilot_test"), plan.Ends[0]);
        Assert.Equal(new ConnectionEndPlan(plan.Ends[1].Object, true, "br_n_grp", "jumpgate_br_02", "pilot_solar_easiest"),
            plan.Ends[1]);
        var lines = ConnectionWorkspace.Lines(session, plan.Ends[1].Object);
        Assert.Equal(["reputation", "behavior", "difficulty_level", "goto", "loadout", "pilot"],
            lines.Select(l => l.Name).SkipWhile(n => n != "reputation"));

        session.Undo();
        var like = SceneWorkspace.Li02("Object", "Li02_to_Li01");
        plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03"), new ConnectionOptions { Like = like, Faction = "li_p_grp", Pilot = "pilot_solar_easiest" }));
        Assert.All(plan.Ends, e => Assert.Equal(("li_p_grp", "jumpgate_li_test", "pilot_solar_easiest"),
            (e.Reputation, e.Loadout, e.Pilot)));
        Assert.True(session.Save().Saved);
    }

    /// <summary>
    /// An end can name an existing object without a working pair. The plan then writes only its goto line and adds no
    /// object or zone at that end. An object whose partner goes back refuses, and so does the same object at both ends.
    /// </summary>
    [Fact]
    public void AnExistingOneWayEndGetsOnlyItsGotoLine()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var gate = ConnectionWorkspace.Li03("Object", "Li03_gate");

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03") { Object = "li03_GATE" }));

        Assert.Equal([SceneWorkspace.Object("Li01_to_Li03"), SceneWorkspace.Zone("Zone_Li01_to_Li03_traffic")], plan.Adds);
        Assert.Equal([gate], plan.Changes);
        Assert.Equal(new ConnectionEndPlan(gate, false, null, null, null), plan.Ends[1]);
        Assert.Equal(("goto", "Li01, Li01_to_Li03, gate_tunnel_bretonia"), ConnectionWorkspace.Lines(session, gate)[^1]);
        Assert.Equal("Li03, Li03_gate, gate_tunnel_bretonia",
            ConnectionWorkspace.Lines(session, plan.Ends[0].Object).Single(l => l.Name == "goto").Text);
        Assert.Equal(new JumpObjectCount("Li03", 1, 10), plan.JumpObjects[1]);

        Assert.Equal("Li01_to_Li02 already goes to Li02_to_Li01, and Li02_to_Li01 goes back. Pick an object without a pair.",
            Refusal(ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01") { Object = "Li01_to_Li02" },
                new ConnectionEnd("Li03")), ConnectionPlans.EndPaired).Message);
        Assert.Equal("The two ends name the same object. Pick two objects.", Refusal(ConnectionPlans.CreatePair(session,
            ConnectionKind.Hole, new ConnectionEnd("Li03") { Object = "Li03_hole" }, new ConnectionEnd("Li03") { Object = "Li03_hole" }),
            MissionErrors.FieldValue).Message);
    }

    /// <summary>An end in a new system file that the mod did not save yet works, and the first save writes the pair. A
    /// second save after a new pair with an end in the saved file has no error (the case of the truth review).</summary>
    [Fact]
    public void AnEndInANewSystemFileWorksBeforeAndAfterTheSave()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Av01"),
            new ConnectionEnd("Li03")));
        Assert.Equal(NewSystemWorkspace.New("Object", "Av01_to_Li03"), plan.Ends[0].Object);
        Assert.Equal(new ConnectionEndPlan(plan.Ends[0].Object, true, "li_n_grp", "jumpgate_li_test", "pilot_test"), plan.Ends[0]);
        var first = session.Save();
        Assert.True(first.Saved, string.Join("\n", first.Problems));

        Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Av01"), new ConnectionEnd("Li01")));
        var second = session.Save();

        Assert.True(second.Saved, string.Join("\n", second.Problems));
        Assert.DoesNotContain(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error);
        Assert.Contains("goto = Li01, Li01_to_Av01, gate_tunnel_bretonia", ws.ReadModFile(@"files\" + NewSystemWorkspace.NewPath));
        Assert.Contains("goto = Av01, Av01_to_Li01, gate_tunnel_bretonia", ws.ReadModPatch(SceneWorkspace.Li01Path));
    }

    /// <summary>A player lock adds a locked_gate line for each end and a warning for each end. The route tables leave the
    /// pair out. An NPC lock adds the npc_locked_gate lines and gives no warning.</summary>
    [Fact]
    public void ALockedPairGetsTheLockLinesAndThePlayerLockWarns()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03"), new ConnectionOptions { Lock = JumpLock.Both }));

        Assert.Equal([FLHash.CreateID("Li01_to_Li02"), FLHash.CreateID("Li01_to_Li03"), FLHash.CreateID("Li03_to_Li01")],
            ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Equal(FLHash.CreateID("Li03_to_Li01"), ConnectionWorkspace.LockLines(session, "npc_locked_gate")[^1]);
        Assert.Equal([ConnectionWorkspace.Locks], plan.Changes);
        Assert.Equal(new RouteDiff(0, 0, 0, 0), plan.Routes);
        Assert.Equal(["Li01_to_Li03", "Li03_to_Li01"], plan.Warnings.Select(w => w.Target!.Id.Key));
        Assert.Equal("The route tables leave out Li01_to_Li03, because a new game locks it for the player. After a mission " +
                     "unlocks it with Act_LockDock, nav map routes and NPC routes still do not use it. Set its lock to npc or " +
                     "none to keep it on the routes. Use the jump connection tool connection_set_lock of the agent server.",
            plan.Warnings[0].Message);
        Assert.Empty(SteLint.Problems(plan.Warnings[0].Message));

        session.Undo();
        plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03"), new ConnectionOptions { Lock = JumpLock.Npc }));
        Assert.Empty(plan.Warnings);
        Assert.Equal([FLHash.CreateID("Li01_to_Li02")], ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Equal(new RouteDiff(3, 0, 0, 0), plan.Routes);
    }

    /// <summary>
    /// A lock of a gate of the game changes the patch of the gate lock file. The partner gets the same locks, and each
    /// other line of the two gates goes. The warning tells that the story can depend on the lock. A lock of none
    /// removes each line of the pair. An object that is not a jump gate or a jump hole refuses.
    /// </summary>
    [Fact]
    public void ALockOfAGateOfTheGameChangesThePatchOfTheGateLocks()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var gate = SceneWorkspace.Li02("Object", "Li02_to_Li01");

        var plan = Run(session, ConnectionPlans.SetLock(session, gate, JumpLock.Player));

        Assert.Equal([FLHash.CreateID("Li01_to_Li02"), FLHash.CreateID("Li02_to_Li01")],
            ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Empty(session.Get(ConnectionWorkspace.Locks)!.Lines("npc_locked_gate"));
        Assert.Equal(new RouteDiff(0, 0, 0, 1), plan.Routes);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.EndsWith("The story can depend on this lock.", plan.Warnings[0].Message);
        Assert.True(session.Save().Saved);
        Assert.Contains($"locked_gate = {FLHash.CreateID("Li02_to_Li01")}", ws.ReadModPatch(ListWorkspace.GroupsPath));

        plan = Run(session, ConnectionPlans.SetLock(session, gate, JumpLock.None));
        Assert.Empty(session.Get(ConnectionWorkspace.Locks)!.Lines("locked_gate"));
        Assert.Equal(new RouteDiff(2, 0, 0, 0), plan.Routes);
        Assert.Empty(plan.Warnings);

        plan = Run(session, ConnectionPlans.SetLock(session, gate, JumpLock.Npc, pair: false));
        Assert.Equal([FLHash.CreateID("Li02_to_Li01")], ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
        Assert.Equal("Li01_01 is not a jump gate or a jump hole.",
            Refusal(ConnectionPlans.SetLock(session, SceneWorkspace.Object("Li01_01"), JumpLock.Player),
                ConnectionPlans.NotConnection).Message);
    }

    /// <summary>
    /// A lock line can already name the nickname of a new end, as six lines of vanilla 1.1 do. The new end then gets
    /// the lock of the request: a pair without a lock removes the line, and an NPC lock keeps it once. An existing end
    /// keeps its locks when the request has no lock, and it gets the lock of the request when the request has one.
    /// </summary>
    [Fact]
    public void ALockLineOfANewNicknameFollowsTheRequest()
    {
        using var ws = ConnectionWorkspace.Create();
        var newEnd = FLHash.CreateID("Li01_to_Li03_hole");
        var existing = FLHash.CreateID("Li03_hole");
        ws.WriteBase(ListWorkspace.GroupsPath,
            "[Group]\nnickname = li_n_grp\n[Group]\nnickname = li_p_grp\n[Group]\nnickname = br_n_grp\n" +
            $"[locked_gates]\nnpc_locked_gate = {newEnd}\nlocked_gate = {existing}\n");
        var session = ws.Session("act");
        ConnectionPlan Create(JumpLock locked) => Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Hole,
            new ConnectionEnd("Li01"), new ConnectionEnd("Li03") { Object = "Li03_hole" }, new ConnectionOptions { Lock = locked }));

        var plan = Create(JumpLock.None);

        Assert.Equal(SceneWorkspace.Object("Li01_to_Li03_hole"), plan.Ends[0].Object);
        Assert.Empty(ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
        Assert.Equal([existing], ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Contains(ConnectionWorkspace.Locks, plan.Changes);
        Assert.Empty(plan.Warnings);
        session.Undo();
        Assert.Equal([newEnd], ConnectionWorkspace.LockLines(session, "npc_locked_gate"));

        plan = Create(JumpLock.Npc);
        Assert.Equal([newEnd, existing], ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
        Assert.Empty(ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Empty(plan.Warnings);
    }

    /// <summary>A new end gets the msg_id_prefix of the system that it goes to, directly after its archetype. The end
    /// that goes to a system without a prefix gets no such line.</summary>
    [Fact]
    public void ANewEndGetsTheMessagePrefixOfTheSystemThatItGoesTo()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(NewSystemWorkspace.System("Li03"), "msg_id_prefix", ["msg_test_li03"]));

        var plan = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));

        var lines = ConnectionWorkspace.Lines(session, plan.Ends[0].Object);
        Assert.Equal(("msg_id_prefix", "msg_test_li03"), lines[lines.FindIndex(l => l.Name == "archetype") + 1]);
        Assert.DoesNotContain(ConnectionWorkspace.Lines(session, plan.Ends[1].Object), l => l.Name == "msg_id_prefix");
    }

    /// <summary>The lock of a one-way gate does not lock the object that its goto line names, because that object
    /// does not go back. That object keeps its own pair and its locks.</summary>
    [Fact]
    public void TheLockOfAOneWayGateLeavesTheObjectThatItGoesTo()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var gate = ConnectionWorkspace.Li03("Object", "Li03_gate");
        session.Execute(EditCommand.SetLinesFromText(gate, "goto", ["Li02, Li02_to_Li01, gate_tunnel_bretonia"]));

        var plan = Run(session, ConnectionPlans.SetLock(session, gate, JumpLock.Player));

        Assert.Equal([FLHash.CreateID("Li01_to_Li02"), FLHash.CreateID("Li03_gate")],
            ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Equal([FLHash.CreateID("Li02_to_Li01"), FLHash.CreateID("Li01_to_Li02")],
            ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
        Assert.Equal(gate, Assert.Single(plan.Warnings).Target);
    }

    /// <summary>A profile without a <c>[locked_gates]</c> section gets a new one in the gate lock file.</summary>
    [Fact]
    public void ALockWithoutALockSectionAddsTheSection()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ListWorkspace.GroupsPath, "[Group]\nnickname = li_n_grp\n[Group]\nnickname = li_p_grp\n[Group]\nnickname = br_n_grp\n");
        var session = ws.Session("act");

        var plan = Run(session, ConnectionPlans.SetLock(session, SceneWorkspace.Li02("Object", "Li02_to_Li01"), JumpLock.Npc));

        Assert.Empty(plan.Changes);
        Assert.Equal([FLHash.CreateID("Li02_to_Li01"), FLHash.CreateID("Li01_to_Li02")],
            ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
        Assert.True(session.Save().Saved);
    }

    /// <summary>
    /// A delete of one end of a gate pair deletes the pair: the partner in the other file, the traffic zone of each
    /// end and the lock lines of each end. The partner is in another file, so the plan asks. The lock lines go as
    /// fixes. The texts of the ends stay as unused texts of the mod. One undo gives both ends back.
    /// </summary>
    [Fact]
    public void ADeleteOfOneEndDeletesThePairWithItsTrafficZonesAndItsLockLines()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var made = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03"), new ConnectionOptions { Lock = JumpLock.Both }));
        var li01 = SceneWorkspace.Object("Li01_to_Li03");
        var li03 = ConnectionWorkspace.Li03("Object", "Li03_to_Li01");
        var name = session.Get(li03)!.Lines("ids_name")[0].Values[0].ToString()!;

        var plan = ConnectionPlans.DeletePair(session, [li03]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.Equal([(li03, DeleteReason.Target, null), (li01, DeleteReason.Cascade, "goto = Li03, Li03_to_Li01"),
                (made.TrafficZones[1], DeleteReason.Cascade, "traffic zone of Li03_to_Li01"),
                (made.TrafficZones[0], DeleteReason.Cascade, "traffic zone of Li01_to_Li03")],
            plan.Deletes.Select(d => (d.Section, d.Why, d.Cause)));
        Assert.Equal(["initialworld.ini: the locked_gate line of Li01_to_Li03",
                "initialworld.ini: the locked_gate line of Li03_to_Li01",
                "initialworld.ini: the npc_locked_gate line of Li01_to_Li03",
                "initialworld.ini: the npc_locked_gate line of Li03_to_Li01"],
            plan.Fixes.Select(f => f.Text).Order());
        Assert.True(plan.NeedsConfirmation);
        Assert.Equal("Delete Li03_to_Li01 and 3 more", plan.Label);

        session.Execute(plan.Command!);
        Assert.All([li01, li03, made.TrafficZones[0], made.TrafficZones[1]], s => Assert.Null(session.Get(s)));
        Assert.Equal([FLHash.CreateID("Li01_to_Li02")], ConnectionWorkspace.LockLines(session, "locked_gate"));
        Assert.Equal([FLHash.CreateID("Li02_to_Li01"), FLHash.CreateID("Li01_to_Li02")],
            ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
        Assert.Equal("Li01 Jump Gate", ConnectionWorkspace.TextOf(session, name));
        Assert.Equal(new RouteDiff(0, 0, 0, 0), RouteTable.Diff(session.GameRoutes.Tables.All, session.Routes.Tables.All));

        session.Undo();
        Assert.All([li01, li03, made.TrafficZones[0], made.TrafficZones[1]], s => Assert.NotNull(session.Get(s)));
        Assert.Equal([FLHash.CreateID("Li01_to_Li02"), FLHash.CreateID("Li01_to_Li03"), FLHash.CreateID("Li03_to_Li01")],
            ConnectionWorkspace.LockLines(session, "locked_gate"));
    }

    /// <summary>A hole pair in one system is one item of one file. Its partner, its traffic zones and its lock lines do
    /// not make the plan ask, as a delete of one object does not.</summary>
    [Fact]
    public void ADeleteOfAHolePairInOneSystemDoesNotAsk()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var made = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li03"),
            new ConnectionEnd("Li03"), new ConnectionOptions { Lock = JumpLock.Npc }));

        var plan = ConnectionPlans.DeletePair(session, [made.Ends[1].Object]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.Equal(["Li03_to_Li03_hole_1", "Li03_to_Li03_hole", "Zone_Li03_to_Li03_hole_1_traffic",
            "Zone_Li03_to_Li03_hole_traffic"], plan.Deletes.Select(d => d.Section.Id.Key));
        Assert.Equal(2, plan.Fixes.Count);
        Assert.False(plan.NeedsConfirmation);
        session.Execute(plan.Command!);
        Assert.Null(session.Get(made.Ends[0].Object));
        Assert.Equal([FLHash.CreateID("Li02_to_Li01"), FLHash.CreateID("Li01_to_Li02")],
            ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
    }

    /// <summary>A one-way object goes alone and does not make the plan ask. The object that it goes to stays, because
    /// that object does not go back to it.</summary>
    [Fact]
    public void ADeleteOfAOneWayObjectDeletesOnlyThatObject()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var gate = ConnectionWorkspace.Li03("Object", "Li03_gate");
        session.Execute(EditCommand.SetLinesFromText(gate, "goto", ["Li02, Li02_to_Li01, gate_tunnel_bretonia"]));

        var plan = ConnectionPlans.DeletePair(session, [gate]);

        Assert.Equal([(gate, DeleteReason.Target)], plan.Deletes.Select(d => (d.Section, d.Why)));
        Assert.Empty(plan.Fixes);
        Assert.False(plan.NeedsConfirmation);
        session.Execute(plan.Command!);
        Assert.Null(session.Get(gate));
        Assert.NotNull(session.Get(SceneWorkspace.Li02("Object", "Li02_to_Li01")));
    }

    /// <summary>A partner in a file that the editor cannot change refuses the delete with the reason, because a pair
    /// goes as one item. An object that is no jump gate or jump hole gives the detail NotConnection.</summary>
    [Fact]
    public void APartnerInAReadOnlyFileAndAnObjectThatIsNoJumpAreRefused()
    {
        using var ws = ConnectionWorkspace.Create();
        // The active mod replaces the whole file of Li02 outside its folder, so the editor cannot change the file.
        ws.Mod("act", new() { [@"files\" + ListWorkspace.Li02Path] = SceneWorkspace.Li02Text });
        var session = ws.Session("act");
        var partner = SceneWorkspace.Li02("Object", "Li02_to_Li01");
        Assert.NotNull(session.Get(partner)!.ReadOnlyReason);

        var plan = ConnectionPlans.DeletePair(session, [SceneWorkspace.Object("Li01_to_Li02")]);
        var station = ConnectionPlans.DeletePair(session, [SceneWorkspace.Object("Li01_01")]);

        Assert.False(plan.CanRun);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal(partner, refusal.Target);
        Assert.Equal("Li02_to_Li01 in li02.ini is the partner of Li01_to_Li02, and a jump connection goes as one item. " +
                     $"The editor cannot delete Li02_to_Li01: {session.Get(partner)!.ReadOnlyReason}", refusal.Message);
        Assert.Equal(ConnectionPlans.NotConnection, Assert.Single(station.Refusals).Rule);
        Assert.Equal("Li01_01 is not a jump gate or a jump hole.", station.Refusals[0].Message);
    }

    /// <summary>
    /// A one-way object that goes to one end of a pair is no part of the pair. The delete of that end stops at the goto
    /// line of the one-way object. The Inspector and set_fields lock that line, so the text names the actions that can
    /// change it. A delete of the end and the one-way object together runs, and the one-way object goes alone.
    /// </summary>
    [Fact]
    public void AnEndThatAOneWayObjectGoesToRefusesTheDeleteAndNamesTheWayOut()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var oneWay = ConnectionWorkspace.Li03("Object", "Li03_gate");
        var end = SceneWorkspace.Object("Li01_to_Li02");
        session.Execute(EditCommand.SetLinesFromText(oneWay, "goto", ["Li01, Li01_to_Li02, gate_tunnel_bretonia"]));

        var plan = ConnectionPlans.DeletePair(session, [end]);

        Assert.False(plan.CanRun);
        var refusal = Assert.Single(plan.Refusals);
        Assert.Equal(oneWay, refusal.Target);
        Assert.Equal("goto", refusal.Key, ignoreCase: true);
        Assert.Equal("The goto line of Li03_gate in li03.ini names Li01_to_Li02, but Li01_to_Li02 does not go back to " +
                     "Li03_gate. Delete Li03_gate first or in the same delete, or keep Li01_to_Li02. The Delete of the " +
                     "editor and the agent tool delete_items delete a one-way object alone. To keep Li03_gate, change its " +
                     "goto line in the raw INI block. You can also pair Li03_gate with the jump connection tool " +
                     "connection_create of the agent server.", refusal.Message);
        Assert.Equal(refusal.Message, Assert.Single(ScenePlans.Delete(session, [end]).Refusals).Message);

        var alone = ConnectionPlans.DeletePair(session, [oneWay]);
        var both = ConnectionPlans.DeletePair(session, [end, oneWay]);

        Assert.Equal([oneWay], alone.Deletes.Select(d => d.Section));
        Assert.True(both.CanRun, string.Join("\n", both.Refusals.Select(r => r.Message)));
        Assert.Equal([(end, DeleteReason.Target), (oneWay, DeleteReason.Target),
            (SceneWorkspace.Li02("Object", "Li02_to_Li01"), DeleteReason.Cascade)], both.Deletes.Select(d => (d.Section, d.Why)));
        session.Execute(both.Command!);
        Assert.Null(session.Get(oneWay));
        Assert.Null(session.Get(end));
    }

    /// <summary>A one-way object in a file that the editor cannot change leaves only the keep of the end. The text tells
    /// why.</summary>
    [Fact]
    public void AOneWayObjectInAReadOnlyFileTellsWhyTheEndMustStay()
    {
        using var ws = ConnectionWorkspace.Create();
        // The active mod replaces the whole file of Li03 outside its folder, so the editor cannot change the file.
        ws.Mod("act", new()
        {
            [@"files\" + ConnectionWorkspace.Li03Path] = ConnectionWorkspace.Li03Text.Replace("archetype = jumpgate",
                "archetype = jumpgate\ngoto = Li01, Li01_to_Li02, gate_tunnel_bretonia", StringComparison.Ordinal),
        });
        var session = ws.Session("act");
        var oneWay = ConnectionWorkspace.Li03("Object", "Li03_gate");
        var reason = session.Get(oneWay)!.ReadOnlyReason;
        Assert.NotNull(reason);

        var plan = ConnectionPlans.DeletePair(session, [SceneWorkspace.Object("Li01_to_Li02")]);

        Assert.Equal("The goto line of Li03_gate in li03.ini names Li01_to_Li02, but Li01_to_Li02 does not go back to " +
                     $"Li03_gate. Keep Li01_to_Li02, because the editor cannot change Li03_gate: {reason}",
            Assert.Single(plan.Refusals).Message);
    }

    /// <summary>
    /// A traffic zone nickname that another file already has makes the zone of a new end take the next free number. The
    /// delete of the pair takes that zone from the file of the end. The zone of the other file stays.
    /// </summary>
    [Fact]
    public void ADeleteTakesTheNumberedTrafficZoneOfTheFileOfTheEnd()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var other = ConnectionWorkspace.Li03(SceneSections.ZoneType, "Zone_Li01_to_Li03_traffic");
        session.Execute(EditCommand.AddSection(ConnectionWorkspace.Li03Path, SceneSections.ZoneType,
        [
            ListWorkspace.Line("nickname", "Zone_Li01_to_Li03_traffic"), ListWorkspace.Line("pos", "0, 0, 0"),
            ListWorkspace.Line("shape", "SPHERE"), ListWorkspace.Line("size", "4000"),
        ]));
        var made = Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li03")));
        Assert.Equal(["Zone_Li01_to_Li03_traffic_1", "Zone_Li03_to_Li01_traffic"], made.TrafficZones.Select(z => z.Id.Key));

        var plan = ConnectionPlans.DeletePair(session, [SceneWorkspace.Object("Li01_to_Li03")]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.Contains((made.TrafficZones[0], "traffic zone of Li01_to_Li03"), plan.Deletes.Select(d => (d.Section, d.Cause)));
        Assert.DoesNotContain(other, plan.Deletes.Select(d => d.Section));
        session.Execute(plan.Command!);
        Assert.All(made.TrafficZones, z => Assert.Null(session.Get(z)));
        Assert.NotNull(session.Get(other));
    }

    /// <summary>
    /// The delete takes only the traffic zones that OpenLancer makes for an end. A jump traffic zone with another name,
    /// as the retail zones have, stays. When its size holds a deleted end, the plan warns and asks. A zone with another
    /// pop_type or a zone that does not hold the end gives no warning.
    /// </summary>
    [Fact]
    public void ADeleteWarnsAboutAJumpTrafficZoneWithAnotherNameAtAnEnd()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li01"),
            new ConnectionEnd("Li01")));
        void Zone(string name, float x, string pop) => session.Execute(EditCommand.AddSection(ListWorkspace.Li01Path,
            SceneSections.ZoneType,
        [
            ListWorkspace.Line("nickname", name), ListWorkspace.Line("pos", $"{x}", "0", "0"), ListWorkspace.Line("shape", "SPHERE"),
            ListWorkspace.Line("size", "4000"), ListWorkspace.Line("pop_type", pop),
        ]));
        Zone("Zone_ol_hole_traffic", 50900, "wormhole");
        Zone("Zone_ol_far_traffic", 40000, "wormhole");
        Zone("Zone_ol_patrol", 50000, "lane_patrol");
        var zone = SceneWorkspace.Zone("Zone_ol_hole_traffic");

        var plan = ConnectionPlans.DeletePair(session, [SceneWorkspace.Object("Li01_to_Li01_hole")]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.DoesNotContain(zone, plan.Deletes.Select(d => d.Section));
        var warning = Assert.Single(plan.Warnings);
        Assert.Equal(zone, warning.Target);
        Assert.Equal("The traffic zone Zone_ol_hole_traffic stays. It is 900 m from Li01_to_Li01_hole. When the zone has " +
                     "encounters, NPC ships still come and go at that place. Delete the zone too when no other jump " +
                     "object uses it.", warning.Message);
        Assert.Empty(SteLint.Problems(warning.Message));
        Assert.True(plan.NeedsConfirmation);
    }
}
