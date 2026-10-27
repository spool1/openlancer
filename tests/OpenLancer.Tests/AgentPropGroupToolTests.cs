using System.Linq;
using System.Numerics;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The prop group tools of section 7.3 of the MCP1 design. A placement is one entry of the edit history.
/// </summary>
public sealed class AgentPropGroupToolTests
{
    /// <summary>A placement is one entry with the W of the command of the UI. Its result selects the first member, as a
    /// Duplicate does (decision D288 of the M23 design).</summary>
    [Fact]
    public void APlacementIsOneEntryWithTheWOfTheCommandOfTheUi()
    {
        using var a = GroupWorkspace.Create();
        var expected = a.Session("act");
        var group = PropGroupCommands.Save(expected, PropGroups.Draft(expected, [SceneWorkspace.Object("Li01_14")]), "trio",
            "Trio").Group!;
        FakeCommandHost.Runner(expected).Run(PropGroupCommands.Place(expected, "Li01", group, new Vector3(0, 0, 5000),
            SceneWorkspace.Object("Li01_dock")));

        using var b = GroupWorkspace.Create();
        var session = b.Session("act");
        var host = new TestAgentHost(session);
        PropGroupCommands.Save(session, PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]), "trio", "Trio");
        var preview = Ok(host, "prop_group_place",
            """{ "group": "group:trio", "system": "Li01", "anchor": [0, 0, 5000], "parent": "obj:Li01_dock", "preview": true }""");
        var result = Ok(host, "prop_group_place",
            """{ "group": "group:trio", "system": "Li01", "anchor": [0, 0, 5000], "parent": "obj:Li01_dock" }""");

        Assert.Equal(AgentSceneToolTests.W(expected, SceneWorkspace.Li01Path), AgentSceneToolTests.W(session, SceneWorkspace.Li01Path));
        Assert.Equal("Agent: Place Trio", (string?)result["undo_label"]);
        Assert.Equal(Texts(preview["adds"]), Texts(result["adds"]));
        Assert.Equal(3, Texts(result["adds"]).Length);
        Assert.Equal(Texts(result["adds"])[0], (string?)result["select"]);
        Assert.Equal(1, session.Revision);
    }

    /// <summary>A member line can name an entry that the profile does not have. Such a line refuses the placement, and
    /// the error gives each such line (decision D290 of the M23 design). leave_out_error_lines places the rest, as "Place
    /// without these lines" does.</summary>
    [Fact]
    public void APlacementWithErrorLinesRefusesUntilTheAgentLeavesThemOut()
    {
        using var ws = GroupWorkspace.Create();
        GroupWorkspace.WriteGroup(ws, "lost", "[PropGroup]\nnickname = lost\nformat = 1\n[Object]\nmember = a\n" +
                                              "archetype = station1\nloadout = lo_missing\n");
        var session = ws.Session("act");
        var host = new TestAgentHost(session);

        var refused = Error(host, "prop_group_place", """{ "group": "group:lost", "system": "Li01", "anchor": [0, 0, 0] }""");
        var placed = Ok(host, "prop_group_place",
            """{ "group": "group:lost", "system": "Li01", "anchor": [0, 0, 0], "leave_out_error_lines": true }""");

        Assert.Equal(("refused", SceneEditTools.PlacementErrors), (refused.Code, refused.Detail));
        Assert.StartsWith("Some lines of group:lost name entries that this profile does not have.", refused.Message);
        Assert.Contains("a, loadout: ", refused.Message);
        Assert.Equal("applied", (string?)placed["status"]);
        Assert.Contains(placed["warnings"]!.AsArray(), w => ((string)w!["message"]!).Contains("the 'loadout' line goes."));
        Assert.Equal(1, session.Revision);
    }
}
