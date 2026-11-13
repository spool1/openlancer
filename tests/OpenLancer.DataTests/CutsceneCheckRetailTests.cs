using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Thn;
using OpenLancer.Runtime.Validation;
using Xunit;
using static OpenLancer.DataTests.ThnPatchRetailTests;

namespace OpenLancer.DataTests;

/// <summary>
/// The cutscene checks on the retail data (tests 13.2 items 3 and 10 of the M8 design): no error in the 1,506 retail
/// cutscenes with the retail game data, also for the 15 REFERENCE COMPOUNDs without a category, and one retail name
/// problem. <c>oltool validate
/// --active</c> reports no new problem for the edit of the manual check A, one error with its place for a LIGHT without
/// a type, and no new problem when the lower mod has that error already.
/// </summary>
public sealed class CutsceneCheckRetailTests(ITestOutputHelper output)
{
    /// <summary>The intro scene with the one retail name problem.</summary>
    public const string WaterPlanet = @"DATA\Scripts\Intro\Intro_waterplanet.thn";

    /// <summary>The one name problem of the retail cutscenes: the PSYS "ring_2" names the effect "ring", which the game
    /// data does not have. The engine does not stop for it, and the scene does not show the entity.</summary>
    public const string RetailNameProblem =
        "entity \"ring_2\", template_name: The game data has no effect \"ring\". The scene does not show this entity.";

    static CutsceneCheckRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRetailCutscenesHaveNoErrorAndTheReferenceExemptionIsNeeded()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var data = EngineDataLoader.Load(view).RequireComplete();
        var evaluator = ThornEvaluator.Instance;
        var clock = Stopwatch.StartNew();
        var (files, entities, events) = (0, 0, 0);
        var errors = new List<Diagnostic>();
        var references = new List<(string File, ThnTable Globals, int Index)>();
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            files++;
            errors.AddRange(CutsceneCheck.Errors(view, file, data));
            var globals = evaluator.Evaluate(view.ReadAllBytes(file), file);
            var model = ThnScriptModel.From(globals, ThnElementId.Counter());
            entities += model.Entities.Count;
            events += model.Events.Count;
            for (var i = 0; i < model.Entities.Count; i++)
            {
                var entity = model.Entities[i].Table;
                if (ThnSchema.TypeOf(ThnElementKind.Entity, entity, evaluator.Symbols) == "COMPOUND" &&
                    ThnFlags.IsReference(entity, evaluator.Symbols) &&
                    entity["template_name"] is ThnText { Value.Length: > 0 } &&
                    (entity["userprops"] as ThnTable)?.Record.Any(f => f.Key.Equals("category", StringComparison.OrdinalIgnoreCase)) != true)
                    references.Add((file, globals, i));
            }
        }
        var time = clock.Elapsed;

        // Without the REFERENCE flag, the scene makes such a COMPOUND, and its category switch stops the engine.
        var withoutFlag = references.Select(r => ThnChecks.Errors(r.File, WithoutReference(r.Globals, r.Index), evaluator))
            .ToList();

        output.WriteLine($"{files} files, {entities} entities, {events} events: {errors.Count} errors in {time.TotalSeconds:0.0} s.");
        output.WriteLine($"{references.Count} REFERENCE COMPOUNDs with a template and no category, for example " +
                         $"{references[0].File}. Without the flag: {withoutFlag.Sum(e => e.Count)} errors.");
        foreach (var error in errors.Take(20))
            output.WriteLine(error.ToString());
        Assert.Equal(1506, files);
        Assert.Equal((41_250, 50_785), (entities, events));
        // The one retail name problem: an intro scene names an effect that the game data does not have.
        Assert.Equal([(WaterPlanet, RetailNameProblem)], errors.Select(e => (e.Source, e.Message)));
        Assert.Equal(15, references.Count);
        Assert.All(withoutFlag, e => Assert.Contains("userprops.category: The key is missing", Assert.Single(e).Message));
    }

    /// <summary>
    /// The full validation through oltool: the edit of the manual check A adds no problem. A patch that removes the
    /// type of a LIGHT gives one error with its place, and a patch that does not apply gives a merge error with the
    /// cutscene. When the lower mod has the LIGHT error, the active mod gets no new problem. The checks of the templates
    /// and the costumes read the game data that the validation loads. When the INI checks find an error, the validation
    /// does not load the game data, but it still reports the LIGHT error.
    /// </summary>
    [Fact]
    public void OltoolValidateReportsOnlyTheCutsceneErrorsThatTheActiveModAdds()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var view = FileSystem.FromPath(baseFolder);
        var symbols = ThornEvaluator.Instance.Symbols;
        var s002xe = ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(view.ReadAllBytes(S002xe), S002xe), ThnElementId.Counter());
        // The entities that the scene makes, with their place.
        var made = s002xe.Entities.Select((e, i) => (Name: ThnScriptModel.NameOf(e.Table)!, Index: i, e.Table,
                Type: ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, symbols)))
            .Where(e => !ThnFlags.IsReference(e.Table, symbols) && s002xe.EntityNamed(e.Name)!.Table == e.Table).ToList();
        var light = made.First(e => e.Type == "LIGHT");
        var character = made.First(e => e.Type == "DEFORMABLE");
        var ship = made.First(e => e.Type == "COMPOUND" &&
                                   (e.Table["userprops"] as ThnTable)?["category"] is ThnText { Value: "Spaceship" });
        var noType = $"ol_thn_patch = 1\nentities = {{ {{ ol_match = \"{light.Name}\", ol_remove = {{ \"lightprops.type\" }} }} }}\n";
        var lower = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"), "broken");
        try
        {
            WriteFile(Path.Combine(lower, ModManifest.FileName), """{ "id": "broken", "formatVersion": 1 }""");
            WriteFile(Path.Combine(lower, Mod.PatchesFolder, S002xe), noType);
            var expected = Error(light, "lightprops.type", "The key is missing. The engine needs it.");
            using (var ws = new RetailWorkspace(baseFolder, "m8proof", lowerMods: [lower]))
            {
                // The lower mod adds the error to vanilla.
                Assert.Equal(expected, Assert.Single(Validate(ws, "broken", 1)));

                // The edit of check A on top of it adds nothing, and the error of the lower mod is not new.
                WriteDesignPatch(ws.ModFolder);
                Assert.Empty(Validate(ws, "m8proof", 0));
            }

            using (var ws = new RetailWorkspace(baseFolder, "m8proof"))
            {
                // The edit of check A and the removed light type in the active mod: one error, at the same place.
                var patch = Path.Combine(ws.ModFolder, Mod.PatchesFolder, S002xe);
                WriteFile(patch, noType.Replace("entities = {", "entities = {\n\t{ ol_match = \"Cam_02_Intro_Trent_A\", " +
                                                               "ol_set = { [\"cameraprops.fovh\"] = 20 } },"));
                Assert.Equal(expected, Assert.Single(Validate(ws, "m8proof", 1)));

                // The INI checks find a market good that does not exist, so the validation does not load the game data.
                // The cutscene check still runs, without the checks of the templates and the costumes.
                var market = Path.Combine(ws.ModFolder, Mod.PatchesFolder, "DATA", "EQUIPMENT", "market_misc.ini");
                WriteFile(market, "[BaseGood]\r\nbase = li01_01_base\r\nmarketgood = no_such_good, 0, -1, 1, 1, 0, 1\r\n");
                var problems = Validate(ws, "m8proof", 1);
                Assert.Equal(2, problems.Count);
                Assert.Contains(problems, p => p.Severity == DiagnosticSeverity.Error && p.Message.Contains("no_such_good"));
                Assert.Contains(expected, problems);
                File.Delete(market);

                // The scene of a room makes the COMPOUNDs of s002xe, so a ship that does not exist stops the engine. A
                // character without an Actor loads its costume.
                var actor = ((ThnTable)character.Table["userprops"]!).Record
                    .Last(f => f.Key.Equals("Actor", StringComparison.OrdinalIgnoreCase)).Key;
                WriteFile(patch, $$"""
                    ol_thn_patch = 1
                    entities = {
                        { ol_match = "{{character.Name}}", ol_set = { template_name = "no_such_costume" }, ol_remove = { "userprops.{{actor}}" } },
                        { ol_match = "{{ship.Name}}", ol_set = { template_name = "no_such_ship" } }
                    }
                    """);
                Assert.Equal([
                    Error(character, "template_name", "The costume \"no_such_costume\" is not in the game data. A DEFORMABLE " +
                                                      "without userprops.Actor needs it."),
                    Error(ship, "template_name", "The game data has no ship \"no_such_ship\"."),
                ], Validate(ws, "m8proof", 1));

                // A patch that does not apply is a merge error. It has the cutscene as its place.
                File.WriteAllText(patch, "ol_thn_patch = 1\nentities = { { ol_match = \"No_such_entity\", ol_delete = Y } }\n");
                var merge = Assert.Single(Validate(ws, "m8proof", 1));
                Assert.Equal((DiagnosticSeverity.Error, new ThnLocation(S002xe)), (merge.Severity, merge.Cutscene));
                Assert.Contains("No_such_entity", merge.Message);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(lower)!, recursive: true);
        }
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>The error of an entity of s002xe, with its place.</summary>
    private static Diagnostic Error((string Name, int Index, ThnTable Table, string? Type) entity, string value, string message) =>
        Diagnostic.Error(S002xe, $"entity \"{entity.Name}\", {value}: {message}") with
        {
            Cutscene = new ThnLocation(S002xe, ThnElementKind.Entity, entity.Index, value, $"\"{entity.Name}\""),
        };

    /// <summary>Runs <c>oltool validate --active --json</c> and returns its problems.</summary>
    private List<Diagnostic> Validate(RetailWorkspace ws, string active, int exit)
    {
        var (code, json, errors) = Oltool.Run("validate", ws.ProfilePath, "--active", active, "--json");
        output.WriteLine($"validate --active {active}: exit {code}\n{json}{errors}");
        Assert.True(code == exit, errors);
        return [.. ValidationJson.Read(json)];
    }

    /// <summary>The globals with the REFERENCE flag of one entity removed.</summary>
    private static ThnTable WithoutReference(ThnTable globals, int index)
    {
        var model = ThnScriptModel.From(globals, ThnElementId.Counter());
        var entity = model.Entities[index];
        var table = new ThnTable(entity.Table.List, [.. entity.Table.Record.Where(f => f.Key != ThnFlags.Key)]);
        return (model with { Entities = model.Entities.SetItem(index, entity with { Table = table }) }).ToGlobals();
    }

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
