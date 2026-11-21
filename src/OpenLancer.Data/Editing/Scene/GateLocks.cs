using System;
using System.Collections.Generic;
using System.Globalization;
using LibreLancer.Data;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>One value of a gate lock: value <paramref name="Index"/> of the line <paramref name="Line"/> (from 0) of
/// <paramref name="Key"/> in a <c>[locked_gates]</c> section.</summary>
internal sealed record GateLock(SectionRef Section, string Key, int Line, int Index);

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
    public static ValueBase ValueOf(string nickname) => new Int32Value(unchecked((int)FLHash.CreateID(nickname)), unsigned: true);

    /// <summary>The hash that a value holds, or null when it holds no number.</summary>
    private static uint? HashOf(ValueBase value)
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
