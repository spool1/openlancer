using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the checks of the jump connections and of the rule data of a new connection: each jump object of
/// the game passes the checks, no system has more than 9 jump objects, and the infocards of the rule data are texts of
/// both games. The checks find the six gate locks of vanilla 1.1 that name no object.
/// </summary>
public sealed class ConnectionRetailTests(ITestOutputHelper output)
{
    static ConnectionRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// Vanilla 1.1 has 232 objects with goto, at most 9 in one system. Each pair goes both ways and has one kind, so no
    /// jump object gets a problem. Six NPC locks of <c>InitialWorld.ini</c> name no object of the game, and HD Edition
    /// removes these six lines. So they are the only warnings.
    /// </summary>
    [Fact]
    public void EachJumpObjectOfVanillaPassesTheConnectionChecks()
    {
        var (jumps, most, problems) = Check(GameDataPaths.RequireBase());

        Assert.Equal(232, jumps);
        Assert.Equal(9, most);
        Assert.Equal(6, problems.Count);
        Assert.All(problems, p => Assert.Equal(("locked_gates", DiagnosticSeverity.Warning, "npc_locked_gate"),
            (p.Target!.Id.Type, p.Severity, p.Key)));
    }

    /// <summary>HD Edition 0.7.1 has the same jump objects. Its gate locks name jump objects only, so the checks give no
    /// problem at all.</summary>
    [Fact]
    public void EachJumpObjectOfHdEditionPassesTheConnectionChecks()
    {
        var (jumps, most, problems) = Check(GameDataPaths.RequireHde());

        Assert.Equal(232, jumps);
        Assert.Equal(9, most);
        Assert.Empty(problems);
    }

    /// <summary>The infocard of a new jump gate and of a new jump hole is a text of both games.</summary>
    [Fact]
    public void TheInfocardsOfTheRuleDataAreTextsOfBothGames()
    {
        var rule = ListRules.Retail.Builders.Connection!;
        foreach (var root in new[] { GameDataPaths.RequireBase(), GameDataPaths.RequireHde() })
        {
            var texts = new InfocardManager(new FreelancerIni(FileSystem.FromPath(root)).Resources);
            foreach (var id in new[] { rule.Gate.Infocard, rule.Hole.Infocard })
            {
                Assert.True(texts.HasXmlResource(id), $"{root}: {id}");
                Assert.False(string.IsNullOrWhiteSpace(texts.GetXmlResource(id)), $"{root}: {id}");
            }
        }
    }

    /// <summary>The count of the jump objects, the most of one system, and the problems of the checks of each jump
    /// object and each gate lock section of a game.</summary>
    private (int Jumps, int Most, List<Diagnostic> Problems) Check(string root)
    {
        using var workspace = new RetailWorkspace(root, "olconnect");
        var session = workspace.Open();
        var problems = new List<Diagnostic>();
        var jumps = 0;
        var most = 0;
        foreach (var system in session.Routes.Graph.Systems.Where(s => s.File != null))
        {
            var objects = session.Sections(system.File!)
                .Where(s => s.Id.Type.Equals("Object", StringComparison.OrdinalIgnoreCase) &&
                            session.Get(s) is { } view && ConnectionChecks.IsJump(view))
                .ToList();
            jumps += objects.Count;
            most = Math.Max(most, objects.Count);
            foreach (var item in objects)
                problems.AddRange(ConnectionChecks.Check(session, item));
        }
        foreach (var path in session.DataFiles.Paths(UniverseGraph.LockRole))
        {
            foreach (var section in session.Sections(path).Where(s => s.Id.Type.Equals("locked_gates", StringComparison.OrdinalIgnoreCase)))
                problems.AddRange(ConnectionChecks.Check(session, section));
        }
        output.WriteLine($"{root}: {jumps} jump objects, at most {most} in one system, {problems.Count} problems");
        foreach (var problem in problems)
            output.WriteLine(problem.ToString());
        return (jumps, most, problems);
    }
}
