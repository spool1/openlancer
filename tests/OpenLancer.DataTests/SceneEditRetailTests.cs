using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Resources;
using LibreLancer.World.Components;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Validation;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>The retail data and the actions of the Scene (section 18.2 of the M3 design).</summary>
internal static class SceneRetail
{
    public const string Li01 = @"DATA\UNIVERSE\SYSTEMS\LI01\li01.ini";
    public const string Li02 = @"DATA\UNIVERSE\SYSTEMS\LI02\li02.ini";
    public const string Bw05 = @"DATA\UNIVERSE\SYSTEMS\BW05\bw05.ini";
    public const string Crow = @"DATA\SOLAR\NEBULA\Bw05_Crow_nebula.ini";

    public static RetailWorkspace Workspace(string id) => new(GameDataPaths.RequireBase(), id);

    public static SectionRef Section(EditSession session, string path, string type, string nickname) =>
        session.Sections(path).Single(s => s.Id.Type.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(s.Id.Key, nickname, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves the session and loads the game data of the edit view with the engine.</summary>
    public static GameItemDb SaveAndLoad(EditSession session)
    {
        var result = session.Save();
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        return EngineDataLoader.Load(session.EditView.CreateFileSystem()).RequireComplete();
    }

    /// <summary>The first object of a system that docks with a base: the launch object of the base.</summary>
    public static SystemObject? LaunchObject(StarSystem system, string baseName) =>
        system.Objects.FirstOrDefault(o => o.Dock is { Kind: DockKinds.Base } dock &&
                                           string.Equals(dock.Target, baseName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The retail proofs of Create (section 18.2 of the M3 design).</summary>
public sealed class SceneCreateRetailTests
{
    static SceneCreateRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>A new station that docks with Manhattan loads, has a base dock, and changes neither the launch object nor
    /// the base object of Manhattan.</summary>
    [Fact]
    public void ADockingStationThatDocksWithManhattanLoads()
    {
        using var workspace = SceneRetail.Workspace("m3create");
        var session = workspace.Open();
        var archetype = ScenePlans.Archetypes(session, SceneTemplate.Station).First().Nickname;
        Assert.Equal("Li01_01_Base", ScenePlans.Bases(session, "Li01")[0].Value, ignoreCase: true);

        var plan = ScenePlans.Create(session, "Li01", SceneTemplate.Station, archetype, new Vector3(-30000, 0, -25000), null,
            "Li01_01_Base");
        session.Execute(plan.Command!);
        var db = SceneRetail.SaveAndLoad(session);

        Assert.Equal("Create li01_station_1", session.UndoLabel);
        var patch = File.ReadAllText(Path.Combine(workspace.ModFolder, "patches", SceneRetail.Li01));
        Assert.Equal(1, patch.Split("ol.new = true").Length - 1);
        var li01 = db.Systems.Get("li01")!;
        var station = li01.Objects.Single(o => o.Nickname == "li01_station_1");
        Assert.Equal(DockKinds.Base, station.Dock!.Kind);
        Assert.Equal("Dock_Ring_Li01_01", SceneRetail.LaunchObject(li01, "Li01_01_Base")!.Nickname, ignoreCase: true);
        Assert.Equal("Li01_01", li01.Objects.First(o => o.Base?.Nickname.Equals("Li01_01_Base",
            StringComparison.OrdinalIgnoreCase) == true).Nickname, ignoreCase: true);
    }
}

/// <summary>The retail proofs of Delete (section 18.2 of the M3 design).</summary>
public sealed class SceneDeleteRetailTests(ITestOutputHelper output)
{
    private const string FirstHoleZone = "Zone_Bw05_01_exclusion";
    private const string YanagiField = "Zone_Bw05_yanagi_debris_field";

    static SceneDeleteRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The zone of the first hole of the Crow nebula goes with the 6 lines of its hole. The engine loads, the
    /// nebula has one hole fewer, and one undo gives the W states before.</summary>
    [Fact]
    public void AZoneThatTheCrowNebulaExcludesGoesWithItsHoleLines()
    {
        using var workspace = SceneRetail.Workspace("m3delete");
        var session = workspace.Open();
        var zone = SceneRetail.Section(session, SceneRetail.Bw05, "zone", FirstHoleZone);
        var holes = session.Sections(SceneRetail.Crow).Single(s => s.Id.Type == "Exclusion Zones");
        var lines = session.Get(holes)!.Entries.Count;
        var before = new[] { SceneRetail.Crow, SceneRetail.Bw05 }.Select(p => session.OpenDocument(p)!.State).ToList();

        var plan = ScenePlans.Delete(session, [zone]);
        foreach (var fix in plan.Fixes)
            output.WriteLine(fix.Text);

        Assert.Empty(plan.Refusals);
        Assert.Contains($"Bw05_Crow_nebula.ini: the hole of {FirstHoleZone} (6 lines)", plan.Fixes.Select(f => f.Text));
        Assert.True(plan.NeedsConfirmation);
        session.Execute(plan.Command!);
        Assert.Equal(lines - 6, session.Get(holes)!.Entries.Count);
        var holesBefore = RetailGame.Instance.Data.Items.Systems.Get("bw05")!.Nebulae
            .Single(n => n.Zone!.Nickname.Equals("Zone_Bw05_Crow_nebula", StringComparison.OrdinalIgnoreCase)).ExclusionZones!.Count;

        var db = SceneRetail.SaveAndLoad(session);
        var crow = db.Systems.Get("bw05")!.Nebulae
            .Single(n => n.Zone!.Nickname.Equals("Zone_Bw05_Crow_nebula", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(holesBefore - 1, crow.ExclusionZones!.Count);
        Assert.Empty(session.SaveProblems());

        session.Undo();
        Assert.Equal(before, new[] { SceneRetail.Crow, SceneRetail.Bw05 }.Select(p => session.OpenDocument(p)!.State));
        Assert.Equal(lines, session.Get(holes)!.Entries.Count);
        Assert.NotNull(session.Get(zone));
    }

    /// <summary>The zone of the Yanagi field takes the field and its hole in the Crow nebula (decision D236).</summary>
    [Fact]
    public void AFieldZoneTakesItsFieldAndItsHoleInTheCrowNebula()
    {
        using var workspace = SceneRetail.Workspace("m3field");
        var session = workspace.Open();
        var zone = SceneRetail.Section(session, SceneRetail.Bw05, "zone", YanagiField);

        var plan = ScenePlans.Delete(session, [zone]);

        Assert.Contains(plan.Deletes, d => d.Section.Id.Type == "Asteroids" && d.Why == DeleteReason.Cascade);
        Assert.Contains(plan.Fixes, f => f.Section.Path.Equals(SceneRetail.Crow, StringComparison.OrdinalIgnoreCase));
        session.Execute(plan.Command!);
        var db = SceneRetail.SaveAndLoad(session);
        Assert.DoesNotContain(db.Systems.Get("bw05")!.AsteroidFields,
            f => f.Zone!.Nickname.Equals(YanagiField, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A delete of a retail jump gate deletes its pair: the partner in Li02 and the lock lines of both ends. The partner
    /// is in another file, so the plan asks. The story missions name both ends, so their lines show "Missing". One undo
    /// gives both ends back.
    /// </summary>
    [Fact]
    public void AJumpGateDeleteTakesThePartnerAndTheLockLines()
    {
        using var workspace = SceneRetail.Workspace("m3gate");
        var session = workspace.Open();
        var gate = SceneRetail.Section(session, SceneRetail.Li01, "Object", "Li01_to_Li02");
        var locks = GateLocks.Of(session, "Li01_to_Li02").Count + GateLocks.Of(session, "Li02_to_Li01").Count;

        var plan = ScenePlans.Delete(session, [gate]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.True(plan.NeedsConfirmation);
        var partner = Assert.Single(plan.Deletes, d => d.Section.Id.Key!.Equals("Li02_to_Li01", StringComparison.OrdinalIgnoreCase));
        Assert.True(VfsPath.Comparer.Equals(SceneRetail.Li02, partner.Section.Path), partner.Section.Path);
        Assert.Equal(DeleteReason.Cascade, partner.Why);
        Assert.Equal(locks, plan.Fixes.Count(f => f.Key.Contains("locked_gate", StringComparison.OrdinalIgnoreCase)));
        Assert.NotEmpty(plan.MissionMissing);
        // The retail traffic zone of the gate has another name than the zones of a new end, so it stays with a warning.
        var traffic = Assert.Single(plan.Warnings, w => w.Target?.Id.Key?.Equals("Zone_Li01_Jumpgate_003",
            StringComparison.OrdinalIgnoreCase) == true);
        Assert.StartsWith("The traffic zone Zone_Li01_Jumpgate_003 stays. It is ", traffic.Message);
        Assert.DoesNotContain(plan.Deletes, d => d.Section.Equals(traffic.Target));
        session.Execute(plan.Command!);

        Assert.Null(session.Get(gate));
        Assert.Null(session.Get(partner.Section));
        Assert.NotNull(session.Get(traffic.Target!));
        session.Undo();
        Assert.NotNull(session.Get(gate));
        Assert.NotNull(session.Get(partner.Section));
    }

    /// <summary>The player lock of Li01_to_Iw03 goes with the pair as a fix of the gate lock file.</summary>
    [Fact]
    public void ALockedRetailGateDeleteRemovesItsLockLine()
    {
        using var workspace = SceneRetail.Workspace("m3gatelock");
        var session = workspace.Open();
        var gate = SceneRetail.Section(session, SceneRetail.Li01, "Object", "Li01_to_Iw03");
        Assert.True(GateLocks.PlayerLocked(session, "Li01_to_Iw03"));

        var plan = ScenePlans.Delete(session, [gate]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.Contains(plan.Fixes, f => f.Text.EndsWith("the locked_gate line of Li01_to_Iw03", StringComparison.Ordinal));
        session.Execute(plan.Command!);
        Assert.False(GateLocks.PlayerLocked(session, "Li01_to_Iw03"));
    }

    /// <summary><c>Li01_14</c> takes its 5 children and gives the two base warnings of decision D254.</summary>
    [Fact]
    public void AnObjectDeleteTakesItsChildren()
    {
        using var workspace = SceneRetail.Workspace("m3children");
        var session = workspace.Open();
        var parentLines = session.Systems.Files.Sum(f => session.Sections(f)
            .Count(s => s.Id.Type == "Object" && session.Get(s)!.Lines("parent").Count > 0));
        Assert.Equal(269, parentLines);

        var plan = ScenePlans.Delete(session, [SceneRetail.Section(session, SceneRetail.Li01, "Object", "Li01_14")]);
        foreach (var warning in plan.Warnings)
            output.WriteLine(warning.Message);

        Assert.Equal(5, plan.Deletes.Count(d => d.Why == DeleteReason.Cascade));
        Assert.Contains(plan.Warnings, w => w.Message.StartsWith("No object in Li01 docks with", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Message.StartsWith("No object in Li01 has 'base = ", StringComparison.Ordinal));
        session.Execute(plan.Command!);
        session.Undo();
        Assert.NotNull(session.Get(SceneRetail.Section(session, SceneRetail.Li01, "Object", "Li01_14")));
    }

    /// <summary>A light of the St02c file whose nickname repeats is read-only, so its delete is refused (review finding
    /// R4).</summary>
    [Fact]
    public void ARepeatedLightIsRefused()
    {
        using var workspace = SceneRetail.Workspace("m3light");
        var session = workspace.Open();
        var path = session.SystemPath("St02c")!;
        var repeated = session.Sections(path).Where(s => s.Id.Type == "LightSource")
            .GroupBy(s => s.Id.Key, StringComparer.OrdinalIgnoreCase).First(g => g.Count() > 1).First();

        var plan = ScenePlans.Delete(session, [repeated]);

        Assert.Contains("also has nickname", Assert.Single(plan.Refusals).Message);
    }
}

/// <summary>The retail proofs of Rename (section 18.2 of the M3 design).</summary>
public sealed class SceneRenameRetailTests(ITestOutputHelper output)
{
    static SceneRenameRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>A gate rename writes <c>ol.rename</c> in Li01 and the <c>goto</c> line in Li02, and the gate locks of
    /// <c>InitialWorld.ini</c> when they name the gate (decisions D12 and D257). The engine loads, the partner names the
    /// new nickname as its exit, and the gate keeps its index in the system.</summary>
    [Fact]
    public void AJumpGateRenameMovesThePartnerGotoAndTheLocks()
    {
        using var workspace = SceneRetail.Workspace("m3gaterename");
        var session = workspace.Open();
        var gate = SceneRetail.Section(session, SceneRetail.Li01, "Object", "Li01_to_Li02");
        var index = RetailGame.Instance.Data.Items.Systems.Get("li01")!.Objects
            .FindIndex(o => o.Nickname.Equals("Li01_to_Li02", StringComparison.OrdinalIgnoreCase));
        var locked = GateLocks.Of(session, "Li01_to_Li02").Count;
        output.WriteLine($"{locked} gate locks name Li01_to_Li02.");

        var plan = ScenePlans.Rename(session, gate, "Li01_to_Li02_m3");
        foreach (var warning in plan.Warnings)
            output.WriteLine(warning.Message);
        session.Execute(plan.Command!);
        var db = SceneRetail.SaveAndLoad(session);

        var patches = Path.Combine(workspace.ModFolder, "patches");
        Assert.Contains("ol.rename = Li01_to_Li02_m3", File.ReadAllText(Path.Combine(patches, SceneRetail.Li01)));
        Assert.Contains("Li01_to_Li02_m3", File.ReadAllText(Path.Combine(patches, SceneRetail.Li02)));
        var initialWorld = Directory.EnumerateFiles(patches, "*.ini", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Equals("InitialWorld.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(locked > 0, initialWorld != null);
        if (initialWorld != null)
            Assert.Contains($"{FLHash.CreateID("Li01_to_Li02_m3")}", File.ReadAllText(initialWorld));
        var li01 = db.Systems.Get("li01")!;
        Assert.Equal(index, li01.Objects.FindIndex(o => o.Nickname.Equals("Li01_to_Li02_m3", StringComparison.OrdinalIgnoreCase)));
        var partner = db.Systems.Get("li02")!.Objects.Single(o => o.Nickname.Equals("Li02_to_Li01", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Li01_to_Li02_m3", partner.Dock!.Exit, ignoreCase: true);
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>A rename of the zone of a retail nebula renames its <c>[Nebula]</c> section by <c>ol.rename</c>, and the
    /// nebula keeps its zone in the engine.</summary>
    [Fact]
    public void ANebulaZoneRenameRenamesItsSection()
    {
        using var workspace = SceneRetail.Workspace("m3nebularename");
        var session = workspace.Open();
        var nebula = SceneRetail.Section(session, SceneRetail.Li01, "Nebula", "Zone_Li01_Badlands_Nebula");

        session.Execute(ScenePlans.Rename(session, nebula, "Zone_Li01_Badlands_m3").Command!);
        var db = SceneRetail.SaveAndLoad(session);

        var patch = File.ReadAllText(Path.Combine(workspace.ModFolder, "patches", SceneRetail.Li01));
        Assert.Contains("ol.rename = Zone_Li01_Badlands_m3", patch);
        Assert.Contains(db.Systems.Get("li01")!.Nebulae,
            n => n.Zone?.Nickname.Equals("Zone_Li01_Badlands_m3", StringComparison.OrdinalIgnoreCase) == true);
    }
}

/// <summary>The retail baselines of the nicknames, the components and the story mentions (section 4.4 of the M3
/// design).</summary>
public sealed class SceneBaselineRetailTests(ITestOutputHelper output)
{
    static SceneBaselineRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>Across the 53 system files, no zone or object nickname collides by name or FLHash, and no nickname is both.
    /// A vanilla profile gives no error of layer 3.</summary>
    [Fact]
    public void TheRetailNamesHaveThisBaseline()
    {
        using var workspace = SceneRetail.Workspace("m3names");
        var session = workspace.Open();
        var names = session.Nicknames;

        Assert.Equal(5428, names.All.Count(n => n.IsZone));
        Assert.Equal(3430, names.All.Count(n => !n.IsZone));
        Assert.Empty(names.Collisions(zones: true));
        Assert.Empty(names.Collisions(zones: false));
        Assert.Equal(names.All.Count, names.All.Select(n => n.Hash).Distinct().Count());
        Assert.Empty(ComponentChecks.SaveErrors(session));
    }

    /// <summary>Each retail object gives its components without an exception, and the warnings of the component checks
    /// have their retail counts: 2, 18, 15 and 15.</summary>
    [Fact]
    public void EachRetailObjectGetsItsComponents()
    {
        using var workspace = SceneRetail.Workspace("m3components");
        var session = workspace.Open();
        var objects = session.Systems.Files.SelectMany(f => session.Sections(f).Where(s => s.Id.Type == "Object")).ToList();

        var warnings = new List<Diagnostic>();
        foreach (var obj in objects)
        {
            Assert.Contains(ObjectComponents.Of(session, obj), c => c.Kind == ObjectComponent.Solar);
            warnings.AddRange(ComponentChecks.Check(session, obj, onlyActiveMod: false));
        }
        foreach (var baseSection in session.Sections(session.UniversePath!).Where(s => s.Id.Type.Equals("Base", StringComparison.OrdinalIgnoreCase)))
            warnings.AddRange(ComponentChecks.Check(session, baseSection, onlyActiveMod: false));

        output.WriteLine($"{objects.Count} objects");
        Assert.Equal(3430, objects.Count);
        Assert.Equal(2, warnings.Count(w => w.Message.Contains("has no docking sphere")));
        Assert.Equal(18, warnings.Count(w => w.Message.Contains("After a launch, the game puts the player in")));
        Assert.Equal(15, warnings.Count(w => w.Message.Contains(" docks with ") && w.Message.Contains("No object in")));
        Assert.Equal(15, warnings.Count(w => w.Message.Contains("has 'base = ")));
    }

    /// <summary>Manhattan is in the story: a mission file names <c>Li01_01</c> as a whole word, and
    /// <c>KnowledgeMap.ini</c> names objects of Li01. The index meets its budget of section 13: a build under 1.5 s and
    /// a lookup under 1 ms.</summary>
    /// <remarks>The design expected the mission files of M01A. They name only longer words with <c>Li01_01</c> in them,
    /// for example <c>Li01_01_Base</c>, so the test asks for any mission file.</remarks>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void ManhattanIsInTheStory()
    {
        using var workspace = SceneRetail.Workspace("m3story");
        var session = workspace.Open();
        // The index builds once for the edit view of a session. A build in a second session is faster, because the
        // runtime compiled the code. So the gate measures the first build once and does not try again (see Timing).
        IReadOnlyList<StoryMention> manhattan = [];
        var build = Timing.Measure(() =>
        {
            session.StartStoryMentions();
            manhattan = session.StoryMentions("Li01_01");
        });
        output.WriteLine($"Index build and first lookup: {build.TotalMilliseconds:F0} ms");
        foreach (var mention in manhattan)
            output.WriteLine($"{mention.Path}: {mention.Count}");

        // The mission scripts are not in the text scan. The mission index has their references, and the reference
        // provider of the mission editor gives them (decision D668 of the M6b design).
        Assert.DoesNotContain(manhattan, m => m.Path.Contains(@"\MISSIONS\", StringComparison.OrdinalIgnoreCase) &&
                                              session.Missions.ScriptPaths().Contains(m.Path, StringComparer.OrdinalIgnoreCase));
        Assert.NotEmpty(session.Missions.References.Uses(OpenLancer.Data.Missions.Editing.MissionReferences.ObjectKinds,
            "Li01_01"));
        Assert.DoesNotContain(manhattan, m => m.Path.Equals(SceneRetail.Li01, StringComparison.OrdinalIgnoreCase));
        var mapped = session.Sections(SceneRetail.Li01).Where(s => s.Id.Type == "Object")
            .Count(s => session.StoryMentions(s.Id.Key!).Any(m => m.Path.EndsWith("KnowledgeMap.ini", StringComparison.OrdinalIgnoreCase)));
        output.WriteLine($"KnowledgeMap.ini names {mapped} objects of Li01.");
        Assert.True(mapped > 0);
        Assert.True(build.TotalMilliseconds < 1500, $"The build took {build.TotalMilliseconds:F0} ms.");

        // A lookup runs often, so its gate can try again (see TimingGates).
        var lookups = new TimingGates().WarmGate(TimeSpan.FromMilliseconds(100), 3, () =>
        {
            for (var i = 0; i < 100; i++)
                session.StoryMentions("Li01_14");
        });
        output.WriteLine($"Lookup: {lookups.TotalMilliseconds / 100:F3} ms (the best of 3 runs of 100 lookups)");
        Assert.True(lookups.TotalMilliseconds / 100 < 1, $"A lookup took {lookups.TotalMilliseconds / 100:F3} ms.");
    }
}

/// <summary>The live scene and the structure step (section 14 of the M3 design), on the headless Li01 and Bw05.</summary>
public sealed class SceneStructureRetailTests(ITestOutputHelper output)
{
    static SceneStructureRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>A create adds an item with its world object. A rename keeps the item and loads no model. A delete removes
    /// the item and its world object. Each undo and redo gives the scene of W.</summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void ACreateARenameAndADeleteChangeTheWorldAndUndoGivesItBack()
    {
        using var workspace = SceneRetail.Workspace("m3scene");
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var objects = scene.System.Objects.Count;
        var archetype = ScenePlans.Archetypes(session, SceneTemplate.Station).First().Nickname;

        var create = ScenePlans.Create(session, "Li01", SceneTemplate.Station, archetype, new Vector3(1000, 0, 1000), null);
        session.Execute(create.Command!);
        var watch = Stopwatch.StartNew();
        scene.Applier.Run();
        watch.Stop();
        output.WriteLine($"Create in Li01: {watch.Elapsed.TotalMilliseconds:F1} ms");
        var station = scene.Object("li01_station_1");
        Assert.Equal(objects + 1, scene.System.Objects.Count);
        var gameObject = scene.Objects[station];
        Assert.Contains(gameObject, scene.World.Objects);
        Assert.Equal(new Vector3(1000, 0, 1000), gameObject.Transform.Position);
        Assert.Equal(create.Adds[0], scene.SectionOf(station));

        var model = gameObject.Model;
        session.Execute(ScenePlans.Rename(session, create.Adds[0], "li01_station_m3").Command!);
        scene.Applier.Run();
        Assert.Same(station, scene.Object("li01_station_m3"));
        Assert.Same(model, scene.Objects[station].Model);
        Assert.Equal("li01_station_m3", scene.Objects[station].Nickname);
        session.Undo();
        scene.Applier.Run();
        Assert.Same(station, scene.Object("li01_station_1"));

        var delete = ScenePlans.Delete(session, [scene.SectionOf(station)]);
        session.Execute(delete.Command!);
        // A delete runs once, so its measure runs once (see Timing).
        var deleteTime = Timing.Measure(scene.Applier.Run);
        output.WriteLine($"Delete in Li01: {deleteTime.TotalMilliseconds:F1} ms");
        Assert.Equal(objects, scene.System.Objects.Count);
        Assert.DoesNotContain(gameObject, scene.World.Objects);
        Assert.False(scene.Objects.ContainsKey(station));
        session.Undo();
        scene.Applier.Run();
        Assert.Equal(objects + 1, scene.System.Objects.Count);
        session.Undo();
        scene.Applier.Run();
        Assert.Equal(objects, scene.System.Objects.Count);
        Assert.Equal(0, scene.Applier.CatchUp());
        Assert.True(deleteTime.TotalMilliseconds < 16, $"The delete took {deleteTime.TotalMilliseconds:F1} ms.");
    }

    /// <summary>An undo of the delete of a zone or a light gives the same item back, with its section (decisions D248 and
    /// D249).</summary>
    [Fact]
    public void AnUndoOfADeleteGivesTheSameZoneAndLightBack()
    {
        using var workspace = SceneRetail.Workspace("m3keep");
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var light = scene.System.LightSources.First();
        var zone = scene.System.Zones.First(z => !scene.System.Nebulae.Any(n => n.Zone == z) &&
                                                 !scene.System.AsteroidFields.Any(f => f.Zone == z));

        session.Execute(EditCommand.DeleteSection(scene.SectionOf(light)));
        session.Execute(EditCommand.DeleteSection(scene.SectionOf(zone)));
        scene.Applier.Run();
        Assert.DoesNotContain(light, scene.System.LightSources);
        Assert.DoesNotContain(zone, scene.System.Zones);
        Assert.False(scene.System.ZoneDict.ContainsKey(zone.Nickname));

        session.Undo();
        session.Undo();
        scene.Applier.Run();
        Assert.Contains(light, scene.System.LightSources);
        Assert.Contains(zone, scene.System.Zones);
        Assert.Same(zone, scene.System.ZoneDict[zone.Nickname]);
        Assert.NotNull(scene.Applier.SectionOf(light));
        Assert.NotNull(scene.Applier.SectionOf(zone));
        Assert.Equal(0, scene.Applier.CatchUp());
    }

    /// <summary>A zone delete removes its nebula, and the undo puts back the same nebula item (decision D248, review
    /// finding R3). A delete of the nebula alone keeps its zone (decision D237).</summary>
    [Fact]
    public void AZoneDeleteRemovesItsNebulaAndAnUndoPutsBackTheSameItem()
    {
        using var workspace = SceneRetail.Workspace("m3volume");
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var nebula = scene.System.Nebulae.Single(n => n.Zone!.Nickname == "Zone_Li01_Badlands_Nebula");
        var zone = scene.SectionOf(nebula.Zone!);
        var loads = scene.Host.VolumeLoads;

        session.Execute(ScenePlans.Delete(session, [zone]).Command!);
        scene.Applier.Run();
        Assert.DoesNotContain(nebula, scene.System.Nebulae);
        Assert.DoesNotContain(scene.System.Zones, z => z.Nickname == "Zone_Li01_Badlands_Nebula");
        Assert.True(scene.Host.VolumeLoads > loads);

        session.Undo();
        scene.Applier.Run();
        Assert.Contains(nebula, scene.System.Nebulae);
        Assert.Same(scene.System.ZoneDict["Zone_Li01_Badlands_Nebula"], nebula.Zone);
        Assert.Empty(scene.Applier.Problems(scene.SectionOf(nebula)));

        session.Execute(ScenePlans.Delete(session, [scene.SectionOf(nebula)]).Command!);
        scene.Applier.Run();
        Assert.DoesNotContain(nebula, scene.System.Nebulae);
        Assert.Contains(scene.System.Zones, z => z.Nickname == "Zone_Li01_Badlands_Nebula");
        session.Undo();
        scene.Applier.Run();
        Assert.Contains(nebula, scene.System.Nebulae);
        Assert.Equal(0, scene.Applier.CatchUp());
    }

    /// <summary>A field goes with its world object, and the undo of its delete makes a new world object for the same
    /// field.</summary>
    [Fact]
    public void AFieldDeleteRemovesItsWorldObject()
    {
        using var workspace = SceneRetail.Workspace("m3fieldscene");
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Li01");
        scene.Applier.CatchUp();
        var field = scene.System.AsteroidFields[0];
        bool HasObject() => scene.World.Objects.Any(o => o.TryGetComponent<AsteroidFieldComponent>(out var c) &&
                                                         ReferenceEquals(c.Field, field));
        Assert.True(HasObject());

        session.Execute(ScenePlans.Delete(session, [scene.SectionOf(field)]).Command!);
        scene.Applier.Run();
        Assert.False(HasObject());
        session.Undo();
        scene.Applier.Run();
        Assert.True(HasObject());
        Assert.Contains(field, scene.System.AsteroidFields);
    }

    /// <summary>
    /// The asset browser of milestone M23: a spawn and a group placement add their world objects in one flush, and one
    /// Undo removes them. A new static prop archetype gives no item before the reload, so the applier waits for the reload
    /// (decisions D248 and D282). The scene of the data that loads again after the save has the object.
    /// </summary>
    [Fact]
    public void ASpawnAndAPlacementAddTheirItemsAndANewPropWaitsForTheReload()
    {
        using var workspace = SceneRetail.Workspace("m23scene");
        var session = workspace.Open();
        using var scene = new HeadlessScene(session, "Bw05");
        scene.Applier.CatchUp();
        var objects = scene.System.Objects.Count;
        Assert.False(scene.Applier.WaitsForReload);

        var planet = session.Assets.Rows("solars/planet")[0].Nickname;
        var spawn = ScenePlans.Create(session, "Bw05", SceneTemplate.Planet, planet, new Vector3(30_000, 0, 30_000), null);
        session.Execute(spawn.Command!);
        scene.Applier.Run();
        Assert.Equal(objects + 1, scene.System.Objects.Count);
        Assert.Equal(new Vector3(30_000, 0, 30_000), scene.Objects[scene.Object(spawn.Adds[0].Id.Key!)].Transform.Position);

        var li01 = new[] { "Li01_14_large_transport_1", "Li01_large_transport_2", "Li01_large_transport_3" }
            .Select(n => SceneRetail.Section(session, SceneRetail.Li01, "Object", n)).ToList();
        var group = PropGroups.Write(session, PropGroups.Draft(session, li01), "trio", "Trio");
        var place = PropGroups.Place(session, "Bw05", group, new Vector3(-30_000, 0, 30_000), null);
        session.Execute(place.Command!);
        scene.Applier.Run();
        Assert.Equal(objects + 4, scene.System.Objects.Count);
        Assert.All(place.Adds, a => Assert.Contains(scene.Object(a.Id.Key!), scene.Objects.Keys));
        session.Undo();
        scene.Applier.Run();
        Assert.Equal(objects + 1, scene.System.Objects.Count);

        var ship = session.Lists.List("ship")!.Find("br_gunboat")!;
        var prop = ScenePlans.CreateProp(session, "Bw05", ModelSource.Of(session, ship.Section!)!, ship.Value,
            new Vector3(0, 0, 40_000), null);
        session.Execute(prop.Command!);
        scene.Applier.Run();
        var nickname = prop.SelectAfter!.Id.Key!;
        Assert.DoesNotContain(scene.System.Objects, o => o.Nickname == nickname);
        Assert.True(scene.Applier.WaitsForReload);
        Assert.Contains(scene.Applier.Problems(prop.SelectAfter), p => p.Message.EndsWith(
            "The Scene shows this object after the save and the game data reload.", StringComparison.Ordinal));

        Assert.True(session.Save().Saved);
        var fs = session.EditView.CreateFileSystem();
        var resources = new ServerResourceManager(null, fs);
        // The resource manager is not disposable, but its physics shape pool is. A Debug build fails an assert when the
        // collector finds the pool not cleared.
        using var shapes = resources.ConvexCollection;
        var data = new GameDataManager(new GameItemDb(fs), resources);
        data.LoadData(null);
        using var reloaded = new HeadlessScene(session, "Bw05", data, resources);
        reloaded.Applier.CatchUp();
        Assert.Contains(reloaded.System.Objects, o => o.Nickname == nickname);
        Assert.False(reloaded.Applier.WaitsForReload);
        Assert.Empty(reloaded.Applier.Problems(prop.SelectAfter));
        // An undo of the prop takes back the object and its archetype in one entry.
        session.Undo();
        scene.Applier.Run();
        Assert.False(scene.Applier.WaitsForReload);
    }
}
