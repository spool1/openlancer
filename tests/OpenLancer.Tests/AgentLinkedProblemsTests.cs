using System;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The problems that an edit adds to and removes from the items that the checks link to the items of the edit (#97).
/// Examples are the system that a new jump gate leads to, the partner of a gate whose lock changes, the base of a dock
/// object and the other rings of a trade lane. The result gives each such problem in <c>new_problems</c> or
/// <c>fixed_problems</c>, as <c>check</c> gives it after the edit. The data is invented.
/// </summary>
public sealed class AgentLinkedProblemsTests
{
    /// <summary>The arguments of Av01 like Li02 at 4, 5.</summary>
    private const string Av01 = """ "nickname": "Av01", "name": "Avalon", "like": "Li02", "map_pos": [4, 5] """;

    /// <summary>The start of the warning of a system that no jump leads to.</summary>
    private const string NoJump = "No jump gate or jump hole leads to Av01.";

    /// <summary>The problems of a result as pairs of the reference and the message.</summary>
    private static (string? Ref, string Message)[] Problems(JsonNode? problems) =>
        problems?.AsArray().Select(p => ((string?)p!["ref"], (string)p["message"]!)).ToArray() ?? [];

    /// <summary>The references of the problems of a result whose message starts with a text.</summary>
    private static string[] RefsOf(JsonNode? problems, string start) =>
        Problems(problems).Where(p => p.Message.StartsWith(start, StringComparison.Ordinal)).Select(p => p.Ref ?? "")
            .ToArray();

    /// <summary>The messages of the problems of <c>check</c> on one item.</summary>
    private static string[] Check(TestAgentHost host, string item) =>
        Problems(Ok(host, "check", $$"""{ "scope": "item", "ref": "{{item}}" }""")["problems"]).Select(p => p.Message)
            .ToArray();

    /// <summary>The universe of <see cref="ConnectionWorkspace"/> with the new system Av01 and a gate pair from Av01 to
    /// Li02.</summary>
    private static (TestWorkspace Ws, TestAgentHost Host) Av01WithGate()
    {
        var ws = ConnectionWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));
        Ok(host, "system_create", $$"""{ {{Av01}} }""");
        Ok(host, "connection_create", """{ "kind": "jump_gate", "from": { "system": "Av01" }, "to": { "system": "Li02" } }""");
        return (ws, host);
    }

    /// <summary>
    /// Case 1 of #97: system_create warns that no jump leads to the new system. connection_create from the new system
    /// then removes the warning, and its result gives the warning in <c>fixed_problems</c> on the system. The preview
    /// gives the same. The warning is on no item that the call names: the new gate in Li02 leads to the system.
    /// </summary>
    [Fact]
    public void ConnectionCreateGivesTheFixedWarningOfTheNewSystem()
    {
        using var ws = ConnectionWorkspace.Create();
        var host = new TestAgentHost(ws.Session("act"));
        var system = Ok(host, "system_create", $$"""{ {{Av01}} }""");
        const string pair = """ "kind": "jump_gate", "from": { "system": "Av01" }, "to": { "system": "Li02" } """;

        var preview = Ok(host, "connection_create", $$"""{ {{pair}}, "preview": true }""");
        var result = Ok(host, "connection_create", $$"""{ {{pair}} }""");

        Assert.Equal(["system:Av01"], RefsOf(system["new_problems"], NoJump));
        Assert.Equal(["system:Av01"], RefsOf(preview["fixed_problems"], NoJump));
        Assert.Equal(["system:Av01"], RefsOf(result["fixed_problems"], NoJump));
        Assert.Empty(RefsOf(result["new_problems"], NoJump));
        Assert.DoesNotContain(Check(host, "system:Av01"), m => m.StartsWith(NoJump, StringComparison.Ordinal));
    }

    /// <summary>
    /// Cases 2 and 3 of #97: connection_set_lock with a player lock locks the gate and its partner. The result gives the
    /// lock warning of each end in <c>new_problems</c>, as <c>check</c> gives it on each end. The unlock gives the
    /// warning of each end in <c>fixed_problems</c>.
    /// </summary>
    [Fact]
    public void ConnectionSetLockGivesTheLockWarningOfBothEnds()
    {
        var (ws, host) = Av01WithGate();
        using var _ = ws;
        const string warning = "The route tables leave out ";

        var locked = Ok(host, "connection_set_lock", """{ "object": "obj:Av01_to_Li02", "locked": "player" }""");
        var partner = Check(host, "obj:Li02_to_Av01");
        var unlocked = Ok(host, "connection_set_lock", """{ "object": "obj:Av01_to_Li02", "locked": "none" }""");

        Assert.Equal(["obj:Av01_to_Li02", "obj:Li02_to_Av01"], RefsOf(locked["new_problems"], warning).Order().ToArray());
        Assert.Contains(partner, m => m.StartsWith(warning + "Li02_to_Av01", StringComparison.Ordinal));
        Assert.Equal(["obj:Av01_to_Li02", "obj:Li02_to_Av01"], RefsOf(unlocked["fixed_problems"], warning).Order().ToArray());
        Assert.Empty(RefsOf(unlocked["new_problems"], warning));
    }

    /// <summary>The delete of the only gate pair of a system gives the warning that no jump leads to the system in
    /// <c>new_problems</c>, as <c>check</c> gives it. The system is no item of the delete.</summary>
    [Fact]
    public void TheDeleteOfAGatePairGivesTheNewWarningOfItsSystem()
    {
        var (ws, host) = Av01WithGate();
        using var _ = ws;

        var result = Ok(host, "delete_items", """{ "refs": ["obj:Li02_to_Av01"], "confirm": true }""");

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal(["system:Av01"], RefsOf(result["new_problems"], NoJump));
        Assert.Contains(Check(host, "system:Av01"), m => m.StartsWith(NoJump, StringComparison.Ordinal));
    }

    /// <summary>
    /// Case 4 of #97: set_text on the station of a new base gives the station its own name text. So no object with the
    /// base line shows the name of the base. The result gives the warning of the base in <c>new_problems</c>, as
    /// <c>get_base</c> gives it.
    /// </summary>
    [Fact]
    public void SetTextOnTheStationOfANewBaseGivesTheNewWarningOfTheBase()
    {
        using var ws = BasePlansCreateTests.Workspace(true);
        var host = new TestAgentHost(ws.Session("act"));
        Ok(host, "base_create", """
            { "nickname": "Ol01_05_Base", "system": "Ol01", "template": "Ol01_01_Base", "name": "Harbor Rock",
              "dock_object": { "position": [5000, 0, -3000] } }
            """);
        const string warning = "No object with 'base = Ol01_05_Base' shows the name of the base.";

        var result = Ok(host, "set_text", """{ "ref": "obj:ol01_station_1", "field": "ids_name", "text": "Harbor Rock Two" }""");

        Assert.Equal(["base:Ol01_05_Base"], RefsOf(result["new_problems"], warning));
        var problems = Ok(host, "get_base", """{ "base": "Ol01_05_Base" }""")["problems"];
        Assert.Contains(Problems(problems), p => p.Message.StartsWith(warning, StringComparison.Ordinal));
    }

    /// <summary>
    /// A lane that goes in a circle has the circle error on each of its four rings. trade_lane_repair on ring 1 ends the
    /// circle with new links of ring 1 and ring 4 only. The result gives the fixed error of each ring, also of ring 2 and
    /// ring 3, whose links stay the same.
    /// </summary>
    [Fact]
    public void TheRepairOfACircleGivesTheFixedErrorOfEachRing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(LaneWorkspace.Ring(4), "next_ring", ["Li01_Trade_Lane_Ring_1"]));
        session.Execute(EditCommand.SetLinesFromText(LaneWorkspace.Ring(1), "prev_ring", ["Li01_Trade_Lane_Ring_4"]));
        var host = new TestAgentHost(session);

        var result = Ok(host, "trade_lane_repair", """{ "ring": "obj:Li01_Trade_Lane_Ring_1" }""");

        Assert.DoesNotContain("obj:Li01_Trade_Lane_Ring_2", AgentEditToolTests.Texts(result["changes"]));
        var circles = Problems(result["fixed_problems"]).Where(p => p.Message.Contains("goes in a circle", StringComparison.Ordinal))
            .Select(p => p.Ref ?? "").Order().ToArray();
        Assert.Equal([.. Enumerable.Range(1, 4).Select(n => $"obj:Li01_Trade_Lane_Ring_{n}")], circles);
        Assert.Empty(Problems(result["new_problems"]));
    }
}
