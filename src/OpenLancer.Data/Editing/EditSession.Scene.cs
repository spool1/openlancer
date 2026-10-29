using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private Nicknames? nicknames;
    private StoryMentions? storyMentions;

    /// <summary>The nicknames of the objects and zones of all system files (decisions D240 and D265 of the M3
    /// design).</summary>
    internal Nicknames Nicknames => nicknames ??= new Nicknames(this);

    /// <summary>The text scan for nicknames in the files that the reverse index does not read (decision D261).</summary>
    internal StoryMentions Mentions => storyMentions ??= new StoryMentions(this);

    /// <summary>Starts the build of the index of the story mentions in a worker task, when it does not run or exist. The
    /// editor calls it when a system opens, so that the first lookup does not wait.</summary>
    public void StartStoryMentions() => Mentions.Start();

    /// <summary>True when the index of the story mentions of the edit view is complete, so a lookup does not
    /// wait.</summary>
    public bool StoryMentionsReady => Mentions.IsReady;

    /// <summary>
    /// The files that name a nickname as text, outside the files that the reverse index reads: the mission files,
    /// <c>KnowledgeMap.ini</c>, the cutscenes and the other INI files (decision D261). A mention is a warning only. Waits
    /// for the index when its build runs.
    /// </summary>
    public IReadOnlyList<StoryMention> StoryMentions(string nickname) => Mentions.Of(nickname);

    /// <summary>
    /// The later mods that change a section in any way: its lines, an add or a delete of it, or a whole file. Their
    /// patches cannot find the section after a delete or a rename (review finding R22 of the M3 design).
    /// </summary>
    /// <exception cref="System.ArgumentException">The edit view has no such section.</exception>
    public IReadOnlyList<string> LaterModsThatChangeSection(SectionRef target)
    {
        if (LaterMods.Count == 0)
            return [];
        var (document, index) = Find(target);
        var id = document.BaseIdOf(document.IdentityAt(index)) ?? document.IdentityAt(index);
        var later = LaterMods.Select(m => m.Id).ToHashSet();
        return fullView.Value.Ledger.ModsThatChangeSection(document.Path, id).Where(later.Contains).ToList();
    }

    /// <summary>The section of W of a reference, or null. Do not change it: a command builds new sections.</summary>
    internal LibreLancer.Data.Ini.Section? WorkingSection(SectionRef target) =>
        TryFind(target, out var document, out var index) ? document.Working[index] : null;

    /// <summary>The number of child sections that follow a section in W, or 0.</summary>
    internal int ChildCount(SectionRef target) =>
        TryFind(target, out var document, out var index) ? SectionKeys.ChildCount(document.Working, index) : 0;

    /// <summary>True when the lower layer has the section of W, also under another key.</summary>
    internal bool InBase(SectionRef target) =>
        TryFind(target, out var document, out var index) && document.InBase(document.IdentityAt(index));

    /// <summary>True when a section of a type in the lower layer of a file has the key, without letter case. A rename
    /// cannot give such a key to another section (decision D231).</summary>
    internal bool BaseHasKey(string path, string type, string key) =>
        OpenDocument(path) is { } document &&
        document.Base.Any(s => s.Name.Equals(type, System.StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(SectionIdentity.KeyOf(s), key, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The nickname of the system whose file is the install path, or null.</summary>
    public string? SystemNicknameOf(string path) => Systems.NicknameOf(path);

    /// <summary>True when the section of W has another key in the lower layer: the active mod renames it.</summary>
    internal bool IsRenamed(SectionRef target) =>
        TryFind(target, out var document, out var index) && document.IsRenamed(document.IdentityAt(index));

    /// <summary>True when the file of the install path is a binary (BINI) file, or false for a text file or for a file
    /// that the edit view does not have.</summary>
    internal bool IsBinaryFile(string path) => OpenDocument(path)?.IsBinary == true;

    /// <summary>The key line name of a section type, for example <c>nickname</c>, or null for a type without a
    /// key.</summary>
    internal static string? KeyNameOf(string type) =>
        SectionKeys.For(type) is { Kind: SectionMatchKind.Keyed } match ? match.KeyEntry : null;
}
