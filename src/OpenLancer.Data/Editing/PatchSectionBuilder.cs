using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>
/// Builds the lines of one section of a patch that <see cref="PatchWriter"/> writes.
/// </summary>
/// <remarks>
/// <para>Each line that the writer adds can take the place of a line of the loaded section (decisions D4 and
/// D25):</para>
/// <list type="bullet">
/// <item>The lines above the loaded line (comment, blank and <c>@</c> lines) come first.</item>
/// <item>The line gets the text of the loaded line when that text gives the same entry through the merge. So an
/// untouched line keeps its text and its inline comment. Otherwise the line gets new text.</item>
/// </list>
/// <para>A loaded line that no line takes leaves the patch with the lines above it, because an edit removed it. The
/// leading lines and the tail of the loaded section stay with the section. When the section gets each loaded line
/// again with its text, and no other line, it keeps the loaded order of the lines.</para>
/// <para>Each method adds one or more lines and returns null, or returns why it cannot.</para>
/// </remarks>
/// <param name="source">The loaded section, or null.</param>
/// <param name="binary">True when the target file is a binary (BINI) file.</param>
/// <param name="inB">True for a section of B. The merge finds it by its type and key without letter case, and keeps
/// the type name and the key line of B.</param>
/// <param name="keepText">False to give each line new text. The lines above the loaded lines still stay.</param>
internal sealed class PatchSectionBuilder(SourceSection? source, bool binary, bool inB, bool keepText)
{
    private readonly List<string> lines = [];
    private readonly bool[] used = new bool[source?.Lines.Count ?? 0];
    private readonly Dictionary<string, int> occurrence = new(StringComparer.OrdinalIgnoreCase);
    private bool headerLoaded;
    private bool allLoaded = true;

    /// <summary>True when the header line or an entry line has the text of the loaded section.</summary>
    public bool UsesLoadedText { get; private set; }

    /// <summary>
    /// The lines. When the section gave each loaded line with its text, these are all loaded lines in their order.
    /// Else they are the leading lines of the loaded section, the lines in the order added, and the tail.
    /// </summary>
    public IReadOnlyList<string> Lines =>
        source == null ? lines
        : headerLoaded && allLoaded && used.All(u => u) ? [.. source.AllLines]
        : [.. source.Leading, .. lines, .. source.Tail];

    /// <summary>
    /// Adds the header line. A new section gets the name of its header, so a loaded header of a new section must
    /// have the same letter case.
    /// </summary>
    public string? Header(string name)
    {
        var comparison = inB ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (keepText && source != null && source.Name.Equals(name, comparison))
        {
            headerLoaded = true;
            UsesLoadedText = true;
            lines.Add(source.Header);
            return null;
        }
        return PatchFormatter.TryFormatHeader(name, out var line, out var error) ? Formatted(line) : error;
    }

    /// <summary>Adds the key line of a section of B. The merge finds the section by the key without letter case.</summary>
    public string? Key(Entry key)
    {
        var index = FirstUnused(p => p.Count == 1 && p.Name.Equals(key.Name, StringComparison.OrdinalIgnoreCase) &&
                                     p[0].ToString().Equals(key[0].ToString(), StringComparison.OrdinalIgnoreCase));
        if (Place(index, fits: true))
            return null;
        return PatchFormatter.TryFormatLine(key, key, binary, out var line, out var error) ? Formatted(line) : error;
    }

    /// <summary>Adds a directive line. The merge compares its name and texts without letter case.</summary>
    public string? Directive(string name, IReadOnlyList<string> texts)
    {
        var index = FirstUnused(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Count == texts.Count &&
                                     p.Select((value, i) => value.ToString().Equals(texts[i], StringComparison.OrdinalIgnoreCase))
                                         .All(same => same));
        if (Place(index, fits: true))
            return null;
        return PatchFormatter.TryFormatDirective(name, texts, out var line, out var error) ? Formatted(line) : error;
    }

    /// <summary>
    /// Adds the <c>ol.base</c> line of a hash (see <see cref="SectionHash"/>). The text parser reads a hash of decimal
    /// digits, or a hash with the form of a float, as a number. So the line keeps the text of the hash, and the merge
    /// compares the hash through the parser (see <see cref="SectionHash.Matches"/>).
    /// </summary>
    public string? Base(string hash)
    {
        var index = FirstUnused(p => p.Name.Equals(IniPatcher.DirectiveBase, StringComparison.OrdinalIgnoreCase) &&
                                     p.Count == 1 && SectionHash.TextOf(p[0]).Equals(hash, StringComparison.OrdinalIgnoreCase));
        return Place(index, fits: true) ? null : Formatted($"{IniPatcher.DirectiveBase} = {hash}");
    }

    /// <summary>
    /// Adds the line of an entry. The n-th line of a name takes the place of the n-th loaded line of that name in
    /// the last loaded section that has the name, because the merge replaces the lines of a name in an earlier loaded
    /// section with the lines of a later one. Names compare without letter case, as the merge compares them.
    /// </summary>
    /// <param name="replaced">The target line that the line replaces, or null for a new line.</param>
    public string? Entry(Entry entry, Entry? replaced)
    {
        var n = occurrence.GetValueOrDefault(entry.Name);
        occurrence[entry.Name] = n + 1;
        var index = LoadedLineOf(entry.Name, n);
        if (Place(index, index >= 0 && PatchFormatter.ReadsBack(source!.Lines[index].Parsed, entry, replaced, binary)))
            return null;
        return PatchFormatter.TryFormatLine(entry, replaced, binary, out var line, out var error) ? Formatted(line) : error;
    }

    /// <summary>
    /// Adds a line that <c>ol.append</c> adds to an item list (see <see cref="SectionKeys.ItemListsOf"/>). The line takes
    /// the place of the first unused loaded line with the same name and item, also of a loaded list with all its lines.
    /// So the line keeps its text and the comment lines above it.
    /// </summary>
    /// <param name="replaced">The target line that gives the value types of the line, or null.</param>
    /// <param name="sameItem">True for a loaded line with the item of the line.</param>
    public string? Item(Entry entry, Entry? replaced, Func<Entry, bool> sameItem)
    {
        var index = FirstUnused(p => p.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) && sameItem(p));
        if (Place(index, index >= 0 && PatchFormatter.ReadsBack(source!.Lines[index].Parsed, entry, replaced, binary)))
            return null;
        return PatchFormatter.TryFormatLine(entry, replaced, binary, out var line, out var error) ? Formatted(line) : error;
    }

    /// <summary>
    /// Adds the <c>ol.remove_line</c> line of an item list. The line takes the place of the first unused loaded
    /// <c>ol.remove_line</c> line with the same name and item, so it keeps its text. A new line holds the item text. The
    /// merge must read the same name and item back from it. The text parser reads a large number as a float, so the
    /// check compares the item, not the text of the value.
    /// </summary>
    /// <param name="sameItem">True for the values of a loaded line after its name that give the item.</param>
    public string? RemoveLine(string name, string item, Func<IEnumerable<ValueBase>, bool> sameItem)
    {
        var index = FirstUnused(p => p.Name.Equals(IniPatcher.DirectiveRemoveLine, StringComparison.OrdinalIgnoreCase) &&
                                     p.Count >= 2 && p[0].ToString().Trim().Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                     sameItem(p.Skip(1)));
        if (Place(index, fits: true))
            return null;
        var line = $"{IniPatcher.DirectiveRemoveLine} = {name}, {item}";
        return PatchFormatter.NameError(name) == null && IniDocument.ParseTextLine(line) is { Count: 2 } parsed &&
               parsed[0].ToString().Equals(name, StringComparison.OrdinalIgnoreCase) && sameItem(parsed.Skip(1))
            ? Formatted(line)
            : $"'{line}': the merge does not read back the same name and item.";
    }

    /// <summary>
    /// Adds <c>ol.remove</c> lines for the names. A loaded <c>ol.remove</c> line stays when each name that it holds is
    /// in the list. The other names go into one new line.
    /// </summary>
    public string? Remove(IReadOnlyList<string> names)
    {
        var left = names.ToList();
        bool Fits(Entry line) => line.Name.Equals(IniPatcher.DirectiveRemove, StringComparison.OrdinalIgnoreCase) &&
                                 line.Count > 0 && line.All(v => left.Contains(v.ToString(), StringComparer.OrdinalIgnoreCase));
        int index;
        while ((index = FirstUnused(Fits)) >= 0)
        {
            var loaded = source!.Lines[index].Parsed;
            var removed = left.Where(n => loaded.Any(v => v.ToString().Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
            left.RemoveAll(removed.Contains);
            if (!Place(index, fits: true) && RemoveLine(removed) is { } error)
                return error;
        }
        if (left.Count == 0)
            return null;
        return left.Select(PatchFormatter.NameError).FirstOrDefault(e => e != null) ?? RemoveLine(left);

        string? RemoveLine(List<string> removed) =>
            PatchFormatter.TryFormatDirective(IniPatcher.DirectiveRemove, removed, out var line, out var error)
                ? Formatted(line)
                : error;
    }

    /// <summary>
    /// Puts the line that the caller adds at the loaded line at <paramref name="index"/>: adds the lines above the
    /// loaded line, and its text when <paramref name="fits"/> is true. Does nothing for an index of -1.
    /// </summary>
    /// <returns>True when the loaded text is added. The caller then adds no text.</returns>
    private bool Place(int index, bool fits)
    {
        if (index < 0)
            return false;
        used[index] = true;
        lines.AddRange(source!.Lines[index].Above);
        if (!keepText || !fits)
            return false;
        lines.Add(source.Lines[index].Text);
        UsesLoadedText = true;
        return true;
    }

    private string? Formatted(string line)
    {
        lines.Add(line);
        allLoaded = false;
        return null;
    }

    /// <summary>The index of the first unused loaded line that fits, or -1.</summary>
    private int FirstUnused(Func<Entry, bool> fits)
    {
        for (var i = 0; i < used.Length; i++)
        {
            if (!used[i] && fits(source!.Lines[i].Parsed))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// The index of the n-th loaded line of a name in the last loaded section that has the name, or -1 when there is
    /// no such line or a line already took it.
    /// </summary>
    private int LoadedLineOf(string name, int n)
    {
        var candidates = Enumerable.Range(0, used.Length)
            .Where(i => source!.Lines[i].Parsed.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return -1;
        var part = candidates.Max(i => source!.Lines[i].Part);
        var index = candidates.Where(i => source!.Lines[i].Part == part).Skip(n).DefaultIfEmpty(-1).First();
        return index >= 0 && !used[index] ? index : -1;
    }
}
