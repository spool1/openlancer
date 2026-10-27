using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of the data lists (milestone M21): the counts of section 9 of the design, the retail baseline of the
/// list checks, the save gate, the removed entries, the raw edits and the speed budgets of section 16.2.
/// </summary>
public sealed class ListRetailTests(ITestOutputHelper output)
{
    /// <summary>The battleship <c>Li01_03</c> of Li01, with the archetype <c>l_dreadnought</c>.</summary>
    private const string Battleship = "Li01_03";

    private const string BattleshipLoadout = "li_dreadnought_li_01";

    [Fact]
    public void TheListsHaveTheRetailCounts()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m21proof");
        var session = workspace.Open();
        var lists = session.Lists;

        var expected = new Dictionary<string, int>
        {
            ["solar_archetype"] = 321, ["ship"] = 115, ["loadout"] = 803, ["faction"] = 55, ["system"] = 53,
            ["base"] = 197, ["star"] = 38, ["star_glow"] = 45, ["spines"] = 15, ["lens_flare"] = 7, ["lens_glow"] = 5,
            ["pilot"] = 318, ["voice"] = 109, ["sound"] = 1817, ["effect"] = 705, ["vis_effect"] = 1218,
            ["effect_type"] = 45, ["beam"] = 55, ["explosion_fx"] = 70, ["explosion_equipment"] = 86, ["motor"] = 76,
            ["munition"] = 513, ["fuse"] = 192, ["good"] = 855, ["head"] = 104, ["body"] = 88, ["left_hand"] = 6,
            ["right_hand"] = 6, ["accessory"] = 106, ["curve"] = 6, ["gate_tunnel"] = 4, ["jump_gate_effect"] = 5,
            ["weapon_type"] = 21, ["shield_type"] = 9, ["dock_sphere_type"] = 7, ["cutscene"] = 1506,
        };
        foreach (var (id, count) in expected)
        {
            var list = lists.List(id);
            output.WriteLine($"{id}: {list?.Entries.Count}");
            Assert.NotNull(list);
            Assert.Equal(count, list.Entries.Count);
        }

        var equipment = lists.List("equipment")!;
        output.WriteLine($"equipment: {equipment.Entries.Count}");
        Assert.InRange(equipment.Entries.Count, 1700, 1900);
        Assert.Contains(equipment.Entries, e => e.Value.Equals("li_gun01_mark01", StringComparison.OrdinalIgnoreCase));
        Assert.Null(equipment.Find(lists.List("motor")!.Entries[0].Value));

        // The fuses come from 16 files.
        Assert.Equal(16, session.DataFiles.Paths("fuses").Count);
        // The scoped lists: the objects, zones and encounters of each system file.
        var files = session.DataFiles.Paths("system");
        Assert.Equal(53, files.Count);
        Assert.Equal(3430, files.Sum(f => lists.List("object_of_system", f)!.Entries.Count));
        Assert.Equal(5428, files.Sum(f => lists.List("zone_of_system", f)!.Entries.Count));
        Assert.Equal(39, files.SelectMany(f => lists.List("encounter_of_system", f)!.Entries)
            .Select(e => e.Value.ToLowerInvariant()).Distinct().Count());
        // No hash list of the retail data has an FLHash collision, else the engine would not load it.
        Assert.All(lists.RuleData.Lists.Values.Where(d => d.Hash), d => Assert.Empty(lists.List(d.Id)!.Collisions));
        Assert.Equal(["airlock", "berth", "jump", "moor_large", "moor_medium", "moor_small", "ring"],
            lists.List("dock_sphere_type")!.Entries.Select(e => e.Value).Order());
    }

    /// <summary>
    /// The lists of the nebula and asteroid files (section 11 of the M22 design). They hold the INI files of
    /// <c>SOLAR\NEBULA</c> and <c>SOLAR\ASTEROIDS</c> whose sections all belong to the file class. The 5 texture files of
    /// each folder are no entries. Each of the 259 <c>file</c> lines of the 53 retail system files names an entry, and no <c>file</c> or
    /// <c>zone</c> line gives a list problem.
    /// </summary>
    [Fact]
    public void TheFileListsHoldTheRetailFiles()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m22lists");
        var session = workspace.Open();
        var lists = session.Lists;
        var nebulae = lists.List("nebula_file")!;
        var fields = lists.List("asteroid_file")!;

        output.WriteLine($"nebula files {nebulae.Entries.Count}, asteroid files {fields.Entries.Count}");
        Assert.Equal((59, 153), (nebulae.Entries.Count, fields.Entries.Count));
        Assert.DoesNotContain(nebulae.Entries.Concat(fields.Entries),
            e => e.Value.EndsWith("_shapes.ini", StringComparison.OrdinalIgnoreCase));
        Assert.All(nebulae.Entries, e => Assert.StartsWith(@"SOLAR\NEBULA\", e.Value, StringComparison.OrdinalIgnoreCase));

        var lines = 0;
        foreach (var path in session.DataFiles.Paths("system"))
        {
            foreach (var section in session.Sections(path).Where(s => s.Id.Type.ToLowerInvariant() is "nebula" or "asteroids"))
            {
                var file = session.Get(section)!.Lines("file")[^1].Values[0].ToString()!;
                var list = section.Id.Type.Equals("Nebula", StringComparison.OrdinalIgnoreCase) ? nebulae : fields;
                Assert.True(list.Find(file) != null, $"{section}: {file}");
                Assert.Empty(lists.Problems(section));
                lines++;
            }
        }

        Assert.Equal(259, lines);
    }

    /// <summary>
    /// With no edit of the active mod, no rule gives an error. The warnings are the 4 retail values outside a list: two
    /// <c>parent</c> lines that name an object of another system and two <c>lens_glow</c> lines. The 3 rooms without
    /// <c>set_script</c> give no warning: they are the <c>Cityscape</c> rooms of the intro bases, which the engine plays
    /// through <c>IntroScenes</c>, not as rooms (section 5.6 of the M8b design), so the rule skips them (section 6 of the
    /// console filter design). Each rule matches a retail line, or the rule data marks it as unused.
    /// </summary>
    [Fact]
    public void TheRetailDataGivesNoListErrorAndFourWarnings()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m21proof");
        var session = workspace.Open();
        var lists = session.Lists;
        var paths = lists.RuleData.Roles.Keys.SelectMany(session.DataFiles.Paths).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var problems = new List<ListProblem>();
        var used = new HashSet<ListRule>();
        var sections = 0;
        foreach (var path in paths)
        {
            foreach (var section in session.Sections(path))
            {
                sections++;
                problems.AddRange(lists.Problems(section));
            }
            foreach (var role in session.DataFiles.RolesOf(path))
            {
                foreach (var rule in lists.RuleData.All.Where(r => r.Role.Equals(role, StringComparison.OrdinalIgnoreCase)))
                {
                    if (used.Contains(rule))
                        continue;
                    if (session.Sections(path).Any(s => s.Id.Type.Equals(rule.Section, StringComparison.OrdinalIgnoreCase) &&
                                                        session.Get(s)!.Lines(rule.Key).Any(l => l.Values.Count > rule.Index)))
                        used.Add(rule);
                }
            }
        }
        output.WriteLine($"{paths.Count} files, {sections} sections");
        foreach (var problem in problems)
            output.WriteLine(problem.Diagnostic.ToString());

        Assert.DoesNotContain(problems, p => p.IsError);
        Assert.Equal(4, problems.Count);
        Assert.DoesNotContain(problems, p => p.Kind == ListProblemKind.RequiredMissing);
        Assert.Equal(3, session.Rooms.Files.Count(session.Rooms.IsIntroRoom));
        Assert.Equal(2, problems.Count(p => p.Diagnostic.Key == "parent" && p.Value!.Equals("Li01_space_police_dmg_1",
            StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(problems, p => p.Diagnostic.Key == "lens_glow" && p.Value == "blue_lens_glow");
        Assert.Contains(problems, p => p.Diagnostic.Key == "lens_glow" && p.Value == "sm_red_lens_glow");
        var unused = lists.RuleData.All.Where(r => !used.Contains(r)).ToList();
        foreach (var rule in unused)
            output.WriteLine($"No retail line: {rule.Role} [{rule.Section}] {rule.Key} ({rule.Index})");
        Assert.Equal(unused.Where(r => !r.Unused), []);
        Assert.All(lists.RuleData.All.Where(r => r.Unused), r => Assert.DoesNotContain(r, used));
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>Decision D96: a loadout of the active mod that no section has stops the save, and the save names the
    /// section. Decision D32 as amended by M21: an archetype that no section has stops the save too.</summary>
    [Fact]
    public void TheGateStopsASaveWithAnUnknownLoadoutOrArchetype()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m21proof");
        var session = workspace.Open();
        var li01 = session.SystemPath("Li01")!;
        var battleship = new SectionRef(li01, new SectionIdentity("Object", Battleship));
        Assert.Equal(BattleshipLoadout, session.Get(battleship)!.Lines("loadout")[0].Values[0].ToString());

        session.Execute(EditCommand.SetLinesFromText(battleship, "loadout", ["li_no_such_loadout"]));
        var refused = session.Save();
        Assert.False(refused.Saved);
        var problem = Assert.Single(refused.Problems, p => p.Severity == DiagnosticSeverity.Error);
        output.WriteLine(problem.ToString());
        Assert.Equal((battleship, "loadout"), (problem.Target, problem.Key));
        Assert.Contains("no loadout has the name 'li_no_such_loadout'", problem.Message);
        Assert.False(Directory.Exists(Path.Combine(workspace.ModFolder, "patches")));

        // Pick the loadout again: the save writes nothing new.
        session.Execute(EditCommand.SetLinesFromText(battleship, "loadout", [BattleshipLoadout]));
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
        Assert.Empty(saved.Written);

        var planet = new SectionRef(li01, new SectionIdentity("Object", "Li01_01"));
        session.Execute(EditCommand.SetLinesFromText(planet, "archetype", ["no_such_archetype"]));
        refused = session.Save();
        Assert.False(refused.Saved);
        problem = Assert.Single(refused.Problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.Equal((planet, "archetype"), (problem.Target, problem.Key));
        Assert.EndsWith("The game drops this object.", problem.Message);
        RetailChecks.AssertBaseIsUnchanged(GameDataPaths.RequireBase());
    }

    /// <summary>Decision D81: an <c>ol.delete</c> patch of the active mod removes a retail loadout. The retail object
    /// that names it is an error of the active mod, and the gate reports it.</summary>
    [Fact]
    public void AnOlDeleteOfARetailLoadoutMakesItsUserAnError()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m21proof");
        var patch = Path.Combine(workspace.ModFolder, "patches", "DATA", "SHIPS", "loadouts_special.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
        File.WriteAllText(patch, $"[Loadout]\r\nnickname = {BattleshipLoadout}\r\nol.delete = true\r\n");
        var session = workspace.Open();
        var battleship = new SectionRef(session.SystemPath("Li01")!, new SectionIdentity("Object", Battleship));

        var removed = Assert.Single(session.Lists.Removed());
        Assert.Equal(("loadout", BattleshipLoadout), (removed.List, removed.Value));
        var problems = session.SaveProblems();
        foreach (var problem in problems)
            output.WriteLine(problem.ToString());
        Assert.Contains(problems, p => p.Target == battleship && p.Key == "loadout");
        Assert.All(problems, p => Assert.Equal(DiagnosticSeverity.Error, p.Severity));
        Assert.False(session.Save().Saved);
    }

    /// <summary>
    /// Decision D82, as decided on 2026-10-18: the active mod removes the explosion of a retail missile, so the engine
    /// stops the load. The missile is unusable, so each retail gun that fires it is an error, also though the active
    /// mod did not change the gun. The engine drops such a gun, so each loadout line that names it is an error too. One
    /// undo heals all of them.
    /// </summary>
    [Fact]
    public void AMissileWithoutItsExplosionMakesEachRetailLineThatNamesItAnError()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m21proof");
        var session = workspace.Open();
        var lists = session.Lists;
        // The first missile of the list that a gun fires and a loadout carries on a gun.
        var missile = lists.List("munition")!.Entries.First(e =>
            e.Section is { } s && session.Get(s)!.Lines("motor").Count > 0 &&
            lists.ReferencesTo("munition", e.Value).Any(r => lists.ReferencesTo("equipment", r.Section.Id.Key!)
                .Any(g => g.Key.Equals("equip", StringComparison.OrdinalIgnoreCase))));
        var guns = lists.ReferencesTo("munition", missile.Value).Select(r => r.Section).Distinct().ToList();
        var equips = guns.SelectMany(g => lists.ReferencesTo("equipment", g.Id.Key!)
                .Where(r => r.Key.Equals("equip", StringComparison.OrdinalIgnoreCase)).Select(r => (Gun: g.Id.Key!, r.Section)))
            .ToList();
        output.WriteLine($"Missile {missile.Value}: {guns.Count} guns fire it, {equips.Count} loadout lines name a gun.");

        session.Execute(EditCommand.RemoveName(missile.Section!, "explosion_arch"));
        var watch = Stopwatch.StartNew();
        var problems = session.SaveProblems();
        output.WriteLine($"The save gate: {watch.Elapsed.TotalMilliseconds:0.0} ms, {problems.Count} errors");
        foreach (var problem in problems)
            output.WriteLine(problem.ToString());

        Assert.All(problems, p => Assert.Equal(DiagnosticSeverity.Error, p.Severity));
        var own = Assert.Single(problems, p => p.Target == missile.Section);
        Assert.Equal("explosion_arch", own.Key);
        Assert.EndsWith("The game stops loading without this key.", own.Message);
        foreach (var gun in guns)
        {
            var error = Assert.Single(problems, p => p.Target == gun && p.Key == "projectile_archetype");
            Assert.EndsWith($"Munition '{missile.Value}' is not usable: the key 'explosion_arch' is missing. A section " +
                            "with 'motor' needs it. The game stops loading without this key. " +
                            $"Fix '{missile.Value}', or pick another value.", error.Message);
        }
        foreach (var (gun, loadout) in equips)
        {
            Assert.Contains(problems, p => p.Target == loadout && p.Key == "equip" &&
                                           p.Message.Contains($"Equipment '{gun}' is not usable",
                                               StringComparison.OrdinalIgnoreCase));
        }
        Assert.False(session.Save().Saved);
        Assert.False(Directory.Exists(Path.Combine(workspace.ModFolder, "patches")));

        session.Undo();
        Assert.Empty(session.SaveProblems());
        Assert.All(guns, g => Assert.Empty(lists.Problems(g)));
        RetailChecks.AssertBaseIsUnchanged(GameDataPaths.RequireBase());
    }

    /// <summary>A raw edit of <c>pos</c> of the battleship writes the same patch line as a row edit. A raw edit of a
    /// section of a binary file keeps the value types of the merge, as a row edit does.</summary>
    [Fact]
    public void ARawEditWritesWhatARowEditWrites()
    {
        var baseFolder = GameDataPaths.RequireBase();
        string Patch(bool raw, out string hitPoints)
        {
            using var workspace = new RetailWorkspace(baseFolder, raw ? "rawedit" : "rowedit");
            var session = workspace.Open();
            var battleship = new SectionRef(session.SystemPath("Li01")!, new SectionIdentity("Object", Battleship));
            var gun = session.Find(ItemFileKind.Equipment, "li_gun01_mark01")!;
            Assert.True(session.Items.IsBinary(gun));
            if (raw)
            {
                Edit(session, battleship, "pos", "-30000, 0, -25000");
                Edit(session, gun, "hit_pts", "1234");
            }
            else
            {
                session.Execute(EditCommand.SetLinesFromText(battleship, "pos", ["-30000, 0, -25000"]));
                session.Execute(EditCommand.SetLinesFromText(gun, "hit_pts", ["1234"]));
            }

            var value = session.Get(gun)!.Lines("hit_pts")[0].Values[0];
            hitPoints = $"{value.GetType().Name} {value}";
            var result = session.Save();
            Assert.True(result.Saved, string.Join("\n", result.Problems));
            return string.Join("\n---\n", result.Written.Order().Select(File.ReadAllText));
        }

        var rawPatch = Patch(raw: true, out var rawHitPoints);
        var rowPatch = Patch(raw: false, out var rowHitPoints);
        output.WriteLine(rawPatch);
        Assert.Equal(rowPatch, rawPatch);
        Assert.Equal(rowHitPoints, rawHitPoints);
        Assert.Contains("pos = -30000, 0, -25000", rawPatch);
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>A raw edit of one line of a section: the text of the raw block with the line changed.</summary>
    private static void Edit(EditSession session, SectionRef section, string key, string values)
    {
        var start = RawSectionEdit.Format(session.Get(section)!);
        var lines = start.Split('\n').Select(l => l.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase)
            ? $"{key} = {values}"
            : l);
        var plan = RawSectionEdit.Plan(session, section, start, string.Join("\n", lines));
        Assert.Empty(plan.Problems);
        session.Execute(plan.Edit!.Command!);
        Assert.Equal("Edit INI of " + section.Id, session.UndoLabel);
    }

    /// <summary>Section 16.2 of the design: the budgets on the retail profile, with a generous factor for slow
    /// machines. The test writes the times to the test output.</summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void TheListsMeetTheirBudgets()
    {
        const double factor = 10;
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m21proof");
        var session = workspace.Open();
        // The steps change the session one after the other, so they cannot run again. The priority keeps the other
        // programs of the computer out of the measures (see Timing).
        using var priority = Timing.HighPriority();
        var lists = session.Lists;
        var li01 = session.SystemPath("Li01")!;
        var battleship = new SectionRef(li01, new SectionIdentity("Object", Battleship));
        var rule = Assert.Single(lists.Rules(battleship, "loadout"));

        var watch = Stopwatch.StartNew();
        Assert.NotNull(lists.List("loadout"));
        var first = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"First build of the loadout list: {first:0.0} ms");

        watch.Restart();
        for (var i = 0; i < 1000; i++)
            _ = lists.List(rule, battleship, null)!.Find(BattleshipLoadout);
        var row = watch.Elapsed.TotalMilliseconds / 1000;
        output.WriteLine($"A row with a rule: {row:0.0000} ms");

        session.Execute(EditCommand.SetLinesFromText(battleship, "pos", ["1, 2, 3"]));
        watch.Restart();
        _ = lists.List("loadout");
        var other = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"The loadout list after an edit of another file: {other:0.000} ms");

        var loadouts = session.DataFiles.Paths("loadouts").First(p => p.Contains("special", StringComparison.OrdinalIgnoreCase));
        var loadout = session.Sections(loadouts).First(s => s.Id.Type.Equals("Loadout", StringComparison.OrdinalIgnoreCase));
        session.Execute(EditCommand.SetLinesFromText(loadout, "archetype", ["l_dreadnought"]));
        watch.Restart();
        _ = lists.List("loadout");
        var own = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"The loadout list after an edit of one of its files: {own:0.0} ms");

        watch.Restart();
        var references = lists.CountReferences("object_of_system", "Li01_to_Li02", li01);
        var reverse = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"First reverse index of the objects of the systems: {reverse:0.0} ms ({references} references)");

        // The first gate builds the lists of the changed sections. A gate after the next edit reads only the changed
        // sections again.
        watch.Restart();
        var problems = session.SaveProblems();
        var firstGate = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"The first save gate: {firstGate:0.0} ms, {problems.Count} problems");
        session.Execute(EditCommand.SetLinesFromText(battleship, "pos", ["1, 2, 4"]));
        watch.Restart();
        Assert.Empty(session.SaveProblems());
        var gate = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"The save gate after an edit: {gate:0.0} ms");

        var opening = lists.List("equipment")!;
        watch.Restart();
        var names = opening.Entries.Select(e => e.Section == null ? e.Value : session.Get(e.Section)?.Type).Count();
        var rows = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"The rows of a picker opening ({names} entries): {rows:0.0} ms");

        Assert.InRange(first, 0, 100 * factor);
        Assert.InRange(row, 0, 0.01 * factor);
        Assert.InRange(other, 0, 5 * factor);
        Assert.InRange(own, 0, 5 * factor);
        Assert.InRange(reverse, 0, 300 * factor);
        Assert.InRange(firstGate, 0, 2000);
        Assert.InRange(gate, 0, 20 * factor);
        Assert.InRange(rows, 0, 10 * factor);
        Assert.Empty(problems);
    }
}
