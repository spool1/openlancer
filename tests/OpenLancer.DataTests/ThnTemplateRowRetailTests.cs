using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The template rows of the cutscene Inspector on the retail data (#19, section 12.1 of the M8b design). Each template
/// of a row with a closed list is in the list of a retail session, except the templates that the save gate also
/// reports, and the templates of entities that the game does not build.
/// </summary>
public sealed class ThnTemplateRowRetailTests(ITestOutputHelper output)
{
    static ThnTemplateRowRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// The test reads each retail cutscene and gives each entity its template row, as the Inspector does. A template
    /// outside a closed list shows "Missing" in the row. Each such template that the checks of the session data report
    /// is a retail template problem of the save gate, so their count equals the count of
    /// <see cref="ThnNameGateRetailTests"/>: one, the effect "ring" of an intro scene. The test writes the count of the
    /// templates of each row, the count of the open rows outside their list, and each miss.
    /// </summary>
    [Fact]
    public void EachRetailTemplateIsInTheListOfItsRow()
    {
        var game = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(game, "m8crows");
        var session = workspace.Open();
        var view = FileSystem.FromPath(game);
        var evaluator = ThornEvaluator.Instance;
        var counts = new SortedDictionary<string, int>();
        var (open, text) = (0, 0);
        var checkedMisses = new List<string>();
        var otherMisses = new List<string>();
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            var globals = evaluator.Evaluate(view.ReadAllBytes(file), file);
            var script = ThnScriptModel.From(globals, ThnElementId.Counter());
            var result = ThnChecks.Check(file, globals, evaluator, new SessionThnData(session, file));
            var problems = result.Errors.Concat(result.NameProblems).ToList();
            foreach (var entity in script.Entities)
            {
                if (ThnTemplateRow.Of(entity.Table, evaluator.Symbols) is not { } row ||
                    entity.Table[ThnTemplateRow.Path] is not ThnText { Value.Length: > 0 } template)
                {
                    continue;
                }

                if (row.Lists.Count == 0)
                {
                    text++;
                    continue;
                }

                counts[row.Label!] = counts.GetValueOrDefault(row.Label!) + 1;
                if (session.Lists.List(row.Lists)!.Find(template.Value) != null)
                {
                    continue;
                }

                if (!row.Closed)
                {
                    open++;
                    continue;
                }

                var target = new ThnRef(file, ThnElementKind.Entity, entity.Id);
                var miss = $"{file}: {ThnScriptModel.NameOf(entity.Table)}, {row.Label} \"{template.Value}\"";
                (ThnFieldProblems.At(problems, script, target, ThnTemplateRow.Path) != null ? checkedMisses : otherMisses)
                    .Add(miss);
            }
        }

        output.WriteLine($"Templates in rows with a list: {string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}"))}.");
        output.WriteLine($"{text} templates keep the text field. {open} templates are outside an open list.");
        output.WriteLine($"{checkedMisses.Count} templates outside a closed list that the checks report:");
        checkedMisses.ForEach(output.WriteLine);
        output.WriteLine($"{otherMisses.Count} templates outside a closed list of entities that the game does not build:");
        otherMisses.ForEach(output.WriteLine);
        var ring = Assert.Single(checkedMisses);
        Assert.Contains("\"ring\"", ring);
        Assert.Empty(otherMisses);
        Assert.True(counts.GetValueOrDefault("Costume") > 1000, "Too few costumes.");
    }
}
