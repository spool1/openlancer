using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace OpenLancer.Data.Thn;

/// <summary>
/// The entity flags of the Inspector, how the engine reads the <c>flags</c> key, and how an edit writes it. The bits of
/// the flags keys of events follow the same write rules (see <see cref="WriteBits"/>).
/// </summary>
/// <remarks>
/// The engine casts a THN name to <c>ThnObjectFlags</c> (ThnScript.cs:293-302). The cast takes a name of each group
/// with the integer type of <c>ThnObjectFlags</c>, so <c>CAMERA</c> (<c>EntityTypes</c> 3) gives REFERENCE and
/// LIT_DYNAMIC. The engine reads a number with <c>ConvertFlags</c> (ThnScript.cs:203-218): 0 is no flag, 1 is
/// REFERENCE, and on a SOUND the bit 2 is SPATIAL. All other bits keep their value. Retail stores one flag as its name
/// and a combination as a number.
/// </remarks>
public static class ThnFlags
{
    public const string Key = "flags";
    public const string Reference = "REFERENCE";
    public const string LitDynamic = "LIT_DYNAMIC";
    public const string LitAmbient = "LIT_AMBIENT";
    public const string Hidden = "HIDDEN";
    public const string Spatial = "SPATIAL";

    /// <summary>The bit of SPATIAL in a number on a SOUND (ThnScript.cs:209-215).</summary>
    private const int SoundSpatialBit = 2;

    /// <summary>The value of each flag name, as <c>ThnObjectFlags</c> has it (ThnScript.cs:22-27).</summary>
    private static readonly FrozenDictionary<string, int> Values = new Dictionary<string, int>
    {
        [Reference] = 1,
        [LitDynamic] = 2,
        [LitAmbient] = 4,
        [Hidden] = 16,
        [Spatial] = 1 << 20,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] SoundFlags = [Reference, LitAmbient, Hidden, Spatial];
    private static readonly string[] OtherFlags = [Reference, LitDynamic, LitAmbient, Hidden];

    /// <summary>
    /// The flags that the Inspector shows for an entity. A SOUND shows SPATIAL and not LIT_DYNAMIC: the bit 2 of a
    /// number is SPATIAL on a SOUND, so a number cannot hold LIT_DYNAMIC there.
    /// </summary>
    public static IReadOnlyList<string> Shown(bool sound) => sound ? SoundFlags : OtherFlags;

    /// <summary>True for an entity whose type is SOUND, as a name or as a number.</summary>
    public static bool IsSound(ThnTable entity, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var sound = symbols["SOUND"].Value;
        return entity["type"] switch
        {
            ThnNumber number => (long)number.Value == sound,
            ThnSymbol name => symbols.TryGetValue(name.Name, out var info) && info.Value == sound,
            _ => false,
        };
    }

    /// <summary>The flags of <see cref="Shown"/> that are on in a value of <c>flags</c>.</summary>
    /// <param name="value">The value, or null when the entity has no <c>flags</c>.</param>
    /// <exception cref="InvalidOperationException">The engine cannot read the value as flags.</exception>
    public static IReadOnlySet<string> On(ThnValue? value, bool sound, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var bits = Read(value, sound, symbols);
        return Shown(sound).Where(n => (bits & Values[n]) != 0).ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>True for an entity with the REFERENCE flag. The engine does not make such an entity
    /// (ThnScriptInstance.cs:70-71). A flags value that the engine cannot read gives false, because the checks of the
    /// script report it.</summary>
    public static bool IsReference(ThnTable entity, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        try
        {
            return (Read(entity[Key], IsSound(entity, symbols), symbols) & Values[Reference]) != 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>True when the engine can read a value of <c>flags</c>: a number, or a THN name that the cast takes
    /// (ThnScript.cs:295-302).</summary>
    internal static bool CanRead(ThnValue value, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        value is ThnNumber || value is ThnSymbol name && NameBits(name.Name, symbols) != null;

    /// <summary>The bits of a flags key of an event that are on: a number, or the THN name of a bit of the group of
    /// the key. A missing key has the bits that the engine uses then (<see cref="ThnKeyRow.DefaultBits"/>).</summary>
    /// <exception cref="InvalidOperationException">The value is not a number or a name of the group.</exception>
    public static IReadOnlySet<string> BitsOn(ThnValue? value, ThnKeyRow row, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var bits = BitsOf(value, row, symbols);
        return row.Bits.Where(b => (bits & b.Bit) != 0).Select(b => b.Name).ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The value of a flags key of an event that gives the chosen bits, with the rules of <see cref="Write"/>: a
    /// stored number stays a number, one bit from a stored name or a missing key is the THN name of the bit when the
    /// group of the key has names, and else the value is the number. The bits that the row does not list keep their
    /// state. A missing key starts from the bits that the engine uses then, so a click adds a bit to them.
    /// </summary>
    /// <returns>The new value, or <paramref name="stored"/> when the bits do not change.</returns>
    /// <exception cref="ArgumentException">A chosen name is not a bit of the row.</exception>
    /// <exception cref="InvalidOperationException">The stored value is not a number or a name of the group.</exception>
    public static ThnValue? WriteBits(ThnValue? stored, ThnKeyRow row, IReadOnlySet<string> chosen,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        if (chosen.FirstOrDefault(n => row.Bits.All(b => b.Name != n)) is { } other)
            throw new ArgumentException($"'{other}' is not a bit of {row.Path}.", nameof(chosen));
        var current = BitsOf(stored, row, symbols);
        var mask = row.Bits.Sum(b => b.Bit);
        var result = (current & ~mask) | row.Bits.Where(b => chosen.Contains(b.Name)).Sum(b => b.Bit);
        if (result == current)
            return stored;
        if (stored is not ThnNumber && row.Group != null && BitOperations.PopCount((uint)result) == 1 &&
            row.Bits.FirstOrDefault(b => b.Bit == result) is { Name: { } name } && symbols.ContainsKey(name))
            return new ThnSymbol(name);
        return new ThnNumber(result);
    }

    /// <summary>The bits of a value of a flags key of an event, as the engine reads them.</summary>
    private static int BitsOf(ThnValue? value, ThnKeyRow row, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        value switch
        {
            null => row.DefaultBits,
            ThnNumber number => (int)number.Value,
            ThnSymbol name when symbols.TryGetValue(name.Name, out var info) && info.Group == row.Group => (int)info.Value,
            _ => throw new InvalidOperationException(
                $"'{row.Path}' holds {ThnWriter.Value(value)}. The engine reads a number or a name of {row.Group ?? "no group"} there."),
        };

    /// <summary>The flags that the engine reads from a value of <c>flags</c>, as the bits of
    /// <c>ThnObjectFlags</c>.</summary>
    /// <param name="value">The value, or null when the entity has no <c>flags</c>.</param>
    /// <exception cref="InvalidOperationException">The engine cannot read the value as flags.</exception>
    internal static int Read(ThnValue? value, bool sound, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        switch (value)
        {
            case null:
                return 0;
            case ThnSymbol name when NameBits(name.Name, symbols) is { } flags:
                return flags;
            case ThnNumber number:
                var bits = (int)number.Value;
                return sound && bits != 1 && (bits & SoundSpatialBit) != 0
                    ? (bits & ~SoundSpatialBit) | Values[Spatial]
                    : bits;
            default:
                throw new InvalidOperationException(
                    $"'{Key}' holds {ThnWriter.Value(value)}. The engine reads a number or a flag name there.");
        }
    }

    /// <summary>The bits that the cast of a THN name to <c>ThnObjectFlags</c> gives: the number of a name of each
    /// group with the integer type of <c>ThnObjectFlags</c> (ThnScript.cs:301). Null for another name, because the
    /// cast throws for it.</summary>
    private static int? NameBits(string name, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        symbols.TryGetValue(name, out var info) && info.Storage == symbols[Reference].Storage ? (int)info.Value : null;

    /// <summary>
    /// The value of <c>flags</c> that gives the chosen flags. The flags that <see cref="Shown"/> does not list keep
    /// their state. The rules (design section 5.2):
    /// <list type="number">
    /// <item>A stored number stays a number. Only the bits of the changed flags change.</item>
    /// <item>When the result is one flag, a stored name or a missing key gives the name of the flag.</item>
    /// <item>Else the value is the number that the engine reads as the result. No flag gives 0.</item>
    /// </list>
    /// </summary>
    /// <param name="stored">The value now, or null when the entity has no <c>flags</c>.</param>
    /// <returns>The new value. It is <paramref name="stored"/> when the engine reads the same flags.</returns>
    /// <exception cref="ArgumentException">A chosen name is not a flag that <see cref="Shown"/> lists.</exception>
    /// <exception cref="InvalidOperationException">The engine cannot read the stored value as flags, or no value
    /// gives the result: a number on a SOUND cannot hold LIT_DYNAMIC.</exception>
    internal static ThnValue? Write(ThnValue? stored, bool sound, IReadOnlySet<string> chosen,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var shown = Shown(sound);
        if (chosen.FirstOrDefault(n => !shown.Contains(n)) is { } other)
            throw new ArgumentException(Values.ContainsKey(other)
                ? $"{other} is not a flag of {(sound ? "a SOUND" : "an entity that is not a SOUND")}. The flags are " +
                  $"{string.Join(", ", shown)}."
                : $"'{other}' is not an entity flag. The flags are {string.Join(", ", shown)}.", nameof(chosen));
        var current = Read(stored, sound, symbols);
        var mask = shown.Sum(n => Values[n]);
        var result = (current & ~mask) | chosen.Sum(n => Values[n]);
        if (result == current)
            return stored;
        if (stored is ThnNumber number)
            return new ThnNumber(ChangeBits((int)number.Value, sound, current, result));
        if (BitOperations.PopCount((uint)result) == 1 && Values.FirstOrDefault(f => f.Value == result).Key is { } single)
            return new ThnSymbol(single);
        return new ThnNumber(Number(result, sound));
    }

    /// <summary>A stored number with the bits of the flags that change from <paramref name="current"/> to
    /// <paramref name="result"/>.</summary>
    private static int ChangeBits(int number, bool sound, int current, int result)
    {
        foreach (var name in Shown(sound))
        {
            var flag = Values[name];
            var wanted = (result & flag) != 0;
            if (wanted == ((current & flag) != 0))
                continue;
            // On a SOUND, the bit 2 holds SPATIAL in a number, and the value of SPATIAL holds it too.
            var bit = sound && name == Spatial ? SoundSpatialBit : flag;
            number = wanted ? number | bit : number & ~(bit | (sound && name == Spatial ? flag : 0));
        }
        return number;
    }

    /// <summary>The number that the engine reads as the flags.</summary>
    private static int Number(int flags, bool sound)
    {
        if (!sound)
            return flags;
        if ((flags & Values[LitDynamic]) != 0)
            throw new InvalidOperationException(
                $"A number cannot give {LitDynamic} with other flags on a SOUND, because the bit 2 of the number is {Spatial} there.");
        return (flags & Values[Spatial]) != 0 ? (flags & ~Values[Spatial]) | SoundSpatialBit : flags;
    }
}
