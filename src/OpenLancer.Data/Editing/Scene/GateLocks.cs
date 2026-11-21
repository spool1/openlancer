using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>One value of a gate lock: value <paramref name="Index"/> of the line <paramref name="Line"/> (from 0) of
/// <paramref name="Key"/> in a <c>[locked_gates]</c> section.</summary>
internal sealed record GateLock(SectionRef Section, string Key, int Line, int Index);

/// <summary>The edit of the gate locks of <see cref="GateLocks.Set"/>: its commands and the sections that it changes,
/// or the reason why it cannot run.</summary>
/// <param name="Commands">The commands, or none when the locks are as the edit asks.</param>
/// <param name="Changes">The <c>[locked_gates]</c> sections that the commands change. A new section is not in the
/// list.</param>
/// <param name="Refusal">Why the edit cannot run, or null.</param>
internal sealed record LockEdit(IReadOnlyList<EditCommand> Commands, IReadOnlyList<SectionRef> Changes, string? Refusal);

/// <summary>
/// The gate locks of <c>InitialWorld.ini</c> (decision D257 of the M3 design). Each <c>locked_gate</c> and
/// <c>npc_locked_gate</c> line of <c>[locked_gates]</c> holds the FLHash of a gate or hole nickname as a decimal number.
/// Retail locks these gates for the player or for the NPCs at the start of a new game. Neither the reverse index of the
/// lists nor a text search finds such a line, so a rename of a gate changes it here.
/// </summary>
internal static class GateLocks
{
    private const string Section = "locked_gates";
    private static readonly string[] Keys = ["locked_gate", "npc_locked_gate"];

    /// <summary>The values of the gate locks of the files of the role <c>groups</c> that hold the FLHash of a
    /// nickname.</summary>
    public static IReadOnlyList<GateLock> Of(EditSession session, string nickname)
    {
        var hash = FLHash.CreateID(nickname);
        var result = new List<GateLock>();
        foreach (var path in session.DataFiles.Paths("groups"))
        {
            foreach (var section in session.Sections(path))
            {
                if (!section.Id.Type.Equals(Section, StringComparison.OrdinalIgnoreCase) || session.Get(section) is not { } view)
                    continue;
                foreach (var key in Keys)
                {
                    var lines = view.Lines(key);
                    for (var line = 0; line < lines.Count; line++)
                    {
                        if (lines[line].Values.Count > 0 && HashOf(lines[line].Values[0]) == hash)
                            result.Add(new GateLock(section, lines[line].Name, line, 0));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The edit that gives gates a player lock (<c>locked_gate</c>) and an NPC lock (<c>npc_locked_gate</c>), or
    /// removes them. A lock line of a gate goes into the first <c>[locked_gates]</c> section of the files of the role
    /// <c>groups</c>. Without such a section, it goes into a new section of the first such file. Each other line of the
    /// gates goes, also from another section. The other lines keep their place.
    /// </summary>
    /// <param name="gates">The nicknames of the gates and holes.</param>
    /// <param name="player">True to lock the gates for the player, false to remove that lock.</param>
    /// <param name="npc">True to lock the gates for the NPCs, false to remove that lock.</param>
    /// <param name="planned">Gets the sections after the edit, for the route tables of a plan.</param>
    public static LockEdit Set(EditSession session, IReadOnlyCollection<string> gates, bool player, bool npc,
        PlannedSections planned)
    {
        var hashes = gates.Select(FLHash.CreateID).Distinct().ToList();
        var sections = session.DataFiles.Paths(UniverseGraph.LockRole)
            .SelectMany(path => session.Sections(path).Where(s => s.Id.Type.Equals(Section, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var target = sections.FirstOrDefault();
        var commands = new List<EditCommand>();
        var changes = new List<SectionRef>();
        bool Wanted(string key) => key == Keys[0] ? player : npc;
        foreach (var section in sections)
        {
            if (session.Get(section) is not { } view || session.WorkingSection(section) is not { } working)
                continue;
            var after = working;
            foreach (var key in Keys)
            {
                var wanted = section.Equals(target) && Wanted(key);
                var kept = new HashSet<uint>();
                var lines = new List<IReadOnlyList<ValueBase>>();
                var changed = false;
                foreach (var line in view.Lines(key))
                {
                    if (line.Values.Count == 0 || HashOf(line.Values[0]) is not { } hash || !hashes.Contains(hash))
                        lines.Add(line.Values);
                    else if (wanted && kept.Add(hash))
                        lines.Add(line.Values);
                    else
                        changed = true;
                }
                if (wanted)
                {
                    foreach (var hash in hashes.Where(h => !kept.Contains(h)))
                    {
                        lines.Add([ValueOf(hash)]);
                        changed = true;
                    }
                }
                if (!changed)
                    continue;
                if (view.ReadOnlyReason is { } reason)
                    return new LockEdit([], [], $"{SceneSections.FileName(section.Path)}: {reason}");
                commands.Add(EditCommand.SetLines(section, key, lines));
                after = SectionEdits.WithLines(after, key, lines);
            }
            if (!ReferenceEquals(after, working))
            {
                planned.Replace(section, after);
                changes.Add(section);
            }
        }
        if (target == null && (player || npc))
        {
            if (session.DataFiles.Paths(UniverseGraph.LockRole).FirstOrDefault() is not { } path ||
                session.OpenDocument(path) is not { State.Exists: true } document)
                return new LockEdit([], [], "freelancer.ini names no file of the initial world, so the game has no gate " +
                                            "locks.");
            if (document.ReadOnlyReason is { } reason)
                return new LockEdit([], [], $"{SceneSections.FileName(path)}: {reason}");
            var entries = Keys.Where(Wanted).SelectMany(k => hashes.Select(h => new EntryView(k, [ValueOf(h)]))).ToList();
            commands.Add(EditCommand.AddSection(path, Section, entries));
            planned.Add(path, SectionEdits.Build(Section, entries));
        }
        return new LockEdit(commands, changes, null);
    }

    /// <summary>
    /// True when a <c>locked_gate</c> line of the files of the role <c>groups</c> names a nickname by its FLHash. With
    /// <paramref name="lower"/>, the call reads the lower layer: the base data with the mods that load before the
    /// active mod.
    /// </summary>
    public static bool PlayerLocked(EditSession session, string nickname, bool lower = false)
    {
        var hash = FLHash.CreateID(nickname);
        foreach (var path in session.DataFiles.Paths(UniverseGraph.LockRole))
        {
            var sections = lower
                ? session.OpenDocument(path)?.Base ?? []
                : session.WorkingSections(path) ?? [];
            var locked = new HashSet<uint>();
            Read(sections, locked, new HashSet<uint>());
            if (locked.Contains(hash))
                return true;
        }
        return false;
    }

    /// <summary>The hashes that the gate locks of the sections of a file hold: the player locks (<c>locked_gate</c>)
    /// and the NPC locks (<c>npc_locked_gate</c>). The jump graph of the route tables reads them.</summary>
    public static void Read(IReadOnlyList<Section> sections, ISet<uint> player, ISet<uint> npc)
    {
        foreach (var section in sections)
        {
            if (!section.Name.Equals(Section, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var entry in section)
            {
                var into = entry.Name.Equals(Keys[0], StringComparison.OrdinalIgnoreCase) ? player
                    : entry.Name.Equals(Keys[1], StringComparison.OrdinalIgnoreCase) ? npc
                    : null;
                if (into != null && entry.Count > 0 && HashOf(entry[0]) is { } hash)
                    into.Add(hash);
            }
        }
    }

    /// <summary>The value of a gate lock for a nickname: its FLHash as an unsigned decimal number.</summary>
    public static ValueBase ValueOf(string nickname) => ValueOf(FLHash.CreateID(nickname));

    /// <summary>The value of a gate lock for a hash: the hash as an unsigned decimal number.</summary>
    private static ValueBase ValueOf(uint hash) => new Int32Value(unchecked((int)hash), unsigned: true);

    /// <summary>The hash that a value holds, or null when it holds no number.</summary>
    internal static uint? HashOf(ValueBase value)
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
