using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The canonical writer on all retail cutscene scripts: evaluate the bytecode, write the text, compile and evaluate the
/// text, and compare exactly, with the float bits and the key order. Equal trees write the same text
/// (ThnWriterTests.EqualTreesWriteTheSameText), so the text of the result is the same text.
/// </summary>
public sealed class ThnCorpusRoundTripTests(ITestOutputHelper output)
{
    private const int RetailScripts = 1506;

    static ThnCorpusRoundTripTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EveryRetailScriptGivesTheSameTablesInKeyOrderAndAFixedPointText()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var files = Directory.EnumerateFiles(Path.Combine(baseFolder, "DATA"), "*.thn", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

        var problems = new List<string>();
        var buildProblems = new List<string>();
        var counts = new Counts();
        foreach (var file in files)
        {
            var name = Path.GetRelativePath(baseFolder, file);
            var globals = ThornEvaluator.Instance.Evaluate(File.ReadAllBytes(file), name);
            counts.Add(globals);
            if (ThnWriter.RoundTripProblem(globals, ThornEvaluator.Instance, name) is { } problem)
                problems.Add($"{name}: {problem}");
            // The builds of the editor check the path texts of each script (section 8.7, item 2 of the MCP1 design).
            if (ThnMotionPath.ScriptProblem(globals) is { } paths)
                buildProblems.Add($"{name}: {paths}");
        }

        output.WriteLine($"{files.Count - problems.Count} of {files.Count} scripts give the same tables, with key order, " +
                         "and a fixed point text.");
        output.WriteLine($"Numbers {counts.Numbers}, entities {counts.Entities}, events {counts.Events}, entity types " +
                         $"as numbers {counts.EntityTypeNumbers}, event types as numbers {counts.EventTypeNumbers}.");
        Assert.True(problems.Count == 0,
            $"{problems.Count} of {files.Count} scripts differ:{Environment.NewLine}{string.Join(Environment.NewLine, problems.Take(20))}");
        Assert.Equal(RetailScripts, files.Count);
        // So each retail script still builds in the Cutscene tab and in an image of the agent.
        Assert.True(buildProblems.Count == 0, string.Join(Environment.NewLine, buildProblems.Take(20)));
        // The counts of the design (section 3). They show that the tree holds every value of the VM, and that a type
        // that the file stores as a number stays a number.
        Assert.Equal((1_119_470, 41_250, 50_785), (counts.Numbers, counts.Entities, counts.Events));
        Assert.Equal((10_728, 4_020), (counts.EntityTypeNumbers, counts.EventTypeNumbers));
    }

    private sealed class Counts
    {
        public int Numbers;
        public int Entities;
        public int Events;
        public int EntityTypeNumbers;
        public int EventTypeNumbers;

        public void Add(ThnTable globals)
        {
            var entities = ((ThnTable)globals["entities"]!).List;
            var events = ((ThnTable)globals["events"]!).List;
            Entities += entities.Length;
            Events += events.Length;
            EntityTypeNumbers += entities.Count(e => ((ThnTable)e)["type"] is ThnNumber);
            EventTypeNumbers += events.Count(e => ((ThnTable)e).List[1] is ThnNumber);
            CountNumbers(globals);
        }

        private void CountNumbers(ThnValue value)
        {
            switch (value)
            {
                case ThnNumber:
                    Numbers++;
                    break;
                case ThnTable table:
                    foreach (var item in table.List)
                        CountNumbers(item);
                    foreach (var field in table.Record)
                        CountNumbers(field.Value);
                    break;
            }
        }
    }
}
