using System.Collections.Generic;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The check of a Lua value text of a cutscene field before the Thorn VM reads it (section 8.7, item 2 of the MCP1
/// design). A field takes a value, not a script: a number, a text, a THN name, or a table of them. The VM would also run
/// a statement, a call or an operator, for example a text that doubles a string until the memory runs out. So the check
/// reads the tokens of Lua 3.2 and refuses each other part. The VM then reads only a value, as it reads the values of a
/// THN file.
/// </summary>
/// <remarks>A text that the check cannot read to its end, for example a string without its end or a table without its
/// <c>}</c>, is not valid Lua. The check then gives no problem, and the compiler gives the error, so nothing runs.</remarks>
internal static class ThnValueText
{
    /// <summary>Why a text is not one value, or null when it is one value or when the compiler refuses it.</summary>
    /// <param name="symbols">The THN names that a value can name, for example <c>L_POINT</c>. <c>Y</c>, <c>N</c> and
    /// <c>nil</c> are values too.</param>
    public static string? Problem(string text, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var reader = new Reader(text, symbols);
        reader.Next();
        var problem = reader.Value();
        if (problem == Reader.Unreadable)
            return null;
        // After the value, an operator gives its own refusal. A table, a name, a number or a text starts a second value
        // or a statement.
        return problem ?? reader.Kind switch
        {
            TokenKind.End or TokenKind.Open => null,
            TokenKind.Mark when reader.Token != "{" => NotValue(reader.Token),
            _ => MoreThanOne,
        };
    }

    /// <summary>The refusal of a text with more than one value, for example a value and a statement.</summary>
    internal const string MoreThanOne = "The text holds more than one value. Give one value, for example { 1, 2 }.";

    /// <summary>The refusal of a part that a value cannot hold, for example an operator or a call.</summary>
    internal static string NotValue(string part) =>
        $"The text holds '{part}', which a value cannot hold. A value is a number, a text, a THN name or a table of " +
        "them, for example { 1, \"a\", L_POINT }.";

    /// <summary>The refusal of a name that is not a THN name, for example a global of the VM.</summary>
    internal static string NotName(string name) =>
        $"The text names {name}, which is not a THN name. A value can name only THN names, for example L_POINT.";

    private enum TokenKind
    {
        End,
        Number,
        Text,
        Name,
        Mark,

        /// <summary>A string or a long string without its end.</summary>
        Open,
    }

    /// <summary>The tokens of a text, and the rules of a value over them.</summary>
    private sealed class Reader(string text, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        /// <summary>The result of a rule when the text is not valid Lua. The compiler then gives the error.</summary>
        public const string Unreadable = "";

        private int at;
        private int start;

        public TokenKind Kind { get; private set; }

        /// <summary>The text of the token.</summary>
        public string Token => text[start..at];

        private bool Is(char mark) => Kind == TokenKind.Mark && text[start] == mark;

        /// <summary>Reads the next token. A comment runs from <c>--</c> to the end of the line, as in Lua 3.2.</summary>
        public void Next()
        {
            while (at < text.Length)
            {
                if (char.IsWhiteSpace(text[at]))
                    at++;
                else if (text[at] == '-' && at + 1 < text.Length && text[at + 1] == '-')
                    at = text.IndexOf('\n', at) is var line and >= 0 ? line + 1 : text.Length;
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
            {
                ReadNumber();
                Kind = TokenKind.Number;
            }
            else if (c is '"' or '\'')
            {
                Kind = ReadShortText(c) ? TokenKind.Text : TokenKind.Open;
            }
            else if (c == '[' && next == '[')
            {
                var close = ThnFieldEdit.LongStringEnd(text, at + 2);
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

        private void ReadNumber()
        {
            while (at < text.Length && (char.IsAsciiDigit(text[at]) || text[at] == '.'))
                at++;
            if (at < text.Length && text[at] is 'e' or 'E')
            {
                at++;
                if (at < text.Length && text[at] is '+' or '-')
                    at++;
                while (at < text.Length && char.IsAsciiDigit(text[at]))
                    at++;
            }
        }

        /// <summary>Reads a short string. A <c>\</c> escapes the next character. A line end ends the text without the
        /// end of the string, as in Lua 3.2.</summary>
        /// <returns>False for a string without its end.</returns>
        private bool ReadShortText(char quote)
        {
            for (at++; at < text.Length; at++)
            {
                if (text[at] == '\n')
                    return false;
                if (text[at] == '\\')
                    at++;
                else if (text[at] == quote)
                {
                    at++;
                    return true;
                }
            }
            at = text.Length;
            return false;
        }

        /// <summary>A value: a number with an optional <c>-</c>, a text, a THN name, or a table.</summary>
        public string? Value()
        {
            switch (Kind)
            {
                case TokenKind.Number or TokenKind.Text:
                    Next();
                    return null;
                case TokenKind.Name:
                    var name = Token;
                    if (name is not ("nil" or "Y" or "N") && !symbols.ContainsKey(name))
                        return NotName(name);
                    Next();
                    return null;
                case TokenKind.Mark when Is('-'):
                    Next();
                    if (Kind != TokenKind.Number)
                        return Refusal();
                    Next();
                    return null;
                case TokenKind.Mark when Is('{'):
                    Next();
                    return Fields();
                default:
                    return Refusal();
            }
        }

        /// <summary>The fields of a table after its <c>{</c>: values, <c>name = value</c> and <c>[value] = value</c>,
        /// with <c>,</c> or <c>;</c> between them, then <c>}</c>.</summary>
        private string? Fields()
        {
            while (true)
            {
                if (Is('}'))
                {
                    Next();
                    return null;
                }
                var problem = Kind == TokenKind.Name && NextIs('=') ? Key()
                    : Is('[') ? Bracket()
                    : Value();
                if (problem != null)
                    return problem;
                if (Is(',') || Is(';'))
                {
                    Next();
                    continue;
                }
                if (Is('}'))
                {
                    Next();
                    return null;
                }
                return Refusal();
            }
        }

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
            var (savedAt, savedStart, savedKind) = (at, start, Kind);
            Next();
            var result = Is(mark);
            (at, start, Kind) = (savedAt, savedStart, savedKind);
            return result;
        }

        /// <summary>The refusal of the token, or <see cref="Unreadable"/> at the end of the text or in a string without
        /// its end, where the compiler gives the error.</summary>
        private string Refusal() => Kind is TokenKind.End or TokenKind.Open ? Unreadable : NotValue(Token);
    }
}
