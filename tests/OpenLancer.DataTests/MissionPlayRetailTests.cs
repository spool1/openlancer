using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Resources;
using LibreLancer.Server;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Data.Mods;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proofs of a mission play from a start save (sections 5.6 and 12.3 of the M6c design): a headless server loads
/// the start save of each checkpoint and each mission start of the 14 story scripts, with the mission observer of the
/// play (fork commit OL-0070). The trigger of the start reports Started or Ran. Three approximate starts load with their
/// test layer, and the triggers that wait at the start report Started or Ran. The tests count and compare triggers. They
/// hold no retail text.
/// </summary>
public sealed class MissionPlayRetailTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The limit from the start of the server to the changes of the observer.</summary>
    private static readonly TimeSpan ChangeLimit = TimeSpan.FromSeconds(20);

    private readonly string folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));

    static MissionPlayRetailTests() => AppHandler.ConsoleInit(silent: true);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }

    private static IReadOnlyList<MissionAnalysis> Scripts()
    {
        var run = RetailMissions.Run();
        return run.Scripts.Where(s => s.Slot != null).Select(s => run.Of(s.Path)!).ToList();
    }

    [Fact]
    public void EachCheckpointStartLoadsAndItsRestoreTriggerReports()
    {
        var data = RetailGame.Instance.Data;
        var count = 0;
        foreach (var analysis in Scripts())
        {
            foreach (var restore in DryRun.Checkpoints(analysis.Index))
            {
                var plan = StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.At(restore.Nickname));
                var changes = Load(data, plan, [restore.Nickname]);
                Assert.True(changes.Contains(restore.Nickname),
                    $"{analysis.Script.Slot!.Slot}: the restore trigger {count} gave no change.");
                count++;
            }
        }

        Assert.Equal(34, count);
    }

    [Fact]
    public void EachMissionStartLoadsAndItsStartTriggersReport()
    {
        var data = RetailGame.Instance.Data;
        var count = 0;
        foreach (var analysis in Scripts())
        {
            var plan = StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.MissionStart);
            var starts = analysis.Index.StartTriggers.Select(t => t.Nickname).ToList();
            var changes = Load(data, plan, starts);
            foreach (var start in starts)
                Assert.True(changes.Contains(start), $"{analysis.Script.Slot!.Slot}: a start trigger gave no change.");
            count++;
        }

        Assert.Equal(14, count);
    }

    /// <summary>One approximate start in M02, M04 and M07: the first trigger in file order whose plan can start and has
    /// a setup trigger of the world. One test layer holds the three patches, so the data loads once.</summary>
    [Fact]
    public void ThreeApproximateStartsLoadAndTheirActiveSetsReport()
    {
        var plans = new List<StartPlan>();
        foreach (var slot in new[] { "M02", "M04", "M07" })
        {
            var analysis = RetailMissions.Slot(slot);
            var index = analysis.Index;
            var exact = index.StartTriggers.Concat(DryRun.Checkpoints(index)).ToHashSet();
            plans.Add(index.Triggers.Where(t => !t.IsDuplicate && index.Reached.Contains(t) && !exact.Contains(t))
                .Select(t => StartPlan.Make(index, analysis.Script.Slot, MissionStartPoint.At(t.Nickname)))
                .First(p => p.CanStart && p.Setup.Count == 2));
        }

        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m6cplay");
        var layer = Path.Combine(folder, "start", "layer");
        TestLayer.Write(layer, plans[0]);
        foreach (var plan in plans.Skip(1))
        {
            var patch = Path.Combine([layer, Mod.PatchesFolder, .. plan.Script.Split('\\')]);
            Directory.CreateDirectory(Path.GetDirectoryName(patch)!);
            File.WriteAllText(patch, plan.SetupPatch);
        }
        var files = PlayView.Build(ModProfile.Load(workspace.ProfilePath), workspace.ModId, layer).CreateFileSystem();
        // The layer changes no shape, so the data shares the convex shapes of the retail data, which live as long as the
        // test host. A world of a stopped server can still load its shapes in the background. Shapes of its own would
        // then fill a buffer pool after their dispose, and in a Debug build the pool fails an assert when the collector
        // finds it, and the test host ends.
        var data = new GameDataManager(new GameItemDb(files),
            new ServerResourceManager(RetailGame.Instance.Data.Resources.ConvexCollection, files));
        data.LoadData(null);

        foreach (var plan in plans)
        {
            var changes = Load(data, plan, [plan.Setup[0].Nickname, .. plan.ActiveSet]);
            output.WriteLine($"{plan.Slot!.Slot}: {plan.ActiveSet.Count} triggers wait, {plan.Path.Count} on the path, " +
                             $"{changes.Count} changes.");
            Assert.True(changes.Contains(plan.Setup[0].Nickname), $"{plan.Slot.Slot}: the setup trigger gave no change.");
            foreach (var trigger in plan.ActiveSet)
                Assert.True(changes.Contains(trigger), $"{plan.Slot.Slot}: a trigger of the active set gave no change.");
        }
    }

    /// <summary>
    /// The first approximate start whose path adds an ambient script: the start save gives the id of the room of the
    /// base, so the server finds the room and loads the mission (review fix of M6c-2). The id of the room name alone
    /// stopped the load on the server thread. A start save whose room the base does not have refuses with its reason.
    /// </summary>
    [Fact]
    public void AStartWithAnAmbientScriptLoads()
    {
        var data = RetailGame.Instance.Data;
        var plan = Scripts().SelectMany(a => a.Index.Triggers
                .Where(t => !t.IsDuplicate && a.Index.Reached.Contains(t))
                .Select(t => StartPlan.Make(a.Index, a.Script.Slot, MissionStartPoint.At(t.Nickname))))
            .First(p => p.CanStart && p.State.Ambients.Count > 0);
        output.WriteLine($"{plan.Slot!.Slot}: {plan.State.Ambients.Count} ambient scripts, {plan.Path.Count} on the path.");

        Load(data, plan, []);

        var (script, room, dock) = plan.State.Ambients[0];
        var file = Path.Combine(folder, "start.fl");
        var right = FLHash.CreateLocationID(dock, room).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var wrong = FLHash.CreateLocationID(dock, "ol_no_room").ToString(System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(file, File.ReadAllText(file).Replace($", {right}, ", $", {wrong}, "));
        var known = data.Items.Bases.Get(dock)!;
        Assert.Equal($"The start save names the ambient script '{script}' in a room of base '{known.Nickname}' that the game " +
                     "data does not have. The game cannot load the save.",
            PlaySave.Check(data.Items, new PlayStart.FromSave(file, null), null));
    }

    /// <summary>A start save with the ship package of the Play menu: the save of the play has the story, mission and
    /// trigger parts of the start save, and the ship of the package (section 12.1 of the M6c design).</summary>
    [Fact]
    public void AStartSaveWithAShipPackageKeepsItsMissionParts()
    {
        const string package = "le_package";
        var data = RetailGame.Instance.Data;
        var analysis = RetailMissions.Slot("M02");
        var restore = DryRun.Checkpoints(analysis.Index).First();
        var plan = StartPlan.Make(analysis.Index, analysis.Script.Slot, MissionStartPoint.At(restore.Nickname));
        var file = Path.Combine(Directory.CreateDirectory(folder).FullName, "start.fl");
        File.WriteAllText(file, StartSaveText.OpenLancer(plan, Place(data.Items, plan.Place), DateTime.Now));
        var start = new PlayStart.FromSave(file, null);
        Assert.Null(PlaySave.Check(data.Items, start, package));
        using var headless = new HeadlessServer(data);

        var save = PlaySave.Create(headless.Server, start, package);

        Assert.Equal(plan.Slot!.Controller, save.StoryInfo!.Mission);
        Assert.Equal(plan.Stage, save.StoryInfo.MissionNum);
        Assert.NotNull(save.MissionState);
        Assert.Equal([FLHash.CreateID(restore.Nickname)], save.TriggerSave.Select(t => (uint)t.Trigger));
        Assert.Equal(new HashValue(data.Items.GetShipPackage(FLHash.CreateID(package))!.Ship.Nickname),
            save.Player!.ShipArchetype);
    }

    /// <summary>The check of the start place of the Play start page (UI rule 2, review fix of M6c-2): a system or a base
    /// that the game data does not have, and the Scene camera without an open system, give their reasons. A known place
    /// gives none.</summary>
    [Fact]
    public void TheCheckOfTheStartPlaceGivesTheReasonOfEachPlace()
    {
        var items = RetailGame.Instance.Data.Items;
        var home = HomeBase(items);
        StartPlace Base(string dock) => new(StartPlaceKind.Base, null, dock, Vector3.Zero, Quaternion.Identity, "");
        StartPlace Space(string system) => new(StartPlaceKind.Space, system, null, Vector3.Zero, Quaternion.Identity, "");

        Assert.Null(Editor.Missions.MissionPlay.PlaceProblem(items, false, Base(home.Base!)));
        Assert.Null(Editor.Missions.MissionPlay.PlaceProblem(items, false, Space(home.System)));
        Assert.Null(Editor.Missions.MissionPlay.PlaceProblem(items, true, StartPlace.Camera("")));
        Assert.Equal("The start place names the base 'ol_no_base', which is not in the game data. Pick another base on " +
                     "the Play start page.", Editor.Missions.MissionPlay.PlaceProblem(items, true, Base("ol_no_base")));
        Assert.Equal("The start place names the system 'ol_no_system', which is not in the game data. Pick a base or the " +
                     "Scene camera on the Play start page.",
            Editor.Missions.MissionPlay.PlaceProblem(items, true, Space("ol_no_system")));
        Assert.Equal(Editor.Missions.MissionPlay.NoCameraSystem,
            Editor.Missions.MissionPlay.PlaceProblem(items, false, StartPlace.Camera("")));
        Assert.Equal("The game data of the profile did not load.",
            Editor.Missions.MissionPlay.PlaceProblem(null, true, Base(home.Base!)));
    }

    /// <summary>The problem of a place choice that holds for each trigger (the warning chip of the play start, note of
    /// 2026-10-27 in section 8.1 of the M6c design): a base that the game data does not have, and the Scene camera
    /// without an open system. The choice "Suggested" gives each trigger its own place, so it has no such problem while
    /// the game data loaded.</summary>
    [Fact]
    public void ThePlaceChoiceOfAPlayStartGivesTheProblemOfEachTrigger()
    {
        var items = RetailGame.Instance.Data.Items;
        var home = HomeBase(items);
        static Editor.PlayStartSetting Choice(string place) => new("t_any", place);

        Assert.Null(Editor.Missions.MissionPlay.ChoiceProblem(items, false, Choice($"base:{home.Base}")));
        Assert.Null(Editor.Missions.MissionPlay.ChoiceProblem(items, true, Choice("camera")));
        Assert.Null(Editor.Missions.MissionPlay.ChoiceProblem(items, false, Choice("")));
        Assert.Equal("The start place names the base 'ol_no_base', which is not in the game data. Pick another base on " +
                     "the Play start page.", Editor.Missions.MissionPlay.ChoiceProblem(items, true, Choice("base:ol_no_base")));
        Assert.Equal(Editor.Missions.MissionPlay.NoCameraSystem,
            Editor.Missions.MissionPlay.ChoiceProblem(items, false, Choice("camera")));
    }

    [Fact]
    public void AStartSaveOfAnUnknownMissionRefusesWithItsReason()
    {
        var data = RetailGame.Instance.Data;
        var file = Path.Combine(Directory.CreateDirectory(folder).FullName, "start.fl");
        var home = HomeBase(data.Items);
        File.WriteAllText(file, $"[Player]\r\nsystem = {home.System}\r\nbase = {home.Base}\r\n\r\n[StoryInfo]\r\nMission = ol_mission\r\n");

        var problem = PlaySave.Check(data.Items, new PlayStart.FromSave(file, null), null);

        Assert.Equal("The start save names the mission 'ol_mission', which the storyline of the game data does not have.",
            problem);
    }

    /// <summary>
    /// Writes the start save of a plan, checks it, and loads it on a new headless server with a mission observer, as
    /// the play host does. Gives the names of the triggers that reported a change, when each wanted trigger reported a
    /// change or after <see cref="ChangeLimit"/>.
    /// </summary>
    private HashSet<string> Load(GameDataManager data, StartPlan plan, IReadOnlyList<string> wanted)
    {
        Assert.True(plan.CanStart, plan.Reason);
        var file = Path.Combine(Directory.CreateDirectory(folder).FullName, "start.fl");
        File.WriteAllText(file, StartSaveText.OpenLancer(plan, Place(data.Items, plan.Place), DateTime.Now));
        var start = new PlayStart.FromSave(file, null);
        Assert.Null(PlaySave.Check(data.Items, start, null));
        using var headless = new HeadlessServer(data);
        var observer = new MissionObserver();
        headless.Server.MissionObserver = observer;
        headless.Start(PlaySave.Create(headless.Server, start, null));

        var changes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaded = false;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < ChangeLimit)
        {
            foreach (var line in observer.Take())
            {
                loaded |= line.Reset && line.Mission.Equals(plan.Slot!.Controller, StringComparison.OrdinalIgnoreCase);
                changes.UnionWith(line.Changes.Select(c => c.Trigger));
            }
            if (loaded && wanted.All(changes.Contains))
                break;
            Thread.Sleep(20);
        }

        Assert.True(loaded, $"{plan.Slot!.Slot}: the server did not load the mission.");
        return changes;
    }

    /// <summary>The place of the start save: the place of the plan, or the home base of a new character for the Scene
    /// camera, which a headless test does not have.</summary>
    private static StartSavePlace Place(GameItemDb items, StartPlace place) => place.Kind switch
    {
        StartPlaceKind.Space => new StartSavePlace(place.System!, null, place.Position, place.Orientation),
        StartPlaceKind.Base when items.Bases.Get(place.Base!) is { System: { } system } dock =>
            StartSavePlace.AtBase(system, dock.Nickname),
        _ => HomeBase(items),
    };

    private static StartSavePlace HomeBase(GameItemDb items)
    {
        var dock = items.Bases.Get(items.Ini.NewCharDB.Factions[0].Base)!;
        return StartSavePlace.AtBase(dock.System!, dock.Nickname);
    }
}
