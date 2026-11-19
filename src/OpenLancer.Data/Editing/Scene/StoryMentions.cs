using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibreLancer.Data.IO;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>A file that names a nickname as text, and the number of matches in it.</summary>
/// <param name="Path">The install path of the file.</param>
public sealed record StoryMention(string Path, int Count);

/// <summary>
/// Finds a nickname as text in the files that the reverse index of the lists does not read (decisions D239 and D261 of
/// the M3 design): each <c>.ini</c> and <c>.thn</c> file of the edit view, except the system files of
/// <c>universe.ini</c> and their nebula and field files. So the scan covers <c>KnowledgeMap.ini</c>,
/// <c>InitialWorld.ini</c>, the cutscenes and the files of the missions that are not scripts. A mention is a warning
/// only. The scan leaves out the mission scripts, because the mission index has each of their references, and a
/// rename changes them (decision D668 of the M6b design).
/// </summary>
/// <remarks>
/// <para>A match is the nickname without letter case, with no letter, digit or <c>_</c> just before or after it. A
/// binary INI file and a compiled cutscene hold their strings as plain bytes, so one method serves all files.</para>
/// <para>The index splits the lowercase bytes of each file into words of letters, digits and <c>_</c> one time, in a
/// worker task, and counts each word for each file. A nickname of such characters is then a dictionary lookup. A
/// nickname with another character, for example a space, uses a byte search over the kept bytes.</para>
/// <para>The index belongs to the edit view of the session. A save builds the edit view again, and the index follows
/// it. An unsaved edit of a cutscene is not in the scan.</para>
/// </remarks>
internal sealed class StoryMentions(EditSession session)
{
    private static readonly string[] Extensions = [".ini", ".thn"];

    private ModOverlay? view;
    private Task<WordIndex>? build;

    /// <summary>Starts the build of the index for the edit view now, in a worker task, when it does not run or exist.
    /// The editor calls it when a system opens.</summary>
    public void Start()
    {
        if (build != null && ReferenceEquals(view, session.EditView))
            return;
        view = session.EditView;
        var files = view.CreateFileSystem();
        build = Task.Run(() => WordIndex.Build(files));
    }

    /// <summary>True when the index of the edit view is complete.</summary>
    public bool IsReady => build is { IsCompletedSuccessfully: true } && ReferenceEquals(view, session.EditView);

    /// <summary>True when the build of the index of the edit view started: it runs or it is complete. A test reads it
    /// to check that a call did not start the build.</summary>
    public bool IsStarted => build != null && ReferenceEquals(view, session.EditView);

    /// <summary>The files that name the nickname, in path order, without the system files and their nebula and field
    /// files. Waits for the index when its build runs.</summary>
    public IReadOnlyList<StoryMention> Of(string nickname)
    {
        Start();
        var index = build!.GetAwaiter().GetResult();
        var skip = session.Systems.Files
            .Concat(session.FieldFiles.Files("Nebula"))
            .Concat(session.FieldFiles.Files("Asteroids"))
            .ToHashSet(VfsPath.Comparer);
        // The mission index has each reference of a mission script, so the scan leaves them out (decision D668).
        return index.Find(nickname.Trim())
            .Where(m => !skip.Contains(m.Path) &&
                        !(Ini.SectionKeys.IsMissionFile(m.Path) && session.OpenDocument(m.Path) is { HoldsScript: true }))
            .OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The cutscene scripts (<c>.thn</c>) of the edit view that hold a name as a word, without letter case. They are
    /// install paths in normal form, in path order. Unlike <see cref="Of"/>, the result skips no file. The save gate
    /// reads it to find the cutscenes that name an item that a change of the active mod breaks. A match in a script is
    /// not always a use of the item, so the caller checks each script. Waits for the index when its build runs.
    /// </summary>
    public IReadOnlyList<string> Cutscenes(string name)
    {
        Start();
        var index = build!.GetAwaiter().GetResult();
        return index.Find(name.Trim()).Select(m => m.Path)
            .Where(p => p.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The words of the files of one edit view.</summary>
    private sealed class WordIndex
    {
        private readonly List<string> paths = [];
        private readonly List<byte[]> lower = [];
        private readonly Dictionary<string, List<(int File, int Count)>> words = new(StringComparer.Ordinal);

        public static WordIndex Build(FileSystem files)
        {
            var index = new WordIndex();
            foreach (var path in Extensions.SelectMany(e => VfsFiles.Find(files, e)))
            {
                byte[] bytes;
                try
                {
                    bytes = files.ReadAllBytes(path);
                }
                catch (Exception)
                {
                    // A file that cannot be read has no mention. The scan gives warnings only.
                    continue;
                }
                index.Add(VfsPath.Normalize(path) ?? path, bytes);
            }
            return index;
        }

        private void Add(string path, byte[] read)
        {
            var file = paths.Count;
            paths.Add(path);
            // A copy, because the file system can share its bytes.
            var bytes = new byte[read.Length];
            for (var i = 0; i < read.Length; i++)
                bytes[i] = read[i] is >= (byte)'A' and <= (byte)'Z' ? (byte)(read[i] + ('a' - 'A')) : read[i];
            lower.Add(bytes);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var start = -1;
            for (var i = 0; i <= bytes.Length; i++)
            {
                if (i < bytes.Length && IsWord(bytes[i]))
                {
                    if (start < 0)
                        start = i;
                    continue;
                }
                if (start < 0)
                    continue;
                var word = Encoding.Latin1.GetString(bytes, start, i - start);
                counts[word] = counts.GetValueOrDefault(word) + 1;
                start = -1;
            }
            foreach (var (word, count) in counts)
            {
                if (!words.TryGetValue(word, out var list))
                    words[word] = list = [];
                list.Add((file, count));
            }
        }

        public IEnumerable<StoryMention> Find(string nickname)
        {
            var key = nickname.ToLowerInvariant();
            if (key.Length == 0)
                return [];
            if (key.All(c => c < 128 && IsWord((byte)c)))
                return (words.GetValueOrDefault(key) ?? []).Select(m => new StoryMention(paths[m.File], m.Count));
            return Search(Encoding.Latin1.GetBytes(key));
        }

        /// <summary>The files that hold the bytes with no word byte just before or after them.</summary>
        private List<StoryMention> Search(byte[] needle)
        {
            var result = new List<StoryMention>();
            for (var file = 0; file < lower.Count; file++)
            {
                if (Count(lower[file], needle) is > 0 and var count)
                    result.Add(new StoryMention(paths[file], count));
            }
            return result;
        }

        private static int Count(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> needle)
        {
            var count = 0;
            var from = 0;
            while (from < bytes.Length)
            {
                var at = bytes[from..].IndexOf(needle);
                if (at < 0)
                    break;
                at += from;
                var end = at + needle.Length;
                if ((at == 0 || !IsWord(bytes[at - 1])) && (end >= bytes.Length || !IsWord(bytes[end])))
                    count++;
                from = at + 1;
            }
            return count;
        }

        private static bool IsWord(byte b) => b is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_';
    }
}
