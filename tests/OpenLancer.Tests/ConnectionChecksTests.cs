using System.IO;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The checks of the jump connections: two errors that stop the save and five warnings, each with its text. A problem
/// that the active mod does not cause is a warning. The data is invented.
/// </summary>
public sealed class ConnectionChecksTests
{
    private const string Count = "Li03 has 11 jump objects. The game crashes with more than 10 in one system. Remove 1 of them.";

    private static SectionRef Li03(string key) => ConnectionWorkspace.Li03("Object", key);

    /// <summary>Li03 with nine jump holes that go to the gate of Li01.</summary>
    private static string NineJumps(string extra = "") => ConnectionWorkspace.Li03Text + string.Concat(
        Enumerable.Range(1, 9).Select(i => $"\n[Object]\nnickname = Li03_jump_{i}\narchetype = jumphole\n" +
                                           "goto = Li01, Li01_to_Li02, gate_tunnel_bretonia\n")) + extra;

    private static void AddJump(EditSession session, string nickname) =>
        session.Execute(EditCommand.AddSection(ConnectionWorkspace.Li03Path, "Object",
        [
            ListWorkspace.Line("nickname", nickname), ListWorkspace.Line("archetype", "jumphole"),
            ListWorkspace.Line("goto", "Li01", "Li01_to_Li02", "gate_tunnel_bretonia"),
        ]));

    private static Diagnostic One(EditSession session, SectionRef section, string start) =>
        Assert.Single(ObjectComponents.Checks(session, section), p => p.Message.StartsWith(start));

    /// <summary>
    /// More than 10 jump objects in one system crash the game. The save gate gives one error for the file. This is also
    /// true for a data mod with a retail target, whose content checks count the jump objects too. A data mod with only the target
    /// OpenLancer gets the same error. A mod of tier 2 gets a warning, because only OpenLancer runs it.
    /// </summary>
    [Theory]
    [InlineData(null, DiagnosticSeverity.Error)]
    [InlineData("""{ "id": "act", "formatVersion": 2, "tier": 1, "targets": [ "openlancer" ] }""", DiagnosticSeverity.Error)]
    [InlineData("""{ "id": "act", "formatVersion": 2, "tier": 2, "targets": [ "openlancer" ] }""", DiagnosticSeverity.Warning)]
    public void MoreThanTenJumpObjectsAreOneErrorOfTheSaveGate(string? manifest, DiagnosticSeverity severity)
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ConnectionWorkspace.Li03Path, NineJumps());
        if (manifest != null)
            File.WriteAllText(Path.Combine(ws.ModFolder("act"), ModManifest.FileName), manifest);
        var session = ws.Session("act");
        AddJump(session, "Li03_jump_10");
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.Contains("jump objects"));
        AddJump(session, "Li03_jump_11");

        var problems = session.SaveProblems();
        var inspector = One(session, Li03("Li03_jump_1"), Count);

        Assert.Equal(severity, inspector.Severity);
        Assert.DoesNotContain(problems, p => p.Message.Contains("jump holes and jump gates"));
        if (severity == DiagnosticSeverity.Error)
        {
            var error = Assert.Single(problems, p => p.Message.Contains("jump objects"));
            Assert.Equal(Count, error.Message);
            Assert.Equal((Li03("Li03_jump_11"), "goto"), (error.Target, error.Key));
            Assert.False(session.Save().Saved);
        }
        else
            Assert.DoesNotContain(problems, p => p.Message.Contains("jump objects"));
    }

    /// <summary>A file of the lower layer with more than 10 jump objects gives a warning only. This is also true when
    /// the active mod changes another object of the file.</summary>
    [Fact]
    public void TooManyJumpObjectsOfTheLowerLayerAreAWarning()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ConnectionWorkspace.Li03Path, NineJumps(
            "\n[Object]\nnickname = Li03_jump_10\narchetype = jumphole\ngoto = Li01, Li01_to_Li02, t\n" +
            "[Object]\nnickname = Li03_jump_11\narchetype = jumphole\ngoto = Li01, Li01_to_Li02, t\n"));
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Li03("Li03_station"), "pos", ["1, 2, 3"]));

        Assert.Equal(DiagnosticSeverity.Warning, One(session, Li03("Li03_jump_11"), Count).Severity);
        Assert.True(session.Save().Saved);
    }

    /// <summary>A goto line before the nickname line records the jump under another object. It is an error when the
    /// active mod writes it, and a warning for a line of the game.</summary>
    [Fact]
    public void AGotoLineBeforeTheNicknameLineIsAnErrorOfTheActiveMod()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ConnectionWorkspace.Li03Path, ConnectionWorkspace.Li03Text +
                                                   "\n[Object]\ngoto = Li01, Li01_to_Li02, t\nnickname = Li03_old\narchetype = jumphole\n");
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(ConnectionWorkspace.Li03Path, "Object",
        [
            ListWorkspace.Line("goto", "Li01", "Li01_to_Li02", "gate_tunnel_bretonia"), ListWorkspace.Line("nickname", "Li03_new"),
            ListWorkspace.Line("archetype", "jumphole"),
        ]));

        var error = One(session, Li03("Li03_new"), "Li03_new has its goto line");
        var warning = One(session, Li03("Li03_old"), "Li03_old has its goto line");

        Assert.Equal("Li03_new has its goto line before its nickname line. The game then records the jump under another " +
                     "object. Move the nickname line up.", error.Message);
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Single(session.SaveProblems(), p => p.Message == error.Message);
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.StartsWith("Li03_old"));
    }

    /// <summary>
    /// A partner that does not go back gives a warning that names the fix. This is also true for a partner that goes
    /// back to another object of the same system. A pair of two kinds and a goto line on an
    /// archetype that is no jump give warnings too. A partner that does not exist gives no warning here, because the list
    /// checks report it. The agent check of the item gives the warning.
    /// </summary>
    [Fact]
    public void OneWayMixedAndNoJumpObjectsGiveWarnings()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ConnectionWorkspace.Li03Path, ConnectionWorkspace.Li03Text.Replace("archetype = station1",
            "archetype = station1\ngoto = Li01, Li01_to_Li09, t", System.StringComparison.Ordinal) + """

            [Object]
            nickname = Li03_oneway
            archetype = jumphole
            goto = Li01, Li01_to_Li02, gate_tunnel_bretonia
            [Object]
            nickname = Li03_mixed
            archetype = jumphole
            goto = Li02, Li02_mixed, gate_tunnel_bretonia
            [Object]
            nickname = Li03_other
            archetype = jumphole
            goto = Li02, Li02_mixed, gate_tunnel_bretonia
            """);
        ws.WriteBase(ListWorkspace.Li02Path, NewSystemWorkspace.Li02Text +
                                             "\n[Object]\nnickname = Li02_mixed\narchetype = jumpgate\ngoto = Li03, Li03_mixed, t\n");
        var host = new TestAgentHost(ws.Session("act"));
        var session = host.Session!;
        const string oneWay = "Li03_oneway goes to Li01_to_Li02 in Li01, but Li01_to_Li02 does not go back. The player cannot " +
                              "jump back. To write the missing goto line, use the jump connection tool connection_create of " +
                              "the agent server with Li03_oneway as one end.";

        Assert.Equal(oneWay, One(session, Li03("Li03_oneway"), "Li03_oneway goes").Message);
        // Li02_mixed goes back to Li03, but to another object of Li03.
        Assert.StartsWith("Li03_other goes to Li02_mixed in Li02, but Li02_mixed does not go back.",
            One(session, Li03("Li03_other"), "Li03_other goes").Message);
        Assert.Equal("Li03_mixed is a jump hole and its partner Li02_mixed is a jump gate. The game counts each end in another " +
                     "route table. Delete the pair with the agent tool delete_items, and make it again with connection_create.",
            One(session, Li03("Li03_mixed"), "Li03_mixed is").Message);
        Assert.Equal("Li03_station has a goto line, but its archetype station1 is a STATION. The player cannot use it as a jump.",
            Assert.Single(ObjectComponents.Checks(session, Li03("Li03_station"))).Message);
        Assert.All(new[] { "Li03_oneway", "Li03_mixed", "Li03_station" }.SelectMany(n => ObjectComponents.Checks(session, Li03(n))),
            p => Assert.Equal((DiagnosticSeverity.Warning, "goto"), (p.Severity, p.Key)));
        Assert.Empty(ObjectComponents.Checks(session, SceneWorkspace.Li02("Object", "Li02_to_Li01")));
        var component = ObjectComponents.Of(session, Li03("Li03_oneway")).Single(c => c.Kind == ObjectComponent.JumpConnection);
        Assert.Equal(oneWay, Assert.Single(component.Problems).Message);

        var check = Ok(host, "check", """{ "scope": "item", "ref": "obj:Li03_oneway" }""");
        var problem = Assert.Single(check["problems"]!.AsArray(), p => (string?)p!["message"] == oneWay)!;
        Assert.Equal("warning", (string?)problem["severity"]);
        Assert.Contains(AgentRows.DiagnosticsOf(session, Li03("Li03_oneway")), p => p.Message == oneWay);
    }

    /// <summary>A gate lock that names no jump gate or jump hole gives a warning on its line, and the raw INI block marks
    /// that line. The locks of existing gates give none.</summary>
    [Fact]
    public void AGateLockOfNoGateGivesAWarning()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(ConnectionWorkspace.Locks, "locked_gate",
            [FLHash.CreateID("Li01_to_Li02").ToString(), "12345"]));

        var problem = Assert.Single(ObjectComponents.Checks(session, ConnectionWorkspace.Locks));

        Assert.Equal("The gate lock 12345 in initialworld.ini names no jump gate or jump hole. The game ignores the line.",
            problem.Message);
        Assert.Equal((DiagnosticSeverity.Warning, "locked_gate", 1), (problem.Severity, problem.Key, problem.EntryIndex));
        Assert.Contains(AgentRows.DiagnosticsOf(session, ConnectionWorkspace.Locks), p => p.Message == problem.Message);
        // Line 1 of the raw block is the header, so the second line of the section is line 3.
        Assert.Equal(3, Assert.Single(SectionExtras.SectionProblems(session, ConnectionWorkspace.Locks,
            session.Get(ConnectionWorkspace.Locks)!)).Line);
    }

    /// <summary>A player lock that the active mod adds warns on the gate. A gate of the game gets the sentence about the
    /// story. A lock of the lower layer gives no warning.</summary>
    [Fact]
    public void APlayerLockOfTheActiveModWarnsOnTheGate()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var gate = SceneWorkspace.Li02("Object", "Li02_to_Li01");
        session.Execute(ConnectionPlans.SetLock(session, gate, JumpLock.Player).Command!);

        var warning = One(session, gate, "The route tables leave out Li02_to_Li01");

        Assert.Equal((DiagnosticSeverity.Warning, "goto"), (warning.Severity, warning.Key));
        Assert.EndsWith("The story can depend on this lock.", warning.Message);
        Assert.Empty(ObjectComponents.Checks(session, SceneWorkspace.Object("Li01_to_Li02")));
        Assert.True(session.Save().Saved);
    }
}
