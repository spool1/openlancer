using System;
using System.Linq;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The write scope of the edit tools (section 13.1, item 12 of the MCP1 design). Each edit tool of MCP1-2 with a
/// reference argument gives <c>invalid_argument</c> for an install path with a drive, a stream name or a <c>..</c> above
/// the root. The path never reaches the session, so nothing changes and no file is written.
/// </summary>
public sealed class AgentWriteScopeTests
{
    /// <summary>The references with a path that is not an install path.</summary>
    private static readonly string[] BadReferences =
    [
        @"sec:C:\x.ini#Object/a", @"sec:C:x.ini#Object/a", @"sec:DATA\x.ini:stream#Object/a", @"sec:..\x.ini#Object/a",
        @"sec:DATA\..\..\x.ini#Object/a",
    ];

    /// <summary>Each tool with a reference argument and valid other arguments. <c>REF</c> marks the reference.</summary>
    private static readonly (string Tool, string Arguments)[] Calls =
    [
        ("set_fields", """{ "ref": REF, "fields": { "pos": [1, 2, 3] } }"""),
        ("set_text", """{ "ref": REF, "field": "name", "text": "x" }"""),
        ("rename_item", """{ "ref": REF, "new_name": "my_name" }"""),
        ("delete_items", """{ "refs": ["obj:Li01_dock", REF], "confirm": true }"""),
        ("duplicate_items", """{ "refs": [REF] }"""),
        ("scene_create_object", """{ "system": REF, "template": "planet", "archetype": "planet1", "position": [0, 0, 0] }"""),
        ("scene_create_object", """{ "system": "Li01", "template": "planet", "archetype": "planet1", "position": [0, 0, 0], "parent": REF }"""),
        ("scene_create_object", """{ "system": "Li01", "template": "station", "archetype": "station1", "position": [0, 0, 0], "base": REF }"""),
        ("scene_create_zone", """{ "system": REF, "position": [0, 0, 0] }"""),
        ("scene_create_light", """{ "system": REF, "position": [0, 0, 0] }"""),
        ("scene_spawn_asset", """{ "system": "Li01", "asset": REF, "position": [0, 0, 0] }"""),
        ("scene_spawn_asset", """{ "system": "Li01", "asset": "asset:solar_archetype/buoy", "position": [0, 0, 0], "parent": REF }"""),
        ("scene_transform", """{ "moves": [{ "ref": "obj:Li01_14", "position": [1, 1, 1] }, { "ref": REF, "position": [1, 1, 1] }] }"""),
        ("scene_set_component", """{ "ref": REF, "component": "loadout_user", "action": "remove" }"""),
        ("scene_place_wreck", """{ "system": REF, "position": [0, 0, 0], "archetype": "buoy", "ship_archetype": "ship1" }"""),
        ("scene_place_patrol_route", """{ "system": REF, "name": "p1", "points": [[0, 0, 0], [0, 0, 1000]] }"""),
        ("scene_set_encounters", """{ "zone": REF, "encounters": [] }"""),
        ("prop_group_place", """{ "group": REF, "system": "Li01", "anchor": [0, 0, 0] }"""),
        ("prop_group_place", """{ "group": "group:trio", "system": "Li01", "anchor": [0, 0, 0], "parent": REF }"""),
        ("prop_group_save", """{ "refs": [REF], "confirm": true }"""),
        ("prop_group_rename", """{ "group": REF, "nickname": "quay", "confirm": true }"""),
        ("prop_group_delete", """{ "group": REF, "confirm": true }"""),
        ("create_from_template", """{ "kind": "equipment", "template": REF, "nickname": "gun9" }"""),
        ("mission_edit", """{ "commands": [ { "kind": "set_item_key", "item": REF, "key": "pos", "lines": [] } ] }"""),
        ("mission_add_trigger", """{ "script": REF, "condition": { "key": "Cnd_True" } }"""),
        ("mission_add_trigger", """{ "script": "msn:DATA\\MISSIONS\\x.ini", "condition": { "key": "Cnd_True" }, "started_by": [REF] }"""),
        ("mission_set_condition", """{ "trigger": REF, "condition": { "key": "Cnd_True" } }"""),
        ("mission_add_action", """{ "trigger": REF, "action": { "key": "Act_ChangeState", "texts": ["FAIL"] } }"""),
        ("mission_add_action", """{ "trigger": "trg:DATA\\MISSIONS\\x.ini#t", "action": { "key": "Act_ChangeState", "texts": ["FAIL"] }, "before": REF }"""),
        ("mission_set_action", """{ "line": REF, "values": { "state": "FAIL" } }"""),
        ("mission_move_action", """{ "line": REF }"""),
        ("mission_move_action", """{ "line": "line:DATA\\MISSIONS\\x.ini#Trigger/t/3", "before": REF }"""),
        ("mission_link_triggers", """{ "from": REF, "to": "t" }"""),
        ("mission_link_triggers", """{ "from": "trg:DATA\\MISSIONS\\x.ini#t", "to": REF }"""),
        ("mission_insert_trigger", """{ "line": REF, "condition": { "key": "Cnd_True" } }"""),
        ("mission_add_item", """{ "script": REF, "kind": "solar", "fields": { "system": "ol_system" } }"""),
    ];

    /// <summary>The paths of a call of a mission tool that are not install paths, or that leave the folder of the
    /// missions (section 8.2, item 4 of the MCP1 design): the msn:, trg: and line: references, the path of a new script
    /// and the keys of the versions. <c>PATH</c> marks the path, as JSON text.</summary>
    private static readonly (string Tool, string Arguments)[] MissionCalls =
    [
        ("mission_edit", """{ "commands": [ { "kind": "add_trigger", "script": "msn:PATH", "nickname": "ol_new" } ] }"""),
        ("mission_edit", """{ "commands": [ { "kind": "remove_trigger", "trigger": "trg:PATH#ol_end" } ], "confirm": true }"""),
        ("mission_edit", """{ "commands": [ { "kind": "create_script", "path": "PATH" } ] }"""),
        ("mission_edit", """{ "commands": [ { "kind": "set_condition_value", "trigger": "trg:DATA\\MISSIONS\\OL_TEST\\ol_test.ini#ol_wave", "name": "seconds", "value": 9 } ], "expect_versions": { "PATH": 1 } }"""),
        ("mission_add_trigger", """{ "script": "msn:PATH", "condition": { "key": "Cnd_True" } }"""),
        ("mission_add_trigger", """{ "script": "msn:DATA\\MISSIONS\\OL_TEST\\ol_test.ini", "condition": { "key": "Cnd_True" }, "started_by": ["trg:PATH#ol_start"] }"""),
        ("mission_set_condition", """{ "trigger": "trg:PATH#ol_wave", "condition": { "key": "Cnd_True" } }"""),
        ("mission_add_action", """{ "trigger": "trg:PATH#ol_wave", "action": { "key": "Act_ChangeState", "texts": ["FAIL"] } }"""),
        ("mission_add_action", """{ "trigger": "trg:DATA\\MISSIONS\\OL_TEST\\ol_test.ini#ol_wave", "action": { "key": "Act_ChangeState", "texts": ["FAIL"] }, "before": "line:PATH#Trigger/ol_wave/3" }"""),
        ("mission_set_action", """{ "line": "line:PATH#Trigger/ol_wave/3", "values": { "ship": "ol_ship_3" } }"""),
        ("mission_move_action", """{ "line": "line:PATH#Trigger/ol_wave/3" }"""),
        ("mission_link_triggers", """{ "from": "trg:PATH#ol_wave", "to": "ol_end" }"""),
        ("mission_link_triggers", """{ "from": "trg:DATA\\MISSIONS\\OL_TEST\\ol_test.ini#ol_wave", "to": "trg:PATH#ol_end" }"""),
        ("mission_insert_trigger", """{ "line": "line:PATH#Trigger/ol_start/3", "condition": { "key": "Cnd_True" } }"""),
        ("mission_add_item", """{ "script": "msn:PATH", "kind": "ship", "fields": { "npc": "ol_npc_1" } }"""),
    ];

    /// <summary>The details of a refused path: the install path check, and the read of a reference.</summary>
    private static readonly string?[] PathDetails = [InstallPaths.NotInstallPath, ItemRefs.BadReference];

    /// <summary>The bad paths for <see cref="MissionCalls"/>: a drive, a stream name, a <c>..</c> above the root and a
    /// rooted path.</summary>
    private static readonly string[] BadMissionPaths =
    [
        @"C:\x.ini", @"C:x.ini", @"DATA\MISSIONS\x.ini:stream", @"..\x.ini", @"DATA\..\..\x.ini", @"\x.ini",
        @"\\server\share\x.ini",
    ];

    /// <summary>Each path of a mission tool that is not an install path gives <c>invalid_argument</c>: a path of a
    /// reference, the path of a new script and a key of <c>expect_versions</c>. The detail is the detail of the path
    /// check, so the path went through the check. Another refusal of the same code, for example a trigger of another
    /// script, does not count. A new script outside <c>DATA\MISSIONS</c> gives <c>invalid_argument</c> with the detail
    /// <c>BadPath</c>. Nothing changes.</summary>
    [Fact]
    public void TheMissionToolsRefuseEachPathThatIsNotAnInstallPath()
    {
        using var m = MissionEdits.Create();
        var host = new TestAgentHost(m.Session);
        var before = m.State();

        var wrong = (from call in MissionCalls
            from path in BadMissionPaths
            let arguments = call.Arguments.Replace("PATH", Json(path)[1..^1])
            let error = Error(host, call.Tool, arguments)
            where error.Code != "invalid_argument" || !PathDetails.Contains(error.Detail)
            select $"{call.Tool} {arguments}: {error.Code} {error.Detail} {error.Message}").ToList();
        var tools = MissionCalls.Select(c => c.Tool).Distinct().Order();
        var outside = Error(host, "mission_edit", """{ "commands": [ { "kind": "create_script", "path": "DATA\\SHIPS\\ol_new.ini" } ] }""");

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.Equal(EditToolNames().Where(t => t.StartsWith("mission_", StringComparison.Ordinal)).Order(), tools);
        Assert.Equal(("invalid_argument", "BadPath"), (outside.Code, outside.Detail));
        Assert.Equal(before, m.State());
        Assert.False(m.Session.CanUndo);
    }

    [Fact]
    public void EachToolWithAReferenceRefusesAPathThatIsNotAnInstallPath()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        PropGroupCommands.Save(session, PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]), "trio", "Trio");
        var groups = PropGroups.Version(session);

        var wrong = (from call in Calls
            from reference in BadReferences
            let error = Error(host, call.Tool, call.Arguments.Replace("REF", Json(reference)))
            where error.Code != "invalid_argument"
            select $"{call.Tool} {call.Arguments} {reference}: {error.Code} {error.Message}").ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.Equal(0, session.Revision);
        Assert.False(session.CanUndo);
        Assert.Equal(groups, PropGroups.Version(session));
    }

    /// <summary>The table has each edit tool and each asset write of this build that takes a reference.
    /// <c>create_loadout</c> takes no reference: its nickname, its archetype and its rows are values of
    /// lists.</summary>
    [Fact]
    public void TheTableHasEachEditToolOfThisBuild()
    {
        var tools = Calls.Select(c => c.Tool).Distinct().Order().ToList();

        Assert.Equal(EditToolNames().Except(["create_loadout"]).Order(), tools);
    }

    /// <summary>The edit tools and the asset writes of this build.</summary>
    private static string[] EditToolNames() => AgentTools.All
        .Where(t => t.Kind is ToolKind.Edit or ToolKind.AssetWrite)
        .Select(t => t.Name).ToArray();
}
