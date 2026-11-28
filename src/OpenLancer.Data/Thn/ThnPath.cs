using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace OpenLancer.Data.Thn;

/// <summary>
/// The address of a value in an element: record keys joined with <c>.</c>, for example <c>cameraprops.fovh</c>. An
/// event has four named slots for the items of its list: <c>time</c>, <c>type</c>, <c>targets</c> and <c>props</c>,
/// for example <c>props.duration</c>.
/// </summary>
/// <remarks>
/// <para>A path ends at a value or at a table with a list part, because a list is one value. A record is a container.
/// An empty table counts as a record, so a path can go into an empty <c>psysprops</c>. No retail key has a
/// <c>.</c>.</para>
/// <para>The engine reads a key from the record part of a table, whatever the list part of the table holds, for
/// example the speaker of <c>userprops = { 1; speaker = "Trent" }</c>. <see cref="EngineValueIn"/> reads such a key.
/// An edit of such a key sets the whole table (see <see cref="ThnEdits.SetEngineValue"/>).</para>
/// </remarks>
public readonly record struct ThnPath
{
    public const char Separator = '.';

    /// <exception cref="ArgumentException">The text is empty, or a key of it is empty.</exception>
    public ThnPath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Problem(text) is { } problem)
            throw new ArgumentException(problem, nameof(text));
        Text = text;
    }

    public string Text { get; }

    /// <summary>The keys of the path, from the element down.</summary>
    internal string[] Keys => Text.Split(Separator);

    public override string ToString() => Text;

    /// <summary>The value at this path in an element, or null when the element has no value there.</summary>
    public ThnValue? ValueIn(ThnElementKind kind, ThnTable element) => ThnElementPaths.Get(kind, element, this);

    /// <summary>The value at this path as the engine reads it, or null when the engine reads no value there. Unlike
    /// <see cref="ValueIn"/>, the path also goes into a table with a list part.</summary>
    public ThnValue? EngineValueIn(ThnElementKind kind, ThnTable element) => ThnElementPaths.EngineGet(kind, element, this);

    /// <summary>The path of the record that holds the value, or null for a path of one key.</summary>
    internal ThnPath? Parent => Text.LastIndexOf(Separator) is var at and > 0 ? new ThnPath(Text[..at]) : null;

    /// <summary>True when the path is <paramref name="container"/>, or a path in the value of it.</summary>
    internal bool IsWithin(ThnPath container) =>
        Text.StartsWith(container.Text, StringComparison.Ordinal) &&
        (Text.Length == container.Text.Length || Text[container.Text.Length] == Separator);

    /// <summary>Why a text is not a path, or null.</summary>
    internal static string? Problem(string text) => text.Split(Separator).Any(k => k.Length == 0)
        ? $"'{text}' is not a path. A path is one or more keys joined with '.', and no key is empty."
        : null;
}

/// <summary>A value on a path cannot take the change, for example a set into a value that is not a record.</summary>
internal sealed class ThnPathException(string message) : Exception(message);

/// <summary>
/// Reads and changes the values of an element by path, and finds the paths at which two elements differ.
/// </summary>
/// <remarks>
/// <para>A change of an existing key keeps the place of the key in its record. A new key goes at the end of its record.
/// A set makes each missing record on its path. The other values of the element stay the same objects.</para>
/// <para>The engine reads each key from the record part of its table, whatever the list part holds. The engine
/// functions (<see cref="EngineGet"/>, <see cref="EngineSet"/> and <see cref="EngineRemoveWithMadeRecords"/>) go
/// through such a table and keep its list part. The other functions follow the paths of a patch, which end at such a
/// table. So the patch gives a change of an engine function as a set of a whole table (see
/// <see cref="PatchPath"/>).</para>
/// </remarks>
internal static class ThnElementPaths
{
    /// <summary>The names of the items of an event list, in order (ThnEvent.cs:43-65).</summary>
    public static readonly ImmutableArray<string> EventSlots = ["time", "type", "targets", "props"];

    /// <summary>The index of the props table in an event list. It is the only event slot that is a container.</summary>
    public const int PropsSlot = 3;

    /// <summary>Why a path does not address a value of an element of this kind, or null.</summary>
    /// <param name="remove">True for a path to remove. An event always keeps its time, type and targets.</param>
    public static string? Problem(ThnElementKind kind, ThnPath path, bool remove)
    {
        if (kind == ThnElementKind.Entity)
            return null;
        var keys = path.Keys;
        var slot = EventSlots.IndexOf(keys[0]);
        if (slot < 0)
            return $"'{path}' is not a path of an event. An event path starts with {string.Join(", ", EventSlots)}.";
        if (slot != PropsSlot && keys.Length > 1)
            return $"'{path}' is not a path of an event, because the {keys[0]} of an event is a value, not a record.";
        if (slot != PropsSlot && remove)
            return $"An event always has its time, type and targets, so '{path}' cannot be removed.";
        return null;
    }

    /// <summary>The value at a path, or null when the element has no value there.</summary>
    public static ThnValue? Get(ThnElementKind kind, ThnTable element, ThnPath path) =>
        Get(kind, element, path, throughLists: false);

    /// <summary>
    /// The value at a path as the engine reads it, or null when the engine reads no value there. Unlike
    /// <see cref="Get"/>, the path also goes into a table with a list part, for example
    /// <c>userprops = { 1; speaker = "Trent" }</c>. Keys compare with letter case.
    /// </summary>
    public static ThnValue? EngineGet(ThnElementKind kind, ThnTable element, ThnPath path) =>
        Get(kind, element, path, throughLists: true);

    /// <summary>Sets the value at a path. A missing record on the path is made.</summary>
    /// <returns>The changed element.</returns>
    /// <exception cref="ThnPathException">A value on the path is not a record, or an event has too few items for the
    /// slot.</exception>
    public static ThnTable Set(ThnElementKind kind, ThnTable element, ThnPath path, ThnValue value) =>
        Set(kind, element, path, value, throughLists: false);

    /// <summary>
    /// Sets the value at a path where the engine reads it (see <see cref="EngineGet"/>). A table with a list part on
    /// the path keeps its list part. A missing record on the path is made.
    /// </summary>
    /// <returns>The changed element.</returns>
    /// <exception cref="ThnPathException">A value on the path is not a table, or an event has too few items for the
    /// slot.</exception>
    public static ThnTable EngineSet(ThnElementKind kind, ThnTable element, ThnPath path, ThnValue value) =>
        Set(kind, element, path, value, throughLists: true);

    /// <summary>Removes the value at a path.</summary>
    /// <returns>The changed element, or null when the element has no value at the path.</returns>
    /// <exception cref="ThnPathException">The path removes the props of an event that has items after its props.</exception>
    public static ThnTable? Remove(ThnElementKind kind, ThnTable element, ThnPath path) =>
        Remove(kind, element, path, throughLists: false);

    /// <summary>
    /// Removes the value at a path, then each record on the path that is now empty and that <paramref name="lower"/>
    /// does not have. So the value goes together with the records that a <see cref="Set"/> made for it, and the props
    /// of an event go when the lower event has none.
    /// </summary>
    /// <param name="lower">The lower element.</param>
    /// <returns>The changed element, or the element when it has no value at the path.</returns>
    /// <exception cref="ThnPathException">See <see cref="Remove"/>.</exception>
    public static ThnTable RemoveWithMadeRecords(ThnElementKind kind, ThnTable element, ThnPath path, ThnTable lower) =>
        RemoveWithMadeRecords(kind, element, path, lower, throughLists: false);

    /// <summary>
    /// <see cref="RemoveWithMadeRecords"/> where the engine reads the value (see <see cref="EngineGet"/>). A table with
    /// a list part on the path keeps its list part, and it never counts as an empty record.
    /// </summary>
    /// <exception cref="ThnPathException">See <see cref="Remove"/>.</exception>
    public static ThnTable EngineRemoveWithMadeRecords(ThnElementKind kind, ThnTable element, ThnPath path,
        ThnTable lower) => RemoveWithMadeRecords(kind, element, path, lower, throughLists: true);

    /// <summary>
    /// The path that a patch changes for a change at a path: the path, or the path of the outermost table with a list
    /// part that holds the path. A patch path ends at such a table (MOD_FORMAT.md, "The THN patch"). So the patch
    /// gives a change in the table as a set of the whole table, and an edit removes the pins of the table.
    /// </summary>
    public static ThnPath PatchPath(ThnElementKind kind, ThnTable element, ThnPath path)
    {
        var keys = path.Keys;
        for (var depth = 1; depth < keys.Length; depth++)
        {
            var holder = new ThnPath(string.Join(ThnPath.Separator, keys[..depth]));
            switch (Get(kind, element, holder))
            {
                case ThnTable { IsRecord: false }:
                    return holder;
                case not ThnTable:
                    return path;
            }
        }
        return path;
    }

    private static ThnValue? Get(ThnElementKind kind, ThnTable element, ThnPath path, bool throughLists)
    {
        var keys = path.Keys;
        if (kind == ThnElementKind.Entity)
            return GetIn(element, keys, 0, throughLists);
        var slot = EventSlots.IndexOf(keys[0]);
        if (slot < 0 || slot >= element.List.Length)
            return null;
        if (keys.Length == 1)
            return element.List[slot];
        return slot == PropsSlot && Container(element.List[slot], throughLists) is { } props
            ? GetIn(props, keys, 1, throughLists)
            : null;
    }

    private static ThnTable Set(ThnElementKind kind, ThnTable element, ThnPath path, ThnValue value, bool throughLists)
    {
        CheckPath(kind, path, remove: false);
        var keys = path.Keys;
        if (kind == ThnElementKind.Entity)
            return SetIn(element, keys, 0, value, path, throughLists);
        var slot = EventSlots.IndexOf(keys[0]);
        if (keys.Length == 1)
            return SetItem(element, slot, value);
        var props = element.List.Length > PropsSlot ? element.List[PropsSlot] : ThnTable.Empty;
        if (Container(props, throughLists) is not { } record)
            throw new ThnPathException(NotAContainer(path, "props", throughLists));
        return SetItem(element, PropsSlot, SetIn(record, keys, 1, value, path, throughLists));
    }

    private static ThnTable? Remove(ThnElementKind kind, ThnTable element, ThnPath path, bool throughLists)
    {
        CheckPath(kind, path, remove: true);
        var keys = path.Keys;
        if (kind == ThnElementKind.Entity)
            return RemoveIn(element, keys, 0, throughLists);
        if (element.List.Length <= PropsSlot)
            return null;
        if (keys.Length == 1)
        {
            if (element.List.Length > PropsSlot + 1)
                throw new ThnPathException("The event has items after its props, so its props cannot be removed.");
            return new ThnTable(element.List.RemoveAt(PropsSlot), element.Record);
        }
        if (Container(element.List[PropsSlot], throughLists) is not { } props ||
            RemoveIn(props, keys, 1, throughLists) is not { } changed)
            return null;
        return SetItem(element, PropsSlot, changed);
    }

    private static ThnTable RemoveWithMadeRecords(ThnElementKind kind, ThnTable element, ThnPath path, ThnTable lower,
        bool throughLists)
    {
        var result = Remove(kind, element, path, throughLists) ?? element;
        for (var parent = path.Parent; parent is { } holder; parent = holder.Parent)
        {
            if (Get(kind, lower, holder, throughLists) != null ||
                Get(kind, result, holder, throughLists) is not ThnTable { IsRecord: true, Record.IsEmpty: true })
                break;
            result = Remove(kind, result, holder, throughLists) ?? result;
        }
        return result;
    }

    /// <summary>
    /// The paths of the other values of the records from <paramref name="container"/> down to <paramref name="path"/>:
    /// each key of each such record, without the key on the way to the path. With the path, they name each value of
    /// the container. A pin of the container splits into them when the path loses its pin (see
    /// <see cref="ThnPin"/>).
    /// </summary>
    /// <param name="path">A path in the value of <paramref name="container"/>, not the container itself.</param>
    /// <exception cref="ArgumentException">A key is empty or has a <c>.</c>, so a path cannot name it.</exception>
    public static IReadOnlyList<ThnPath> Beside(ThnElementKind kind, ThnTable element, ThnPath container, ThnPath path)
    {
        var keys = path.Keys;
        var result = new List<ThnPath>();
        var at = container;
        for (var depth = container.Keys.Length; depth < keys.Length; depth++)
        {
            if (Get(kind, element, at) is not ThnTable { IsRecord: true } record)
                break;
            result.AddRange(record.Record.Where(f => !string.Equals(f.Key, keys[depth], StringComparison.Ordinal))
                .Select(f => Join(at.Text, f.Key)));
            at = Join(at.Text, keys[depth]);
        }
        return result;
    }

    /// <summary>
    /// Adds the sets and the removes that change <paramref name="lower"/> into <paramref name="working"/>. The removes
    /// run first and the sets after them, in order. Then the result equals <paramref name="working"/> exactly, also in
    /// the order of the keys.
    /// </summary>
    /// <remarks>A key of the lower record that is out of order in the working record gets a remove and a set, so that
    /// the set puts it at the end. A changed value that is a record in both elements gives the paths in it.</remarks>
    /// <exception cref="ArgumentException">A path cannot give the change: the list part of an entity, the items after
    /// the props of an event or the record part of an event changes, the time, the type or the targets of an event go
    /// away, or a changed key is empty or has a <c>.</c>.</exception>
    public static void Diff(ThnElementKind kind, ThnTable lower, ThnTable working, List<(ThnPath Path, ThnValue Value)> sets,
        List<ThnPath> removes)
    {
        if (kind == ThnElementKind.Entity)
        {
            if (!lower.List.SequenceEqual(working.List))
                throw new ArgumentException("The list part of the entity changes. A patch can change only its record.");
            DiffRecord(lower, working, "", sets, removes);
            return;
        }
        if (!lower.Record.SequenceEqual(working.Record) ||
            !lower.List.Skip(PropsSlot + 1).SequenceEqual(working.List.Skip(PropsSlot + 1)))
            throw new ArgumentException("The event changes outside its time, type, targets and props. A patch cannot give that change.");
        for (var slot = 0; slot < PropsSlot; slot++)
        {
            var b = Item(lower, slot);
            var w = Item(working, slot);
            if (Equals(b, w))
                continue;
            if (w == null)
                throw new ArgumentException($"The event loses its {EventSlots[slot]}. An event always has its time, type and targets.");
            sets.Add((new ThnPath(EventSlots[slot]), w));
        }
        var lowerProps = Item(lower, PropsSlot);
        var workingProps = Item(working, PropsSlot);
        var propsPath = EventSlots[PropsSlot];
        if (Equals(lowerProps, workingProps))
            return;
        if (workingProps == null)
            removes.Add(new ThnPath(propsPath));
        else if (lowerProps is ThnTable { IsRecord: true } b && workingProps is ThnTable { IsRecord: true } w)
            DiffRecord(b, w, propsPath, sets, removes);
        else
            sets.Add((new ThnPath(propsPath), workingProps));
    }

    private static void DiffRecord(ThnTable lower, ThnTable working, string prefix,
        List<(ThnPath Path, ThnValue Value)> sets, List<ThnPath> removes)
    {
        // The kept keys: the longest start of the working record whose keys the lower record has, in its order. The
        // other working keys must go at the end, after the kept keys.
        var kept = 0;
        for (var last = -1; kept < working.Record.Length; kept++)
        {
            var index = IndexOf(lower, working.Record[kept].Key);
            if (index <= last)
                break;
            last = index;
        }
        var keptKeys = working.Record.Take(kept).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, b) in lower.Record)
        {
            if (!keptKeys.Contains(key))
            {
                removes.Add(Join(prefix, key));
                continue;
            }
            var w = working[key]!;
            if (b.Equals(w))
                continue;
            if (b is ThnTable { IsRecord: true } lowerRecord && w is ThnTable { IsRecord: true } workingRecord)
                DiffRecord(lowerRecord, workingRecord, Join(prefix, key).Text, sets, removes);
            else
                sets.Add((Join(prefix, key), w));
        }
        for (var i = kept; i < working.Record.Length; i++)
            sets.Add((Join(prefix, working.Record[i].Key), working.Record[i].Value));
    }

    private static ThnPath Join(string prefix, string key)
    {
        if (key.Length == 0 || key.Contains(ThnPath.Separator))
            throw new ArgumentException($"The key '{key}' is empty or has a '.', so a patch cannot name it.");
        return new ThnPath(prefix.Length == 0 ? key : prefix + ThnPath.Separator + key);
    }

    private static void CheckPath(ThnElementKind kind, ThnPath path, bool remove)
    {
        if (Problem(kind, path, remove) is { } problem)
            throw new ThnPathException(problem);
    }

    private static ThnValue? Item(ThnTable list, int index) => index < list.List.Length ? list.List[index] : null;

    /// <summary>The table that a path can go into: a record, or with <paramref name="throughLists"/> each table. Else
    /// null.</summary>
    private static ThnTable? Container(ThnValue? value, bool throughLists) =>
        value is ThnTable table && (throughLists || table.IsRecord) ? table : null;

    /// <summary>The refusal of a path that goes through a value that <see cref="Container"/> does not give.</summary>
    private static string NotAContainer(ThnPath path, string holder, bool throughLists) =>
        $"'{path}' goes through '{holder}', which is not a {(throughLists ? "table" : "record")}.";

    private static ThnValue? GetIn(ThnTable record, string[] keys, int index, bool throughLists)
    {
        var value = record[keys[index]];
        if (index == keys.Length - 1 || value == null)
            return value;
        return Container(value, throughLists) is { } child ? GetIn(child, keys, index + 1, throughLists) : null;
    }

    private static ThnTable SetIn(ThnTable record, string[] keys, int index, ThnValue value, ThnPath path,
        bool throughLists)
    {
        var at = IndexOf(record, keys[index]);
        if (index < keys.Length - 1)
        {
            var child = at >= 0 ? record.Record[at].Value : ThnTable.Empty;
            if (Container(child, throughLists) is not { } childRecord)
                throw new ThnPathException(
                    NotAContainer(path, string.Join(ThnPath.Separator, keys.Take(index + 1)), throughLists));
            value = SetIn(childRecord, keys, index + 1, value, path, throughLists);
        }
        var field = new ThnField(keys[index], value);
        return new ThnTable(record.List, at >= 0 ? record.Record.SetItem(at, field) : record.Record.Add(field));
    }

    private static ThnTable? RemoveIn(ThnTable record, string[] keys, int index, bool throughLists)
    {
        var at = IndexOf(record, keys[index]);
        if (at < 0)
            return null;
        if (index == keys.Length - 1)
            return new ThnTable(record.List, record.Record.RemoveAt(at));
        if (Container(record.Record[at].Value, throughLists) is not { } child ||
            RemoveIn(child, keys, index + 1, throughLists) is not { } changed)
            return null;
        return new ThnTable(record.List, record.Record.SetItem(at, new ThnField(keys[index], changed)));
    }

    /// <summary>Sets an item of an event list. The item after the last one is added.</summary>
    private static ThnTable SetItem(ThnTable element, int slot, ThnValue value)
    {
        if (slot < element.List.Length)
            return new ThnTable(element.List.SetItem(slot, value), element.Record);
        if (slot == element.List.Length)
            return new ThnTable(element.List.Add(value), element.Record);
        throw new ThnPathException(
            $"The event has {element.List.Length} items, so it has no {EventSlots[slot - 1]} before its {EventSlots[slot]}.");
    }

    private static int IndexOf(ThnTable record, string key)
    {
        for (var i = 0; i < record.Record.Length; i++)
        {
            if (string.Equals(record.Record[i].Key, key, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }
}
