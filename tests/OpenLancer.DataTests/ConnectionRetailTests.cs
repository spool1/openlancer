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
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the checks of the jump connections and of the rule data of a new connection. Each jump object of
/// the game passes the checks, and no system has more than 9 jump objects. The infocards of the rule data are texts of
/// both games. The checks find the six gate locks of vanilla 1.1 that name no object.
/// </summary>
public sealed class ConnectionRetailTests(ITestOutputHelper output)
{
    static ConnectionRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// Vanilla 1.1 has 232 objects with goto, at most 9 in one system. Each pair goes both ways and has one kind, so no
    /// jump object gets a problem, and the Inspector shows each jump gate and jump hole complete. Six NPC locks of <c>InitialWorld.ini</c> name no object of the game, and HD Edition
    /// removes these six lines. So they are the only warnings.
    /// </summary>
    [Fact]
    public void EachJumpObjectOfVanillaPassesTheConnectionChecks()
    {
        var (jumps, most, problems, incomplete) = Check(GameDataPaths.RequireBase());

        Assert.Equal(232, jumps);
        Assert.Equal(9, most);
        Assert.Empty(incomplete);
        Assert.Equal(6, problems.Count);
        Assert.All(problems, p => Assert.Equal(("locked_gates", DiagnosticSeverity.Warning, "npc_locked_gate"),
            (p.Target!.Id.Type, p.Severity, p.Key)));
    }

    /// <summary>HD Edition 0.7.1 has the same jump objects, and each jump gate and jump hole is complete. Its gate locks
    /// name jump objects only, so the checks give no problem at all.</summary>
    [Fact]
    public void EachJumpObjectOfHdEditionPassesTheConnectionChecks()
    {
        var (jumps, most, problems, incomplete) = Check(GameDataPaths.RequireHde());

        Assert.Equal(232, jumps);
        Assert.Equal(9, most);
        Assert.Empty(incomplete);
        Assert.Empty(problems);
    }

    /// <summary>
    /// The six NPC locks of vanilla 1.1 that name no object hold the FLHash of the plain nicknames of three hole pairs.
    /// The system files do not have these holes. A new hole pair with such a nickname gets the lock of the request, so a
    /// pair without a lock removes the lines of its two ends.
    /// </summary>
    [Fact]
    public void ANewHolePairWithTheNicknameOfAVanillaLockGetsTheLockOfTheRequest()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "olconnect");
        var session = workspace.Open();
        var holes = new[]
        {
            "Li01_to_Ew01_hole", "Ew01_to_Li01_hole", "Li04_to_Bw05_hole", "Bw05_to_Li04_hole", "Li05_to_St01_hole",
            "St01_to_Li05_hole",
        }.Select(FLHash.CreateID).ToList();
        Assert.Equal(holes.Order(), LocksOfNoObject(session).Order());

        var plan = ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li01"), new ConnectionEnd("Ew01"));
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);

        Assert.Equal(holes.Take(2), plan.Ends.Select(e => FLHash.CreateID(e.Object.Id.Key!)));
        Assert.Equal(holes.Skip(2).Order(), LocksOfNoObject(session).Order());
        Assert.Empty(GateLocks.Of(session, "Li01_to_Ew01_hole"));
        Assert.Empty(plan.Warnings);
    }

    /// <summary>The hashes of the gate lock lines that name no jump object.</summary>
    private static List<uint> LocksOfNoObject(EditSession session)
    {
        var result = new List<uint>();
        foreach (var path in session.DataFiles.Paths(UniverseGraph.LockRole))
        {
            foreach (var section in session.Sections(path).Where(s => s.Id.Type.Equals("locked_gates", StringComparison.OrdinalIgnoreCase)))
            {
                var view = session.Get(section)!;
                result.AddRange(ConnectionChecks.Check(session, section)
                    .Select(p => ItemLists.HashOf(view.Entries[p.EntryIndex].Values[0])!.Value));
            }
        }
        return result;
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

    /// <summary>The count of the jump objects of a game and the most of one system. Also the problems of the checks of
    /// each jump object and each gate lock section, and the missing steps of the completeness of each jump gate and jump
    /// hole.</summary>
    private (int Jumps, int Most, List<Diagnostic> Problems, List<string> Incomplete) Check(string root)
    {
        using var workspace = new RetailWorkspace(root, "olconnect");
        var session = workspace.Open();
        var problems = new List<Diagnostic>();
        var incomplete = new List<string>();
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
            {
                problems.AddRange(ConnectionChecks.Check(session, item));
                incomplete.AddRange(ComponentCompleteness.JumpConnection(session, item)?.Missing
                    .Select(s => $"{item.Id.Key}: {s.Name}: {s.Detail}") ?? []);
            }
        }
        foreach (var path in session.DataFiles.Paths(UniverseGraph.LockRole))
        {
            foreach (var section in session.Sections(path).Where(s => s.Id.Type.Equals("locked_gates", StringComparison.OrdinalIgnoreCase)))
                problems.AddRange(ConnectionChecks.Check(session, section));
        }
        output.WriteLine($"{root}: {jumps} jump objects, at most {most} in one system, {problems.Count} problems");
        foreach (var problem in problems)
            output.WriteLine(problem.ToString());
        incomplete.ForEach(output.WriteLine);
        return (jumps, most, problems, incomplete);
    }
}
