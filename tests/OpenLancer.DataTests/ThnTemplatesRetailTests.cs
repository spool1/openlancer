using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The lens of the camera of a new cutscene (decision D134, retail test 3 of the M8b design): the camera values of
/// <see cref="ThnTemplates.NewScript"/> are the medians of the retail CAMERA entities in <c>SCRIPTS\BASES</c>. The light
/// of a new LIGHT has the most common values of the retail lights.
/// </summary>
public sealed class ThnTemplatesRetailTests(ITestOutputHelper output)
{
    private static readonly string[] Keys = ["fovh", "hvaspect", "nearplane", "farplane"];

    [Fact]
    public void TheCameraOfANewCutsceneHasTheMedianLensOfTheRetailBaseCameras()
    {
        var fs = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var values = Keys.ToDictionary(k => k, _ => new List<float>());
        var (files, cameras) = (0, 0);
        foreach (var file in VfsFiles.Find(fs, ".thn")
                     .Where(f => f.StartsWith(@"DATA\SCRIPTS\BASES\", StringComparison.OrdinalIgnoreCase)))
        {
            files++;
            var model = ThnScriptModel.From(evaluator.Evaluate(fs.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var entity in model.Entities.Where(e =>
                         ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, evaluator.Symbols) == "CAMERA"))
            {
                cameras++;
                if (entity.Table["cameraprops"] is not ThnTable props)
                    continue;
                foreach (var key in Keys)
                {
                    if (props[key] is ThnNumber number)
                        values[key].Add(number.Value);
                }
            }
        }

        var medians = Keys.ToDictionary(k => k, k => Median(values[k]));
        output.WriteLine($"{files} files in SCRIPTS\\BASES, {cameras} CAMERA entities.");
        foreach (var key in Keys)
            output.WriteLine($"{key}: median {medians[key]:R} of {values[key].Count} values.");

        var camera = ThnScriptModel.From(ThnTemplates.NewScript(), ThnElementId.Counter()).EntityNamed("Camera_01")!;
        var lens = (ThnTable)camera.Table["cameraprops"]!;
        foreach (var key in Keys)
            Assert.Equal(medians[key], ((ThnNumber)lens[key]!).Value);
    }

    /// <summary>
    /// The light of a new LIGHT entity (milestone MCP1-4): for each key, the value that most LIGHT entities of the retail
    /// scripts have. Most retail lights have no diffuse color, so the color of a new light is the most common color of
    /// the other lights.
    /// </summary>
    [Fact]
    public void TheLightOfANewEntityHasTheMostCommonValuesOfTheRetailLights()
    {
        var fs = FileSystem.FromPath(GameDataPaths.RequireBase());
        var evaluator = ThornEvaluator.Instance;
        var counts = new Dictionary<string, Dictionary<string, int>>();
        void Count(string key, string value)
        {
            if (!counts.TryGetValue(key, out var values))
                counts[key] = values = [];
            values[value] = values.GetValueOrDefault(value) + 1;
        }
        var lights = 0;
        foreach (var file in VfsFiles.Find(fs, ".thn"))
        {
            var model = ThnScriptModel.From(evaluator.Evaluate(fs.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var entity in model.Entities.Where(e =>
                         ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, evaluator.Symbols) == "LIGHT"))
            {
                lights++;
                if (entity.Table["lightprops"] is not ThnTable props)
                    continue;
                Count("type", ThnSchema.TypeName(props["type"], "LightTypes", evaluator.Symbols) ?? "none");
                Count("on", props["on"] switch { ThnBool b => b.Value.ToString(), ThnNumber n => (n.Value != 0).ToString(), _ => "none" });
                foreach (var key in new[] { "diffuse", "direction", "range", "atten" })
                    Count(key, props[key] is { } value ? ThnWriter.Value(value) : "none");
            }
        }

        string Most(string key, Func<string, bool>? where = null) =>
            counts[key].Where(c => where?.Invoke(c.Key) != false).MaxBy(c => c.Value).Key;
        var black = ThnWriter.Value(ThnPose.Vector(Vector3.Zero));
        foreach (var (key, values) in counts)
            output.WriteLine($"{key}: {string.Join(", ", values.OrderByDescending(v => v.Value).Take(3).Select(v => $"{v.Key} x{v.Value}"))}");
        output.WriteLine($"{lights} LIGHT entities.");

        var light = (ThnTable)ThnTemplates.NewEntity("LIGHT", "lamp", null, null, null)["lightprops"]!;
        Assert.Equal(Most("type"), ((ThnSymbol)light["type"]!).Name);
        Assert.Equal(Most("on"), ((ThnBool)light["on"]!).Value.ToString());
        Assert.Equal(black, Most("diffuse"));
        Assert.Equal(Most("diffuse", v => v != black), ThnWriter.Value(light["diffuse"]!));
        foreach (var key in new[] { "direction", "range", "atten" })
            Assert.Equal(Most(key), ThnWriter.Value(light[key]!));
    }

    /// <summary>The median of the values: the value in the middle, or for an even count the lower of the two middle
    /// values, so that the median is a value that a retail camera has.</summary>
    private static float Median(List<float> values)
    {
        var sorted = values.Order().ToList();
        return sorted[(sorted.Count - 1) / 2];
    }
}
