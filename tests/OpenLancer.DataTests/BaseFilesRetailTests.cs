using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema.Universe.Rooms;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The base files, the room files and the room lines on the retail data (retail tests 4, 5 and 6 of the M8b design, and
/// section H.2 of the bases design): the counts of the bases, rooms and bar data, the role <c>room</c> holds the room
/// files that the engine reads, the <c>scene</c> rule reads the value that the engine reads in each of the 666 retail
/// lines, and a new cutscene that a room line names plays in the room after the save.
/// </summary>
public sealed class BaseFilesRetailTests(ITestOutputHelper output)
{
    private static readonly ThnRoomRole[] RoomKeys =
        [ThnRoomRole.Set, ThnRoomRole.Scene, ThnRoomRole.Cart, ThnRoomRole.Start, ThnRoomRole.Landing, ThnRoomRole.Launching];

    static BaseFilesRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The role holds the same files as the rooms of the engine data and the room chain of the reference scan:
    /// 446 room files of 461 [Room] sections of 197 bases. The first use takes less than 200 ms, and it opens no base
    /// file as a document of the session.</summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void TheRoomRoleHoldsTheRoomFilesOfTheEngine()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m8bproof");
        var session = workspace.Open();
        _ = session.DataFiles.Paths("solar");

        // The gate measures the first use once (see Timing).
        IReadOnlyList<string> rooms = [];
        var first = Timing.Measure(() => rooms = session.DataFiles.Paths("room"));

        var view = FileSystem.FromPath(baseFolder);
        var items = EngineDataLoader.Load(view).RequireComplete();
        var engineRooms = items.Bases.SelectMany(b => b.Rooms).ToList();
        var engineFiles = engineRooms.Select(r => ThnReferences.PathOf(r.SourceFile)!).ToHashSet(VfsPath.Comparer);
        var references = ThnReferences.Scan(view);
        var chain = references.Paths.SelectMany(references.Rooms).Where(u => RoomKeys.Contains(u.Role))
            .Select(u => u.File).ToHashSet(VfsPath.Comparer);
        output.WriteLine($"{rooms.Count} room files in the role, first use {first.TotalMilliseconds:0.0} ms. The engine " +
                         $"has {items.Bases.Count()} bases with {engineRooms.Count} rooms in {engineFiles.Count} files. " +
                         $"The room keys of the reference scan name scripts in {chain.Count} of them.");

        Assert.Equal(446, rooms.Count);
        Assert.Equal(197, items.Bases.Count());
        Assert.Equal(461, engineRooms.Count);
        Assert.True(engineFiles.SetEquals(rooms));
        Assert.True(chain.IsSubsetOf(rooms));
        Assert.Contains(rooms, p => p.EndsWith(@"\li01_01_bar.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rooms, p => p.EndsWith(@"\li01_01_cityscape.ini", StringComparison.OrdinalIgnoreCase));
        Assert.All(items.Bases, b => Assert.Null(session.OpenedDocument(ThnReferences.PathOf(b.SourceFile)!)));
        Assert.True(first < TimeSpan.FromMilliseconds(200), $"The first use took {first.TotalMilliseconds:0.0} ms.");
    }

    /// <summary>
    /// The counts of section B of the bases design: 197 <c>[Base]</c> before 53 <c>[system]</c>, each with its own base
    /// file, 461 <c>[Room]</c> that name 446 room files, one room file with 16 bases, 194 <c>[MBase]</c> with 1642
    /// characters, 69 of them with <c>base_appr</c> and without body and head. The view of each base reads its parts:
    /// 161 stations, 34 planets and 2 bases that start in the bar. 178 bases have an object with the base line and the
    /// name of the base, and 187 bases have the 14694 market lines.
    /// </summary>
    [Fact]
    public void TheBasesOfTheRetailDataHaveTheCountsOfTheDesign()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "c4bases");
        var session = workspace.Open();
        var bases = session.Bases;
        var universe = session.Sections(session.UniversePath!).Select(s => s.Id.Type.ToLowerInvariant()).ToList();

        Assert.Equal((197, 53), (universe.Count(t => t == "base"), universe.Count(t => t == "system")));
        Assert.True(universe.LastIndexOf("base") < universe.IndexOf("system"));
        Assert.Equal((197, 197), (bases.Nicknames.Count, bases.Files.Count));
        Assert.Equal(461, bases.Nicknames.Sum(b => bases.RoomsOf(b).Count));
        Assert.Equal(446, bases.RoomFiles.Count);
        var shared = Assert.Single(bases.RoomFiles, f => bases.BasesOfRoomFile(f).Count > 1);
        Assert.EndsWith(@"\li05_01_deck.ini", shared, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(16, bases.BasesOfRoomFile(shared).Count);
        Assert.Equal(197, session.DataFiles.Paths("base_files").Count);
        Assert.Single(session.DataFiles.Paths("mbases"));
        var mbases = session.Sections(bases.MBasesPath!);
        Assert.Equal(194, mbases.Count(s => s.Id.Type.Equals("MBase", StringComparison.OrdinalIgnoreCase)));
        var characters = mbases.Where(s => s.Id.Type.Equals("GF_NPC", StringComparison.OrdinalIgnoreCase))
            .Select(s => session.Get(s)!).ToList();
        Assert.Equal(69, characters.Count(c => c.Lines("base_appr").Count > 0 && c.Lines("body").Count == 0 &&
                                               c.Lines("head").Count == 0));

        var views = bases.Nicknames.Select(n => session.Base(n)!).ToList();
        output.WriteLine(string.Join(", ", views.GroupBy(v => v.Kind).Select(g => $"{g.Key} {g.Count()}")));
        Assert.Equal((161, 34, 2), (views.Count(v => v.Kind == BaseKind.Station), views.Count(v => v.Kind == BaseKind.Planet),
            views.Count(v => v.Kind == BaseKind.Other)));
        Assert.Equal(461, views.Sum(v => v.Rooms.Count));
        Assert.Equal(1642, views.Sum(v => v.Characters.Count));
        Assert.Equal(194, views.Count(v => v.MBase != null));
        Assert.Equal(178, views.Count(v => v.NamedObject != null));
        Assert.Equal((187, 14694), (views.Count(v => v.Market.Count > 0), views.Sum(v => v.Market.Count)));
        Assert.All(views.SelectMany(v => v.Market), m => Assert.False(m.CanChange));
        Assert.All(views, v => Assert.Equal(ValueOrigin.Base, v.Origin));
        var manhattan = views.Single(v => v.Nickname == "Li01_01_Base");
        Assert.Equal(["Cityscape", "Bar", "Trader", "ShipDealer", "Equipment"], manhattan.Rooms.Select(r => r.Nickname));
        Assert.Contains(BaseDealer.ShipDealer, manhattan.Rooms.Single(r => r.Nickname == "ShipDealer").Dealers);
    }

    /// <summary>
    /// For each of the 666 retail <c>scene</c> lines, the value at the place of the rule is the script that
    /// <c>RoomInfo</c> reads. The retail lines have three forms: 322 <c>all, ambient, script</c>, 312
    /// <c>ambient, script</c> and 32 <c>all, ambient, script, TRAFFIC_PRIORITY</c>.
    /// </summary>
    [Fact]
    public void TheSceneRuleReadsTheValueThatRoomInfoReadsInEachRetailLine()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8bproof");
        var session = workspace.Open();
        var rule = session.Lists.RuleData.Rules("room", "Room_Info", "scene").Single();
        var (lines, all, plain, priority) = (0, 0, 0, 0);
        foreach (var path in session.DataFiles.Paths("room"))
        {
            foreach (var section in session.Sections(path).Where(s => s.Id.Type.Equals("Room_Info", StringComparison.OrdinalIgnoreCase)))
            {
                var info = session.Parse<RoomInfo>(section, RoomInfo.TryParse);
                Assert.NotNull(info);
                var scenes = session.Get(section)!.Lines("scene");
                Assert.Equal(info.SceneScripts.Count, scenes.Count);
                for (var i = 0; i < scenes.Count; i++)
                {
                    var entry = new LibreLancer.Data.Ini.Entry(new LibreLancer.Data.Ini.Section("Room_Info"), "scene");
                    foreach (var value in scenes[i].Values)
                        entry.Add(value);
                    var at = rule.IndexIn(entry);
                    Assert.Equal(info.SceneScripts[i].Path, entry[at].ToString());
                    lines++;
                    if (info.SceneScripts[i].TrafficPriority)
                        priority++;
                    else if (info.SceneScripts[i].AmbientAll)
                        all++;
                    else
                        plain++;
                }
            }
        }

        output.WriteLine($"{lines} scene lines: {all} 'all, ambient, script', {plain} 'ambient, script', {priority} with " +
                         "TRAFFIC_PRIORITY.");
        Assert.Equal((666, 322, 312, 32), (lines, all, plain, priority));
    }

    /// <summary>
    /// End to end on a temporary mod: New cutscene, then the start script of the Manhattan bar names it. The save
    /// writes the whole file and the room patch. The engine loads the merged view, the bar of Li01_01_Base resolves its
    /// start script to the new file, and the new file builds a script. The bar is then a context of the cutscene.
    /// </summary>
    [Fact]
    public void ANewCutsceneThatARoomLineNamesPlaysInTheRoomAfterTheSave()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8bproof");
        var session = workspace.Open();
        var path = session.NewCutscenePath("bar_shot")!;
        Assert.Null(ThnEdits.NewCutsceneRefusal(session, path));
        var bar = session.DataFiles.Paths("room").Single(p => p.EndsWith(@"\li01_01_bar.ini", StringComparison.OrdinalIgnoreCase));
        var placement = session.Sections(bar).Single(s => s.Id.Type.Equals("CharacterPlacement", StringComparison.OrdinalIgnoreCase));

        session.Execute(ThnEdits.NewCutscene(path, ThnTemplates.NewScript()));
        var value = session.Lists.List("cutscene")!.Find(ContentPaths.DataValue(path)!)!;
        Assert.True(value.Unsaved);
        session.Execute(EditCommand.SetLinesFromText(placement, "start_script", [value.Value]));
        Assert.Empty(session.Lists.Problems(placement));
        var result = session.Save();

        Assert.True(result.Saved, string.Join("\n", result.Problems));
        var whole = Path.Combine(workspace.ModFolder, "files", path);
        Assert.Contains(whole, result.Written, StringComparer.OrdinalIgnoreCase);
        var patch = result.Written.Single(f => f.EndsWith("li01_01_bar.ini", StringComparison.OrdinalIgnoreCase));
        var patchText = File.ReadAllText(patch);
        output.WriteLine(patchText);
        Assert.Contains("[CharacterPlacement]", patchText);
        Assert.Contains(@"start_script = MODS\m8bproof\CUTSCENES\bar_shot.thn", patchText);

        var view = session.EditView.CreateFileSystem();
        var items = EngineDataLoader.Load(view).RequireComplete();
        var room = items.Bases.Get("Li01_01_Base")!.Rooms.Get("Bar")!;
        Assert.Equal(VfsPath.Normalize(path), VfsPath.Normalize(room.StartScript!.DataPath!), VfsPath.Comparer);
        var script = new ThnScript(view.ReadAllBytes(path), items.ThornReadCallback, path);
        Assert.Equal(ThnTemplates.NewScriptDuration, script.Duration);
        var references = ThnReferences.Scan(view);
        var choices = PreviewContext.Choices(path, references, items,
            new SpaceScenes(view, references, SpaceFacts.From(items)));
        Assert.Contains(choices, c => c is { Kind: PreviewContextKind.Room, Base: "Li01_01_Base", Room: "Bar" });
    }
}
