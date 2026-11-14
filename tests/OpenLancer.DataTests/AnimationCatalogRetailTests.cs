using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The clip list of the animation picker and the data list <c>animation</c> on the retail data (section 12.1 of the M8b
/// design). The picker reads the data list of the session. The lists have the names of the animation table of the
/// engine, and the last file wins for a name in two files. Each START_MOTION value on a character is in the data list,
/// so the save gate gives no retail clip problem.
/// </summary>
public sealed class AnimationCatalogRetailTests(ITestOutputHelper output)
{
    private const string Flicker = "Sc_ADHEAD_MOTION_FLCKR_EYES_000LV_XA_06";

    /// <summary>
    /// The clip list of the picker comes from the data list <c>animation</c> of a session. It has the names of the table
    /// that the engine plays from, without letter case. A facial clip that four files have comes from the last file,
    /// FacialJuni.anm. The clip also belongs to the skeletons of the other files, so each costume with a male head of
    /// the costume list of W gets it in the group of its own skeletons.
    /// </summary>
    [Fact]
    public void TheListHasTheNamesOfTheEngineTable()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8clist");
        var session = workspace.Open();
        var data = RetailGame.Instance.Data;
        var clock = Stopwatch.StartNew();
        var clips = AnimationClips.From(session.Lists.Animations()!);
        var read = clock.Elapsed;
        var table = data.GetCharacterAnimations().Scripts.Keys;
        var costumes = session.Lists.List("costume")!;

        Assert.Equal(table.Count, clips.Clips.Count);
        Assert.True(clips.Clips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(table));
        var flicker = clips.Find(Flicker);
        Assert.NotNull(flicker);
        Assert.Equal("FacialJuni.anm", FileName(flicker.Archive), ignoreCase: true);
        Assert.Contains("FacialMale.anm", flicker.Archives.Select(FileName), StringComparer.OrdinalIgnoreCase);
        var males = data.Items.Costumes.Where(c => c.Head?.Sex == FLGender.male).ToList();
        Assert.NotEmpty(males);
        Assert.All(males, costume => Assert.Equal("Skeletons of the target",
            clips.Model(Flicker, CostumeParts.Of(session.Get(costumes.Find(costume.Nickname)!.Section!)!).Names,
                "the target").Rows.Single(r => r.Value == Flicker).Group));
        output.WriteLine($"{clips.Clips.Count} clips in {clips.Clips.Select(c => c.Archive).Distinct().Count()} files, " +
                         $"read in {read.TotalMilliseconds:0.0} ms. {Flicker} is in {flicker.Archives.Count} files.");
    }

    /// <summary>
    /// The juni case of the M21 design (section 15.2, item 7): the START_MOTION list of a DEFORMABLE with the costume
    /// juni starts with the clips of FacialJuni.anm and BodyGenericFemale.anm, from the session list. The costume comes
    /// from the costume list of W, as the picker reads it, and its body gives the female idle.
    /// </summary>
    [Fact]
    public void TheListOfACharacterWithTheCostumeJuniStartsWithItsOwnFiles()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8clist");
        var session = workspace.Open();
        var clips = AnimationClips.From(session.Lists.Animations()!);
        var juni = CostumeParts.Of(session.Get(session.Lists.List("costume")!.Find("juni")!.Section!)!);

        var model = clips.Model("", juni.Names, "Juni");
        var own = model.Rows.TakeWhile(r => r.Group == "Skeletons of Juni").Select(r => clips.Find(r.Value)!).ToList();
        var skeletonFiles = session.Lists.Animations()!.Skeletons.Where(s => s.Bodyparts.Overlaps(juni.Names))
            .SelectMany(s => s.Archives).Select(FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool OfJuni(AnimationClip clip) => clip.Archives.Select(FileName).Any(skeletonFiles.Contains);

        output.WriteLine($"juni: {string.Join(", ", juni.Names.Order(StringComparer.OrdinalIgnoreCase))}. The files of its " +
                         $"skeletons: {string.Join(", ", skeletonFiles.Order(StringComparer.OrdinalIgnoreCase))}. " +
                         $"{own.Count} of {model.Rows.Count} clips come first.");
        Assert.Contains("FacialJuni.anm", skeletonFiles);
        Assert.Contains("BodyGenericFemale.anm", skeletonFiles);
        Assert.Equal(clips.Clips.Count(OfJuni), own.Count);
        Assert.All(own, c => Assert.True(OfJuni(c), c.Name));
        Assert.All(model.Rows.Skip(own.Count), r => Assert.Equal(AnimationClips.OtherSkeletons, r.Group));
        Assert.Contains(own, c => FileName(c.Archive).Equals("FacialJuni.anm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(own, c => FileName(c.Archive).Equals("BodyGenericFemale.anm", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FLGender.female, clips.SexOf(juni));
    }

    /// <summary>
    /// The data list <c>animation</c> of a retail session has the clips of the engine table, without letter case, each
    /// with the file of the engine: the last file with the name. The skeletons come with the clips. The test writes the
    /// time of the first read and of a second build whose files are all in the cache. A new <c>[Animations]</c> line
    /// that names a file of the cache reads no file.
    /// </summary>
    [Fact]
    public void TheSessionListHasTheClipsOfTheEngineTable()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8clist");
        var session = workspace.Open();
        var items = RetailGame.Instance.Data.Items;
        var engine = AnimationCatalog.Read(items.VFS, items.Ini.Bodyparts.Animations.Select(items.DataPath).OfType<string>());

        var clock = Stopwatch.StartNew();
        var list = session.Lists.List("animation")!;
        var first = clock.Elapsed;
        var reads = session.Lists.AnimationReads;
        var data = session.Lists.Animations()!;
        var archive = ThnReferences.IniValueOf(data.Clips[0].Archive)!;
        session.Execute(EditCommand.AddSection(session.DataFiles.Paths("bodyparts")[0], "Animations",
            [new EntryView("anim", [new StringValue(archive)])]));
        clock.Restart();
        var again = session.Lists.List("animation")!;
        var cached = clock.Elapsed;

        Assert.True(list.Closed);
        Assert.Equal(engine.Select(c => (c.Name.ToLowerInvariant(), FileName(c.Archive).ToLowerInvariant())),
            data.Clips.Select(c => (c.Name.ToLowerInvariant(), FileName(c.Archive).ToLowerInvariant())));
        Assert.True(list.Entries.Select(e => e.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(RetailGame.Instance.Data.GetCharacterAnimations().Scripts.Keys));
        Assert.NotEmpty(data.Skeletons);
        Assert.Empty(data.Warnings);
        Assert.NotSame(list, again);
        Assert.Equal(list.Entries.Count, again.Entries.Count);
        Assert.Equal(reads, session.Lists.AnimationReads);
        output.WriteLine($"{list.Entries.Count} clips from {reads} files. First read {first.TotalMilliseconds:0.0} ms, " +
                         $"build with the cached names {cached.TotalMilliseconds:0.0} ms.");
    }

    /// <summary>
    /// Each START_MOTION event of the retail cutscenes whose first target is a character (DEFORMABLE) has a value that is
    /// in the data list <c>animation</c> of a retail session, by the name as it is. So no such event lacks the key or has
    /// an empty clip. The test chooses the row as the Inspector does: from
    /// the targets of the Timeline event and the entities of the script. A value on a COMPOUND names a script of its
    /// model, so the row of such an event keeps its text field (risk R5), and the save gate does not check it.
    /// </summary>
    [Fact]
    public void EachRetailAnimationOfACharacterIsInTheList()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8clist");
        var clips = workspace.Open().Lists.List("animation")!;
        var evaluator = ThornEvaluator.Instance;
        var path = new ThnPath(ThnTemplateRules.AnimationPath);
        var (characters, others) = (0, 0);
        var missing = new List<string>();
        var otherFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            var script = ThnScriptModel.From(evaluator.Evaluate(view.ReadAllBytes(file), file), ThnElementId.Counter());
            foreach (var ev in TimelineModel.Build(file, script, evaluator.Symbols, ScriptPlay.Normal).Events)
            {
                if (ev.Kind != EventTypes.StartMotion ||
                    script.Element(ThnElementKind.Event, ev.Ref.Id) is not { } element)
                {
                    continue;
                }

                var value = ThnElementPaths.Get(ThnElementKind.Event, element.Table, path);
                if (!ThnAnimationPicker.Applies(ev.Kind, path.Text, script, ev.Targets))
                {
                    if (value is ThnText)
                    {
                        others++;
                        otherFiles.Add(file);
                    }

                    continue;
                }

                // An event without the key crashes the game, and an empty clip names no clip.
                characters++;
                if (value is not ThnText text || clips.Find(text.Value) is not { } clip ||
                    !clip.Value.Equals(text.Value, StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add($"{file}: {(value is ThnText shown ? shown.Value : "no props.animation")}");
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
