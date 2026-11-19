using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>
/// The animation clips of W and the skeletons of bodyparts.ini (see <see cref="DataLists.Animations"/>).
/// </summary>
/// <param name="Clips">The clips, sorted by name. Each clip has all its files in load order.</param>
/// <param name="Skeletons">The skeletons of bodyparts.ini (see <see cref="AnimationCatalog.Skeletons"/>).</param>
/// <param name="Warnings">One warning for each animation file that does not read. The clips of the other files stay
/// in the list, but the game closes at such a file (see <see cref="AnimationCatalog.GameStops"/>). The save gives the
/// warnings of the files of the active mod.</param>
public sealed record AnimationData(IReadOnlyList<AnimationClip> Clips, IReadOnlyList<AnimationSkeleton> Skeletons,
    IReadOnlyList<Diagnostic> Warnings)
{
    /// <summary>The entries of the list <c>animation</c>, in the order of <see cref="Clips"/>.</summary>
    internal IReadOnlyList<ListEntry> Entries { get; init; } = [];
}

/// <summary>
/// The source of the list <c>animation</c> (source kind <c>animationScripts</c>, section 12.1 of the M8b design): the
/// scripts of the animation files that the <c>[Animations]</c> lines of W of bodyparts.ini name, as the engine loads
/// them (GameDataManager.cs:65-85). The engine reads value 0 of each line of each <c>[Animations]</c> section
/// (BodypartsIni.cs:30-36), joins it to the data path, and skips a file that does not exist (GameItemDb.cs:122-138).
/// </summary>
/// <remarks>
/// <para>A file of the edit view has a backing file on disk. The class keeps the names of each backing file with its
/// length and its last write time (UTC), so a new edit view after a save reads no animation file again, and a new
/// <c>[Animations]</c> line reads only its own file. A file without a backing file is read at each build. The 11 retail
/// files hold about 100 MB, but the reader reads only the node and the string blocks (see
/// <see cref="AnimationNames"/>).</para>
/// <para>The data changes only when the lines change or the edit view changes. An edit of another section of
/// bodyparts.ini makes only the skeletons again.</para>
/// </remarks>
internal sealed class AnimationList(EditSession session)
{
    private const string Role = "bodyparts";
    private const string AnimationsSection = "Animations";

    private readonly Dictionary<string, Stamped> names = new(StringComparer.OrdinalIgnoreCase);

    // The data of the last build, with the table, the lines and the edit view that gave it.
    private (FileTable? Table, IReadOnlyList<string> Lines, ModOverlay View, AnimationData Data)? last;
    private HashSet<string>? lowerClips;

    /// <summary>The number of animation files that the list read. A test reads it to check the cache.</summary>
    public int Reads { get; private set; }

    /// <summary>The bodyparts.ini of the engine: the last <c>bodyparts</c> line of <c>freelancer.ini</c>, or null.</summary>
    public string? BodypartsPath => session.DataFiles.Paths(Role) is [.., var path] ? path : null;

    /// <summary>The clips and the skeletons of W, or null when the edit view has no bodyparts.ini.</summary>
    public AnimationData? Get()
    {
        if (BodypartsPath is not { } path || session.Lists.TableOf(path) is not { } table)
            return null;
        var view = session.EditView;
        if (last is { } known && ReferenceEquals(known.Table, table) && ReferenceEquals(known.View, view))
            return known.Data;
        var lines = Lines(table.Working);
        var files = view.CreateFileSystem();
        var skeletons = AnimationCatalog.Skeletons(table.Working, value => Resolve(files, value));
        AnimationData data;
        if (last is { } previous && ReferenceEquals(previous.View, view) && previous.Lines.SequenceEqual(lines))
            data = previous.Data with { Skeletons = skeletons };
        else
            data = Build(files, lines, skeletons);
        last = (table, lines, view, data);
        return data;
    }

    /// <summary>
    /// True when the lower layer has a clip with the name, without letter case: the <c>[Animations]</c> lines of the
    /// lower layer name a file with the clip. The lower layer does not change in a session, so the class reads it once.
    /// The save gate asks only for a clip that W does not have, to know if the active mod removed it.
    /// </summary>
    public bool LowerLayerHas(string clip) => LowerClips().Contains(clip);

    /// <summary>
    /// The clips that the lower layer has and W does not have, sorted by name. Only an animation file of the active mod,
    /// or a change of bodyparts.ini by the active mod, can remove a clip. A change in the open transaction or batch
    /// counts too. Else the result is empty, and the class reads no file. The save gate reads the result to find the
    /// cutscenes that a change of the active mod can break.
    /// </summary>
    public IReadOnlyList<string> RemovedClips()
    {
        if (session.ActiveMod is not { } active || BodypartsPath is not { } path ||
            !active.Files.Keys.Any(k => k.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) &&
            !session.ActiveChangedPathsWithOpen().Contains(path, VfsPath.Comparer) ||
            Get() is not { } data)
            return [];
        var now = data.Clips.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return LowerClips().Where(c => !now.Contains(c)).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The clips of the lower layer, without letter case. The class reads them once for each session.</summary>
    private HashSet<string> LowerClips()
    {
        if (lowerClips == null)
        {
            var files = session.LowerFiles;
            var sections = BodypartsPath is { } path ? session.LowerSections(path) : null;
            lowerClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var archive in Archives(files, Lines(sections ?? [])))
            {
                if (NamesOf(files, archive, out _) is { } list)
                    lowerClips.UnionWith(list);
            }
        }
        return lowerClips;
    }

    private AnimationData Build(FileSystem files, IReadOnlyList<string> lines, IReadOnlyList<AnimationSkeleton> skeletons)
    {
        var read = new List<(string, IReadOnlyList<string>)>();
        var warnings = new List<Diagnostic>();
        foreach (var archive in Archives(files, lines))
        {
            if (NamesOf(files, archive, out var problem) is { } list)
                read.Add((archive, list));
            else
                warnings.Add(AnimationCatalog.Unreadable(archive, problem!));
        }
        var clips = AnimationCatalog.Merge(read);
        var entries = clips.Select(clip =>
        {
            var (origin, mod, _) = session.FileOrigin(clip.Archive);
            return new ListEntry(clip.Name, null, origin, mod, Path.GetFileName(clip.Archive.Replace('\\', '/')));
        }).ToList();
        return new AnimationData(clips, skeletons, warnings) { Entries = entries };
    }

    /// <summary>The install paths of the files that the lines name and that exist, in the order of the lines.</summary>
    private IEnumerable<string> Archives(FileSystem files, IReadOnlyList<string> lines) =>
        lines.Select(line => Resolve(files, line)).OfType<string>();

    /// <summary>The install path of a file name of a line, joined to the data path, or null when the file does not
    /// exist.</summary>
    private string? Resolve(FileSystem files, string value) =>
        session.DataPath(value) is { } path && files.FileExists(path) ? path : null;

    /// <summary>The names of a file, from the cache when its backing file did not change, or null with the problem
    /// when the file does not read.</summary>
    private IReadOnlyList<string>? NamesOf(FileSystem files, string path, out string? problem)
    {
        problem = null;
        var backing = files.GetBackingFileName(path);
        var stamp = backing == null ? null : Stamp(backing);
        if (stamp != null && names.TryGetValue(backing!, out var known) && known.Stamp == stamp)
        {
            problem = known.Problem;
            return known.Problem == null ? known.Names : null;
        }
        Reads++;
        var ok = AnimationCatalog.TryNames(files, path, out var list, out var failure);
        if (stamp != null)
            names[backing!] = new Stamped(stamp.Value, list, ok ? null : failure);
        problem = ok ? null : failure;
        return ok ? list : null;
    }

    /// <summary>The length and the last write time of a file on disk, or null when the file does not exist.</summary>
    private static (long Length, DateTime Written)? Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
    }

    /// <summary>Value 0 of each line of each <c>[Animations]</c> section, in file order.</summary>
    private static List<string> Lines(IEnumerable<Section> sections) =>
        sections.Where(s => s.Name.Equals(AnimationsSection, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => s).Where(e => e.Count > 0).Select(e => e[0].ToString()!.Trim())
            .Where(v => v.Length > 0).ToList();

    /// <summary>The names of a backing file at one stamp, or the problem when it does not read.</summary>
    private sealed record Stamped((long Length, DateTime Written) Stamp, IReadOnlyList<string> Names, string? Problem);
}
