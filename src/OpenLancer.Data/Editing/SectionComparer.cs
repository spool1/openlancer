using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>
/// Compares the lines of sections as the engine reads them. Values compare engine-equal (see
/// <see cref="IniValueComparer.EngineEqual"/>), and entry names compare with letter case.
/// </summary>
internal static class SectionComparer
{
    /// <summary>
    /// True when the engine reads the same data from both sections. They have the same name and, in each unit, the
    /// same lines in the same order.
    /// </summary>
    /// <remarks>
    /// A unit is the lines of one name, or the lines of all names of one linked group (see
    /// <see cref="SectionKeys.LinkedGroups"/>), because the engine attaches each line of a group to the line before
    /// it. The order of the units does not count. For a type whose line order counts in the file (see
    /// <see cref="SectionKeys.IsOrderSensitive(string, string)"/>), the whole section is one unit. Each item list (see
    /// <see cref="SectionKeys.ItemListsOf"/>) compares by its items: the engine reads them as a set.
    /// </remarks>
    /// <param name="path">The install path of the file of the sections.</param>
    public static bool Equivalent(Section a, Section b, bool binary, string path)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Name != b.Name)
            return false;
        var items = SectionKeys.ItemListsOf(a.Name);
        if (items != null && !SameItems(a, b, items, binary))
            return false;
        var linesA = a.Where(e => items?.Has(e.Name) != true).ToList();
        var linesB = b.Where(e => items?.Has(e.Name) != true).ToList();
        if (linesA.Count != linesB.Count)
            return false;
        if (SectionKeys.IsOrderSensitive(a.Name, path))
            return SameLines(linesA, linesB, binary);
        var unitsA = Lines(a.Name, linesA, Unit);
        var unitsB = Lines(b.Name, linesB, Unit);
        return unitsA.Count == unitsB.Count &&
               unitsA.All(u => unitsB.TryGetValue(u.Key, out var lines) && SameLines(u.Value, lines, binary));
    }

    /// <summary>
    /// True when each item list of two sections has the same items (see <see cref="ItemLists"/>). The order of the
    /// lines and a repeated item do not count. The lines of a list without an item compare in order.
    /// </summary>
    private static bool SameItems(Section a, Section b, ItemLists items, bool binary)
    {
        foreach (var name in items.Keys)
        {
            var linesA = a.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var linesB = b.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!linesA.Select(items.ItemOf).OfType<string>().ToHashSet()
                    .SetEquals(linesB.Select(items.ItemOf).OfType<string>()) ||
                !SameLines(linesA.Where(e => items.ItemOf(e) == null).ToList(),
                    linesB.Where(e => items.ItemOf(e) == null).ToList(), binary))
                return false;
        }
        return true;
    }

    /// <summary>The lines of a section by name, in file order. Names compare without letter case.</summary>
    public static Dictionary<string, List<Entry>> ByName(Section section) => Lines(section.Name, section, (_, name) => name);

    /// <summary>True when both lists hold the same lines in the same order.</summary>
    public static bool SameLines(IReadOnlyList<Entry> a, IReadOnlyList<Entry> b, bool binary)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!SameLine(a[i], b[i], binary))
                return false;
        }
        return true;
    }

    private static bool SameLine(Entry a, Entry b, bool binary)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a.Name != b.Name || a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i]) && !IniValueComparer.EngineEqual(a[i], b[i], binary))
                return false;
        }
        return true;
    }

    /// <summary>The unit of a name: the first name of its linked group, or the name itself.</summary>
    private static string Unit(string sectionName, string entryName) =>
        SectionKeys.LinkedGroups(sectionName)
            .FirstOrDefault(g => g.Contains(entryName, StringComparer.OrdinalIgnoreCase))?[0] ?? entryName;

    private static Dictionary<string, List<Entry>> Lines(string sectionName, IEnumerable<Entry> entries,
        Func<string, string, string> keyOf)
    {
        var lines = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = keyOf(sectionName, entry.Name);
            if (!lines.TryGetValue(key, out var list))
                lines[key] = list = [];
            list.Add(entry);
        }
        return lines;
    }
}
