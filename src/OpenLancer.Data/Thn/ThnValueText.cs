using System;
using System.Collections.Generic;

namespace OpenLancer.Data.Thn;

/// <summary>
/// The check of a Lua text before the Thorn VM reads it (section 8.7, item 2 of the MCP1 design): the value text of a
/// cutscene field and the path text of a MOTION_PATH. Such a text holds values, not a script: numbers, texts, names and
/// tables of them. The VM would also run a statement, a call or an operator, for example a text that doubles a string
/// until the memory runs out. So the check reads the text with the rules of the Lua 3.2 lexer of the native compiler
/// (llex.c) and refuses each other part.
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
        var reader = new Reader(text,
            name => name is "nil" or "Y" or "N" || symbols.ContainsKey(name) ? null : NotName(name));
        reader.Next();
        return reader.Value() ?? reader.Rest();
    }

    /// <summary>Why a text is not the items of a table, or null when it is. The items are values and fields as in a
    /// table, without the <c>{</c> and the <c>}</c>, for example <c>OPEN, { 1, 2, 3 }, </c>.</summary>
    /// <param name="nameProblem">Why a name is not a name that the items can hold, or null when it is.</param>
    public static string? ItemsProblem(string text, Func<string, string?> nameProblem)
    {
        if (TextProblem(text) is { } problem)
            return problem;
        var reader = new Reader(text, nameProblem);
        reader.Next();
        return reader.Fields(inTable: false);
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

    private enum TokenKind
    {
        End,
        Number,
        Text,
        Name,
        Mark,

        /// <summary>A string or a long string without its end.</summary>
        Open,

        /// <summary>A token that the lexer refuses, for example a bad number. The reader keeps the reason.</summary>
        Bad,
    }

    /// <summary>The tokens of a text, as the Lua 3.2 lexer reads them, and the rules of a value over them.</summary>
    private sealed class Reader(string text, Func<string, string?> nameProblem)
    {
        private int at;
        private int start;
        private int depth;
        private string? bad;

        public TokenKind Kind { get; private set; }

        /// <summary>The text of the token.</summary>
        public string Token => text[start..at];

        private bool Is(char mark) => Kind == TokenKind.Mark && text[start] == mark;

        /// <summary>Reads the next token. The lexer skips a space, a tab, a carriage return and a line end. A comment
        /// runs from <c>--</c> to the end of the line.</summary>
        public void Next()
        {
            while (at < text.Length)
            {
                if (text[at] is ' ' or '\t' or '\r' or '\n')
                    at++;
                else if (text[at] == '-' && at + 1 < text.Length && text[at + 1] == '-')
                    at = text.IndexOf('\n', at) is var line and >= 0 ? line : text.Length;
                else
                    break;
            }
            start = at;
            if (at >= text.Length)
            {
                Kind = TokenKind.End;
                return;
            }
            var c = text[at];
            var next = at + 1 < text.Length ? text[at + 1] : '\0';
            if (char.IsAsciiDigit(c) || c == '.' && char.IsAsciiDigit(next))
                Kind = ReadNumber() ? TokenKind.Number : Fail(NotNumber(Token));
            else if (c is '"' or '\'')
                Kind = ReadShortText(c);
            else if (c == '[' && next == '[')
            {
                var close = LongStringEnd(at + 2);
                at = close < 0 ? text.Length : close + 1;
                Kind = close < 0 ? TokenKind.Open : TokenKind.Text;
            }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_'))
                    at++;
                Kind = TokenKind.Name;
            }
            else
            {
                at++;
                Kind = TokenKind.Mark;
            }
        }

        private TokenKind Fail(string reason)
        {
            bad = reason;
            return TokenKind.Bad;
        }

        /// <summary>Reads a number as the lexer reads it: digits, a point and digits, then an optional exponent. Two
        /// points in a row do not compile. An exponent needs digits, with an optional sign before them
        /// (lobject.c, luaO_str2d).</summary>
        /// <returns>False for a number that the lexer refuses.</returns>
        private bool ReadNumber()
        {
            SkipDigits();
            if (at < text.Length && text[at] == '.')
            {
                at++;
                if (at < text.Length && text[at] == '.')
                {
                    at++;
                    return false;
                }
                SkipDigits();
            }
            if (at >= text.Length || text[at] is not ('e' or 'E'))
                return true;
            at++;
            if (at < text.Length && text[at] is '+' or '-')
                at++;
            var digits = at;
            SkipDigits();
            return at > digits;
        }

        private void SkipDigits()
        {
            while (at < text.Length && char.IsAsciiDigit(text[at]))
                at++;
        }

        /// <summary>Reads a short string. A <c>\</c> escapes the next character, also a line end. A line end without a
        /// <c>\</c> ends the string without its end, as in Lua 3.2. A decimal escape gives the character of its value,
        /// with at most three digits.</summary>
        private TokenKind ReadShortText(char quote)
        {
            for (at++; at < text.Length; at++)
            {
                if (text[at] == '\n')
                    return TokenKind.Open;
                if (text[at] == quote)
                {
                    at++;
                    return TokenKind.Text;
                }
                if (text[at] != '\\')
                    continue;
                at++;
                if (at >= text.Length)
                    break;
                if (!char.IsAsciiDigit(text[at]))
                    continue;
                var digits = at;
                var value = 0;
                for (; at < text.Length && at - digits < 3 && char.IsAsciiDigit(text[at]); at++)
                    value = value * 10 + (text[at] - '0');
                if (value is 0 or > 127)
                {
                    var reason = BadEscape(text[digits..at], value);
                    at = text.Length;
                    return Fail(reason);
                }
                at--;
            }
            at = text.Length;
            return TokenKind.Open;
        }

        /// <summary>The index of the last <c>]</c> of a long string whose text starts at <paramref name="from"/>, or
        /// -1. A long string of Lua 3.2 can hold other long strings.</summary>
        private int LongStringEnd(int from)
        {
            var level = 0;
            for (var i = from; i + 1 < text.Length; i++)
            {
                if (text[i] == '[' && text[i + 1] == '[')
                {
                    level++;
                    i++;
                }
                else if (text[i] == ']' && text[i + 1] == ']')
                {
                    if (level == 0)
                        return i + 1;
                    level--;
                    i++;
                }
            }
            return -1;
        }

        /// <summary>A value: a number with an optional <c>-</c>, a text, a name, or a table.</summary>
        public string? Value()
        {
            switch (Kind)
            {
                case TokenKind.Number or TokenKind.Text:
                    Next();
                    return null;
                case TokenKind.Name:
                    if (nameProblem(Token) is { } problem)
                        return problem;
                    Next();
                    return null;
                case TokenKind.Mark when Is('-'):
                    Next();
                    if (Kind != TokenKind.Number)
                        return Refusal();
                    Next();
                    return null;
                case TokenKind.Mark when Is('{'):
                    if (++depth > MaxDepth)
                        return TooDeep;
                    Next();
                    var fields = Fields(inTable: true);
                    depth--;
                    return fields;
                default:
                    return Refusal();
            }
        }

        /// <summary>The fields of a table after its <c>{</c>, or of the whole text: values, <c>name = value</c> and
        /// <c>[value] = value</c>, with <c>,</c> or <c>;</c> between them. A table then ends with <c>}</c>, and the
        /// whole text ends with its end.</summary>
        public string? Fields(bool inTable)
        {
            while (!Closes(inTable))
            {
                var problem = Kind == TokenKind.Name && NextIs('=') ? Key()
                    : Is('[') ? Bracket()
                    : Value();
                if (problem != null)
                    return problem;
                if (Is(',') || Is(';'))
                    Next();
                else if (!Closes(inTable))
                    return Refusal();
            }
            if (inTable)
                Next();
            return null;
        }

        private bool Closes(bool inTable) => inTable ? Is('}') : Kind == TokenKind.End;

        /// <summary>Why the text after the value is not empty, or null when it is. An operator gives its own refusal.
        /// A table, a name, a number or a text starts a second value or a statement.</summary>
        public string? Rest() => Kind switch
        {
            TokenKind.End => null,
            TokenKind.Open or TokenKind.Bad => Refusal(),
            TokenKind.Mark when !Is('{') => NotValue(Token),
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

        /// <summary>True when the token after this one is the mark.</summary>
        private bool NextIs(char mark)
        {
            var (savedAt, savedStart, savedKind, savedBad) = (at, start, Kind, bad);
            Next();
            var result = Is(mark);
            (at, start, Kind, bad) = (savedAt, savedStart, savedKind, savedBad);
            return result;
        }

        /// <summary>The refusal of the token where a value or a mark of a table must be.</summary>
        private string Refusal() => Kind switch
        {
            TokenKind.End => Unfinished,
            TokenKind.Open => OpenString,
            TokenKind.Bad => bad!,
            _ => NotValue(Token),
        };
    }
}
