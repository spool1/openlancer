using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Ini;

/// <summary>
/// The item lists of a section type (see <see cref="SectionKeys.ItemListsOf"/>). Each line of an item list gives one
/// item. The engine reads the items of a list as a set: the order of the lines has no effect, and two lines with the
/// same item give one item. So a patch can add lines with <c>ol.append</c> and remove one line with
/// <c>ol.remove_line</c>, and the other lines of the lower layers stay (issue #94). A mod for two games then keeps the
/// lines of each game.
/// </summary>
/// <param name="Keys">The names of the lines of the lists.</param>
/// <param name="ItemOf">Gives the item of the values of a line as text, or null when the values give no item. Two lines
/// with the same name and the same item text give the same item.</param>
/// <param name="ItemText">Tells what an item is, for the messages of the merge.</param>
internal sealed record ItemLists(IReadOnlyList<string> Keys, Func<IEnumerable<ValueBase>, string?> ItemOf, string ItemText)
{
    /// <summary>True when the lines of a name belong to an item list. Names compare without letter case.</summary>
    public bool Has(string name) => Keys.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The item of a hash list: the first value of the line as a 32-bit hash, in unsigned decimal form. The
    /// signed form and the unsigned form of a hash give the same item, for example -1368878011 and 2926089285.</summary>
    public static string? HashItem(IEnumerable<ValueBase> values) =>
        values.FirstOrDefault() is { } first && HashOf(first) is { } hash
            ? hash.ToString(CultureInfo.InvariantCulture)
            : null;

    /// <summary>The 32-bit hash that a value holds, or null when it holds no number. A text value holds the unsigned
    /// decimal form. A number value holds the hash in its 32 bits.</summary>
    public static uint? HashOf(ValueBase value)
    {
        if (value is StringValue)
        {
            return ulong.TryParse(value.ToString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var text) &&
                   text <= uint.MaxValue
                ? (uint)text
                : null;
        }
        return value.TryToInt32(out var number) ? unchecked((uint)number) : null;
    }
}
