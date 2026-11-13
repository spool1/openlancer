using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The template checks of the save gate on the retail data (#19, section 12.1 of the M8b design). The gate checks the
/// templates and the costumes with the data lists of the session. With that data, the 1,506 retail cutscenes have no
/// error, so a rename of a retail entity cannot turn a retail error into a false gate error. They have one name
/// problem: the PSYS "ring_2" of an intro scene names the effect "ring", which the game data does not have. So the gate
/// compares the name problems by their elements, and a rename of that entity adds no problem. The lists of the
/// templates hold the items of the engine.
/// </summary>
public sealed class ThnNameGateRetailTests(ITestOutputHelper output)
{
    static ThnNameGateRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRetailCutscenesHaveNoProblemWithTheDataOfASession()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8cproof");
        var session = workspace.Open();
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var clock = Stopwatch.StartNew();
        var (files, errors, names) = (0, new List<Diagnostic>(), new List<Diagnostic>());
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            files++;
            var result = ThnChecks.Check(file, evaluator.Evaluate(view.ReadAllBytes(file), file), evaluator,
                new SessionThnData(session, file));
            errors.AddRange(result.Errors);
            names.AddRange(result.NameProblems);
        }

        output.WriteLine($"{files} files: {errors.Count} errors and {names.Count} name problems with the data of a session " +
                         $"in {clock.Elapsed.TotalSeconds:0.0} s.");
        foreach (var problem in errors.Concat(names).Take(40))
            output.WriteLine(problem.ToString());
        Assert.Equal(1506, files);
        Assert.Empty(errors);
        var name = Assert.Single(names);
        Assert.Equal((CutsceneCheckRetailTests.WaterPlanet, CutsceneCheckRetailTests.RetailNameProblem),
            (name.Source, name.Message));
    }

    /// <summary>A rename of the entity of the retail name problem adds no problem to the save gate, because the gate
    /// compares the name problems by their elements. A new missing effect of that entity blocks the save.</summary>
    [Fact]
    public void ARenameOfTheEntityOfTheRetailNameProblemAddsNoGateError()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8cproof");
        var session = workspace.Open();
        var ring = session.Cutscene(CutsceneCheckRetailTests.WaterPlanet)!.Entities
            .Single(e => ThnScriptModel.NameOf(e.Table) == "ring_2");
        var path = session.CutscenePath(CutsceneCheckRetailTests.WaterPlanet)!;
        var target = new ThnRef(path, ThnElementKind.Entity, ring.Id);

        session.Execute(ThnEdits.RenameEntity(target, "my_ring"));
        Assert.Empty(session.CutsceneErrors(path));
        Assert.Empty(session.SaveProblems());

        session.Execute(ThnEdits.Set(target, new ThnPath("template_name"), new ThnText("no_such_effect")));
        Assert.Equal(["entity \"my_ring\", template_name: The game data has no effect \"no_such_effect\". The scene does not " +
                      "show this entity."], session.CutsceneErrors(path).Select(e => e.Message));
    }

    /// <summary>The petalDB lists hold the rooms, the props and the carts of the engine, and the asteroid lists
    /// together hold the asteroids and the mines of the engine. The template lists of a PSYS hold each effect and
    /// visual effect of the engine.</summary>
    [Fact]
    public void TheTemplateListsHoldTheItemsOfTheEngine()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8cproof");
        var session = workspace.Open();
        var lists = session.Lists;
        var db = EngineDataLoader.Load(FileSystem.FromPath(GameDataPaths.RequireBase())).RequireComplete();

        string[] Names(ThnItemKind kind) =>
        [
            .. lists.List(ThnTemplateRules.ListsOf(kind))!.Entries.Select(e => e.Value.ToLowerInvariant()).Order(StringComparer.Ordinal),
        ];
        static string[] Engine(IEnumerable<string> names) =>
            [.. names.Select(n => n.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)];

        output.WriteLine($"rooms {db.Ini.PetalDb.Rooms.Count}, props {db.Ini.PetalDb.Props.Count}, carts " +
                         $"{db.Ini.PetalDb.Carts.Count}, asteroids {db.Asteroids.Count()}, mines " +
                         $"{lists.List("asteroid_mine")?.Entries.Count}");
        Assert.Equal(Engine(db.Ini.PetalDb.Rooms.Keys), Names(ThnItemKind.Room));
        Assert.Equal(Engine(db.Ini.PetalDb.Props.Keys), Names(ThnItemKind.Prop));
        Assert.Equal(Engine(db.Ini.PetalDb.Carts.Keys), Names(ThnItemKind.Cart));
        Assert.Equal(Engine(db.Asteroids.Select(a => a.Nickname)), Names(ThnItemKind.Asteroid));
        Assert.NotEmpty(lists.List("asteroid_mine")!.Entries);
        Assert.Equal(Engine(db.Effects.Select(e => e.Nickname).Concat(db.VisEffects.Select(e => e.Nickname))),
            Names(ThnItemKind.Effect));
        Assert.Equal(Engine(db.Costumes.Select(c => c.Nickname)), Names(ThnItemKind.Costume));
    }
}
