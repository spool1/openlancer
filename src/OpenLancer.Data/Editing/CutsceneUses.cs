using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The items that each cutscene of the edit view uses: the templates and the clips that the checks of its script read,
/// by kind of item (see <see cref="ThnChecks.ItemUses"/>). The save gate reads the index to find the cutscenes that
/// use an item that a change of the active mod breaks (see <see cref="EditSession.ReferrerCutscenes"/>). So the gate
/// checks only these cutscenes, and not each cutscene that holds the name of the item as a word.
/// </summary>
/// <remarks>
/// <para>The index runs each script of the edit view one time, in a worker task. It keeps the uses of each script by
/// the content hash of its file. A save makes a new edit view, and the build for the new edit view runs only the files
/// with a new hash. So a save that changes only INI files runs no script. After such a save, the session starts that
/// build at once when it had an index (see <see cref="Follow"/>).</para>
/// <para>A script that does not run has no uses. The gate cannot open such a script, so it does not check it
/// either.</para>
/// <para>The names of the items compare without letter case and without the spaces around them. So the index can find
/// a cutscene that the gate then checks and finds no error in. It never leaves out a cutscene whose check asks for the
/// item.</para>
/// </remarks>
internal sealed class CutsceneUses(EditSession session)
{
    private ModOverlay? view;
    private Task<UseIndex>? build;

    /// <summary>The last complete index. The next build takes the uses of its hashes.</summary>
    private UseIndex? complete;

    /// <summary>Starts the build of the index for the edit view now, in a worker task, when it does not run or
    /// exist.</summary>
    public void Start()
    {
        if (build != null && ReferenceEquals(view, session.EditView))
            return;
        if (build is { IsCompletedSuccessfully: true } done)
            complete = done.Result;
        view = session.EditView;
        var files = view.CreateFileSystem();
        var known = complete;
        var evaluator = session.ThnEvaluator;
        build = Task.Run(() => UseIndex.Build(files, evaluator, known));
    }

    /// <summary>Starts the build for a new edit view when the index had a build for an earlier one. The session calls it
    /// after a save that made a new edit view, so the index is ready again before the next save.</summary>
    public void Follow()
    {
        if (build != null)
            Start();
    }

    /// <summary>True when the index of the edit view is complete.</summary>
    public bool IsReady => build is { IsCompletedSuccessfully: true } && ReferenceEquals(view, session.EditView);

    /// <summary>True when the build of the index of the edit view started: it runs or it is complete. A test reads it
    /// to check that a call did not start the build.</summary>
    public bool IsStarted => build != null && ReferenceEquals(view, session.EditView);

    /// <summary>The number of scripts that the build of the edit view ran, and the time of that build. Waits for the
    /// build. A test reads them.</summary>
    internal (int Runs, TimeSpan Time) LastBuild
    {
        get
        {
            Start();
            var index = build!.GetAwaiter().GetResult();
            return (index.Runs, index.Time);
        }
    }

    /// <summary>
    /// The cutscene scripts (<c>.thn</c>) of the edit view whose checks ask for one of the items, as install paths in
    /// normal form, each one time, in path order. Waits for the index when its build runs.
    /// </summary>
    /// <param name="items">The kinds and the names of the items.</param>
    public IReadOnlyList<string> Using(IEnumerable<(ThnItemKind Kind, string Name)> items)
    {
        Start();
        var index = build!.GetAwaiter().GetResult();
        return items.SelectMany(i => index.Using(i.Kind, i.Name))
            .Distinct(VfsPath.Comparer)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The uses of the scripts of one edit view.</summary>
    private sealed class UseIndex
    {
        /// <summary>The uses of each content hash of a script file.</summary>
        private readonly Dictionary<string, IReadOnlySet<(ThnItemKind Kind, string Template)>> uses = new(StringComparer.Ordinal);

        /// <summary>The scripts of each use, by the key of <see cref="Key"/>.</summary>
        private readonly Dictionary<string, List<string>> scripts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The number of scripts that the build ran. The other scripts had a known hash.</summary>
        public int Runs { get; private set; }

        /// <summary>The time of the build.</summary>
        public TimeSpan Time { get; private set; }

        /// <param name="known">The last complete index, or null. A file with one of its hashes does not run
        /// again.</param>
        public static UseIndex Build(FileSystem files, IThnEvaluator evaluator, UseIndex? known)
        {
            var clock = Stopwatch.StartNew();
            var index = new UseIndex();
            foreach (var path in VfsFiles.Find(files, ".thn"))
            {
                byte[] bytes;
                try
                {
                    bytes = files.ReadAllBytes(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The gate cannot open a file that does not read, so the file has no uses.
                    continue;
                }
                var hash = ContentHash.Of(bytes);
                if (!index.uses.TryGetValue(hash, out var found))
                {
                    if (known?.uses.GetValueOrDefault(hash) is not { } kept)
                    {
                        kept = Run(bytes, path, evaluator);
                        index.Runs++;
                    }
                    index.uses[hash] = found = kept;
                }
                var normal = VfsPath.Normalize(path) ?? path;
                foreach (var (kind, template) in found)
                {
                    var key = Key(kind, template);
                    if (!index.scripts.TryGetValue(key, out var list))
                        index.scripts[key] = list = [];
                    list.Add(normal);
                }
            }
            index.Time = clock.Elapsed;
            return index;
        }

        /// <summary>The scripts that use an item.</summary>
        public IReadOnlyList<string> Using(ThnItemKind kind, string name) =>
            scripts.GetValueOrDefault(Key(kind, name)) ?? [];

        /// <summary>The uses of one script, or none when it does not run. The gate opens a script with the same
        /// evaluator and gets the same exception, so it does not check such a script.</summary>
        private static IReadOnlySet<(ThnItemKind, string)> Run(byte[] bytes, string path, IThnEvaluator evaluator)
        {
            try
            {
                return ThnChecks.ItemUses(evaluator.Evaluate(bytes, path), evaluator);
            }
            catch (ThnEvaluationException)
            {
                return new HashSet<(ThnItemKind, string)>();
            }
        }

        /// <summary>The key of a use: the kind and the name without the spaces around it. The key compares without
        /// letter case, as the engine finds an item.</summary>
        private static string Key(ThnItemKind kind, string name) => $"{(int)kind}:{name.Trim()}";
    }
}
