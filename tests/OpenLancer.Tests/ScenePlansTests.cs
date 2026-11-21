using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Strings;
using Xunit;
using static OpenLancer.Tests.SceneWorkspace;
using Shape = LibreLancer.Data.Schema.Universe.ZoneShape;

namespace OpenLancer.Tests;

/// <summary>The delete plans of the Scene (section 11 of the M3 design, decision D81).</summary>
public sealed class ScenePlansDeleteTests
{
    [Fact]
    public void AnObjectTakesItsChildrenAgainAndAgainAndOneUndoRestoresAll()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var before = session.OpenDocument(Li01Path)!.State;

        var plan = ScenePlans.Delete(session, [Object("Li01_01")]);

        Assert.Empty(plan.Refusals);
        Assert.Equal([("Li01_01", DeleteReason.Target, null), ("Li01_child", DeleteReason.Cascade, "parent = Li01_01"),
            ("Li01_grandchild", DeleteReason.Cascade, "parent = Li01_child")],
            plan.Deletes.Select(d => (d.Section.Id.Key!, d.Why, d.Cause)));
        Assert.Equal("Delete Li01_01 and 2 more", plan.Label);
        Assert.True(plan.NeedsConfirmation);
        // The mission scripts are not in the text scan, because the mission index has their references (decision
        // D668 of the M6b design).
        Assert.Equal([("Li01_01", 1), ("Li01_child", 1)],
            plan.Mentions.Select(m => (m.Nickname, m.Count)).OrderBy(m => m.Item1).ThenBy(m => m.Item2));
        session.Execute(plan.Command!);

        Assert.Null(session.Get(Object("Li01_01")));
        Assert.Null(session.Get(Object("Li01_grandchild")));
        Assert.Equal(plan.Label, session.UndoLabel);
        session.Undo();
        Assert.Same(before, session.OpenDocument(Li01Path)!.State);
    }

    /// <summary>A delete that removes only the item runs at once, as in Unity (decision D238).</summary>
    [Fact]
    public void ADeleteWithoutConsequencesNeedsNoConfirmation()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Li01("LightSource", "light1")]);

        Assert.False(plan.NeedsConfirmation);
        Assert.Equal("Delete light1", plan.Label);
    }

    [Fact]
    public void AZoneDeleteClearsTheRingLineAndTheHoleInTheSameEntry()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Zone("Zone_ring")]);

        Assert.Equal(["li01.ini: the 'ring' line of Li01_planet", "cloud.ini: the hole of Zone_ring (2 lines)"],
            plan.Fixes.Select(f => f.Text));
        session.Execute(plan.Command!);
        Assert.Empty(session.Get(Object("Li01_planet"))!.Lines("ring"));
        Assert.Equal(["exclusion = Zone_hole", "fog_far = 100", "zone_shell = solar\\x.cmp", "exclusion = Zone_hole",
            "max_alpha = 0.5"], Lines(session, CloudHoles));
        session.Undo();
        Assert.Equal(7, session.Get(CloudHoles)!.Entries.Count);
    }

    /// <summary>A zone takes its loot zone, and loses each of its holes, also two holes of one section (review finding
    /// R5). A save writes the lines.</summary>
    [Fact]
    public void AZoneTakesItsLootZoneAndEachOfItsHoles()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Zone("Zone_hole")]);

        Assert.Equal([Zone("Zone_hole"), Loot("Zone_hole")], plan.Deletes.Select(d => d.Section));
        Assert.Equal(3, plan.Fixes.Count);
        session.Execute(plan.Command!);
        Assert.Equal(["exclusion = Zone_ring", "fog_far = 200"], Lines(session, CloudHoles));
        Assert.Empty(session.Get(RockHoles)!.Entries);
        Assert.Null(session.Get(Loot("Zone_hole")));
        Assert.True(session.Save().Saved);
        Assert.Contains("ol.delete = true", ws.ReadModPatch(RocksPath));
        Assert.Contains("[Exclusion Zones]", ws.ReadModPatch(CloudPath));
        Assert.Contains("nickname = Zone_hole\r\nol.delete = true", ws.ReadModPatch(Li01Path));
    }

    /// <summary>A hole of a file with a second owner system that has the zone still serves that system (section 11.4).</summary>
    [Fact]
    public void AHoleStaysWhenASecondOwnerHasTheZone()
    {
        using var ws = Create();
        ws.WriteBase(Li02Path, Li02Text + "\n[zone]\nnickname = Zone_hole\n[Nebula]\nfile = solar\\nebula\\cloud.ini\nzone = Zone_li02\n");
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Zone("Zone_hole")]);

        Assert.Equal(["rocks.ini: the hole of Zone_hole (1 line)"], plan.Fixes.Select(f => f.Text));
    }

    [Fact]
    public void AZoneTakesItsNebulaAndItsFieldAndANebulaKeepsItsZone()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var zone = ScenePlans.Delete(session, [Zone("Zone_cloud")]);
        var nebula = ScenePlans.Delete(session, [Li01("Nebula", "Zone_cloud")]);

        Assert.Equal([Zone("Zone_cloud"), Li01("Nebula", "Zone_cloud"), Li01("Asteroids", "Zone_cloud")],
            zone.Deletes.Select(d => d.Section));
        // Decision D237 as the review changed it: a delete of a nebula works as Unity "Remove Component".
        Assert.Equal([Li01("Nebula", "Zone_cloud")], nebula.Deletes.Select(d => d.Section));
        Assert.False(nebula.NeedsConfirmation);
        Assert.Equal("Delete the nebula of Zone_cloud", nebula.Label);
    }

    /// <summary>
    /// A delete of a jump gate deletes its pair: the partner in the other system file and the gate lock lines of both
    /// ends. The partner is in another file, so the editor asks. A delete of a ring of a lane of two rings takes the
    /// whole lane, and the editor asks. An airlock gate still refuses the delete, and Unavailable gives the same reason
    /// as the plan.
    /// </summary>
    [Fact]
    public void AGateGoesWithItsPairARingTakesItsLaneAndAnAirlockGateIsRefused()
    {
        using var ws = Create();
        ws.WriteBase(ListWorkspace.SolarPath, Solars + "\n[Solar]\nnickname = airlock\ntype = AIRLOCK_GATE\n");
        ws.WriteBase(Li02Path, Li02Text + "\n[Object]\nnickname = Li02_airlock\narchetype = airlock\n");
        var session = ws.Session("act");

        var gate = ScenePlans.Delete(session, [Object("Li01_to_Li02")]);
        var ring = ScenePlans.Delete(session, [Object("ring2")]);
        var airlock = ScenePlans.Delete(session, [Li02("Object", "Li02_airlock")]);

        Assert.True(gate.CanRun, string.Join("\n", gate.Refusals.Select(r => r.Message)));
        Assert.Equal([("Li01_to_Li02", DeleteReason.Target, null),
            ("Li02_to_Li01", DeleteReason.Cascade, "goto = Li01, Li01_to_Li02")],
            gate.Deletes.Select(d => (d.Section.Id.Key!, d.Why, d.Cause)));
        Assert.Equal(["initialworld.ini: the locked_gate line of Li01_to_Li02",
            "initialworld.ini: the npc_locked_gate line of Li01_to_Li02",
            "initialworld.ini: the npc_locked_gate line of Li02_to_Li01"], gate.Fixes.Select(f => f.Text).Order());
        Assert.True(gate.NeedsConfirmation);
        Assert.Null(ScenePlans.Unavailable(session, SceneAction.Delete, Object("Li01_to_Li02")));
        Assert.True(ring.CanRun, string.Join("\n", ring.Refusals.Select(r => r.Message)));
        Assert.Equal([("ring2", DeleteReason.Target, null), ("ring1", DeleteReason.Cascade, "the trade lane of ring2")],
            ring.Deletes.Select(d => (d.Section.Id.Key!, d.Why, d.Cause)));
        Assert.True(ring.NeedsConfirmation);
        Assert.Null(ScenePlans.Unavailable(session, SceneAction.Delete, Object("ring2")));
        Assert.Equal("Li02_airlock is an airlock gate. This version cannot delete airlock gates.",
            Assert.Single(airlock.Refusals).Message);
        Assert.Equal(Assert.Single(airlock.Refusals).Message,
            ScenePlans.Unavailable(session, SceneAction.Delete, Li02("Object", "Li02_airlock")));

        session.Execute(gate.Command!);
        Assert.Null(session.Get(Object("Li01_to_Li02")));
        Assert.Null(session.Get(Li02("Object", "Li02_to_Li01")));
        Assert.Empty(session.Get(new SectionRef(ListWorkspace.GroupsPath, new SectionIdentity("locked_gates", null)))!.Entries);
        session.Undo();
        Assert.NotNull(session.Get(Object("Li01_to_Li02")));
        Assert.NotNull(session.Get(Li02("Object", "Li02_to_Li01")));
    }

    /// <summary>A ring that goes with its parent takes its lane of two rings, as a delete of the ring does.</summary>
    [Fact]
    public void AConnectionChildTakesItsLane()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Object("ring1"), "parent", ["Li01_dock"]));

        var plan = ScenePlans.Delete(session, [Object("Li01_dock")]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.Equal([("Li01_dock", DeleteReason.Target, null), ("ring1", DeleteReason.Cascade, "parent = Li01_dock"),
            ("ring2", DeleteReason.Cascade, "the trade lane of ring1")], plan.Deletes.Select(d => (d.Section.Id.Key!, d.Why, d.Cause)));
        Assert.True(plan.NeedsConfirmation);
    }

    /// <summary>A light of a file whose nickname repeats is read-only, so its delete is refused (review finding R4).</summary>
    [Fact]
    public void AReadOnlyTargetIsRefusedWithTheReason()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Li01("LightSource", "twin")]);

        Assert.Contains("another [LightSource] section also has nickname = twin", Assert.Single(plan.Refusals).Message);
    }

    /// <summary>A referrer whose rule has the effect load-break refuses the delete (decision D124 (a)), and a rule with
    /// <c>onDelete: missing</c> leaves "Missing" (review finding R16).</summary>
    [Fact]
    public void TheRuleDataRefusesOrLeavesMissing()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var json = ResourceText().Replace(
            "\"key\": \"parent\", \"list\": \"object_of_system\", \"scope\": \"file\",\n      \"onDelete\": \"cascade\"",
            "\"key\": \"parent\", \"list\": \"object_of_system\", \"scope\": \"file\",\n      \"closed\": true, \"effect\": \"loadBreak\"");
        Assert.Contains("\"effect\": \"loadBreak\"", json);
        session.UseRules(ListRules.Parse(json));

        var refused = ScenePlans.Delete(session, [Object("Li01_01")]);
        Assert.Contains("Li01_child", Assert.Single(refused.Refusals).Message);

        session.UseRules(ListRules.Parse(json.Replace("\"closed\": true, \"effect\": \"loadBreak\"",
            "\"closed\": true, \"effect\": \"degrade\", \"onDelete\": \"missing\"")));
        var missing = ScenePlans.Delete(session, [Object("Li01_child")]);
        var line = Assert.Single(missing.Missing);
        Assert.Equal((Object("Li01_grandchild"), "parent", true), (line.Reference.Section, line.Reference.Key, line.BlocksSave));
        session.Execute(missing.Command!);
        Assert.Contains(session.SaveProblems(), p => p.Target == Object("Li01_grandchild") && p.Key == "parent");
    }

    /// <summary>A fix of a line that the editor cannot change leaves "Missing" with the reason.</summary>
    [Fact]
    public void AFixOfAReadOnlySectionLeavesMissing()
    {
        using var ws = Create();
        ws.WriteBase(Li01Path, Li01Text + "\n[Object]\nnickname = Li01_planet\narchetype = planet1\nring = Zone_ring\n");
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Zone("Zone_ring")]);

        Assert.Equal(2, plan.Missing.Count);
        Assert.All(plan.Missing, m => Assert.StartsWith("The editor cannot change this line:", m.Consequence));
        Assert.Single(plan.Fixes);
    }

    [Fact]
    public void TheBaseWarningsNameTheLastObjects()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var one = ScenePlans.Delete(session, [Object("Li01_01")]);
        var both = ScenePlans.Delete(session, [Object("Li01_01"), Object("Li01_dock")]);

        Assert.Equal(["No object in Li01 has 'base = Li01_01_Base'. The navigation map does not list the base, and its rooms " +
                      "show no planet."], one.Warnings.Select(w => w.Message));
        Assert.Contains(both.Warnings, w => w.Message == "No object in Li01 docks with Li01_01_Base. The game puts the player " +
                                                          "at the center of Li01 after a launch.");
    }

    /// <summary>A later mod that patches a section of the delete cannot find it after the delete (review finding R22).</summary>
    [Fact]
    public void ALaterModThatChangesTheSectionGivesAWarning()
    {
        using var ws = Create();
        ws.Mod("later", new() { [@"patches\" + Li01Path] = "[Object]\nnickname = Li01_dock\npos = 1, 1, 1\n" });
        ws.WriteProfile("lower", "act", "later");
        var session = ws.Session("act");

        var plan = ScenePlans.Delete(session, [Object("Li01_dock")]);

        Assert.Contains(plan.Warnings, w => w.Message == "The mod later loads later and changes [Object] nickname = Li01_dock. " +
                                                       "Its patch cannot find the section after the delete.");
    }

    /// <summary>The command of a plan runs only on the state that the plan read.</summary>
    [Fact]
    public void APlanDoesNotRunAfterAChangeOfW()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var plan = ScenePlans.Delete(session, [Li01("LightSource", "light1")]);

        session.Execute(EditCommand.SetLinesFromText(Object("Li01_dock"), "pos", ["1, 2, 3"]));

        Assert.Throws<InvalidOperationException>(() => session.Execute(plan.Command!));
        Assert.NotNull(session.Get(Li01("LightSource", "light1")));
    }

    internal static readonly SectionRef CloudHoles = new(CloudPath, new SectionIdentity("Exclusion Zones", null));

    internal static readonly SectionRef RockHoles = new(RocksPath, new SectionIdentity("Exclusion Zones", null));

    internal static SectionRef Loot(string zone) => new(RocksPath, new SectionIdentity("LootableZone", zone));

    internal static string[] Lines(EditSession session, SectionRef section) =>
        session.Get(section)!.Entries.Select(e => $"{e.Name} = {string.Join(", ", e.Values.Select(v => v.ToString()))}").ToArray();

    /// <summary>The text of the retail rule data, with LF line ends.</summary>
    internal static string ResourceText()
    {
        using var stream = typeof(ListRules).Assembly.GetManifestResourceStream("OpenLancer.Data.Editing.Lists.list-rules.json")!;
        return new StreamReader(stream).ReadToEnd().ReplaceLineEndings("\n");
    }
}

/// <summary>The rename plans of the Scene (section 12 of the M3 design).</summary>
public sealed class ScenePlansRenameTests
{
    [Fact]
    public void AnObjectRenameRelinksItsChildrenAndItsLaneAndKeepsItsPlace()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(ScenePlans.Rename(session, Object("Li01_01"), "Li01_new").Command!);
        session.Execute(ScenePlans.Rename(session, Object("ring1"), "ring9").Command!);

        Assert.Equal("Li01_new", SceneSections.Last(session.Get(Object("Li01_child"))!, "parent"));
        Assert.Equal("ring9", SceneSections.Last(session.Get(Object("ring2"))!, "prev_ring"));
        Assert.Equal(Object("Li01_new"), session.Sections(Li01Path)[0]);
        Assert.True(session.Save().Saved);
        Assert.Contains("nickname = Li01_01\r\nol.rename = Li01_new", ws.ReadModPatch(Li01Path));
    }

    /// <summary>A gate rename changes the goto line of the partner gate (decision D12) and the gate locks by their hash
    /// (decision D257). The other locks keep their values.</summary>
    [Fact]
    public void AGateRenameMovesThePartnerGotoAndTheLocks()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var locks = new SectionRef(ListWorkspace.GroupsPath, new SectionIdentity("locked_gates", null));

        var plan = ScenePlans.Rename(session, Object("Li01_to_Li02"), "Li01_gate");
        session.Execute(plan.Command!);

        Assert.Equal(["Li01", "Li01_gate", "gate_tunnel_bretonia"],
            session.Get(Li02("Object", "Li02_to_Li01"))!.Lines("goto")[0].Values.Select(v => v.ToString()));
        var gates = session.Get(locks)!;
        Assert.Equal(FLHash.CreateID("Li01_gate"), (uint)gates.Lines("locked_gate")[0].Values[0].ToInt32());
        Assert.Equal(FLHash.CreateID("Li02_to_Li01"), (uint)gates.Lines("npc_locked_gate")[0].Values[0].ToInt32());
        Assert.Equal(FLHash.CreateID("Li01_gate"), (uint)gates.Lines("npc_locked_gate")[1].Values[0].ToInt32());
        Assert.True(session.Save().Saved);
        Assert.Contains($"locked_gate = {FLHash.CreateID("Li01_gate")}", ws.ReadModPatch(ListWorkspace.GroupsPath));
    }

    /// <summary>A hole pair of one system names its partner in the same file.</summary>
    [Fact]
    public void AHolePairInOneSystemRelinksInTheSameFile()
    {
        using var ws = Create();
        ws.WriteBase(Li01Path, Li01Text + "\n[Object]\nnickname = Li01_hole_a\narchetype = jumpgate\ngoto = Li01, Li01_hole_b, t\n" +
                                          "[Object]\nnickname = Li01_hole_b\narchetype = jumpgate\ngoto = Li01, Li01_hole_a, t\n");
        var session = ws.Session("act");

        session.Execute(ScenePlans.Rename(session, Object("Li01_hole_a"), "Li01_hole_c").Command!);

        Assert.Equal("Li01_hole_c", session.Get(Object("Li01_hole_b"))!.Lines("goto")[0].Values[1].ToString());
    }

    [Fact]
    public void AZoneRenameRenamesItsVolumesItsLootAndItsHoles()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(ScenePlans.Rename(session, Li01("Nebula", "Zone_cloud"), "Zone_fog").Command!);
        session.Execute(ScenePlans.Rename(session, Zone("Zone_hole"), "Zone_gap").Command!);

        Assert.NotNull(session.Get(Li01("Nebula", "Zone_fog")));
        Assert.NotNull(session.Get(Li01("Asteroids", "Zone_fog")));
        Assert.NotNull(session.Get(Zone("Zone_fog")));
        Assert.NotNull(session.Get(ScenePlansDeleteTests.Loot("Zone_gap")));
        Assert.Equal(["Zone_gap", "Zone_ring", "Zone_gap"],
            session.Get(ScenePlansDeleteTests.CloudHoles)!.Lines("exclusion").Select(l => l.Values[0].ToString()));
        Assert.True(session.Save().Saved);
        var patch = ws.ReadModPatch(Li01Path);
        Assert.Contains("[Nebula]\r\nzone = Zone_cloud\r\nol.rename = Zone_fog", patch);
        Assert.Contains("[LootableZone]\r\nzone = Zone_hole\r\nol.rename = Zone_gap", ws.ReadModPatch(RocksPath));
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>A lower mod with two <c>[locked_gates]</c> sections makes the section read-only, so the plan warns
    /// (risk 9 of the M3 design).</summary>
    [Fact]
    public void AReadOnlyLockGivesAWarning()
    {
        var hash = FLHash.CreateID("Li01_to_Li02");
        using var ws = Create(lowerFiles: new()
        {
            [@"files\" + ListWorkspace.GroupsPath] = $"[Group]\nnickname = li_n_grp\n[Group]\nnickname = li_p_grp\n" +
                                                     $"[locked_gates]\nlocked_gate = {hash}\n[locked_gates]\nlocked_gate = 5\n",
        });
        var session = ws.Session("act");

        var plan = ScenePlans.Rename(session, Object("Li01_to_Li02"), "Li01_gate");

        Assert.Contains(plan.Warnings, w => w.Message.StartsWith("initialworld.ini locks this gate at the start of a new game " +
                                                                 "by its old hash.", StringComparison.OrdinalIgnoreCase));
        Assert.True(plan.NeedsConfirmation);
    }

    [Theory]
    [InlineData("Li01_01", "Li01_dock", "The object Li01_dock in Li01 has the nickname Li01_dock.")]
    [InlineData("Li01_01", "zone_ring", "The zone Zone_ring in Li01 has the nickname zone_ring.")]
    [InlineData("Li01_01", "Li02_01", "The object Li02_01 in Li02 has the nickname Li02_01.")]
    [InlineData("Li01_01", "LI01_01", "differs only in letter case")]
    [InlineData("Li01_01", "bad,name", "cannot contain ','")]
    [InlineData("Li01_01", " ", "Type a nickname.")]
    public void ARenameRefusesATakenOrInvalidNickname(string target, string name, string message)
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Contains(message, ScenePlans.RenameProblem(session, Object(target), name));
        Assert.Null(ScenePlans.Rename(session, Object(target), name).Command);
    }

    [Fact]
    public void ARenameRefusesAnFlHashAndAKeyOfTheLowerLayer()
    {
        var (first, second) = DataListsTests.FlHashPair();
        using var ws = Create();
        ws.WriteBase(Li02Path, Li02Text + $"\n[zone]\nnickname = {first}\n");
        var session = ws.Session("act");

        Assert.Contains($"has the FLHash of {second}", ScenePlans.RenameProblem(session, Object("Li01_dock"), second));
        // Decision D231: the lower layer has the key, also when W deleted its section.
        session.Execute(EditCommand.DeleteSection(Object("Li01_planet")));
        Assert.Equal("A section of the lower layer of li01.ini has the nickname Li01_planet. A patch would then find two " +
                     "sections with it.", ScenePlans.RenameProblem(session, Object("Li01_dock"), "Li01_planet"));
    }

    [Fact]
    public void ARenameWarnsAboutMentionsTheMapLaterModsWordsAndAdoptedLines()
    {
        using var ws = Create();
        ws.Mod("later", new() { [@"patches\" + Li01Path] = "[zone]\nnickname = Zone_Tradelane_a\nsize = 1, 1, 2000\n" });
        ws.WriteProfile("lower", "act", "later");
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Object("Li01_dock"), "parent", ["Li01_ghost"]));

        var station = ScenePlans.Rename(session, Object("Li01_01"), "Li01_ghost");
        var zone = ScenePlans.Rename(session, Zone("Zone_Tradelane_a"), "Zone_lane_a");

        // The mission scripts are not in the text scan (decision D668 of the M6b design).
        Assert.Empty(station.Mentions);
        Assert.Contains(station.Warnings, w => w.Message == "KnowledgeMap.ini names Li01_01. After the rename, a text does not " +
                                                           "reveal this item on the navigation map.");
        Assert.Contains(station.Warnings, w => w.Message == "1 line names Li01_ghost, but no item has this name. After the edit, " +
                                                           "they name this item.");
        Assert.Contains(zone.Warnings, w => w.Message.StartsWith("Retail uses the word 'Tradelane' in a zone nickname."));
        Assert.Contains(zone.Warnings, w => w.Message.StartsWith("The mod later loads later and changes"));
        Assert.True(station.NeedsConfirmation);
        Assert.False(ScenePlans.Rename(session, Li01("LightSource", "light1"), "light2").NeedsConfirmation);
    }

    /// <summary>A rename of a read-only light is refused with the reason.</summary>
    [Fact]
    public void ARenameOfAReadOnlySectionIsRefused()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Contains("another [LightSource] section also has nickname = twin",
            ScenePlans.RenameProblem(session, Li01("LightSource", "twin"), "single"));
    }

    private sealed class PathCount : IEqualityComparer<(string Path, int Count)>
    {
        public bool Equals((string Path, int Count) x, (string Path, int Count) y) =>
            x.Path.Equals(y.Path, StringComparison.OrdinalIgnoreCase) && x.Count == y.Count;

        public int GetHashCode((string Path, int Count) obj) => obj.Count;
    }
}

/// <summary>The create plans of the Scene (section 9 of the M3 design).</summary>
public sealed class ScenePlansCreateTests
{
    [Fact]
    public void AStationWithABaseIsOneEntryWithItsLinesInOrder()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Create(session, "Li01", SceneTemplate.Station, "station1", new Vector3(1.4f, 2.6f, -3.6f), null,
            "Li01_01_Base");
        session.Execute(plan.Command!);

        var added = Object("li01_station_1");
        Assert.Equal([added], plan.Adds);
        Assert.Equal(added, plan.SelectAfter);
        Assert.Equal(["nickname = li01_station_1", "pos = 1, 3, -4", "archetype = station1", "dock_with = Li01_01_Base"],
            ScenePlansDeleteTests.Lines(session, added));
        Assert.Equal("Create li01_station_1", session.UndoLabel);
        session.Undo();
        Assert.Null(session.Get(added));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void TheNicknameIsTheFirstFreeOne()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(ScenePlans.Create(session, "Li01", SceneTemplate.Planet, "planet1", Vector3.Zero, null).Command!);
        var second = ScenePlans.Create(session, "Li01", SceneTemplate.Planet, "planet1", Vector3.Zero, null);

        Assert.Equal(Object("li01_planet_2"), second.Adds[0]);
    }

    [Fact]
    public void AConnectionArchetypeAndAnArchetypeOfAnotherTemplateAreRefused()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Equal("The archetype jumpgate is a jump gate. " + ScenePlans.NoNewConnections,
            Assert.Single(ScenePlans.Create(session, "Li01", SceneTemplate.Solar, "jumpgate", Vector3.Zero, null).Refusals)
                .Message);
        // Decision D277 of the M23 design: a station archetype without a docking sphere is an archetype of the station
        // template now, so this case takes a planet.
        Assert.Contains("not an archetype of the template", Assert.Single(ScenePlans.Create(session, "Li01",
            SceneTemplate.Station, "planet1", Vector3.Zero, null).Refusals).Message);
        Assert.NotEmpty(ScenePlans.Create(session, "Nowhere", SceneTemplate.Zone, null, Vector3.Zero, null).Refusals);
    }

    /// <summary>
    /// Decision D277 of the M23 design: the template of an archetype comes from its type, without the <c>needs</c> line.
    /// So a station archetype without a docking sphere creates without the base step. The Create menu still lists only
    /// the stations with a docking sphere, and a connection type and an unknown archetype have no template.
    /// </summary>
    [Fact]
    public void AStationWithoutADockingSphereCreatesWithoutTheBaseStep()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Equal(SceneTemplate.Station, ScenePlans.TemplateFor(session, "station_nodock"));
        Assert.Equal(SceneTemplate.Station, ScenePlans.TemplateFor(session, "dock_ring"));
        Assert.Equal(SceneTemplate.Solar, ScenePlans.TemplateFor(session, "depot"));
        Assert.Null(ScenePlans.TemplateFor(session, "jumpgate"));
        Assert.Null(ScenePlans.TemplateFor(session, "no_such_archetype"));
        Assert.True(ScenePlans.AsksForBase(session, SceneTemplate.Station, "station1"));
        Assert.False(ScenePlans.AsksForBase(session, SceneTemplate.Station, "station_nodock"));
        Assert.False(ScenePlans.AsksForBase(session, SceneTemplate.Planet, "planet1"));
        Assert.DoesNotContain(ScenePlans.Archetypes(session, SceneTemplate.Station), a => a.Nickname == "station_nodock");

        var plan = ScenePlans.Create(session, "Li01", SceneTemplate.Station, "station_nodock", Vector3.Zero, null);
        session.Execute(plan.Command!);

        Assert.Equal(["nickname = li01_station_1", "pos = 0, 0, 0", "archetype = station_nodock"],
            ScenePlansDeleteTests.Lines(session, plan.Adds[0]));
    }

    /// <summary>A type in no template of the rule data gives the reason of section 5.2 of the M23 design.</summary>
    [Fact]
    public void ATypeInNoTemplateGivesItsReason()
    {
        using var ws = Create();
        ws.WriteBase(ListWorkspace.SolarPath, Solars + "[Solar]\nnickname = odd\ntype = NO_SUCH_TYPE\n");
        var session = ws.Session("act");

        Assert.Null(ScenePlans.TemplateFor(session, "odd"));
        Assert.Equal("The type no_such_type is in no Create template of the rule data.",
            Assert.Single(ScenePlans.Create(session, "Li01", SceneTemplate.Solar, "odd", Vector3.Zero, null).Refusals).Message);
    }

    /// <summary>Review finding R9 of the M23 design: Create checks if the system file is read-only, also for a zone and a
    /// light.</summary>
    [Fact]
    public void CreateRefusesAReadOnlySystemFile()
    {
        using var ws = Create(activeFiles: new() { [@"files\" + Li01Path] = Li01Text });
        var session = ws.Session("act");
        Assert.NotNull(session.OpenDocument(Li01Path)!.ReadOnlyReason);

        foreach (var (template, archetype) in new[] { (SceneTemplate.Planet, "planet1"), (SceneTemplate.Zone, null),
                     (SceneTemplate.Light, (string?)null) })
        {
            var plan = ScenePlans.Create(session, "Li01", template, archetype, Vector3.Zero, null);
            Assert.Null(plan.Command);
            Assert.StartsWith("li01.ini: ", Assert.Single(plan.Refusals).Message);
        }
    }

    /// <summary>Decision D234: an object template on an object row gets <c>parent</c>. A zone and a light have no
    /// <c>parent</c> key, so they get the position only (review finding R17).</summary>
    [Fact]
    public void OnlyAnObjectTakesAParent()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var buoy = ScenePlans.Create(session, "Li01", SceneTemplate.Satellite, "buoy", new Vector3(5, 6, 7), Object("Li01_01"));
        session.Execute(buoy.Command!);
        var zone = ScenePlans.Create(session, "Li01", SceneTemplate.Zone, null, new Vector3(5, 6, 7), Object("Li01_01"));
        session.Execute(zone.Command!);

        Assert.Equal("Li01_01", SceneSections.Last(session.Get(buoy.Adds[0])!, "parent"));
        Assert.Empty(session.Get(zone.Adds[0])!.Lines("parent"));
        Assert.Equal("5", SceneSections.Last(session.Get(zone.Adds[0])!, "pos"));
    }

    [Theory]
    [InlineData(Shape.SPHERE, "1000")]
    [InlineData(Shape.ELLIPSOID, "2000, 1000, 2000")]
    [InlineData(Shape.BOX, "2000, 2000, 2000")]
    [InlineData(Shape.CYLINDER, "1000, 2000")]
    [InlineData(Shape.RING, "2000, 1000, 200")]
    public void AZoneGetsTheSizeOfItsShape(Shape shape, string size)
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Create(session, "Li01", SceneTemplate.Zone, null, Vector3.Zero, null, shape: shape);
        session.Execute(plan.Command!);

        Assert.Equal(["nickname = li01_zone_1", "pos = 0, 0, 0", $"shape = {shape}", $"size = {size}"],
            ScenePlansDeleteTests.Lines(session, plan.Adds[0]));
        Assert.True(session.Save().Saved);
    }

    /// <summary>Decision D250 as the review changed it: a POINT light with an attenuation, so that it lights the models
    /// near it (review finding R10).</summary>
    [Fact]
    public void ALightHasAnAttenuation()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Create(session, "Li01", SceneTemplate.Light, null, Vector3.Zero, null);
        session.Execute(plan.Command!);

        Assert.Equal(Li01("LightSource", "li01_light_1"), plan.Adds[0]);
        var lines = ScenePlansDeleteTests.Lines(session, plan.Adds[0]);
        Assert.Equal(["nickname", "pos", "color", "range", "type", "attenuation"], lines.Select(l => l[..l.IndexOf(' ')]));
        Assert.Equal("type = POINT", lines[4]);
        Assert.Equal(1e-6f, session.Get(plan.Adds[0])!.Lines("attenuation")[0].Values[2].ToSingle());
        Assert.True(session.Save().Saved);
    }

    [Fact]
    public void TheTemplatesListTheirArchetypesAndTheBasesOfTheSystemComeFirst()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Equal(["station1", "dock_ring"], ScenePlans.Archetypes(session, SceneTemplate.Station).Select(a => a.Nickname));
        Assert.Equal(["depot"], ScenePlans.Archetypes(session, SceneTemplate.Solar).Select(a => a.Nickname));
        Assert.True(ScenePlans.AsksForBase(session, SceneTemplate.Station));
        Assert.False(ScenePlans.AsksForBase(session, SceneTemplate.Planet));
        Assert.Equal(["Li02_01_Base", "Li01_01_Base"], ScenePlans.Bases(session, "Li02").Select(b => b.Value));
        Assert.Equal([SceneTemplate.Station, SceneTemplate.Planet, SceneTemplate.Sun, SceneTemplate.Satellite, SceneTemplate.Solar],
            ScenePlans.ObjectTemplates(session).Select(t => t.Template));
    }
}

/// <summary>The duplicate plans of the Scene (section 10 of the M3 design).</summary>
public sealed class ScenePlansDuplicateTests
{
    [Fact]
    public void ACopyTakesItsChildrenAndRelinksThemAndLeavesOutTheBase()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var plan = ScenePlans.Duplicate(session, [Object("Li01_01")]);
        session.Execute(plan.Command!);

        Assert.Equal([Object("Li01_02"), Object("Li01_child_1"), Object("Li01_grandchild_1")], plan.Adds);
        Assert.Equal(Object("Li01_02"), plan.SelectAfter);
        Assert.Empty(session.Get(Object("Li01_02"))!.Lines("base"));
        Assert.Equal("Li01_01_Base", SceneSections.Last(session.Get(Object("Li01_02"))!, "dock_with"));
        Assert.Equal("Li01_02", SceneSections.Last(session.Get(Object("Li01_child_1"))!, "parent"));
        Assert.Equal("Li01_child_1", SceneSections.Last(session.Get(Object("Li01_grandchild_1"))!, "parent"));
        Assert.Contains(plan.Warnings, w => w.Message.StartsWith("Li01_02: the copy has no 'base' line."));
        Assert.False(plan.NeedsConfirmation);
        // The copies go at the end of the file.
        Assert.Equal(Object("Li01_grandchild_1"), session.Sections(Li01Path)[^1]);
        Assert.Equal("Duplicate Li01_01 and 2 more", session.UndoLabel);
    }

    [Fact]
    public void ACopyOfAPlaceholderGetsACopyOfItsText()
    {
        using var ws = Create(activeFiles: new()
        {
            [@"patches\" + Li01Path] = "[Object]\nnickname = Li01_dock\nids_name = $str:dock_name\n",
            ["strings.json"] = """{ "strings": { "dock_name": "Dock" } }""",
        });
        var session = ws.Session("act");

        session.Execute(ScenePlans.Duplicate(session, [Object("Li01_dock")]).Command!);

        Assert.Equal("$str:li01_dock_1", SceneSections.Last(session.Get(Object("Li01_dock_1"))!, "ids_name"));
        Assert.Equal("Dock", session.Text(new StringPlaceholder(StringKind.String, "li01_dock_1")));
    }

    [Fact]
    public void ConnectionsVolumesAndTheirZonesAreRefused()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Contains("cannot copy jump gates", ScenePlans.Duplicate(session, [Object("Li01_to_Li02")]).Refusals[0].Message);
        const string noCopy = "this version cannot copy nebulae, asteroid fields or the zones that hold them";
        Assert.Contains(noCopy, ScenePlans.Duplicate(session, [Li01("Nebula", "Zone_cloud")]).Refusals[0].Message);
        Assert.Contains(noCopy, ScenePlans.Duplicate(session, [Zone("Zone_cloud")]).Refusals[0].Message);
        Assert.Equal(Zone("Zone_hole_1"), ScenePlans.Duplicate(session, [Zone("Zone_hole")]).Adds[0]);
        Assert.Equal(Li01("LightSource", "light1_1"), ScenePlans.Duplicate(session, [Li01("LightSource", "light1")]).Adds[0]);
    }

    /// <summary>
    /// A copy of a jump gate or a jump hole would have no partner, so Duplicate refuses it and names the tool that makes
    /// a pair. A copy of a trade lane ring would not be a part of the lane, so Duplicate names the lane tools. An airlock
    /// gate refuses too. The Delete menu takes a gate and a hole, because a delete takes the pair. It takes a ring,
    /// because a delete relinks the lane. It refuses an airlock gate. The menu and the plan give one reason.
    /// </summary>
    [Fact]
    public void ACopyOfAConnectionObjectIsRefusedAndTheDeleteMenuTakesAGateAHoleAndARing()
    {
        using var ws = Create();
        ws.WriteBase(ListWorkspace.SolarPath, Solars + "\n[Solar]\nnickname = jumphole\ntype = JUMP_HOLE\n" +
                                              "[Solar]\nnickname = airlock\ntype = AIRLOCK_GATE\n");
        ws.WriteBase(Li02Path, Li02Text + "\n[Object]\nnickname = Li02_hole\narchetype = jumphole\n" +
                                          "[Object]\nnickname = Li02_airlock\narchetype = airlock\n");
        var session = ws.Session("act");
        var gate = Object("Li01_to_Li02");
        var hole = Li02("Object", "Li02_hole");
        var airlock = Li02("Object", "Li02_airlock");

        var copy = Assert.Single(ScenePlans.Duplicate(session, [gate]).Refusals).Message;

        Assert.Equal("Li01_to_Li02 is a jump gate. This version cannot copy jump gates or jump holes, because a copy has " +
                     "no partner. To make a pair, use the jump connection tool connection_create of the agent server.", copy);
        Assert.Equal(copy, ScenePlans.Unavailable(session, SceneAction.Duplicate, gate));
        Assert.StartsWith("Li02_hole is a jump hole. This version cannot copy jump gates or jump holes",
            ScenePlans.Unavailable(session, SceneAction.Duplicate, hole));
        Assert.Equal("Li02_airlock is an airlock gate. This version cannot copy airlock gates.",
            Assert.Single(ScenePlans.Duplicate(session, [airlock]).Refusals).Message);
        Assert.Equal("ring2 is a trade lane ring. This version cannot copy trade lane rings, because a copy is not a part " +
                     "of the lane. To make a lane or a ring, use the trade lane tools trade_lane_create and " +
                     "trade_lane_insert_ring of the agent server.",
            ScenePlans.Unavailable(session, SceneAction.Duplicate, Object("ring2")));
        Assert.Equal(ScenePlans.Unavailable(session, SceneAction.Duplicate, Object("ring2")),
            Assert.Single(ScenePlans.Duplicate(session, [Object("ring2")]).Refusals).Message);
        Assert.Null(ScenePlans.Unavailable(session, SceneAction.Delete, gate));
        Assert.Null(ScenePlans.Unavailable(session, SceneAction.Delete, hole));
        Assert.NotNull(ScenePlans.Unavailable(session, SceneAction.Delete, airlock));
        Assert.Null(ScenePlans.Unavailable(session, SceneAction.Delete, Object("ring2")));
        Assert.Equal([hole], ScenePlans.Delete(session, [hole]).Deletes.Select(d => d.Section));
    }
}
