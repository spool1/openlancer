using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The builder tools of section 7.3 of the MCP1 design on the vanilla data (decisions D730, D731 and D732). The
/// builders add no new error, and the route zones of retail follow the geometry that the route builder writes. The
/// tests hold no retail text: each value of a call comes from the data at run time.
/// </summary>
public sealed class AgentBuilderRetailTests(ITestOutputHelper output)
{
    /// <summary>
    /// Each builder runs in the largest retail system with values of the data, and check gives no new error. The save
    /// writes the patches, and a new session reads the same lines of the zone with new encounters, also in a binary
    /// system file.
    /// </summary>
    [Fact]
    public void TheBuildersAddNoNewErrorToTheVanillaData()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1builders");
        var session = workspace.Open();
        var host = new OpenLancer.Tests.TestAgentHost(session);
        static string Json(string text) => JsonSerializer.Serialize(text);
        JsonObject Ok(string tool, string arguments)
        {
            var result = AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject()).Result!;
            Assert.False(result.IsError, $"{tool}: {result.Text}");
            return result.Content;
        }
        void NoNewError(string tool, JsonObject result) =>
            Assert.DoesNotContain(result["new_problems"]!.AsArray(), p => (string?)p!["severity"] == "error");
        string First(string list, string extra = "") =>
            (string)Ok("list_values", $$"""{ "list": {{Json(list)}}{{extra}} }""")["rows"]![0]!["value"]!;

        var errors = (int)Ok("check", "{}")["errors"]!;
        var largest = session.Systems.Files.MaxBy(f => session.Sections(f).Count)!;
        var system = session.SystemNicknameOf(largest)!;
        var wreck = First("solar_archetype", """, "text": "MISSION_SATELLITE" """);
        var good = First("equipment", """, "text": "commodity" """);
        var faction = First("faction");
        var (ship, equipment, hardpoint) = ShipEquipment(session);
        var own = First("encounter_of_system", $$""", "system": {{Json(system)}} """);
        var other = OtherEncounter(session, largest);
        var zone = session.Sections(largest).First(s => SceneSections.KindOf(session, s) == SceneKind.Zone &&
                                                        session.Get(s)!.Lines("encounter").Count > 0 &&
                                                        session.Get(s)!.Lines("density_restriction").Count > 0);
        var kind = session.Get(zone)!.Lines("density_restriction")[0].Values[1].ToString()!;
        var zoneRef = ItemRefs.Canonical(session, zone);
        var equip = $$"""[{ "equipment": {{Json(equipment)}}, "hardpoint": {{Json(hardpoint)}} }]""";
        var group = $$"""{ "encounter": {{Json(own)}}, "level": 4, "chance": 0.4, "factions": [{ "faction": {{Json(faction)}}, "weight": 1 }] }""";

        var calls = new (string Tool, string Arguments)[]
        {
            ("create_loadout", $$"""
                { "nickname": "ol_test_loadout", "archetype": {{Json(ship)}}, "equipment": {{equip}},
                  "cargo": [{ "good": {{Json(good)}}, "count": 5 }] }
                """),
            ("scene_place_wreck", $$"""
                { "system": {{Json(system)}}, "position": [0, 0, 350000], "archetype": {{Json(wreck)}},
                  "ship_archetype": {{Json(ship)}}, "loot": [{ "good": {{Json(good)}}, "count": 20 }], "equipment": {{equip}},
                  "nickname": "ol_test_wreck", "name": "Test Wreck" }
                """),
            ("scene_place_patrol_route", $$"""
                { "system": {{Json(system)}}, "name": "ol_test_patrol",
                  "points": [[0, 0, 360000], [15000, 0, 360000], [15000, 0, 375000]],
                  "encounters": [{{group}},
                    { "encounter": {{Json(other)}}, "level": 2, "chance": 0.2, "factions": [{ "faction": {{Json(faction)}}, "weight": 1 }] }],
                  "faction_weights": [{ "faction": {{Json(faction)}}, "weight": 1 }],
                  "density_restrictions": [{ "count": 2, "class": {{Json(kind)}} }] }
                """),
            ("scene_set_encounters", $$"""
                { "zone": {{Json(zoneRef)}}, "encounters": [{{group}}], "faction_weights": [{ "faction": {{Json(faction)}}, "weight": 2 }] }
                """),
        };
        foreach (var (tool, arguments) in calls)
            NoNewError(tool, Ok(tool, arguments));

        Assert.Equal(errors, (int)Ok("check", "{}")["errors"]!);
        Assert.Equal(4, session.Revision);
        var route = session.Sections(largest).Where(s =>
                                                        s.Id.Key?.Contains("_path_ol_test_patrol_") == true).ToList();
        Assert.Equal(2, route.Count);
        var lines = Lines(session, zone);
        var routeLines = Lines(session, route[0]);
        Assert.True(session.Save().Saved);
        session.Dispose();
        var reloaded = workspace.Open();
        Assert.Equal(lines, Lines(reloaded, zone));
        Assert.Equal(routeLines, Lines(reloaded, route[0]));
        output.WriteLine($"{system}: a wreck of {wreck} and {ship}, a route with {own} and {other}, the encounters of " +
                         $"{zoneRef}");
    }

    /// <summary>
    /// The route zones of the vanilla data follow the geometry of the route builder (decision D731 of the MCP1 design).
    /// The end of each leg is the start of the next leg, at the ends of the local Y axis of the zones. Retail writes each
    /// angle as a whole number of degrees, so the ends meet within 3 percent of the length of the legs.
    /// </summary>
    [Fact]
    public void TheRouteZonesOfTheVanillaDataFollowTheGeometryOfTheBuilder()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "mcp1routes");
        var session = workspace.Open();
        var legs = new Dictionary<(string Path, string Label), Dictionary<int, (Vector3 Start, Vector3 End, float Length)>>();
        foreach (var path in session.Systems.Files)
        {
            foreach (var section in session.Sections(path).Where(s => SceneSections.KindOf(session, s) == SceneKind.Zone))
            {
                var view = session.Get(section)!;
                if (view.Lines("path_label") is not [{ Values.Count: >= 2 } label, ..] ||
                    Vector(view, "pos") is not { } pos || Vector(view, "rotate") is not { } rotate ||
                    view.Lines("size") is not [{ Values.Count: 2 } size] ||
                    !TransformValues.TryNumber(size.Values[1], out var length) ||
                    !TransformValues.TryNumber(label.Values[1], out var leg))
                    continue;
                var (start, end) = PatrolCommands.Ends(pos, rotate, length);
                var key = (path, label.Values[0].ToString()!.Trim().ToLowerInvariant());
                if (!legs.TryGetValue(key, out var route))
                    legs[key] = route = [];
                route[(int)leg] = (start, end, length);
            }
        }

        var pairs = 0;
        var meet = 0;
        foreach (var route in legs.Values)
        {
            foreach (var (n, a) in route)
            {
                if (!route.TryGetValue(n + 1, out var b))
                    continue;
                pairs++;
                if (Vector3.Distance(a.End, b.Start) < 0.03f * (a.Length + b.Length) / 2 + 10)
                    meet++;
            }
        }
        output.WriteLine($"{legs.Count} routes, {pairs} pairs of next legs, {meet} pairs meet");
        Assert.True(legs.Count > 100, $"{legs.Count} routes");
        Assert.True(meet >= pairs * 0.98, $"{meet} of {pairs} pairs meet");
    }

    /// <summary>A ship of a loadout of the data with an equip line that names a hardpoint, and that line.</summary>
    private static (string Ship, string Equipment, string Hardpoint) ShipEquipment(EditSession session)
    {
        var ships = session.Lists.List("ship")!;
        foreach (var entry in session.Lists.List("loadout")!.Entries)
        {
            if (entry.Section == null || session.Get(entry.Section) is not { } view ||
                view.Lines("archetype") is not [{ Values.Count: > 0 } archetype] ||
                ships.Find(archetype.Values[0].ToString()!) is not { } ship ||
                view.Lines("equip").FirstOrDefault(l => l.Values.Count == 2) is not { } equip)
                continue;
            return (ship.Value, equip.Values[0].ToString()!.Trim(), equip.Values[1].ToString()!.Trim());
        }
        throw new Xunit.Sdk.XunitException("No loadout of a ship has an equip line with a hardpoint.");
    }

    /// <summary>An encounter of another system that a system does not have.</summary>
    private static string OtherEncounter(EditSession session, string path)
    {
        var own = session.Lists.List("encounter_of_system", path)!;
        return session.Systems.Files.Where(f => f != path)
            .SelectMany(f => session.Lists.List("encounter_of_system", f)?.Entries ?? [])
            .First(e => own.Find(e.Value) == null).Value;
    }

    private static Vector3? Vector(SectionView view, string key) =>
        view.Lines(key) is [{ Values.Count: 3 } line] && TransformValues.TryNumber(line.Values[0], out var x) &&
        TransformValues.TryNumber(line.Values[1], out var y) && TransformValues.TryNumber(line.Values[2], out var z)
            ? new Vector3(x, y, z)
            : null;

    private static string[] Lines(EditSession session, SectionRef section) =>
        session.Get(section)!.Entries.Select(e => $"{e.Name} = {string.Join(", ", e.Values.Select(v => v.ToString()))}")
            .ToArray();
}
