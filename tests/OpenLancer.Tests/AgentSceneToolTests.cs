using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Editing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;
using ZoneShape = LibreLancer.Data.Schema.Universe.ZoneShape;

namespace OpenLancer.Tests;

/// <summary>
/// The Scene tools of section 7.3 of the MCP1 design. They create objects, zones and lights, spawn an asset, transform
/// items and change the components of an object. Each create is one entry with the edit origin Agent. The entry also
/// holds the nickname, the rotation, the size, the name and the fields of the request. The tests cover the preview, the
/// errors and the selection. They also cover the parity with the commands that the Scene of the editor runs.
/// </summary>
public sealed class AgentSceneToolTests
{
    /// <summary>A zone with a nickname, a size and a rotation is one entry. The result gives the new zone with its
    /// nickname, and the preview gives the same adds and changes nothing. One undo puts back W (section 14.3, steps 24 to
    /// 26 of the MCP1 design).</summary>
    [Fact]
    public void ACreateOfAZoneWithANicknameASizeAndARotationIsOneAgentEntry()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var before = Working(session);
        const string zone = """
            "system": "Li01", "shape": "cylinder", "position": [10, 0, -20.4], "size": [500, 3000],
            "rotation": [0, 0, 90], "nickname": "Zone_test_01"
            """;

        var preview = Ok(host, "scene_create_zone", $$"""{ {{zone}}, "preview": true }""");
        var result = Ok(host, "scene_create_zone", $$"""{ {{zone}} }""");

        Assert.Equal(("previewed", "0"), ((string?)preview["status"], (string?)preview["rev"]));
        Assert.Equal(["zone:Zone_test_01"], Texts(preview["adds"]));
        Assert.Equal(Texts(preview["adds"]), Texts(result["adds"]));
        Assert.Empty(result["renames"]!.AsArray());
        Assert.Empty(result["changes"]!.AsArray());
        Assert.Equal("zone:Zone_test_01", (string?)result["select"]);
        Assert.Equal(("Agent: Create Zone_test_01", "1"), ((string?)result["undo_label"], (string?)result["rev"]));
        Assert.Equal(EditOrigin.Agent, session.UndoOrigin);
        var added = SceneWorkspace.Zone("Zone_test_01");
        Assert.Equal(("CYLINDER", "500, 3000", "0, 0, 90", "10, 0, -20"),
            (Line(session, added, "shape"), Line(session, added, "size"), Line(session, added, "rotate"),
                Line(session, added, "pos")));
        Assert.Equal([("scene_create_zone", "Agent: Create Zone_test_01")],
            host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));

        session.Undo();
        Assert.Equal(before, Working(session));
        Assert.False(session.CanUndo);
    }

    /// <summary>A zone without a nickname gets the next free nickname and the default size of its shape. A size of the
    /// wrong count and a size below 1 give invalid_value, and nothing changes.</summary>
    [Fact]
    public void AZoneWithoutANicknameTakesTheFreeNameAndABadSizeGivesInvalidValue()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var count = Error(host, "scene_create_zone", """{ "system": "Li01", "position": [0, 0, 0], "size": [1, 2] }""");
        var small = Error(host, "scene_create_zone",
            """{ "system": "system:Li01", "shape": "box", "position": [0, 0, 0], "size": [10, 0.5, 10] }""");
        var made = Ok(host, "scene_create_zone", """{ "system": "Li01", "position": [0, 0, 0] }""");

        Assert.Equal(("invalid_value", "FieldValue"), (count.Code, count.Detail));
        Assert.Equal("The size of a sphere zone has one value. Give one value.", count.Message);
        Assert.Equal(("invalid_value", "FieldValue", "A size value must be 1 or more."), small);
        Assert.Equal(["zone:li01_zone_1"], Texts(made["adds"]));
        Assert.Equal("Agent: Create li01_zone_1", (string?)made["undo_label"]);
        Assert.Equal(("SPHERE", "1000"),
            (Line(session, SceneWorkspace.Zone("li01_zone_1"), "shape"), Line(session, SceneWorkspace.Zone("li01_zone_1"), "size")));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>A station archetype with a docking sphere needs a base, as the Create menu asks for one. A station
    /// archetype without one creates without a base and takes no base (review finding R5 of MCP1-2).</summary>
    [Fact]
    public void AStationNeedsABaseOnlyWhenItsArchetypeHasADockingSphere()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var asks = Error(host, "scene_create_object",
            """{ "system": "Li01", "template": "station", "archetype": "station1", "position": [0, 0, 0] }""");
        var notTaken = Error(host, "scene_create_object",
            """{ "system": "Li01", "template": "station", "archetype": "station_nodock", "position": [0, 0, 0], "base": "Li01_01_Base" }""");
        var unknown = Error(host, "scene_create_object",
            """{ "system": "Li01", "template": "station", "archetype": "station1", "position": [0, 0, 0], "base": "Li01_02_Base" }""");
        var nodock = Ok(host, "scene_create_object",
            """{ "system": "Li01", "template": "station", "archetype": "station_nodock", "position": [0, 0, 0] }""");
        var docked = Ok(host, "scene_create_object",
            """{ "system": "Li01", "template": "station", "archetype": "station1", "position": [0, 0, 0], "base": "base:Li01_01_Base" }""");

        Assert.Equal(("invalid_argument", SceneEditTools.NeedsBase), (asks.Code, asks.Detail));
        Assert.Equal("The archetype station1 has a docking sphere, so the station needs a base. Get the bases from " +
                     "list_values with the list base.", asks.Message);
        Assert.Equal(("invalid_argument", SceneEditTools.TakesNoBase), (notTaken.Code, notTaken.Detail));
        Assert.Equal(("not_found", ItemRefs.NotFound), (unknown.Code, unknown.Detail));
        var noBase = ItemRefs.ResolveSection(session, ItemRefs.Parse(Texts(nodock["adds"])[0]));
        Assert.Null(Line(session, noBase, "dock_with"));
        var withBase = ItemRefs.ResolveSection(session, ItemRefs.Parse(Texts(docked["adds"])[0]));
        Assert.Equal("Li01_01_Base", Line(session, withBase, "dock_with"));
        Assert.Equal(2, session.Revision);
    }

    /// <summary>
    /// An object with a nickname, a rotation, a name and fields is one entry (workflow 1, step 5 of the MCP1 design). Its
    /// label is "Create" and the nickname. The new object has the lines of each part, and the result gives it with its
    /// nickname and selects it.
    /// </summary>
    [Fact]
    public void AnObjectWithANicknameARotationANameAndFieldsIsOneEntry()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var texts = new ItemTexts(session, _ => "", _ => "");

        var result = Ok(host, "scene_create_object", """
            {
              "system": "Li01", "template": "station", "archetype": "station1", "position": [100.4, 0, 0],
              "rotation": [0, 90, 0], "nickname": "my_station_1", "name": "Test Station", "base": "Li01_01_Base",
              "parent": "obj:Li01_01", "fields": { "loadout": "lo_station" }
            }
            """);

        var station = SceneWorkspace.Object("my_station_1");
        Assert.Equal("Agent: Create my_station_1", (string?)result["undo_label"]);
        Assert.Equal(["obj:my_station_1"], Texts(result["adds"]));
        Assert.Equal("obj:my_station_1", (string?)result["select"]);
        Assert.Empty(result["renames"]!.AsArray());
        Assert.Equal(("100, 0, 0", "station1", "0, 90, 0", "Li01_01", "Li01_01_Base", "lo_station"),
            (Line(session, station, "pos"), Line(session, station, "archetype"), Line(session, station, "rotate"),
                Line(session, station, "parent"), Line(session, station, "dock_with"), Line(session, station, "loadout")));
        Assert.Equal("Test Station", texts.TextOf(station, "ids_name"));
        Assert.Equal(1, session.Revision);
        Assert.Null(session.Get(SceneWorkspace.Object("li01_station_1")));

        session.Undo();
        Assert.Null(session.Get(station));
    }

    /// <summary>The errors of scene_create_object of section 7.3 of the MCP1 design. An archetype of another template
    /// gives invalid_value with the reason of the template. An archetype that the list does not have gives not_found. A
    /// used nickname gives name_taken. Each error names its argument, and nothing changes.</summary>
    [Fact]
    public void TheErrorsOfACreateNameTheirArgumentAndChangeNothing()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        string Args(string archetype, string more = "") =>
            $$"""{ "system": "Li01", "template": "planet", "archetype": "{{archetype}}", "position": [0, 0, 0]{{more}} }""";

        var other = Call(host, "scene_create_object", Args("buoy"));
        var gate = Error(host, "scene_create_object", Args("jumpgate"));
        var unknown = Error(host, "scene_create_object", Args("planet_9"));
        var taken = Call(host, "scene_create_object", Args("planet1", """, "nickname": "Li01_dock" """));
        var parent = Error(host, "scene_create_object", Args("planet1", """, "parent": "obj:Li02_01" """));
        var notObject = Error(host, "scene_create_object", Args("planet1", """, "parent": "zone:Zone_cloud" """));
        var field = Call(host, "scene_create_object", Args("planet1", """, "fields": { "loadout": "a, b" } """));
        var system = Error(host, "scene_create_object", Args("planet1").Replace("\"Li01\"", "\"Li09\""));

        Assert.Equal(("invalid_value", SceneEditTools.ArchetypeTemplate, "archetype"),
            ((string?)other.Content["code"], (string?)other.Content["detail"], (string?)other.Content["field"]));
        Assert.Equal("The archetype buoy is not an archetype of the template Planet.", (string?)other.Content["message"]);
        Assert.Equal(("invalid_value", SceneEditTools.ArchetypeTemplate), (gate.Code, gate.Detail));
        Assert.Contains("milestone M27", gate.Message);
        Assert.Equal(("not_found", ItemRefs.NotFound), (unknown.Code, unknown.Detail));
        Assert.StartsWith("The list solar_archetype has no archetype planet_9.", unknown.Message);
        Assert.Contains("planet1", unknown.Message);
        Assert.Equal(("name_taken", "NameTaken", "nickname"),
            ((string?)taken.Content["code"], (string?)taken.Content["detail"], (string?)taken.Content["field"]));
        Assert.Equal(("invalid_argument", SceneEditTools.ParentNotInSystem), (parent.Code, parent.Detail));
        Assert.Equal("The parent obj:Li02_01 is not in the system Li01. Give an object of the same system.", parent.Message);
        Assert.Equal(("invalid_argument", ToolInput.BadArgument), (notObject.Code, notObject.Detail));
        Assert.Equal(("invalid_value", "fields.loadout"), ((string?)field.Content["code"], (string?)field.Content["field"]));
        Assert.Equal(("not_found", ItemRefs.NotFound), (system.Code, system.Detail));
        Assert.Equal(0, session.Revision);
        Assert.False(session.CanUndo);
        Assert.Null(session.OpenBatch);
    }

    /// <summary>A light is one entry with its fields. The engine reads no rotation for a light, so the tool has no
    /// rotation.</summary>
    [Fact]
    public void ALightIsOneEntryWithItsFields()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var result = Ok(host, "scene_create_light",
            """{ "system": "Li01", "position": [5, 6, 7], "nickname": "my_lamp", "fields": { "color": [10, 20, 30], "range": 900 } }""");
        var rotation = Error(host, "scene_create_light", """{ "system": "Li01", "position": [0, 0, 0], "rotation": [0, 1, 0] }""");

        var lamp = SceneWorkspace.Li01("LightSource", "my_lamp");
        Assert.Equal(["light:Li01/my_lamp"], Texts(result["adds"]));
        Assert.Equal("Agent: Create my_lamp", (string?)result["undo_label"]);
        Assert.Equal(("5, 6, 7", "10, 20, 30", "900", "POINT"),
            (Line(session, lamp, "pos"), Line(session, lamp, "color"), Line(session, lamp, "range"), Line(session, lamp, "type")));
        Assert.Equal(("invalid_argument", ToolInput.BadArgument), (rotation.Code, rotation.Detail));
    }

    /// <summary>
    /// The spawn of an asset (section 8 of the M23 design). A solar archetype takes the template of its type, and a
    /// station archetype with a docking sphere needs a base. A ship makes a new static prop archetype, and the result
    /// tells that the Scene shows it after the save (decision D282). A type in no template refuses with its reason, and so
    /// does a prop group.
    /// </summary>
    [Fact]
    public void ASpawnPlacesAnArchetypeOrMakesAPropArchetype()
    {
        using var ws = AssetWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        string Spawn(string asset, string more = "") =>
            $$"""{ "system": "Li01", "asset": "{{asset}}", "position": [10, 20, 30]{{more}} }""";

        var buoy = Ok(host, "scene_spawn_asset", Spawn("asset:solar_archetype/buoy"));
        var station = Error(host, "scene_spawn_asset", Spawn("asset:solar_archetype/station1"));
        var odd = Error(host, "scene_spawn_asset", Spawn("asset:solar_archetype/odd"));
        var ship = Ok(host, "scene_spawn_asset", Spawn("asset:ship/ship1"));
        var notAsset = Error(host, "scene_spawn_asset", Spawn("obj:Li01_01"));
        var none = Error(host, "scene_spawn_asset", Spawn("asset:ship/no_ship"));

        Assert.Equal("Agent: Create li01_satellite_1", (string?)buoy["undo_label"]);
        Assert.Equal(["obj:li01_satellite_1"], Texts(buoy["adds"]));
        Assert.Equal(("invalid_argument", SceneEditTools.NeedsBase), (station.Code, station.Detail));
        Assert.Equal(("refused", "The type no_such_type is in no Create template of the rule data."), (odd.Code, odd.Message));
        var adds = Texts(ship["adds"]);
        Assert.Equal(2, adds.Length);
        Assert.StartsWith($"sec:{ListWorkspace.SolarPath}#Solar/", adds[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("obj:li01_solar_1", adds[1]);
        Assert.Equal("obj:li01_solar_1", (string?)ship["select"]);
        var note = Assert.Single(ship["notes"]!.AsArray())!.GetValue<string>();
        Assert.StartsWith("The Scene shows obj:li01_solar_1 after a save. The game data then loads the new archetype ", note);
        Assert.Equal(("invalid_argument", ToolInput.BadArgument), (notAsset.Code, notAsset.Detail));
        Assert.Equal(("not_found", ItemRefs.NotFound), (none.Code, none.Detail));
        Assert.Equal(2, session.Revision);
    }

    /// <summary>
    /// A transform moves only the items that it names, as the gizmo does. The absolute mode sets the values, and the
    /// relative mode adds them: a relative rotation adds the degrees to the values of <c>rotate</c>. All moves are one
    /// entry.
    /// </summary>
    [Fact]
    public void ATransformMovesOnlyTheNamedItemsAbsoluteOrRelative()
    {
        var (ws, session, host) = Scene(Outpost);
        using var _ = ws;
        var buoy = SceneWorkspace.Object("Li01_outpost_buoy");

        var absolute = Ok(host, "scene_transform", """
            { "moves": [
                { "ref": "obj:Li01_outpost", "position": [1, 2, 3], "rotation": [0, 45, 0] },
                { "ref": "zone:Zone_a", "size": [400, 800] } ] }
            """);
        var relative = Ok(host, "scene_transform", """
            { "mode": "relative", "moves": [{ "ref": "obj:Li01_outpost", "position": [10, 0, 0], "rotation": [0, 30, 10] }] }
            """);

        Assert.Equal("Agent: Transform Li01_outpost and 1 more", (string?)absolute["undo_label"]);
        Assert.Equal(["obj:Li01_outpost", "zone:Zone_a"], Texts(absolute["changes"]));
        Assert.Equal("Agent: Transform Li01_outpost", (string?)relative["undo_label"]);
        var outpost = SceneWorkspace.Object("Li01_outpost");
        Assert.Equal(("11, 2, 3", "0, 75, 10"), (Line(session, outpost, "pos"), Line(session, outpost, "rotate")));
        Assert.Equal("400, 800", Line(session, SceneWorkspace.Zone("Zone_a"), "size"));
        Assert.Equal("150, 10, 0", Line(session, buoy, "pos"));
        Assert.Equal(2, session.Revision);
    }

    /// <summary>
    /// The refusals of scene_transform (section 7.3 of the MCP1 design). An item without a position refuses, and so does
    /// a size of an object: retail objects have no scale key (M34). A rotation of a light and more moves than the item
    /// limit also refuse. Nothing changes.
    /// </summary>
    [Fact]
    public void ATransformRefusesAnItemWithoutAPositionAScaleOfAnObjectAndTooManyMoves()
    {
        var (ws, session, host) = Scene(Outpost + "\n[Object]\nnickname = Li01_nowhere\narchetype = buoy\n");
        using var _ = ws;
        host.TestLimits = new AgentLimits { EditItems = 2 };

        var nowhere = Error(host, "scene_transform", """{ "moves": [{ "ref": "obj:Li01_nowhere", "position": [1, 2, 3] }] }""");
        var scale = Error(host, "scene_transform", """{ "moves": [{ "ref": "obj:Li01_outpost", "size": [2, 2, 2] }] }""");
        var lamp = Error(host, "scene_transform", """{ "moves": [{ "ref": "light:Li01/Li01_lamp", "rotation": [0, 9, 0] }] }""");
        var empty = Error(host, "scene_transform", """{ "moves": [{ "ref": "obj:Li01_outpost" }] }""");
        var many = Error(host, "scene_transform", """
            { "moves": [{ "ref": "obj:Li01_outpost", "position": [1, 1, 1] }, { "ref": "zone:Zone_a", "position": [1, 1, 1] },
              { "ref": "light:Li01/Li01_lamp", "position": [1, 1, 1] }] }
            """);
        var twice = Error(host, "scene_transform", """
            { "moves": [{ "ref": "obj:Li01_outpost", "position": [1, 1, 1] }, { "ref": "obj:Li01_outpost", "position": [2, 2, 2] }] }
            """);

        Assert.Equal(("refused", SceneEditTools.NoPosition,
                "obj:Li01_nowhere has no position. Give it a pos with set_fields first."), nowhere);
        Assert.Equal(("refused", EditPlan.PlanRefused, "The engine reads no size for an object."), scale);
        Assert.Equal(("refused", EditPlan.PlanRefused, "The engine reads no rotation for a light."), lamp);
        Assert.Equal(("refused", "Give a position, a rotation or a size."), (empty.Code, empty.Message));
        Assert.Equal(("limit", SceneEditTools.EditItems, "One call can give at most 2 moves. Split the call."), many);
        Assert.Equal("refused", twice.Code);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// The components of an object (decisions D243, D244 and D258 of the M3 design). An add sets the line of the
    /// component, and a remove removes its keys. The remove of Solar refuses, as Unity refuses to remove a component that
    /// another component needs (RequireComponent). An add that the Inspector does not offer refuses with its reason. The
    /// value of an add must be an entry of the list of the component, as in the picker of the Inspector. The line gets
    /// the entry as the list spells it.
    /// </summary>
    [Fact]
    public void AComponentAddsAndRemovesAndSolarCannotGo()
    {
        var (ws, session, host) = Scene(Outpost);
        using var _ = ws;
        string Set(string reference, string component, string action, string? value = null) =>
            $$"""{ "ref": "{{reference}}", "component": "{{component}}", "action": "{{action}}"{{(value == null ? "" : $", \"value\": \"{value}\"")}} }""";

        var add = Ok(host, "scene_set_component", Set("obj:Li01_outpost", "loadout_user", "add", "lo_station"));
        var remove = Ok(host, "scene_set_component", Set("obj:Li01_outpost", "loadout_user", "remove"));
        var solar = Error(host, "scene_set_component", Set("obj:Li01_outpost", "solar", "remove"));
        var dock = Error(host, "scene_set_component", Set("obj:Li01_outpost_buoy", "dockable_station", "add", "Li01_01_Base"));
        var zone = Error(host, "scene_set_component", Set("zone:Zone_a", "loadout_user", "add", "lo_station"));
        var noValue = Error(host, "scene_set_component", Set("obj:Li01_outpost", "loadout_user", "add"));
        var removeValue = Error(host, "scene_set_component", Set("obj:Li01_outpost", "loadout_user", "remove", "x"));
        var comma = Error(host, "scene_set_component", Set("obj:Li01_outpost", "loadout_user", "add", "a, b"));
        var unknown = Error(host, "scene_set_component", Set("obj:Li01_outpost", "loadout_user", "add", "lo_statio"));
        var faction = Error(host, "scene_set_component", Set("obj:Li01_outpost", "faction_member", "add", "li_x_grp"));

        var outpost = SceneWorkspace.Object("Li01_outpost");
        Assert.Equal(("Agent: Add Loadout user", "Agent: Remove Loadout user"),
            ((string?)add["undo_label"], (string?)remove["undo_label"]));
        Assert.Null(Line(session, outpost, "loadout"));
        Assert.Equal(("refused", "Solar: Each object needs an archetype."), (solar.Code, solar.Message));
        Assert.Equal(("refused", "Dockable station: Pick an archetype with a docking sphere first."), (dock.Code, dock.Message));
        Assert.Equal("refused", zone.Code);
        Assert.EndsWith("Zone_a: only an object takes a component.", zone.Message);
        Assert.Equal(("invalid_argument", "ValueMissing"), (noValue.Code, noValue.Detail));
        Assert.Equal(("invalid_argument", ToolInput.BadArgument), (removeValue.Code, removeValue.Detail));
        Assert.Equal(("invalid_value", "FieldValue"), (comma.Code, comma.Detail));
        Assert.Equal(("invalid_value", "FieldValue",
                "The list loadout has no entry lo_statio. Get the values from list_values with the list loadout. " +
                "The closest names are: lo_station."), unknown);
        Assert.Equal(("invalid_value", "FieldValue"), (faction.Code, faction.Detail));
        Assert.StartsWith("The list faction has no entry li_x_grp.", faction.Message);
        Assert.Equal(2, session.Revision);
        Ok(host, "scene_set_component", Set("obj:Li01_outpost", "loadout_user", "add", "LO_STATION"));
        Assert.Equal("lo_station", Line(session, outpost, "loadout"));
    }

    /// <summary>
    /// The parity of section 13.1, item 7 of the MCP1 design. The command runner of the editor runs the command of the
    /// Scene that the UI calls. The tool calls the same command, and both give the same W. The commands are a create of a
    /// zone, of a station with a base and of a light, a spawn, a transform and a component.
    /// </summary>
    [Fact]
    public void TheToolsGiveTheWOfTheSceneCommandsOfTheUi()
    {
        var position = new Vector3(1.4f, 2.6f, -3.6f);
        AssertParity(Outpost, s => SceneCommands.Create(s, "Li01", SceneTemplate.Zone, null, position, shape: ZoneShape.RING),
            "scene_create_zone", """{ "system": "Li01", "shape": "ring", "position": [1.4, 2.6, -3.6] }""");
        AssertParity(Outpost, s => SceneCommands.Create(s, "Li01", SceneTemplate.Station, "station1", position,
                SceneWorkspace.Object("Li01_outpost"), "Li01_01_Base"),
            "scene_create_object", """
                { "system": "Li01", "template": "station", "archetype": "station1", "position": [1.4, 2.6, -3.6],
                  "parent": "obj:Li01_outpost", "base": "Li01_01_Base" }
                """);
        AssertParity(Outpost, s => SceneCommands.Create(s, "Li01", SceneTemplate.Light, null, position),
            "scene_create_light", """{ "system": "Li01", "position": [1.4, 2.6, -3.6] }""");
        AssertParity(Outpost, s => SceneCommands.SpawnAsset(s, "Li01", new AssetRef(AssetKind.SolarArchetype, "buoy"), position),
            "scene_spawn_asset", """{ "system": "Li01", "asset": "asset:solar_archetype/buoy", "position": [1.4, 2.6, -3.6] }""");
        AssertParity(Outpost, s => SceneCommands.Transform(s,
                [new TransformMove(SceneWorkspace.Object("Li01_outpost"), new TransformChange(new Vector3(7, 8, 9), new Vector3(0, 10, 0)))],
                TransformMode.Absolute),
            "scene_transform", """{ "moves": [{ "ref": "obj:Li01_outpost", "position": [7, 8, 9], "rotation": [0, 10, 0] }] }""");
        AssertParity(Outpost, s => SceneCommands.SetComponent(s, SceneWorkspace.Object("Li01_outpost"),
                ObjectComponent.FactionMember, "li_n_grp"),
            "scene_set_component",
            """{ "ref": "obj:Li01_outpost", "component": "faction_member", "action": "add", "value": "li_n_grp" }""");
    }

    /// <summary>The input schema of scene_create_object is the binding schema of section 7.8 of the MCP1 design in its
    /// required list, its properties and their types. It also has with_patch, the common argument of each edit tool of
    /// section 7.0.</summary>
    [Fact]
    public void TheSchemaOfSceneCreateObjectIsTheSchemaOfTheDesign()
    {
        var schema = AgentTools.Find("scene_create_object")!.InputSchema;
        var properties = schema["properties"]!.AsObject();

        Assert.Equal(["system", "template", "archetype", "position"], Texts(schema["required"]));
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.Equal(
        [
            ("system", "string"), ("template", "string"), ("archetype", "string"), ("position", "array"),
            ("rotation", "array"), ("nickname", "string"), ("name", "string"), ("parent", "string"), ("base", "string"),
            ("fields", "object"), ("preview", "boolean"), ("if_revision", "string"), ("with_patch", "boolean"),
        ], properties.Select(p => (p.Key, (string)p.Value!["type"]!)));
        Assert.Equal(["station", "planet", "sun", "satellite", "solar"], Texts(properties["template"]!["enum"]));
        foreach (var vector in new[] { "position", "rotation" })
        {
            Assert.Equal((3, 3, "number"), ((int)properties[vector]!["minItems"]!, (int)properties[vector]!["maxItems"]!,
                (string)properties[vector]!["items"]!["type"]!));
        }
        Assert.Equal(1, (int)properties["nickname"]!["minLength"]!);
        Assert.Equal(128, (int)properties["name"]!["maxLength"]!);
        Assert.False((bool)properties["preview"]!["default"]!);
        Assert.Equal("Creates one object in a system. The tool makes one Undo step. Get the archetypes from list_values " +
                     "with the list solar_archetype. Set preview to true to see the result first.",
            AgentTools.Find("scene_create_object")!.Description);
    }

    /// <summary>A batch of creates is one entry (workflow 1, steps 5 and 6 of the MCP1 design). A later step names the
    /// item of an earlier step by its nickname. The label of the batch is the label of its first step.</summary>
    [Fact]
    public void ABatchOfCreatesIsOneEntryAndALaterStepNamesAnEarlierItem()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var result = Ok(host, "edit_batch", """
            { "steps": [
                { "tool": "scene_create_object", "arguments": { "system": "Li01", "template": "station",
                  "archetype": "station_nodock", "position": [0, 0, 0], "nickname": "my_station_1" } },
                { "tool": "scene_create_object", "arguments": { "system": "Li01", "template": "planet",
                  "archetype": "planet1", "position": [20000, 0, 0], "nickname": "my_planet_1", "parent": "obj:my_station_1" } },
                { "tool": "scene_transform", "arguments": { "moves": [{ "ref": "obj:my_station_1", "position": [5, 0, 0] }] } }
            ] }
            """);

        Assert.Equal("Agent: Create my_station_1", (string?)result["undo_label"]);
        Assert.Equal(["obj:my_station_1", "obj:my_planet_1"], Texts(result["adds"]));
        Assert.Empty(result["changes"]!.AsArray());
        Assert.Equal("my_station_1", Line(session, SceneWorkspace.Object("my_planet_1"), "parent"));
        Assert.Equal("5, 0, 0", Line(session, SceneWorkspace.Object("my_station_1"), "pos"));
        Assert.Equal(1, session.Revision);
        session.Undo();
        Assert.Null(session.Get(SceneWorkspace.Object("my_station_1")));
    }

    /// <summary>The Scene tools are in the tool group Scene (decision D725 of the MCP1 design). A hidden group gives
    /// not_available. create_from_template is a data tool of the group Studio, so it stays.</summary>
    [Fact]
    public void TheSceneToolsAreInTheGroupSceneAndAHiddenGroupGivesNotAvailable()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        host.Hidden.Add(ToolGroup.Scene);

        var hidden = Error(host, "scene_create_zone", """{ "system": "Li01", "position": [0, 0, 0] }""");

        Assert.Equal(("not_available", "GroupHidden"), (hidden.Code, hidden.Detail));
        Assert.All(AgentTools.All.Where(t => t.Name.StartsWith("scene_") || t.Name.StartsWith("prop_group_")),
            t => Assert.Equal(ToolGroup.Scene, t.Group));
        Assert.Equal(ToolGroup.Studio, AgentTools.Find("create_from_template")!.Group);
        Assert.False(session.CanUndo);
    }

    /// <summary>
    /// Each edit tool of section 7.3 of the MCP1 design (section 13.1, items 5 and 6). Its preview changes nothing and
    /// gives the adds and the changes of the real call. The real call adds one entry with the edit origin Agent and the
    /// label "Agent: ...". One undo puts back the states of each file before the call.
    /// </summary>
    [Fact]
    public void EachSceneAndDataToolPreviewsAndIsOneAgentEntryThatOneUndoReverses()
    {
        (string Tool, string Arguments)[] calls =
        [
            ("scene_create_object", """{ "system": "Li01", "template": "planet", "archetype": "planet1", "position": [1, 2, 3], "name": "My Planet" }"""),
            ("scene_create_zone", """{ "system": "Li01", "shape": "box", "position": [1, 2, 3], "size": [10, 20, 30], "nickname": "Zone_box" }"""),
            ("scene_create_light", """{ "system": "Li01", "position": [1, 2, 3], "fields": { "range": 700 } }"""),
            ("scene_spawn_asset", """{ "system": "Li01", "asset": "asset:ship/ship1", "position": [1, 2, 3] }"""),
            ("scene_transform", """{ "moves": [{ "ref": "obj:Li01_14", "position": [5, 5, 5], "rotation": [0, 90, 0] }] }"""),
            ("scene_set_component", """{ "ref": "obj:Li01_14", "component": "faction_member", "action": "add", "value": "li_n_grp" }"""),
            ("prop_group_place", """{ "group": "group:trio", "system": "Li01", "anchor": [0, 0, 9000] }"""),
            ("create_from_template", """{ "kind": "equipment", "template": "gun1", "nickname": "gun_copy" }"""),
        ];
        Assert.Equal(AgentTools.All.Where(t => t.Kind == ToolKind.Edit && (t.Name.StartsWith("scene_") ||
                         t.Name.StartsWith("prop_group_") || t.Name == "create_from_template")).Select(t => t.Name),
            calls.Select(c => c.Tool));

        foreach (var (tool, arguments) in calls)
        {
            using var ws = GroupWorkspace.Create();
            var session = ws.Session("act");
            var host = new TestAgentHost(session);
            PropGroupCommands.Save(session, PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]), "trio", "Trio");
            var paths = new[] { SceneWorkspace.Li01Path, ListWorkspace.SolarPath, ListWorkspace.EquipmentPath };
            var before = paths.Select(p => W(session, p)).ToList();

            var preview = Ok(host, tool, arguments.TrimEnd('}') + """, "preview": true }""");
            Assert.Equal(("previewed", "0", false), ((string?)preview["status"], (string?)preview["rev"], session.CanUndo));
            Assert.Equal(before, paths.Select(p => W(session, p)));
            var result = Ok(host, tool, arguments);

            Assert.Equal((tool, "applied", "1"), (tool, (string?)result["status"], (string?)result["rev"]));
            Assert.StartsWith(EditBatch.AgentPrefix, (string?)result["undo_label"]);
            Assert.Equal((EditOrigin.Agent, (string?)result["undo_label"]), (session.UndoOrigin, session.UndoLabel));
            Assert.Equal(Texts(preview["adds"]), Texts(result["adds"]));
            Assert.Equal(Texts(preview["changes"]), Texts(result["changes"]));
            session.Undo();
            Assert.Equal(before, paths.Select(p => W(session, p)));
            Assert.False(session.CanUndo);
        }
    }

    /// <summary>A system with an outpost that has a child, a zone and a light, each with a position.</summary>
    internal const string Outpost = """
        [Object]
        nickname = Li01_outpost
        archetype = station_nodock
        pos = 100, 0, 0
        [Object]
        nickname = Li01_outpost_buoy
        archetype = buoy
        parent = Li01_outpost
        pos = 150, 10, 0
        [zone]
        nickname = Zone_a
        pos = 0, 0, 0
        shape = CYLINDER
        size = 100, 200
        [LightSource]
        nickname = Li01_lamp
        pos = 0, 0, 0
        """;

    /// <summary>The command of the UI through the command runner and the tool give the same W of Li01 and of the solar
    /// archetypes.</summary>
    private static void AssertParity(string li01, Func<EditSession, EditPlan> command, string tool, string arguments)
    {
        using var a = AssetWorkspace.Create();
        a.WriteBase(SceneWorkspace.Li01Path, li01);
        var expected = a.Session("act");
        var commandHost = new FakeCommandHost(expected);
        Assert.Equal(EditStatus.Applied, new CommandRunner(commandHost).Run(command(expected)).Status);

        using var b = AssetWorkspace.Create();
        b.WriteBase(SceneWorkspace.Li01Path, li01);
        var session = b.Session("act");
        Ok(new TestAgentHost(session), tool, arguments);

        foreach (var path in new[] { SceneWorkspace.Li01Path, ListWorkspace.SolarPath })
            Assert.Equal(W(expected, path), W(session, path));
        Assert.Equal(EditBatch.AgentPrefix + expected.UndoLabel, session.UndoLabel);
        Assert.Equal((EditOrigin.User, EditOrigin.Agent), (expected.UndoOrigin, session.UndoOrigin));
    }

    /// <summary>Each section of a file of W with its lines.</summary>
    internal static string[] W(EditSession session, string path) =>
        session.Sections(path).SelectMany(s => session.Get(s)!.Entries
            .Select(e => $"{e.Name} = {FieldEdit.FormatLine(e.Values)}").Prepend($"[{s.Id}]")).ToArray();

    /// <summary>The working state of each section of Li01, to compare with reference equality after an undo.</summary>
    internal static List<object> Working(EditSession session) =>
        session.Sections(SceneWorkspace.Li01Path).Select(s => (object)session.WorkingSection(s)!).ToList();
}
