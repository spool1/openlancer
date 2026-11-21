using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Tests;

/// <summary>
/// A made-up universe for the route table tests: <c>freelancer.ini</c>, the solar archetypes, <c>universe.ini</c>, one
/// file for each system and the gate locks of <c>initialworld.ini</c>. A connection is an <c>[Object]</c> with a
/// <c>goto</c> line, named <c>&lt;from&gt;_to_&lt;to&gt;</c>, with <c>_2</c>, <c>_3</c> and so on for a second
/// connection between the same systems.
/// </summary>
internal sealed class RouteWorld
{
    public const string FreelancerIniPath = @"EXE\freelancer.ini";
    public const string SolarPath = @"DATA\SOLAR\solararch.ini";
    public const string UniversePath = @"DATA\UNIVERSE\universe.ini";
    public const string LocksPath = @"DATA\initialworld.ini";

    public static readonly string FreelancerIni = $"""
        [Freelancer]
        data path = ..\data

        {TestWorkspace.ResourcesSection}

        [Data]
        solar = solar\solararch.ini
        universe = universe\universe.ini
        """;

    /// <summary>One archetype of each jump type, and a station.</summary>
    public const string Solar = """
        [Solar]
        nickname = jumpgate
        type = JUMP_GATE

        [Solar]
        nickname = jumphole
        type = JUMP_HOLE

        [Solar]
        nickname = airlock
        type = AIRLOCK_GATE

        [Solar]
        nickname = station
        type = STATION
        """;

    private readonly List<string> systems = [];
    private readonly Dictionary<string, List<string>> objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> locks = [];

    /// <summary>The install path of the file of a system. A long nickname gets a short file name, so that the path
    /// stays short on disk.</summary>
    public static string SystemPath(string nickname) =>
        $@"DATA\UNIVERSE\SYSTEMS\{FileName(nickname)}\{FileName(nickname)}.ini";

    private static string FileName(string nickname) =>
        nickname.Length <= 16 ? nickname : $"H{FLHash.CreateID(nickname):X8}";

    /// <summary>Adds a system with its connections, for example <c>"gate Li02"</c>, <c>"hole Li03"</c>,
    /// <c>"airlock Li04"</c> or <c>"station Li05"</c> (a <c>goto</c> on a station, which is no connection).</summary>
    public RouteWorld System(string nickname, params string[] connections)
    {
        systems.Add(nickname);
        var list = objects[nickname] = [];
        foreach (var connection in connections)
        {
            var (kind, target) = (connection.Split(' ')[0], connection.Split(' ')[1]);
            var archetype = kind switch
            {
                "gate" => "jumpgate",
                "hole" => "jumphole",
                "airlock" => "airlock",
                _ => "station",
            };
            var name = $"{nickname}_to_{target}";
            var count = list.Count(o => o.StartsWith($"nickname = {name}\n") || o.StartsWith($"nickname = {name}_"));
            if (count > 0)
                name += $"_{count + 1}";
            list.Add($"nickname = {name}\narchetype = {archetype}\n" +
                     $"goto = {target}, {target}_to_{nickname}, gate_tunnel_bretonia\n");
        }
        return this;
    }

    /// <summary>Adds a <c>locked_gate</c> line, or an <c>npc_locked_gate</c> line, for an object.</summary>
    public RouteWorld Lock(string objectNickname, bool npc = false)
    {
        locks.Add($"{(npc ? "npc_locked_gate" : "locked_gate")} = {FLHash.CreateID(objectNickname)}");
        return this;
    }

    /// <summary>The text of universe.ini.</summary>
    public string Universe => string.Concat(systems.Select((s, i) =>
        $"[system]\nnickname = {s}\nfile = Systems\\{FileName(s)}\\{FileName(s)}.ini\npos = {i}, 1\nstrid_name = 1\n\n"));

    /// <summary>The text of the file of a system.</summary>
    public string SystemFile(string nickname) =>
        "[SystemInfo]\nlocal_faction = li_n_grp\n\n" +
        "[Object]\nnickname = " + nickname + "_sun\narchetype = station\n\n" +
        string.Concat(objects[nickname].Select(o => "[Object]\n" + o + "\n"));

    /// <summary>The text of initialworld.ini.</summary>
    public string Locks => "[locked_gates]\n" + string.Concat(locks.Select(l => l + "\n"));

    /// <summary>Each file of the universe, by install path.</summary>
    public Dictionary<string, string> Files()
    {
        var files = new Dictionary<string, string>(VfsPath.Comparer)
        {
            [FreelancerIniPath] = FreelancerIni,
            [SolarPath] = Solar,
            [UniversePath] = Universe,
            [LocksPath] = Locks,
        };
        foreach (var system in systems)
            files[SystemPath(system)] = SystemFile(system);
        return files;
    }

    /// <summary>The graph of the files, read in memory.</summary>
    public UniverseGraph Graph() => Graph(Files());

    /// <summary>The graph of files in memory.</summary>
    public static UniverseGraph Graph(Dictionary<string, string> files) =>
        UniverseGraph.Build(new FileUniverseSource(path => files.TryGetValue(path, out var text) ? Parse(path, text) : null,
            ListRules.Retail));

    /// <summary>The tables of the files, read in memory.</summary>
    public RouteTables Tables() => RouteTables.Build(Graph());

    /// <summary>The lines of a table without the section headers and blank lines.</summary>
    public static string[] Lines(RouteTable table) => [.. table.Routes.Select(r => r.Line)];

    /// <summary>
    /// Writes the universe into the game of an export test. Its <c>freelancer.ini</c> names the universe, the solar
    /// archetypes and the goods of <see cref="ExportGame"/>. The route tables of the universe become text files of the
    /// game, as the retail game has them.
    /// </summary>
    public ExportGame WriteInto(ExportGame game)
    {
        foreach (var (path, text) in Files().Where(f => f.Key != FreelancerIniPath))
            game.Game.Write(path, text);
        var dlls = game.IsHd ? ExportGame.HdDlls : ["InfoCards.dll"];
        game.Game.Write(FreelancerIniPath, """
            [Freelancer]
            data path = ..\data

            [Data]
            solar = solar\solararch.ini
            universe = universe\universe.ini
            goods = equipment\goods.ini
            markets = equipment\market_ships.ini

            [Resources]

            """ + string.Concat(dlls.Select(d => $"DLL = {d}\n")));
        foreach (var table in Tables().Tables)
            game.Game.WriteBytes(@"DATA\UNIVERSE\" + table.FileName, table.Text());
        return game;
    }

    /// <summary>A mod that adds the system <c>E</c> in its own folder, with a jump gate pair between <c>E</c> and
    /// <c>A</c>.</summary>
    /// <param name="more">More files of the mod, by their path in the mod folder.</param>
    public static Mod ConnectionMod(ExportGame game, string id = "conn", Dictionary<string, string>? more = null)
    {
        var files = new Dictionary<string, string>
        {
            [@"patches\DATA\UNIVERSE\universe.ini"] =
                $"[system]\nol.new = true\nnickname = E\nfile = ..\\MODS\\{id}\\SYSTEMS\\E\\E.ini\npos = 5, 5\n",
            [$@"files\DATA\MODS\{id}\SYSTEMS\E\E.ini"] =
                "[Object]\nnickname = E_to_A\narchetype = jumpgate\ngoto = A, A_to_E, gate_tunnel_bretonia\n",
            [@"patches\" + SystemPath("A")] =
                "[Object]\nol.new = true\nnickname = A_to_E\narchetype = jumpgate\ngoto = E, E_to_A, gate_tunnel_bretonia\n",
        };
        foreach (var (path, text) in more ?? [])
            files[path] = text;
        return game.Mod(id, files);
    }

    /// <summary>Merges one mod on the game with the retail rules, with the route tables of a retail game.</summary>
    public static TargetFiles Build(ExportGame game, Mod mod, bool keepPlaceholders = false, bool routeTables = true) =>
        TargetBuild.Run(game.Base, [mod], ModExport.DllName(mod.Id), TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator, keepPlaceholders, routeTables);

    /// <summary>The generated route files of a build.</summary>
    public static TargetFile[] RouteFiles(TargetFiles files) => [.. files.Files.Where(f => f.Generated)];

    public static IReadOnlyList<Section> Parse(string path, string text) =>
        IniDocument.Parse(path, Encoding.ASCII.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"))).Sections;
}
