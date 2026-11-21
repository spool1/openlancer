using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the plans of the trade lanes. Each retail lane is a chain without a circle. Its rings turn along the
/// lane by the rotation rule of the plans. Its lane zone meets the one lane zone rule and covers its ends. The generic
/// name and infocard of the rule data are texts of both games. A delete of a middle ring of a retail lane
/// relinks the lane in the patch of the mod.
/// </summary>
public sealed class LaneRetailTests(ITestOutputHelper output)
{
    static LaneRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The largest angle in degrees between the front of a retail ring and the front of the rotation rule. The
    /// retail rotate values are whole degrees.</summary>
    private const double Tolerance = 1.01;

    [Fact]
    public void TheLanesOfVanillaFitThePlans() => CheckLanes(GameDataPaths.RequireBase());

    /// <summary>The test needs <c>OPENLANCER_HDE</c>, a copy of an HD Edition install. It only reads it, and it skips
    /// without it.</summary>
    [Fact]
    public void TheLanesOfHdEditionFitThePlans() => CheckLanes(GameDataPaths.RequireHde());

    /// <summary>The name and the infocard that new rings share are texts of both games.</summary>
    [Fact]
    public void TheGenericTextsOfARingAreTextsOfBothGames()
    {
        var rule = ListRules.Retail.Builders.Lane!;
        foreach (var root in new[] { GameDataPaths.RequireBase(), GameDataPaths.RequireHde() })
        {
            var texts = new InfocardManager(new FreelancerIni(FileSystem.FromPath(root)).Resources);
            Assert.True(texts.HasStringResource(rule.Name), $"{root}: {rule.Name}");
            Assert.False(string.IsNullOrWhiteSpace(texts.GetStringResource(rule.Name)), $"{root}: {rule.Name}");
            Assert.True(texts.HasXmlResource(rule.Infocard), $"{root}: {rule.Infocard}");
            Assert.False(string.IsNullOrWhiteSpace(texts.GetXmlResource(rule.Infocard)), $"{root}: {rule.Infocard}");
        }
    }

    /// <summary>
    /// A delete of a middle ring of the first lane of New York links its two neighbors to each other. The new links do
    /// not make the editor ask. The patch of the mod writes the new links and the delete, and a new session reads the
    /// shorter lane.
    /// </summary>
    [Fact]
    public void ADeleteOfAMiddleRingOfARetailLaneRelinksTheLaneInThePatch()
    {
        var root = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(root, "ollanes");
        var session = workspace.Open();
        var li01 = session.SystemPath("Li01")!;
        var lane = FirstRings(session, li01).Select(r => LanePlans.LaneOf(session, r)!).First();
        var (before, middle, after) = (lane.Rings[1], lane.Rings[2], lane.Rings[3]);

        var plan = LanePlans.RemoveRings(session, [middle]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        // The new links do not make the editor ask. The system file of the intro names the ring as text, and only that
        // mention asks.
        Assert.Equal([middle], plan.Deletes.Select(d => d.Section));
        Assert.Equal([before, after], plan.Changes);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Missing);
        Assert.Empty(plan.MissionMissing);
        Assert.Equal([@"DATA\Universe\Systems\Intro\Intro.ini"], plan.Mentions.Select(m => m.Path));
        session.Execute(plan.Command!);
        Assert.Equal(after.Id.Key, SceneSections.Last(session.Get(before)!, "next_ring"));
        Assert.Equal(before.Id.Key, SceneSections.Last(session.Get(after)!, "prev_ring"));
        Assert.True(session.Save().Saved);
        var patch = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(workspace.ModFolder, "patches"), "*.ini",
            SearchOption.AllDirectories).Single(f => Path.GetFileName(f).Equals("li01.ini", StringComparison.OrdinalIgnoreCase)));
        output.WriteLine(patch);
        Assert.Contains($"next_ring = {after.Id.Key}", patch);
        Assert.Contains($"prev_ring = {before.Id.Key}", patch);
        Assert.Contains($"nickname = {middle.Id.Key}", patch);
        session.Dispose();

        var reopened = workspace.Open();
        var shorter = LanePlans.LaneOf(reopened, before)!;
        Assert.Equal(lane.Rings.Where(r => !r.Equals(middle)), shorter.Rings);
        Assert.Null(reopened.Get(middle));
        RetailChecks.AssertBaseIsUnchanged(root);
    }

    /// <summary>
    /// The lanes of one game: 124 chains of 4 to 15 rings without a circle, and 129 zones that meet the lane zone rule.
    /// The rings of at least 90 percent of the lanes turn along the lane within 1 degree. The lane zone of each lane
    /// covers its first and last ring.
    /// </summary>
    private void CheckLanes(string root)
    {
        using var workspace = new RetailWorkspace(root, "ollanes");
        var session = workspace.Open();
        var lanes = new List<LaneChain>();
        var zones = 0;
        foreach (var path in RetailChecks.SystemFiles(session).Skip(1).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lanes.AddRange(FirstRings(session, path).Select(r => LanePlans.LaneOf(session, r)!));
            zones += session.Sections(path).Count(s => s.Id.Type.Equals("zone", StringComparison.OrdinalIgnoreCase) &&
                                                       LaneZones.IsLaneZone(session, s));
        }
        var fit = lanes.Count(l => Fits(session, l));
        var without = lanes.Where(l => LanePlans.ZoneOf(session, l) == null).Select(l => l.Rings[0].Id.Key).ToList();
        output.WriteLine($"{root}: {lanes.Count} lanes, {lanes.Sum(l => l.Rings.Count)} rings, {zones} lane zones, " +
                         $"{fit} lanes fit the rotation rule, {without.Count} lanes without a lane zone");

        Assert.Equal(124, lanes.Count);
        Assert.Equal(129, zones);
        Assert.All(lanes, l => Assert.False(l.Loops, l.Rings[0].Id.Key));
        Assert.Equal((4, 15), (lanes.Min(l => l.Rings.Count), lanes.Max(l => l.Rings.Count)));
        Assert.True(fit * 10 >= lanes.Count * 9, $"{fit} of {lanes.Count} lanes fit the rotation rule");
        Assert.Empty(without);
    }

    /// <summary>The rings of a system file without a previous ring: the first ring of each lane.</summary>
    private static IEnumerable<SectionRef> FirstRings(EditSession session, string path) =>
        session.Sections(path).Where(s => s.Id.Type.Equals("Object", StringComparison.OrdinalIgnoreCase) &&
                                          session.Get(s) is { } view && SceneSections.ArchetypeType(session, view) == "tradelane_ring" &&
                                          view.Lines("prev_ring").Count == 0);

    /// <summary>True when the front of each ring of a lane is within the tolerance of the front of the rotation rule.
    /// The rule gets the direction from the first to the last ring.</summary>
    private static bool Fits(EditSession session, LaneChain lane)
    {
        var first = SceneSections.Position(session.Get(lane.Rings[0])!)!.Value;
        var last = SceneSections.Position(session.Get(lane.Rings[^1])!)!.Value;
        var rule = Front(LanePlans.RotationOf(last - first));
        foreach (var ring in lane.Rings)
        {
            var rotate = session.Get(ring)!.Lines("rotate").LastOrDefault()?.Values;
            if (rotate is not { Count: 3 } || !rotate[0].TryToSingle(out var x) || !rotate[1].TryToSingle(out var y) ||
                !rotate[2].TryToSingle(out var z))
                return false;
            var angle = Math.Acos(Math.Clamp(Vector3.Dot(Front(new Vector3(x, y, z)), rule), -1, 1)) * 180 / Math.PI;
            if (angle > Tolerance)
                return false;
        }
        return true;
    }

    private static Vector3 Front(Vector3 degrees) =>
        Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, MathHelper.MatrixFromEulerDegrees(degrees)));
}
