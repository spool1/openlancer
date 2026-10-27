using System;
using System.Linq;
using OpenLancer.Data.Editing.Assets;
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
        ("prop_group_place", """{ "group": REF, "system": "Li01", "anchor": [0, 0, 0] }"""),
        ("prop_group_place", """{ "group": "group:trio", "system": "Li01", "anchor": [0, 0, 0], "parent": REF }"""),
        ("prop_group_save", """{ "refs": [REF], "confirm": true }"""),
        ("prop_group_rename", """{ "group": REF, "nickname": "quay", "confirm": true }"""),
        ("prop_group_delete", """{ "group": REF, "confirm": true }"""),
        ("create_from_template", """{ "kind": "equipment", "template": REF, "nickname": "gun9" }"""),
    ];

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

    /// <summary>The table has each edit tool and each asset write of this build. Each of them takes a
    /// reference.</summary>
    [Fact]
    public void TheTableHasEachEditToolOfThisBuild()
    {
        var tools = Calls.Select(c => c.Tool).Distinct().Order().ToList();

        Assert.Equal(EditToolNames().Order(), tools);
    }

    /// <summary>The edit tools and the asset writes of this build.</summary>
    private static string[] EditToolNames() => AgentTools.All
        .Where(t => t.Kind is ToolKind.Edit or ToolKind.AssetWrite)
        .Select(t => t.Name).ToArray();
}
