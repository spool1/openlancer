using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using LibreLancer.Thn;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The clip list of the animation picker on the retail data (section 12.1 of the M8b design). The list has the names of
/// the animation table of the engine, and the last file wins for a name in two files. Each START_MOTION value on a
/// character is in the list.
/// </summary>
public sealed class AnimationCatalogRetailTests(ITestOutputHelper output)
{
    private const string Flicker = "Sc_ADHEAD_MOTION_FLCKR_EYES_000LV_XA_06";

    /// <summary>
    /// The list has the names of the table that the engine plays from, without letter case. A facial clip that four files
    /// have comes from the last file, FacialJuni.anm. The clip also belongs to the skeletons of the other files, so a
    /// male head gets it in the group of its own skeletons.
    /// </summary>
    [Fact]
    public void TheListHasTheNamesOfTheEngineTable()
    {
        GameDataPaths.RequireBase();
        var data = RetailGame.Instance.Data;
        var clock = Stopwatch.StartNew();
        var clips = AnimationClips.Read(data);
        var read = clock.Elapsed;
        var table = data.GetCharacterAnimations().Scripts.Keys;

        Assert.Equal(table.Count, clips.Clips.Count);
        Assert.True(clips.Clips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(table));
        var flicker = clips.Find(Flicker);
        Assert.NotNull(flicker);
        Assert.Equal("FacialJuni.anm", FileName(flicker.Archive), ignoreCase: true);
        Assert.Contains("FacialMale.anm", flicker.Archives.Select(FileName), StringComparer.OrdinalIgnoreCase);
        var males = data.Items.Costumes.Where(c => c.Head?.Sex == FLGender.male).ToList();
        Assert.NotEmpty(males);
        Assert.All(males, costume => Assert.Equal("Skeletons of the target",
            clips.Model(Flicker, AnimationClips.Bodyparts(costume), "the target").Rows.Single(r => r.Value == Flicker)
                .Group));
        output.WriteLine($"{clips.Clips.Count} clips in {clips.Clips.Select(c => c.Archive).Distinct().Count()} files, " +
                         $"read in {read.TotalMilliseconds:0.0} ms. {Flicker} is in {flicker.Archives.Count} files.");
    }

    /// <summary>
    /// Each START_MOTION value of the retail cutscenes whose first target is a character (DEFORMABLE) is in the list. The
    /// test chooses the row as the Inspector does: from the targets of the Timeline event and the entities of the
    /// script. A value on a COMPOUND names a script of its model, so the row of such an event keeps its text field (risk
    /// R5).
    /// </summary>
    [Fact]
    public void EachRetailAnimationOfACharacterIsInTheList()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var clips = AnimationClips.Read(RetailGame.Instance.Data);
        var evaluator = ThornEvaluator.Instance;
        var path = new ThnPath(ThnAnimationPicker.AnimationPath);
        var (characters, others) = (0, 0);
        var missing = new List<string>();
        var otherFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            var script = ThnScriptModel.From(evaluator.Evaluate(view.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var ev in TimelineModel.Build(file, script, evaluator.Symbols, ScriptPlay.Normal).Events)
            {
                if (ev.Kind != EventTypes.StartMotion ||
                    script.Element(ThnElementKind.Event, ev.Ref.Id) is not { } element ||
                    ThnElementPaths.Get(ThnElementKind.Event, element.Table, path) is not ThnText value)
                {
                    continue;
                }

                if (!ThnAnimationPicker.Applies(ev.Kind, path.Text, script, ev.Targets))
                {
                    others++;
                    otherFiles.Add(file);
                    continue;
                }

                characters++;
                if (!clips.Contains(value.Value))
                {
                    missing.Add($"{file}: {value.Value}");
                }
            }
        }

        output.WriteLine($"START_MOTION values: {characters} on a DEFORMABLE, {others} on another target in " +
                         $"{otherFiles.Count} files.");
        Assert.Empty(missing);
        Assert.True(characters > 10_000, $"Only {characters} values on a DEFORMABLE.");
        Assert.True(others > 0);
    }

    private static string FileName(string archive) => Path.GetFileName(archive.Replace('\\', '/'));
}
