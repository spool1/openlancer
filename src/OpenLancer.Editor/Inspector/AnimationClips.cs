using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Schema;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Thn;

namespace OpenLancer.Editor.Inspector;

/// <summary>The body part filter of the clip list.</summary>
internal enum ClipPart
{
    /// <summary>Each clip.</summary>
    All,

    /// <summary>The clips of the files of a body skeleton.</summary>
    Body,

    /// <summary>The clips of the files of a head skeleton.</summary>
    Head,

    /// <summary>The clips of the files of a hand skeleton.</summary>
    Hands,

    /// <summary>The clips of the files that no skeleton lists.</summary>
    Other,
}

/// <summary>The body parts of a costume of W, for the order of the clip list and the idle of the clip preview.</summary>
/// <param name="Body">The nickname of the body, or null.</param>
/// <param name="Names">The nicknames of the body, the head and the hands, without letter case.</param>
internal sealed record CostumeParts(string? Body, IReadOnlySet<string> Names)
{
    /// <summary>The body parts that a <c>[Costume]</c> section names: value 0 of the last <c>body</c>, <c>head</c>,
    /// <c>lefthand</c> and <c>righthand</c> line with a value (Costume.cs of the engine schema).</summary>
    public static CostumeParts Of(SectionView costume)
    {
        string? Value(string key) =>
            costume.Lines(key).LastOrDefault(l => l.Values.Count > 0)?.Values[0].ToString()?.Trim() is { Length: > 0 } value
                ? value
                : null;

        return new CostumeParts(Value("body"),
            new[] { "body", "head", "lefthand", "righthand" }.Select(Value).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The animation clips of W for the picker of the animation of a START_MOTION event (section 12.1 of the M8b design),
/// without ImGui. The clips come from the data list <c>animation</c> (see <see cref="DataLists.Animations"/>): the
/// scripts of the files that the <c>[Animations]</c> lines of bodyparts.ini name, in the load order of the engine. For a
/// name in two files, the last file wins. The skeletons of bodyparts.ini group the clips by the files of their
/// <c>[PetalAnimations]</c>. A name in two files belongs to the skeletons of both files.
/// </summary>
internal sealed class AnimationClips
{
    /// <summary>The header of the clips of the skeletons of other costumes.</summary>
    public const string OtherSkeletons = "Other skeletons";

    /// <summary>Retail gives each standing idle of a body this word in its name.</summary>
    private const string StandingIdle = "_STND_IDLE_";

    /// <summary>The words of the labels in the order that they apply, and the label of each word.</summary>
    private static readonly (string Word, string Label)[] Words =
    [
        ("WALK", "Walk"), ("WLKG", "Walk"), ("SHRG", "Shrug"), ("NOD", "Nod"), ("SHAKHEAD", "Shake head"),
        ("LSTN", "Listen"), ("WALLA", "Talk"), ("TALK", "Talk"), ("GEST", "Gesture"), ("SIT", "Sit"), ("TURN", "Turn"),
        ("IDLE", "Idle"),
    ];

    private readonly IReadOnlyList<AnimationSkeleton> skeletons;

    // The index of each clip name in Clips and in rows, without letter case.
    private readonly Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimationSkeleton[]> byArchive = new(StringComparer.OrdinalIgnoreCase);

    // The rows of the list in the order of the clips, with their labels, so an opening does not make the labels again.
    private readonly ListPickerRow[] rows;

    /// <param name="clips">The clips, sorted by name.</param>
    /// <param name="skeletons">The skeletons of bodyparts.ini.</param>
    /// <param name="warnings">One warning for each animation file that the list cannot read, or null for none.</param>
    public AnimationClips(IReadOnlyList<AnimationClip> clips, IReadOnlyList<AnimationSkeleton> skeletons,
        IReadOnlyList<Diagnostic>? warnings = null)
    {
        Warnings = warnings ?? [];
        Clips = clips;
        this.skeletons = skeletons;
        for (var i = 0; i < clips.Count; i++)
        {
            byName[clips[i].Name] = i;
        }

        foreach (var archive in clips.SelectMany(c => c.Archives).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            byArchive[archive] = skeletons.Where(s => s.Archives.Contains(archive, StringComparer.OrdinalIgnoreCase))
                .ToArray();
        }

        rows = clips.Select(c => new ListPickerRow(ListRowKind.Entry, c.Name, c.Name, null) { Note = Label(c.Name) })
            .ToArray();
    }

    /// <summary>The clips, sorted by name.</summary>
    public IReadOnlyList<AnimationClip> Clips { get; }

    /// <summary>One warning for each animation file that the list cannot read. Its clips are not in the list.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; }

    /// <summary>The clips, the skeletons and the warnings of the data list <c>animation</c> of a session.</summary>
    public static AnimationClips From(AnimationData data) => new(data.Clips, data.Skeletons, data.Warnings);

    /// <summary>True when the game has a clip with the name, without letter case.</summary>
    public bool Contains(string name) => byName.ContainsKey(name);

    /// <summary>The clip with a name without letter case, or null. The name keeps its spaces, because the game finds the
    /// clip by the name as it is.</summary>
    public AnimationClip? Find(string name) => byName.TryGetValue(name, out var index) ? Clips[index] : null;

    /// <summary>The label of a clip of the list (see <see cref="Label"/>), which the list made once.</summary>
    public string LabelOf(AnimationClip clip) => byName.TryGetValue(clip.Name, out var index) ? rows[index].Note : "";

    /// <summary>The kinds of body parts of the skeletons that list a file of a clip. None when no skeleton lists
    /// one.</summary>
    public SkeletonParts PartsOf(AnimationClip clip) =>
        SkeletonsOf(clip).Aggregate(SkeletonParts.None, (parts, skeleton) => parts | skeleton.Parts);

    /// <summary>The files of a clip, as a line of the pane, for example "File: body_m.anm". For a name in more files,
    /// the line names each file and the file that the game plays.</summary>
    public static string FileText(AnimationClip clip)
    {
        var names = clip.Archives.Select(FileName).ToList();
        return names.Count < 2
            ? $"File: {FileName(clip.Archive)}"
            : $"Files: {string.Join(", ", names)}. The game plays the clip of the last file, {FileName(clip.Archive)}.";
    }

    /// <summary>The skeletons that list a file of a clip, as a line of the pane, for example "Skeletons: male body,
    /// female body".</summary>
    public string SkeletonText(AnimationClip clip)
    {
        var texts = SkeletonsOf(clip).Select(Describe).Distinct().ToList();
        return texts.Count == 0
            ? "No skeleton of bodyparts.ini lists this file."
            : $"Skeletons: {string.Join(", ", texts)}";
    }

    /// <summary>
    /// The standing idle of the body skeletons of a sex: the first clip by name with the word of a standing idle, in a
    /// file of a body skeleton of that sex. Null when the data has no such clip, or for no sex.
    /// </summary>
    public string? Idle(FLGender sex) =>
        sex == FLGender.unset
            ? null
            : Clips.FirstOrDefault(c => c.Name.Contains(StandingIdle, StringComparison.OrdinalIgnoreCase) &&
                                        SkeletonsOf(c).Any(s => s.Sex == sex && s.Parts.HasFlag(SkeletonParts.Body)))
                ?.Name;

    /// <summary>
    /// The idle that the clip preview starts before a clip, or null. A head or a hand clip moves only the bones of its
    /// part, so without a body clip the body keeps its bind pose with the arms out. So the preview first starts the
    /// standing idle of the body, as a character in a scene stands while it talks. A body clip needs no idle.
    /// </summary>
    /// <param name="sex">The sex of the body part of the body of the character. A character without a known costume
    /// takes the first sex of the skeletons of the clip.</param>
    public string? IdleFor(AnimationClip clip, FLGender sex)
    {
        if (PartsOf(clip).HasFlag(SkeletonParts.Body))
        {
            return null;
        }

        if (sex == FLGender.unset)
        {
            sex = SkeletonsOf(clip).Select(s => s.Sex).FirstOrDefault(s => s != FLGender.unset);
        }

        return Idle(sex);
    }

    /// <summary>
    /// The sex of the body of a costume: the sex of the last skeleton that lists the nickname of the body. The engine
    /// keeps the last body part with a nickname (GameItemCollection.cs:54-69), and it gives a body part the sex of the
    /// last <c>[Skeleton]</c> before it (BodypartsIni.cs:43-60). Unset for no costume, a costume without a body, a body
    /// that no skeleton lists, or a skeleton without a sex.
    /// </summary>
    /// <remarks>A <c>[Skeleton]</c> with another sex value, for example "none", has no sex here. The engine keeps the sex
    /// of the <c>[Skeleton]</c> before it (see <see cref="AnimationCatalog.Skeletons"/>). So a body of such a skeleton
    /// gives unset, and <see cref="IdleFor"/> then takes the sex of the skeletons of the clip.</remarks>
    public FLGender SexOf(CostumeParts? costume) =>
        costume?.Body is { } body
            ? skeletons.LastOrDefault(s => s.Bodyparts.Contains(body))?.Sex ?? FLGender.unset
            : FLGender.unset;

    /// <summary>
    /// The rows of the list (decision D119): the clips of the skeletons of the costume of the target first, under the
    /// header "Skeletons of" the target, and then the other clips under "Other skeletons". Each group is sorted by name.
    /// A target without a costume gets one group without a header. A value outside the list gets the "Missing" row.
    /// </summary>
    /// <param name="current">The value of the field.</param>
    /// <param name="costume">The nicknames of the body parts of the costume of the target, or null.</param>
    /// <param name="target">The name of the target.</param>
    public ListPickerModel Model(string current, IReadOnlySet<string>? costume, string target)
    {
        if (costume == null)
        {
            return new ListPickerModel(rows, current);
        }

        var own = skeletons.Where(s => s.Bodyparts.Overlaps(costume)).SelectMany(s => s.Archives)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mine = Clips.Select(c => c.Archives.Any(own.Contains)).ToArray();
        var header = $"Skeletons of {target}";
        var ordered = new List<ListPickerRow>(rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            if (mine[i])
            {
                ordered.Add(rows[i] with { Group = header });
            }
        }

        for (var i = 0; i < rows.Length; i++)
        {
            if (!mine[i])
            {
                ordered.Add(rows[i] with { Group = OtherSkeletons });
            }
        }

        return new ListPickerModel(ordered, current);
    }

    /// <summary>True when the body part filter shows a row: a clip of a file of a skeleton with that part, or for
    /// <see cref="ClipPart.Other"/> a clip of a file that no skeleton lists.</summary>
    public bool Shows(ListPickerRow row, ClipPart part) =>
        part == ClipPart.All || Find(row.Value) is { } clip && Fits(PartsOf(clip), part);

    /// <summary>True when a clip of skeletons with these parts fits the body part filter.</summary>
    internal static bool Fits(SkeletonParts parts, ClipPart part) => part switch
    {
        ClipPart.Body => (parts & SkeletonParts.Body) != 0,
        ClipPart.Head => (parts & SkeletonParts.Head) != 0,
        ClipPart.Hands => (parts & SkeletonParts.Hands) != 0,
        ClipPart.Other => parts == SkeletonParts.None,
        _ => true,
    };

    /// <summary>
    /// A short label from the words of a clip name, for example "Walk", or empty when no word fits. A word fits when a
    /// part of the name between underscores starts with it. Only a part in capitals counts, so the name of a character,
    /// for example Walker, gives no label. A word in a part, for example TURN in RETURNL, gives no label.
    /// </summary>
    public static string Label(string name)
    {
        var parts = name.Split('_').Where(p => p.Any(char.IsLetter) && !p.Any(char.IsLower)).ToList();
        foreach (var (word, label) in Words)
        {
            if (parts.Any(p => p.StartsWith(word, StringComparison.Ordinal)))
            {
                return label;
            }
        }

        return "";
    }

    /// <summary>
    /// The character of the clip preview: the character that the user chose, else the target of the event, else the
    /// first character. Null when the script has no character.
    /// </summary>
    /// <param name="characters">The names of the characters (DEFORMABLE entities) of the open script.</param>
    /// <param name="chosen">The name that the user chose, or null.</param>
    public static string? PreviewOn(IReadOnlyList<string> characters, string? chosen, string target) =>
        Named(characters, chosen) ?? Named(characters, target) ?? characters.FirstOrDefault();

    private static string? Named(IReadOnlyList<string> characters, string? name) =>
        name == null ? null : characters.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The skeletons that list a file of a clip, each skeleton once, in the order of the files.</summary>
    private IEnumerable<AnimationSkeleton> SkeletonsOf(AnimationClip clip) =>
        clip.Archives.SelectMany(archive => byArchive.GetValueOrDefault(archive) ?? []).Distinct();

    /// <summary>The file name of an install path, for example "body_m.anm".</summary>
    internal static string FileName(string archive) => Path.GetFileName(archive.Replace('\\', '/'));

    /// <summary>A warning of the list as a line, for example "broken.anm: OpenLancer cannot read this animation
    /// file ...".</summary>
    internal static string WarningText(Diagnostic warning) => $"{FileName(warning.Source)}: {warning.Message}";

    /// <summary>A skeleton as text, for example "male body" or "hands".</summary>
    private static string Describe(AnimationSkeleton skeleton)
    {
        var parts = new List<string>();
        if (skeleton.Parts.HasFlag(SkeletonParts.Body))
        {
            parts.Add("body");
        }

        if (skeleton.Parts.HasFlag(SkeletonParts.Head))
        {
            parts.Add("head");
        }

        if (skeleton.Parts.HasFlag(SkeletonParts.Hands))
        {
            parts.Add("hands");
        }

        var text = string.Join(" and ", parts);
        return skeleton.Sex == FLGender.unset ? text : $"{skeleton.Sex} {text}";
    }
}

/// <summary>
/// The state of the clip picker in a profile session, without ImGui. It holds the clips of the data list
/// <c>animation</c> of the session (see <see cref="DataLists.Animations"/>), and the body part filter. So an unsaved
/// edit of the <c>[Animations]</c> lines shows in the list. It also holds the character of the clip preview that the
/// user chose for the shown event. New data of the list makes the clips again. <see cref="Reset"/> forgets all when the
/// profile closes.
/// </summary>
internal sealed class ClipPickerState
{
    /// <summary>The reason when the editor has no game data.</summary>
    public const string NotLoaded = "The game data is not loaded.";

    /// <summary>The reason when the game data has no bodyparts.ini, so the list has no clips.</summary>
    public const string NoBodyparts = "The game data has no bodyparts.ini, so the editor has no animation clips.";

    /// <summary>The reason when the game data has no clip. The warnings of the list follow it, one on each line,
    /// because an animation file that does not read gives no clips.</summary>
    public const string NoClips = "The editor found no animation clips in the game data.";

    private AnimationData? data;
    private AnimationClips? clips;
    private ThnRef? shown;

    /// <summary>The body part filter of the list.</summary>
    public ClipPart Part { get; set; }

    /// <summary>The character of the clip preview that the user chose for the shown event, or null.</summary>
    public string? Chosen { get; set; }

    /// <summary>The clips of the data of the list, or null with the reason when the list is not available. The clips
    /// stay the same object while the data of the list stays the same object.</summary>
    /// <param name="current">The data of the list <c>animation</c>, or null when the game data has no
    /// bodyparts.ini.</param>
    public AnimationClips? Get(AnimationData? current, out string? reason)
    {
        if (current == null)
        {
            reason = NoBodyparts;
            return null;
        }

        if (!ReferenceEquals(current, data))
        {
            data = current;
            clips = current.Clips.Count == 0 ? null : AnimationClips.From(current);
        }

        reason = clips == null
            ? string.Join("\n", current.Warnings.Select(AnimationClips.WarningText).Prepend(NoClips))
            : null;
        return clips;
    }

    /// <summary>Takes the event that the picker shows. Another event forgets the chosen character.</summary>
    public void Show(ThnRef element)
    {
        if (!Equals(shown, element))
        {
            shown = element;
            Chosen = null;
        }
    }

    /// <summary>Forgets the data of the list, the clips, the shown event, the chosen character and the filter.</summary>
    public void Reset()
    {
        data = null;
        clips = null;
        shown = null;
        Chosen = null;
        Part = ClipPart.All;
    }
}
