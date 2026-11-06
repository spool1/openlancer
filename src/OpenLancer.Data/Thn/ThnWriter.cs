using System;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace OpenLancer.Data.Thn;

/// <summary>
/// Writes a <see cref="ThnValue"/> tree as Lua 3.2 text. The engine reads a text script as it reads bytecode
/// (ThornRuntime.cs:49-59).
/// </summary>
/// <remarks>
/// <para>The text is canonical. The evaluation of the text gives the same tree, with the same float bits and the same
/// key order. So the text of the evaluated text is the same text (a fixed point). Byte identity with retail bytecode
/// is not a goal, because the VM keeps neither the source layout nor the double constants of retail.</para>
/// <list type="bullet">
/// <item>A number writes the shortest text that gives the same float (<c>"R"</c>). The compiler parses a number with
/// its own routine (lobject.c:82-128), and the VM loads it as a float (Undump.cs:125). A check of all 4,278,190,079
/// finite floats except -0 found no float that this text changes. The stock format keeps about 7 digits
/// (ThornTable.cs:498).</item>
/// <item>A text escapes <c>"</c>, <c>\</c> and the newline, and writes the other control characters as <c>\ddd</c>
/// (llex.c:325-353). The stock path escapes with JSON (ThornTable.cs:502), so <c>&amp;</c> becomes
/// <c>\u0026</c>, which Lua 3.2 reads as <c>u0026</c>.</item>
/// <item>A name writes the name, and a number writes the number. The writer never turns one into the other. The stock
/// path turns some numbers into names (ThnDecompile.cs:124-154).</item>
/// <item>A table writes its list part, then <c>;</c>, then its record part in its order (lparser.c:1272-1340). A key
/// that is not a Lua name, or that is a reserved word, writes as <c>["key"]</c>.</item>
/// <item>The globals write in their order. The items of a table global write one on each line, so a diff of two texts
/// shows one changed line for each changed entity or event.</item>
/// </list>
/// </remarks>
public static class ThnWriter
{
    /// <summary>The rule of the characters of a THN text, as the last sentence of a refusal.</summary>
    public const string TextRule = "A THN text must be ASCII without U+0000.";

    /// <summary>The reserved words of Lua 3.2 (llex.c:30-32).</summary>
    private static readonly FrozenSet<string> ReservedWords = FrozenSet.Create(StringComparer.Ordinal,
        "and", "do", "else", "elseif", "end", "function", "if", "local", "nil", "not", "or", "repeat", "return", "then",
        "until", "while");

    /// <summary>Writes the globals of a script: one global on each line, in order.</summary>
    /// <returns>ASCII text with <c>\n</c> line ends.</returns>
    /// <exception cref="ArgumentException">The globals have a list part, a global name or a THN name is not a Lua name,
    /// or a text or a key is not ASCII or has the character U+0000.</exception>
    public static string Write(ThnTable globals)
    {
        if (!globals.IsRecord)
            throw new ArgumentException("The globals of a script must be a record.", nameof(globals));
        var text = new StringBuilder();
        foreach (var (name, value) in globals.Record)
        {
            if (!IsName(name))
                throw new ArgumentException($"The global name '{name}' is not a Lua name.", nameof(globals));
            text.Append(name).Append(" = ");
            if (value is ThnTable table)
                WriteTable(text, table, lines: true);
            else
                WriteValue(text, value);
            text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Writes one value on one line.</summary>
    /// <exception cref="ArgumentException">A THN name is not a Lua name, or a text or a key is not ASCII or has the
    /// character U+0000.</exception>
    public static string Value(ThnValue value)
    {
        var text = new StringBuilder();
        WriteValue(text, value);
        return text.ToString();
    }

    /// <summary>
    /// Checks that the text of a tree gives the same tree: writes the tree, evaluates the text, and compares the
    /// result exactly.
    /// </summary>
    /// <remarks>Equal trees write the same text, because equality compares the float bits, the characters and the key
    /// order, and a number is never -0 or NaN. So when the round trip is exact, the result writes the same text, and
    /// the text is a fixed point.</remarks>
    /// <param name="name">The name of the script, for messages.</param>
    /// <returns>Null when the round trip is exact, or else the first problem.</returns>
    public static string? RoundTripProblem(ThnTable globals, IThnEvaluator evaluator, string name)
    {
        string text;
        try
        {
            text = Write(globals);
        }
        catch (ArgumentException e)
        {
            return $"The tree cannot be written: {e.Message}";
        }

        ThnTable again;
        try
        {
            again = evaluator.Evaluate(Encoding.ASCII.GetBytes(text), name);
        }
        catch (ThnEvaluationException e)
        {
            return $"The written text does not evaluate: {e.Message}";
        }
        return ThnValues.FirstDifference(globals, again) is { } path
            ? $"The written text gives a different value at {(path.Length == 0 ? "the globals" : path)}."
            : null;
    }

    /// <summary>True for a Lua name (llex.c:414-432) that is not a reserved word. The writer writes such a key without
    /// brackets. A global name must be such a name, because Lua 3.2 text has no other form for a global.</summary>
    public static bool IsName(string text)
    {
        if (text.Length == 0 || !(char.IsAsciiLetter(text[0]) || text[0] == '_'))
            return false;
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }
        return !ReservedWords.Contains(text);
    }

    /// <param name="lines">True to write each item on its own line, for a table global.</param>
    private static void WriteTable(StringBuilder text, ThnTable table, bool lines)
    {
        if (table.List.IsEmpty && table.Record.IsEmpty)
        {
            text.Append("{}");
            return;
        }
        var first = lines ? "\n\t" : " ";
        var next = lines ? ",\n\t" : ", ";
        text.Append('{');
        for (var i = 0; i < table.List.Length; i++)
        {
            text.Append(i == 0 ? first : next);
            WriteValue(text, table.List[i]);
        }
        for (var i = 0; i < table.Record.Length; i++)
        {
            text.Append(i > 0 ? next : table.List.IsEmpty ? first : lines ? ";\n\t" : "; ");
            WriteKey(text, table.Record[i].Key);
            text.Append(" = ");
            WriteValue(text, table.Record[i].Value);
        }
        text.Append(lines ? "\n}" : " }");
    }

    private static void WriteValue(StringBuilder text, ThnValue value)
    {
        switch (value)
        {
            case ThnNumber number:
                text.Append(number.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case ThnText s:
                WriteText(text, s.Value);
                break;
            case ThnBool flag:
                text.Append(flag.Value ? 'Y' : 'N');
                break;
            case ThnSymbol symbol:
                if (!IsName(symbol.Name))
                    throw new ArgumentException($"The THN name '{symbol.Name}' is not a Lua name.", nameof(value));
                text.Append(symbol.Name);
                break;
            case ThnTable table:
                WriteTable(text, table, lines: false);
                break;
            default:
                throw new UnreachableException($"{value.GetType().Name} is not a kind of THN value.");
        }
    }

    private static void WriteKey(StringBuilder text, string key)
    {
        if (IsName(key))
        {
            text.Append(key);
            return;
        }
        text.Append('[');
        WriteText(text, key);
        text.Append(']');
    }

    /// <summary>Why the writer refuses a text, or null. A text must be ASCII without the character U+0000 (see
    /// <see cref="WriteText"/>).</summary>
    public static string? TextProblem(string value)
    {
        foreach (var c in value)
        {
            if (c is '\0' or > '\x7F')
                return BadCharacter(value, c);
        }
        return null;
    }

    private static string BadCharacter(string value, char c) =>
        $"The text \"{value}\" has the character U+{(int)c:X4}. {TextRule}";

    /// <summary>Writes a quoted text. The VM reads a text as ASCII (Undump.cs:69), and it drops U+0000 at the end of a
    /// text, so the writer refuses other characters and U+0000.</summary>
    private static void WriteText(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"' or '\\':
                    text.Append('\\').Append(c);
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\0' or > '\x7F':
                    throw new ArgumentException(BadCharacter(value, c));
                case < ' ' or '\x7F':
                    // Three digits, so that a digit after the escape is not part of it.
                    text.Append('\\').Append(((int)c).ToString("D3", CultureInfo.InvariantCulture));
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }
        text.Append('"');
    }
}
