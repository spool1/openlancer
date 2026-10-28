using System.IO;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// Decision D82 as the user decided it on 2026-10-18: when the active mod makes an entry unusable without a delete,
/// each line of any layer that names the entry is an error that blocks Save and Play, as for a removed entry. The
/// message tells why, and what the game does with the data. One undo heals each line.
/// </summary>
public sealed class UnusableEntryTests
{
    private const string GraphPath = @"DATA\igraph.ini";

    private const string CurveError =
        "[LightSource] nickname = light1: 'atten_curve = curve1': Curve 'curve1' is not usable: the [IGraph] " +
        "'curve1' has no 'point' line. The game stops loading with this value. Fix 'curve1', or pick another value.";

    private static readonly SectionRef Curve = new(GraphPath, new SectionIdentity("IGraph", "curve1"));
    private static readonly SectionRef Light = Li01Section("LightSource", "light1");
    private static readonly SectionRef Gun = new(EquipmentPath, new SectionIdentity("Gun", "gun1"));
    private static readonly SectionRef Missile = new(EquipmentPath, new SectionIdentity("Munition", "missile1"));
    private static readonly SectionRef Launcher = new(EquipmentPath, new SectionIdentity("Gun", "launcher1"));
    private static readonly SectionRef LauncherLoadout = Loadout(SolarLoadoutsPath, "lo_launcher");

    /// <summary>The list workspace with a missile launcher that fires <c>missile1</c>, and a loadout with the
    /// launcher, in the base data.</summary>
    private static TestWorkspace CreateWithLauncher()
    {
        var ws = Create();
        ws.WriteBase(EquipmentPath, """
            [Munition]
            nickname = ammo1
            [Motor]
            nickname = motor1
            [Explosion]
            nickname = expl1
            [Munition]
            nickname = missile1
            motor = motor1
            explosion_arch = expl1
            [Gun]
            nickname = gun1
            projectile_archetype = ammo1
            [Gun]
            nickname = launcher1
            projectile_archetype = missile1
            """);
        ws.WriteBase(SolarLoadoutsPath, """
            [Loadout]
            nickname = lo_shared
            [Loadout]
            nickname = lo_solar
            [Loadout]
            nickname = lo_launcher
            equip = launcher1, HpWeapon01
            """);
        return ws;
    }

    /// <summary>The case of the M21 verify report: the active mod removes the points of a curve, and a light of the
    /// base data names the curve. The engine stops the load at such a light.</summary>
    [Fact]
    public void ACurveWithoutPointsMakesTheBaseLightAnErrorUntilTheUndo()
    {
        using var ws = Create();
        var session = ws.Session("act");
        Assert.Empty(session.Lists.Problems(Light));

        session.Execute(EditCommand.RemoveName(Curve, "point"));

        var problem = Assert.Single(session.Lists.Problems(Light));
        Assert.Equal((ListProblemKind.Unusable, true, "atten_curve", "curve1", RuleEffect.LoadBreak),
            (problem.Kind, problem.IsError, problem.Diagnostic.Key, problem.Value, problem.Effect));
        Assert.Equal(CurveError, problem.Diagnostic.Message);
        // The active mod did not change the light.
        Assert.Equal(ValueOrigin.Base, session.Origin(Light, "atten_curve").Origin);
        Assert.Equal([problem.Diagnostic], session.SaveProblems());
        Assert.NotNull(session.GateRefusal());
        var refused = session.Save();
        Assert.False(refused.Saved);
        Assert.Contains(problem.Diagnostic, refused.Problems);
        Assert.Empty(TestWorkspace.Files(Path.Combine(ws.ModFolder("act"), "patches")));

        // One undo heals the light.
        session.Undo();
        Assert.Empty(session.Lists.Problems(Light));
        Assert.Empty(session.SaveProblems());
        Assert.Null(session.GateRefusal());
        Assert.True(session.Save().Saved);
    }

    /// <summary>A saved patch of the active mod removes the points. The gate runs on each save, also with nothing to
    /// write, so Play cannot start until the curve has a point again.</summary>
    [Fact]
    public void ASavedPatchThatRemovesThePointsStopsEachSaveUntilTheFix()
    {
        using var ws = Create(activeFiles: new()
        {
            [@"patches\DATA\igraph.ini"] = "[IGraph]\nnickname = curve1\nol.remove = point\n",
        });
        var session = ws.Session("act");

        Assert.False(session.IsDirty);
        Assert.False(session.Lists.List("curve")!.Find("curve1")!.Usable);
        Assert.Equal(CurveError, Assert.Single(session.SaveProblems()).Message);
        Assert.False(session.Save().Saved);

        session.Execute(EditCommand.SetLinesFromText(Curve, "point", ["0, 1"]));
        Assert.Empty(session.SaveProblems());
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));
    }

    /// <summary>A whole file of the active mod with two curves of one name: the engine finds none of them. The active
    /// mod made the curve unusable, so the light of the base data is an error.</summary>
    [Fact]
    public void AWholeFileThatRepeatsTheNameOfACurveMakesTheBaseLightAnError()
    {
        const string twice = "[IGraph]\nnickname = curve1\ntype = FLOAT\npoint = 0, 1\n";
        using var ws = Create(activeFiles: new() { [@"files\DATA\igraph.ini"] = twice + twice });
        var session = ws.Session("act");

        var problem = Assert.Single(session.SaveProblems());
        Assert.Equal((Light, "atten_curve"), (problem.Target, problem.Key));
        Assert.Contains("Curve 'curve1' is not usable: 2 sections have the name 'curve1', so the game finds none of " +
                        "them. The game stops loading with this value.", problem.Message);
    }

    /// <summary>A lower layer made the curve unusable, not the active mod. The light gives a warning, and the save goes
    /// on (decision D108).</summary>
    [Fact]
    public void AnEntryThatALowerModMadeUnusableGivesAWarningOnly()
    {
        using var ws = Create(lowerFiles: new()
        {
            [@"patches\DATA\igraph.ini"] = "[IGraph]\nnickname = curve1\nol.remove = point\n",
        });
        var session = ws.Session("act");

        var problem = Assert.Single(session.Lists.Problems(Light));
        Assert.Equal((ListProblemKind.Unusable, false), (problem.Kind, problem.IsError));
        session.Execute(EditCommand.SetLinesFromText(Light, "pos", ["1, 2, 3"]));
        Assert.Empty(session.SaveProblems());
        Assert.True(session.Save().Saved);
    }

    /// <summary>A missile without its explosion stops the load. Each gun that fires it is an error, and each gun is
    /// unusable in its turn, so each loadout line that names such a gun is an error too.</summary>
    [Fact]
    public void AMissileWithoutItsExplosionMakesTheLauncherAndItsLoadoutErrorsUntilTheUndo()
    {
        using var ws = CreateWithLauncher();
        var session = ws.Session("act");
        Assert.Empty(session.SaveProblems());

        session.Execute(EditCommand.RemoveName(Missile, "explosion_arch"));

        var own = Assert.Single(session.Lists.Problems(Missile));
        Assert.Equal((ListProblemKind.RequiredMissing, true, RuleEffect.LoadBreak), (own.Kind, own.IsError, own.Effect));
        var gun = Assert.Single(session.Lists.Problems(Launcher));
        Assert.Equal((ListProblemKind.Unusable, true, RuleEffect.Drop), (gun.Kind, gun.IsError, gun.Effect));
        Assert.Equal("[Gun] nickname = launcher1: 'projectile_archetype = missile1': Munition 'missile1' is not usable: " +
                     "the key 'explosion_arch' is missing. A section with 'motor' needs it. The game stops loading " +
                     "without this key. Fix 'missile1', or pick another value.", gun.Diagnostic.Message);
        var line = Assert.Single(session.Lists.Problems(LauncherLoadout));
        Assert.Equal((ListProblemKind.Unusable, true, RuleEffect.Degrade), (line.Kind, line.IsError, line.Effect));
        Assert.Equal("[Loadout] nickname = lo_launcher: 'equip = launcher1, HpWeapon01': Equipment 'launcher1' is not " +
                     "usable: 'projectile_archetype = missile1': Munition 'missile1' is not usable: the key " +
                     "'explosion_arch' is missing. A section with 'motor' needs it. The game stops loading without " +
                     "this key. Fix 'launcher1', or pick another value.", line.Diagnostic.Message);
        var gate = session.SaveProblems();
        Assert.Equal(3, gate.Count);
        Assert.Contains(own.Diagnostic, gate);
        Assert.Contains(gun.Diagnostic, gate);
        Assert.Contains(line.Diagnostic, gate);
        Assert.False(session.Save().Saved);

        session.Undo();
        Assert.Empty(session.Lists.Problems(Launcher));
        Assert.Empty(session.Lists.Problems(LauncherLoadout));
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>The engine drops a gun without a valid munition. The two loadout lines and the good that name the gun
    /// are errors. The market line names the good, which the engine keeps, so it is no error.</summary>
    [Theory]
    [InlineData(null, "the key 'projectile_archetype' is missing.")]
    [InlineData("no_such_ammo", "'projectile_archetype = no_such_ammo': no munition has the name 'no_such_ammo'.")]
    public void AGunWithoutAValidMunitionMakesEachLineThatNamesItAnError(string? value, string reason)
    {
        using var ws = Create();
        var session = ws.Session("act");
        var station = Loadout(ShipLoadoutsPath, "lo_station");
        var good = new SectionRef(GoodsPath, new SectionIdentity("Good", "gun1"));

        session.Execute(value == null
            ? EditCommand.RemoveName(Gun, "projectile_archetype")
            : EditCommand.SetLinesFromText(Gun, "projectile_archetype", [value]));

        var own = Assert.Single(session.Lists.Problems(Gun));
        Assert.Equal((true, RuleEffect.Drop), (own.IsError, own.Effect));
        var lines = session.Lists.Problems(station);
        Assert.Equal([0, 1], lines.Select(p => p.Line));
        Assert.All(lines, p => Assert.Equal((ListProblemKind.Unusable, true), (p.Kind, p.IsError)));
        Assert.Equal($"[Loadout] nickname = lo_station: line 1: 'equip = gun1, HpWeapon01': Equipment 'gun1' is not " +
                     $"usable: {reason} The game drops this gun. Fix 'gun1', or pick another value.",
            lines[0].Diagnostic.Message);
        var goodLine = Assert.Single(session.Lists.Problems(good));
        Assert.Equal((ListProblemKind.Unusable, true, "equipment"), (goodLine.Kind, goodLine.IsError, goodLine.Diagnostic.Key));
        Assert.Equal(4, session.SaveProblems().Count);

        session.Undo();
        Assert.Empty(session.Lists.Problems(station));
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>The game crashes when the player docks at an object whose <c>dock_with</c> names no base. The error
    /// stays on the <c>dock_with</c> line, and it blocks Save. The game loads the gate, so the gate is still a valid
    /// exit: the <c>goto</c> line of the partner gate in another system file is no error (decision D82, as decided in the
    /// M8b verify on 2026-10-18).</summary>
    [Fact]
    public void ADockWithThatNamesNoBaseIsAnErrorOfItsOwnLineOnly()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var gate = Li01("Li01_to_Li02");
        var partner = Object(Li02Path, "Li02_to_Li01");

        session.Execute(EditCommand.SetLinesFromText(gate, "dock_with", ["no_such_base"]));

        var own = Assert.Single(session.Lists.Problems(gate));
        Assert.Equal((ListProblemKind.Missing, true, RuleEffect.Crash), (own.Kind, own.IsError, own.Effect));
        Assert.Equal("[Object] nickname = Li01_to_Li02: 'dock_with = no_such_base': no base has the name " +
                     "'no_such_base'. The game crashes when the player docks here.", own.Diagnostic.Message);
        Assert.Empty(session.Lists.Problems(partner));
        Assert.Equal([own.Diagnostic], session.SaveProblems());
        Assert.NotNull(session.GateRefusal());
        Assert.False(session.Save().Saved);

        session.Undo();
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>Only the effects drop and load-break make an entry unusable for the lines that name it. The game loads
    /// an item with a crash error, and crashes only when it uses the bad value.</summary>
    [Fact]
    public void ACrashRuleDoesNotMakeAnEntryUnusable()
    {
        Assert.False(ListRule.MakesUnusable(RuleEffect.Crash));
        Assert.True(ListRule.MakesUnusable(RuleEffect.Drop));
        Assert.True(ListRule.MakesUnusable(RuleEffect.LoadBreak));
        Assert.False(ListRule.MakesUnusable(RuleEffect.Degrade));
        Assert.False(ListRule.MakesUnusable(RuleEffect.None));
    }
}
