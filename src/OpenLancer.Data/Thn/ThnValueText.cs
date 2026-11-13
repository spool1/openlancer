using System;
using System.Collections.Generic;

namespace OpenLancer.Data.Thn;

/// <summary>
/// The check of the Lua value text of a cutscene field before the Thorn VM reads it (section 8.7, item 2 of the MCP1
/// design). Such a text holds one value, not a script: a number, a text, a name or a table of them. The VM would also run
/// a statement, a call or an operator, for example a text that doubles a string until the memory runs out. So the check
/// reads the tokens of the Lua 3.2 lexer of the native compiler (see <see cref="ThnLuaTokens"/>) and refuses each other
/// part. <see cref="ThnMotionPath.TextProblem"/> checks a path text with the same tokens.
/// </summary>
/// <remarks>The check also refuses each text that the lexer refuses or that it cannot read to its end, for example a
/// string without its end. So the compiler gets only a text whose tokens the check read, and only values.</remarks>
internal static class ThnValueText
{
    /// <summary>The most levels of tables in one text. The limit keeps the recursion of the check and of the compiler
    /// small. The elements of the retail scripts fit in it (see the retail tests of the agent tools).</summary>
    public const int MaxDepth = 32;

    /// <summary>Why a text is not one value, or null when it is one value: a number, a text, a THN name, or a table of
    /// them.</summary>
    /// <param name="symbols">The THN names that a value can name, for example <c>L_POINT</c>. <c>Y</c>, <c>N</c> and
    /// <c>nil</c> are values too.</param>
    public static string? Problem(string text, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        if (TextProblem(text) is { } problem)
            return problem;
        var reader = new Reader(text, symbols);
        reader.Next();
        return reader.Value() ?? reader.Rest();
    }

    /// <summary>The refusal of a text with more than one value, for example a value and a statement.</summary>
    internal const string MoreThanOne = "The text holds more than one value. Give one value, for example { 1, 2 }.";

    /// <summary>The refusal of a text that ends before its value is complete.</summary>
    internal const string Unfinished =
        "The text ends before the value is complete, for example in a table without its }. Complete the value.";

    /// <summary>The refusal of a string without its end.</summary>
    internal const string OpenString = "A text in quotes or in [[ ]] has no end. Add the quote or the ]] at its end.";

    /// <summary>The refusal of a line that starts with <c>$</c>. The lexer reads such a line as a pragma, for example
    /// <c>$if</c>, also in a string (llex.c, inclinenumber).</summary>
    internal const string Pragma =
        "A line of the text starts with $. The compiler reads such a line as an instruction, so a value cannot hold it.";

    /// <summary>The refusal of a text with tables in more than <see cref="MaxDepth"/> levels.</summary>
    internal static readonly string TooDeep = $"The text holds tables in more than {MaxDepth} levels. Use fewer levels.";

    /// <summary>The refusal of a part that a value cannot hold, for example an operator or a call.</summary>
    internal static string NotValue(string part) =>
        $"The text holds '{part}', which a value cannot hold. A value is a number, a text, a THN name or a table of " +
        "them, for example { 1, \"a\", L_POINT }.";

    /// <summary>The refusal of a name that is not a THN name, for example a global of the VM.</summary>
    internal static string NotName(string name) =>
        $"The text names {name}, which is not a THN name. A value can name only THN names, for example L_POINT.";

    /// <summary>The refusal of a number that the lexer refuses, for example <c>1..2</c> or <c>1e</c>.</summary>
    internal static string NotNumber(string part) => $"The text holds '{part}', which is not a number.";

    /// <summary>The refusal of a decimal escape in a short string that gives a character that a THN text cannot hold,
    /// for example <c>"\200"</c>. The VM reads a character above U+007F as <c>?</c> (Undump.cs:69), and it drops
    /// U+0000 at the end of a text. So such an escape would change the value without a message. An escape above 255
    /// does not compile.</summary>
    internal static string BadEscape(string digits, int value) => value > byte.MaxValue
        ? $"The escape \\{digits} is more than \\255, so it gives no character."
        : $"The escape \\{digits} gives the character U+{value:X4}. {ThnWriter.TextRule}";

    /// <summary>The rules of the whole text: ASCII without U+0000, as in a THN file, and no pragma line. The lexer reads
    /// a pragma right after each line end, also in a comment and in a string. So the check refuses <c>$</c> after each
    /// line end.</summary>
    private static string? TextProblem(string text) =>
        ThnWriter.TextProblem(text) ?? (text.Contains("\n$", StringComparison.Ordinal) ? Pragma : null);

    /// <summary>The rules of a value over the tokens of a text.</summary>
    private sealed class Reader(string text, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        private readonly ThnLuaTokens tokens = new(text);
        private int depth;

        public void Next() => tokens.Next();

        private ThnTokenKind Kind => tokens.Kind;

        private string Token => tokens.Token;

        private bool Is(char mark) => tokens.Is(mark);

        /// <summary>A value: a number with an optional <c>-</c>, a text, a name, or a table.</summary>
        public string? Value()
        {
            switch (Kind)
            {
                case ThnTokenKind.Number or ThnTokenKind.Text:
                    Next();
                    return null;
                case ThnTokenKind.Name:
                    if (!(Token is "nil" or "Y" or "N" || symbols.ContainsKey(Token)))
                        return NotName(Token);
                    Next();
                    return null;
                case ThnTokenKind.Mark when Is('-'):
                    Next();
                    if (Kind != ThnTokenKind.Number)
                        return Refusal();
                    Next();
                    return null;
                case ThnTokenKind.Mark when Is('{'):
                    if (++depth > MaxDepth)
                        return TooDeep;
                    Next();
                    var fields = Fields();
                    depth--;
                    return fields;
                default:
                    return Refusal();
            }
        }

        /// <summary>The fields of a table after its <c>{</c>, to its <c>}</c>: values, <c>name = value</c> and
        /// <c>[value] = value</c>, with <c>,</c> or <c>;</c> between them.</summary>
        private string? Fields()
        {
            while (!Is('}'))
            {
                var problem = Kind == ThnTokenKind.Name && tokens.NextIs('=') ? Key()
                    : Is('[') ? Bracket()
                    : Value();
                if (problem != null)
                    return problem;
                if (Is(',') || Is(';'))
                    Next();
                else if (!Is('}'))
                    return Refusal();
            }
            Next();
            return null;
        }

        /// <summary>Why the text after the value is not empty, or null when it is. An operator gives its own refusal.
        /// A table, a name, a number or a text starts a second value or a statement.</summary>
        public string? Rest() => Kind switch
        {
            ThnTokenKind.End => null,
            ThnTokenKind.Open or ThnTokenKind.Bad => Refusal(),
            ThnTokenKind.Mark when !Is('{') => NotValue(Token),
            _ => MoreThanOne,
        };

        /// <summary>A field <c>name = value</c>. The name is a key of the table, not a global.</summary>
        private string? Key()
        {
            Next();
            Next();
            return Value();
        }

        /// <summary>A field <c>[value] = value</c>.</summary>
        private string? Bracket()
        {
            Next();
            var problem = Value();
            if (problem != null)
                return problem;
            if (!Is(']'))
                return Refusal();
            Next();
            if (!Is('='))
                return Refusal();
            Next();
            return Value();
        }

        /// <summary>The refusal of the token where a value or a mark of a table must be.</summary>
        private string Refusal() => Kind switch
        {
            ThnTokenKind.End => Unfinished,
            ThnTokenKind.Open => OpenString,
            ThnTokenKind.Bad => tokens.BadReason!,
            _ => NotValue(Token),
        };
    }
}
