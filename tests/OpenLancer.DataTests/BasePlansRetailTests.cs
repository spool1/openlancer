using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The plan of a new base on the retail data (section H.2 of the bases design). Each retail base with a base file is a
/// template, except the intro bases. A plan from each template has no refusal, also with a dock object. A base from
/// Li01_13_Base with a station has no error of the base checks, and one Undo removes it. The texts of the games give
/// each copied fixture a name with three lines. The tests only read the game folders.
/// </summary>
public sealed class BasePlansRetailTests(ITestOutputHelper output)
{
    static BasePlansRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The nickname of each new base of the tests.</summary>
    private const string New = "Li01_50_Base";

    /// <summary>
    /// Each base of vanilla 1.1 is a template, except the three intro bases. Each plan runs, without and with a dock
    /// object far from the center of the system of the template. A dock object needs the archetype of the request only
    /// when the template has no object that names it (11 bases). The main object of each template is in the system of
    /// its <c>[Base]</c> when an object there names the base.
    /// </summary>
    [Fact]
    public void EachVanillaBaseIsATemplateAndItsPlanHasNoError() =>
        CheckTemplates(GameDataPaths.RequireBase(), templates: 194, noDockObject: 11, otherDockObject: 0);

    /// <summary>HD Edition 0.7.1 has the same templates. The object of each template that has one can dock a
    /// ship.</summary>
    [Fact]
    public void EachHdEditionBaseIsATemplateAndItsPlanHasNoError() =>
        CheckTemplates(GameDataPaths.RequireHde(), templates: 194, noDockObject: 11, otherDockObject: 0);

    /// <summary>
    /// In vanilla 1.1, a satellite of Iw03 has the base line, the dock line and the name of gd_im_water_miner. Its
    /// archetype has no docking sphere, so it cannot dock a ship. The two stations of Bw10, the system of the base, dock
    /// with the base. The main object of the template is the first station of Bw10, so a new station gets its archetype.
    /// </summary>
    [Fact]
    public void TheMainObjectOfTheWaterMinerIsAStationOfItsSystem()
    {
        var root = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(root, "c4miner");
        var session = workspace.Open();
        var view = session.Base("gd_im_water_miner")!;
        var satellite = Assert.Single(view.DockObjects, d => !BaseView.InSystem(session, d.Section, view.System));
        Assert.True(satellite.HasBaseLine && satellite.DocksWith);
        Assert.Equal("Roid_Miner", SceneSections.Last(session.Get(satellite.Section)!, "archetype"));

        var template = BaseTemplate.Of(session, "gd_im_water_miner", out _)!;

        Assert.Equal("Bw10", view.System);
        Assert.True(BaseView.InSystem(session, template.MainObject!, "Bw10"), template.MainObject!.ToString());
        Assert.Equal(template.MainObject, view.NamedObject);
        Assert.Contains(view.DockObjects, d => d.Section.Equals(template.MainObject) && d.DocksWith);
        Assert.Null(template.Ring);
        var plan = BasePlans.Create(session, new BaseSpec(New, "Bw10", "gd_im_water_miner", "New Harbor")
        {
            Text = Texts(session, root),
            Dock = new DockSpec { Position = new Vector3(0, 0, 250000) },
        });
        Assert.True(plan.CanRun, string.Join(" ", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        Assert.Equal("Roid_Miner2", SceneSections.Last(session.Get(template.MainObject!)!, "archetype"));
        Assert.Equal(SceneSections.Last(session.Get(template.MainObject!)!, "archetype"),
            SceneSections.Last(session.Get(plan.DockObject!)!, "archetype"));
    }

    private void CheckTemplates(string root, int templates, int noDockObject, int otherDockObject)
    {
        using var workspace = new RetailWorkspace(root, "c4create");
        var session = workspace.Open();
        var text = Texts(session, root);
        var (count, withoutDock, otherDock) = (0, 0, 0);
        foreach (var nickname in session.Bases.Nicknames)
        {
            var template = BaseTemplate.Of(session, nickname, out var refusal);
            if (nickname.StartsWith("intro", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Null(template);
                Assert.Equal(BasePlans.IntroTemplate, refusal!.Value.Detail);
                continue;
            }
            Assert.True(template != null, $"{nickname}: {refusal?.Message}");
            count++;
            var spec = new BaseSpec(New, template!.View.System!, nickname, "New Harbor") { Text = text };
            var plan = BasePlans.Create(session, spec);
            Assert.True(plan.CanRun, $"{nickname}: {string.Join(" ", plan.Refusals.Select(r => r.Message))}");
            Assert.DoesNotContain(plan.Warnings, w => w.Message.Contains("cannot read", StringComparison.Ordinal));

            var view = template.View;
            if (view.DockObjects.Any(d => BaseView.InSystem(session, d.Section, view.System)))
                Assert.True(BaseView.InSystem(session, template.MainObject!, view.System), nickname);
            var docked = BasePlans.Create(session, spec with { Dock = new DockSpec { Position = new Vector3(0, 0, 250000) } });
            if (template.MainObject == null)
            {
                withoutDock++;
                Assert.Equal(BasePlans.DockArchetype, Assert.Single(docked.Refusals).Rule);
                continue;
            }
            if (!docked.CanRun)
            {
                otherDock++;
                output.WriteLine($"{nickname}: {docked.Refusals[0].Message}");
                Assert.Equal(BasePlans.DockArchetype, Assert.Single(docked.Refusals).Rule);
                continue;
            }
            Assert.NotNull(docked.DockObject);
        }
        output.WriteLine($"{count} templates, {withoutDock} without a dock object, {otherDock} with another dock object");
        Assert.Equal((templates, noDockObject, otherDockObject), (count, withoutDock, otherDock));
    }

    /// <summary>
    /// A base from Li01_13_Base with a new station in Li01 has no error of the base checks and of the save gate. It
    /// copies the three rooms and the bar data with a name text of three lines for each fixture. It also copies the news
    /// of the template and the lines of the station. One Undo removes each part and the texts.
    /// </summary>
    [Fact]
    public void ABaseFromLi0113WithAStationHasNoErrorAndOneUndoRemovesIt()
    {
        var root = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(root, "c4li0113");
        var session = workspace.Open();
        var system = session.SystemPath("Li01")!;
        string[] paths = [session.UniversePath!, session.Bases.MBasesPath!, session.Bases.NewsPath!, system];
        var before = paths.Select(p => Dump(session, p)).ToList();
        var template = BaseTemplate.Of(session, "Li01_13_Base", out _)!;

        var plan = BasePlans.Create(session, new BaseSpec(New, "Li01", "Li01_13_Base", "Harbor Rock")
        {
            Info = "A small harbor near the Colorado field.",
            Text = Texts(session, root),
            Dock = new DockSpec { Position = new Vector3(-20000, 0, -45000) },
        });

        Assert.True(plan.CanRun, string.Join(" ", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        foreach (var warning in plan.Warnings)
            output.WriteLine(warning.Message);
        var problems = BaseChecks.Problems(session, New);
        foreach (var problem in problems)
            output.WriteLine($"{problem.Severity}: {problem.Message}");
        Assert.DoesNotContain(problems, p => p.Severity == DiagnosticSeverity.Error);
        Assert.Empty(session.SaveProblems());

        var view = session.Base(New)!;
        Assert.Equal(template.Rooms.Select(r => r.Nickname), view.Rooms.Select(r => r.Nickname));
        Assert.Equal(template.News.Count, plan.News.Count);
        Assert.True(plan.News.Count > 0);
        Assert.Equal(plan.DockObject, view.NamedObject);
        Assert.Equal("space_port_dmg_pi_01", SceneSections.Last(session.Get(plan.DockObject!)!, "loadout"));
        var fixtures = view.Characters.Where(c => c.Role != null).ToList();
        Assert.NotEmpty(fixtures);
        foreach (var fixture in fixtures)
        {
            var name = SceneSections.Last(session.Get(fixture.Section)!, "individual_name")!;
            Assert.True(StringPlaceholder.TryParse(name, out var placeholder), name);
            var lines = session.Text(placeholder)!.Split('\n');
            output.WriteLine(string.Join(" / ", lines));
            Assert.Equal(3, lines.Length);
            Assert.StartsWith("Harbor Rock ", lines[1], StringComparison.Ordinal);
        }

        session.Undo();
        Assert.Equal(before, paths.Select(p => Dump(session, p)));
        Assert.All(plan.Files, f => Assert.False(session.OpenDocument(f)?.State.Exists == true));
        Assert.Null(session.Text(new StringPlaceholder(StringKind.String, New.ToLowerInvariant())));
        Assert.Null(session.Base(New));
        Assert.False(session.IsDirty);
    }

    /// <summary>The texts of the game of a folder, for the name texts of the copied characters.</summary>
    private static Func<SectionRef, string, string?> Texts(EditSession session, string root)
    {
        var texts = new InfocardManager(new FreelancerIni(FileSystem.FromPath(root)).Resources);
        return (section, key) => session.Get(section) is { } view && SceneSections.Last(view, key) is { } value &&
                                 int.TryParse(value, out var id) && texts.HasStringResource(id)
            ? texts.GetStringResource(id)
            : null;
    }

    /// <summary>The sections of a file of W with their lines as text, to compare two states.</summary>
    private static string Dump(EditSession session, string path) => string.Join("\n", session.Sections(path).Select(s =>
        $"{s.Id}: " + string.Join("; ", session.Get(s)!.Entries.Select(e => $"{e.Name} = {string.Join(", ", e.Values)}"))));
}
