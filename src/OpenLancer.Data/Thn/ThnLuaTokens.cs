namespace OpenLancer.Data.Thn;

/// <summary>The kinds of the tokens of <see cref="ThnLuaTokens"/>.</summary>
internal enum ThnTokenKind
{
    End,
    Number,
    Text,
    Name,
    Mark,

    /// <summary>A string or a long string without its end.</summary>
    Open,

    /// <summary>A token that the lexer refuses, for example a bad number. <see cref="ThnLuaTokens.BadReason"/> tells
    /// why.</summary>
    Bad,
}

/// <summary>
/// The tokens of a Lua text, as the Lua 3.2 lexer of the native compiler reads them (llex.c). The checks of a text
/// before the Thorn VM reads it use them (see <see cref="ThnValueText"/> and <see cref="ThnMotionPath.TextProblem"/>).
/// So a check reads the same tokens as the compiler.
/// </summary>
/// <remarks>The lexer reads a line that starts with <c>$</c> as a pragma, also in a comment and in a string. The tokens
/// do not show a pragma, so each check refuses such a line itself.</remarks>
internal sealed class ThnLuaTokens(string text)
{
    private int at;
    private int start;

    public ThnTokenKind Kind { get; private set; }

    /// <summary>The text of the token.</summary>
    public string Token => text[start..at];

    /// <summary>Why the lexer refuses the token, for the kind <see cref="ThnTokenKind.Bad"/>, else null.</summary>
    public string? BadReason { get; private set; }

    /// <summary>True when the token is the mark.</summary>
    public bool Is(char mark) => Kind == ThnTokenKind.Mark && text[start] == mark;

    /// <summary>Reads the next token. The lexer skips a space, a tab, a carriage return and a line end. A comment runs
    /// from <c>--</c> to the end of the line.</summary>
    public void Next()
    {
        BadReason = null;
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
            Kind = ThnTokenKind.End;
            return;
        }
        var c = text[at];
        var next = at + 1 < text.Length ? text[at + 1] : '\0';
        if (char.IsAsciiDigit(c) || c == '.' && char.IsAsciiDigit(next))
            Kind = ReadNumber() ? ThnTokenKind.Number : Fail(ThnValueText.NotNumber(Token));
        else if (c is '"' or '\'')
            Kind = ReadShortText(c);
        else if (c == '[' && next == '[')
        {
            var close = LongStringEnd(at + 2);
            at = close < 0 ? text.Length : close + 1;
            Kind = close < 0 ? ThnTokenKind.Open : ThnTokenKind.Text;
        }
        else if (char.IsAsciiLetter(c) || c == '_')
        {
            while (at < text.Length && (char.IsAsciiLetterOrDigit(text[at]) || text[at] == '_'))
                at++;
            Kind = ThnTokenKind.Name;
        }
        else
        {
            at++;
            Kind = ThnTokenKind.Mark;
        }
    }

    /// <summary>True when the token after this one is the mark. The token stays.</summary>
    public bool NextIs(char mark)
    {
        var (savedAt, savedStart, savedKind, savedBad) = (at, start, Kind, BadReason);
        Next();
        var result = Is(mark);
        (at, start, Kind, BadReason) = (savedAt, savedStart, savedKind, savedBad);
        return result;
    }

    private ThnTokenKind Fail(string reason)
    {
        BadReason = reason;
        return ThnTokenKind.Bad;
    }

    /// <summary>Reads a number as the lexer reads it: digits, a point and digits, then an optional exponent. Two points
    /// in a row do not compile. An exponent needs digits, with an optional sign before them (lobject.c,
    /// luaO_str2d).</summary>
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
    /// <c>\</c> ends the string without its end, as in Lua 3.2. A decimal escape gives the character of its value, with
    /// at most three digits.</summary>
    private ThnTokenKind ReadShortText(char quote)
    {
        for (at++; at < text.Length; at++)
        {
            if (text[at] == '\n')
                return ThnTokenKind.Open;
            if (text[at] == quote)
            {
                at++;
                return ThnTokenKind.Text;
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
                var reason = ThnValueText.BadEscape(text[digits..at], value);
                at = text.Length;
                return Fail(reason);
            }
            at--;
        }
        at = text.Length;
        return ThnTokenKind.Open;
    }

    /// <summary>The index of the last <c>]</c> of a long string whose text starts at <paramref name="from"/>, or -1. A
    /// long string of Lua 3.2 can hold other long strings.</summary>
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
}
