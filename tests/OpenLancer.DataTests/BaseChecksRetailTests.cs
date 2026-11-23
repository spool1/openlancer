using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The base checks on the retail data (section H.2 of the bases design). The retail data gives no error of the base
/// checks. The door of Br05_02_Base without <c>room_switch</c> gives no finding. The tests list the warnings of the
/// base game by kind, with their counts, for vanilla 1.1 and for HD Edition 0.7.1. They only read the game folders.
/// </summary>
public sealed class BaseChecksRetailTests(ITestOutputHelper output)
{
    static BaseChecksRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The number of bases whose room scenes show a <c>$terrain_*</c> template, in both games.</summary>
    private const int TerrainBases = 50;

    /// <summary>The kinds of the warnings, by the start of their text.</summary>
    private static readonly (string Kind, Func<Diagnostic, bool> Is)[] Kinds =
    [
        ("no dock object", p => p.Message.StartsWith("No object docks with", StringComparison.Ordinal)),
        ("no base line", p => p.Message.StartsWith("No object in", StringComparison.Ordinal)),
        ("dock object in another system", p => p.Message.StartsWith("The object", StringComparison.Ordinal)),
        ("door", p => p.Message.StartsWith("The door", StringComparison.Ordinal)),
        ("terrain", p => p.Message.Contains(" shows the template $terrain_", StringComparison.Ordinal)),
        ("fixture", p => p.Message.StartsWith("The fixture", StringComparison.Ordinal)),
        ("faction", p => p.Message.StartsWith("The faction", StringComparison.Ordinal)),
        ("room of the bar data", p => p.Message.Contains(" in the [MBase] of ", StringComparison.Ordinal)),
        ("goods without dealer", p => p.Message.Contains(", but no room of the base has ", StringComparison.Ordinal)),
        ("dealer without goods", p => p.Message.Contains(" shows an empty list.", StringComparison.Ordinal)),
        ("ship places", p => p.Message.Contains(" ships, but its rooms show at most ", StringComparison.Ordinal) ||
                             p.Message.Contains("has no place for a ship for sale", StringComparison.Ordinal)),
        ("trade-in lines", p => p.Message.Contains(" sells ships, but its market has no line for ", StringComparison.Ordinal)),
        ("nickname", p => p.Message.StartsWith("The base nickname", StringComparison.Ordinal)),
        ("base file", p => p.Message.Contains("base file", StringComparison.Ordinal) ||
                           p.Message.StartsWith("The room file", StringComparison.Ordinal)),
    ];

    /// <summary>
    /// Vanilla 1.1 gives these warnings of the base game: 15 bases that no object docks with, 15 bases without an object
    /// with their base line in their system, 18 objects that dock with a base of another system, 16 bases that sell goods
    /// without the dealer (15 miner bases and one base that sells a ship without a ship dealer), 4 dealers without goods
    /// and 2 ship dealers without a place for a ship. The trade-in check gives no warning, because each base that sells a
    /// ship lists each ship package.
    /// </summary>
    [Fact]
    public void TheVanillaDataGivesNoBaseError()
    {
        var counts = Check(GameDataPaths.RequireBase(), hd: false);

        Assert.Equal(new Dictionary<string, int>
        {
            ["no dock object"] = 15, ["no base line"] = 15, ["dock object in another system"] = 18,
            ["goods without dealer"] = 16, ["dealer without goods"] = 4, ["ship places"] = 2,
        }, counts);
    }

    /// <summary>HD Edition 0.7.1 gives one more base that no object docks with and one more object of another system.
    /// It removes the ship of the base without a ship dealer and one ship without a place. Its ship markets have no
    /// buy-only lines, so the trade-in check gives no warning.</summary>
    [Fact]
    public void TheHdEditionDataGivesNoBaseError()
    {
        var counts = Check(GameDataPaths.RequireHde(), hd: true);

        Assert.Equal(new Dictionary<string, int>
        {
            ["no dock object"] = 16, ["no base line"] = 15, ["dock object in another system"] = 19,
            ["goods without dealer"] = 15, ["dealer without goods"] = 4, ["ship places"] = 1,
        }, counts);
    }

    /// <summary>The checks of each base of a game. No problem is an error, each warning has a kind, and Br05_02_Base has
    /// a door without <c>room_switch</c> and no door warning.</summary>
    private Dictionary<string, int> Check(string root, bool hd)
    {
        using var workspace = new RetailWorkspace(root, "c4checks");
        var session = workspace.Open();
        var problems = session.Bases.Nicknames.SelectMany(b => BaseChecks.Problems(session, b)).ToList();

        var counts = new Dictionary<string, int>();
        foreach (var problem in problems)
        {
            output.WriteLine(problem.Message);
            var kind = Kinds.FirstOrDefault(k => k.Is(problem)).Kind ?? "other";
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        foreach (var (kind, count) in counts.OrderBy(c => c.Key))
            output.WriteLine($"{(hd ? "HD Edition" : "vanilla")}: {kind}: {count}");

        Assert.DoesNotContain(problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.Empty(session.SaveProblems());
        var br0502 = session.Base("Br05_02_Base")!;
        Assert.Contains(br0502.Rooms.SelectMany(r => r.Doors), d => d.Target == null);
        Assert.DoesNotContain(BaseChecks.Problems(session, "Br05_02_Base"),
            p => p.Message.StartsWith("The door", StringComparison.Ordinal));
        Assert.DoesNotContain("other", counts.Keys);

        // The checks read the scripts of the rooms: the bases whose room scenes show a terrain template have the lines.
        var terrain = session.Bases.Nicknames.Select(b => session.Base(b)!)
            .Where(v => v.Rooms.Any(r => r.File != null && session.BaseCheckState.TerrainKeys(r.File).Count > 0)).ToList();
        output.WriteLine($"{terrain.Count} bases show terrain templates in their rooms");
        Assert.Equal(TerrainBases, terrain.Count);
        Assert.All(terrain, v => Assert.NotEmpty(v.Terrain));
        return counts;
    }
}
