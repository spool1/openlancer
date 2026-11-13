using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenLancer.Data.Thn;

/// <summary>A key of a motion path: a point, and the orientation at the point when the path has orientations.</summary>
/// <param name="Point">The point in the frame of the path entity.</param>
/// <param name="Orientation">The orientation, or null for a path without orientations.</param>
public readonly record struct ThnPathKey(Vector3 Point, Quaternion? Orientation);

/// <summary>The keys of a motion path.</summary>
/// <param name="Closed">True for a closed path (<c>CLOSED</c>), false for an open one (<c>OPEN</c>).</param>
public sealed record ThnPathKeys(bool Closed, ImmutableArray<ThnPathKey> Keys)
{
    /// <summary>True when the keys have orientations.</summary>
    public bool HasOrientation => Keys.Length > 0 && Keys[0].Orientation != null;

    /// <summary>The keys with one key changed.</summary>
    public ThnPathKeys With(int index, ThnPathKey key) => this with { Keys = Keys.SetItem(index, key) };
}

/// <summary>
/// The <c>path_data</c> text of a MOTION_PATH entity: <c>OPEN, {x,y,z}, {w,x,y,z}, ...</c>. The engine runs the text as
/// a Lua table with the names <c>OPEN</c> (1) and <c>CLOSED</c> (0) (MotionPath.cs:20-26, :54-56). So this class reads
/// it with the evaluator too, and gets the same numbers. It checks the text first, so the evaluator reads only values
/// (see <see cref="TextProblem"/>).
/// </summary>
/// <remarks>The text of an edited path has the number format of <see cref="ThnWriter"/>. A path that no edit changed
/// keeps its text, because an edit replaces only the value that it changes.</remarks>
public static class ThnMotionPath
{
    public const string OpenName = "OPEN";
    public const string ClosedName = "CLOSED";

    /// <summary>The key of the record of a path in an entity.</summary>
    private const string PropsKey = "pathprops";

    /// <summary>The key of the path text in the record of a path.</summary>
    private const string DataKey = "path_data";

    /// <summary>The path of the text in a MOTION_PATH entity. A path goes only into a table without list items, so a
    /// check reads the text with <see cref="DataOf"/>.</summary>
    public static readonly ThnPath Path = new($"{PropsKey}{ThnPath.Separator}{DataKey}");

    private const string Global = "ol_path";

    /// <summary>The engine casts the first item of a path to an int, and only the names OPEN and CLOSED give an int,
    /// because a number of the VM is a float (MotionPath.cs:20-26, :64).</summary>
    private static readonly Regex StartName = new($@"^\s*({OpenName}|{ClosedName})\s*,", RegexOptions.CultureInvariant);

    /// <summary>Reads the keys of a path text, as the engine reads them (MotionPath.cs:60-85).</summary>
    /// <exception cref="FormatException">The text is not a path: it has a <see cref="TextProblem"/>, it does not run, it
    /// does not start with OPEN or CLOSED, or a key is not a list of numbers. The message tells why.</exception>
    public static ThnPathKeys Parse(string text, IThnEvaluator evaluator)
    {
        var path = Items(text, evaluator);
        if (path.List.Length < 2 || path.List[0] is not ThnNumber type || type.Value is not (0 or 1))
            throw new FormatException($"A path text starts with {OpenName} or {ClosedName}, then the keys.");
        // A path has orientations when its second item has four numbers (MotionPath.cs:66-71).
        var oriented = path.List.Length > 2 && path.List[2] is ThnTable { List.Length: >= 4 };
        var keys = ImmutableArray.CreateBuilder<ThnPathKey>();
        var step = oriented ? 2 : 1;
        for (var i = 1; i < path.List.Length; i += step)
        {
            var point = Numbers(path.List[i], 3, i);
            Quaternion? orientation = null;
            if (oriented)
            {
                if (i + 1 >= path.List.Length)
                    throw new FormatException($"The point {keys.Count + 1} has no orientation.");
                var q = Numbers(path.List[i + 1], 4, i + 1);
                orientation = new Quaternion(q[1], q[2], q[3], q[0]);
            }
            keys.Add(new ThnPathKey(new Vector3(point[0], point[1], point[2]), orientation));
        }
        return new ThnPathKeys(type.Value == 0, keys.ToImmutable());
    }

    /// <summary>
    /// Why the engine cannot read a path text, or null when it can (MotionPath.cs:54-103). The first item is the name
    /// OPEN or CLOSED. The third item is a table, and with four items it makes the path a path with orientations: then
    /// an orientation follows each point. The engine needs two points. It takes the first orientation of the path, so
    /// a path without orientations also stops the script.
    /// </summary>
    internal static string? Problem(string text, IThnEvaluator evaluator)
    {
        if (!StartName.IsMatch(text))
            return $"A path text starts with {OpenName} or {ClosedName} and a comma.";
        ThnTable path;
        try
        {
            path = Items(text, evaluator);
        }
        catch (FormatException e)
        {
            return e.Message;
        }
        if (path.List.Length < 3 || path.List[2] is not ThnTable orientation)
            return "The third item of a path text must be the orientation { w, x, y, z } of the first point.";
        if (orientation.List.Length < 4)
            return "A path text needs an orientation { w, x, y, z } after each point.";
        var points = 0;
        for (var i = 1; i < path.List.Length; i++)
        {
            // The items at the Lua places 2, 4, 6 and so on are points. The other items are orientations.
            var count = i % 2 == 1 ? 3 : 4;
            if (path.List[i] is not ThnTable { List: var numbers } || numbers.Length < count ||
                numbers.Take(count).Any(n => n is not ThnNumber))
                return $"The item {i + 1} of the path is not a list of {count} numbers.";
            points += i % 2;
        }
        return points < 2 ? "A path needs at least two points." : null;
    }

    /// <summary>
    /// Why the evaluator and the engine must not run a path text, or null when they can (section 8.7, item 2 of the
    /// MCP1 design). The text is the items of a table, with a comma between two items and an optional comma at the end.
    /// An item is the name OPEN or CLOSED, a number, or a table of numbers. A number can have a minus sign before it.
    /// The VM would also run a statement, a call or an operator, so the check refuses each other part. It reads the
    /// tokens of the Lua 3.2 lexer (see <see cref="ThnLuaTokens"/>), and it refuses each text and comment, so no pragma
    /// line can hide in them. A text without a problem can still be a text that the engine does not read as a path (see
    /// <see cref="Problem"/>).
    /// </summary>
    internal static string? TextProblem(string text)
    {
        if (ThnWriter.TextProblem(text) is { } characters)
            return characters;
        // Two minus signs start a comment, and a path text holds no text in which they can be.
        if (text.Contains("--", StringComparison.Ordinal))
            return PathComment;
        var tokens = new ThnLuaTokens(text);
        tokens.Next();
        while (tokens.Kind != ThnTokenKind.End)
        {
            if (PathItem(tokens) is { } problem)
                return problem;
            if (tokens.Is(','))
                tokens.Next();
            else if (tokens.Kind != ThnTokenKind.End)
                return PathPart(tokens, inTable: false);
        }
        return null;
    }

    /// <summary>
    /// The value of <c>pathprops.path_data</c> of an entity as the engine reads it, or null when the entity has no such
    /// value. The engine reads the key from the record part of the <c>pathprops</c> table, whatever its list part holds
    /// (ThnScript.cs:476-484). So a table such as <c>{ 1; path_data = "..." }</c> gives the entity a path text too. Each
    /// check of the path text of an entity reads the text here, because <see cref="Path"/> does not go into such a
    /// table.
    /// </summary>
    public static ThnValue? DataOf(ThnTable entity) => entity[PropsKey] is ThnTable props ? props[DataKey] : null;

    /// <summary>
    /// Why a build must not give a script to the engine, or null (section 8.7, item 2 of the MCP1 design). The engine
    /// reads <c>pathprops.path_data</c> of an entity of each type and runs it as Lua (ThnScript.cs:476-484,
    /// MotionPath.cs:54-55). So each path text of the script must be free of a <see cref="TextProblem"/>. The engine
    /// reads each value of <c>entities</c>, also a value with a text key (ThnScript.cs:124-127), so the check does too.
    /// </summary>
    /// <param name="globals">The globals of the script.</param>
    /// <returns>The reason, which names the entity.</returns>
    public static string? ScriptProblem(ThnTable globals)
    {
        if (globals["entities"] is not ThnTable entities)
            return null;
        foreach (var value in entities.List.Concat(entities.Record.Select(f => f.Value)))
        {
            if (value is ThnTable entity && DataOf(entity) is ThnText text && TextProblem(text.Value) is { } problem)
            {
                var name = ThnScriptModel.NameOf(entity) is { } entityName
                    ? $"The entity {entityName}"
                    : "An entity without a name";
                return $"{name} has a {Path} text that is not only a path. The engine would run it as a script. " +
                       problem;
            }
        }
        return null;
    }

    /// <summary>The rule of a path text, for the refusals of <see cref="TextProblem"/>.</summary>
    private const string PathRule = "A path text holds OPEN or CLOSED, then tables of numbers, for example " +
                                    "OPEN, {0,0,0}, {1,0,0,0}, {10,0,0}, {1,0,0,0}.";

    /// <summary>The refusal of a comment in a path text.</summary>
    internal const string PathComment = "The path text holds a comment (--). " + PathRule;

    /// <summary>The refusal of a text in quotes or in <c>[[ ]]</c> in a path text.</summary>
    internal const string PathTexts = "The path text holds a text in quotes or in [[ ]]. " + PathRule;

    /// <summary>The refusal of a path text that ends in a table.</summary>
    internal const string PathUnfinished =
        "The path text ends before a table is complete. Add the } at the end of the table.";

    /// <summary>The refusal of a part that a path text cannot hold, for example an operator.</summary>
    internal static string NotPathPart(string part) =>
        $"The path text holds '{part}', which a path text cannot hold. {PathRule}";

    /// <summary>The refusal of a name other than OPEN and CLOSED.</summary>
    internal static string NotPathName(string name) =>
        $"The path text names {name}. A path text can name only {OpenName} and {ClosedName}.";

    /// <summary>The refusal of a part of a table of a path text that is not a number.</summary>
    internal static string NotPathNumber(string part) =>
        $"The path text holds '{part}' in a table. A table of a path text holds only numbers, for example {{1,0,0,0}}.";

    /// <summary>An item of a path text: OPEN, CLOSED, a number, or a table of numbers.</summary>
    private static string? PathItem(ThnLuaTokens tokens)
    {
        if (tokens.Kind == ThnTokenKind.Name)
        {
            if (tokens.Token is not (OpenName or ClosedName))
                return NotPathName(tokens.Token);
            tokens.Next();
            return null;
        }
        if (!tokens.Is('{'))
            return PathNumber(tokens, inTable: false);
        tokens.Next();
        while (!tokens.Is('}'))
        {
            if (PathNumber(tokens, inTable: true) is { } problem)
                return problem;
            if (tokens.Is(','))
                tokens.Next();
            else if (!tokens.Is('}'))
                return PathPart(tokens, inTable: true);
        }
        tokens.Next();
        return null;
    }

    /// <summary>A number with an optional minus sign before it.</summary>
    private static string? PathNumber(ThnLuaTokens tokens, bool inTable)
    {
        if (tokens.Is('-'))
        {
            tokens.Next();
            if (tokens.Kind is ThnTokenKind.Name or ThnTokenKind.Mark)
                return $"The path text holds '-' before '{tokens.Token}'. A minus sign can come only before a number.";
        }
        if (tokens.Kind != ThnTokenKind.Number)
            return PathPart(tokens, inTable);
        tokens.Next();
        return null;
    }

    /// <summary>The refusal of the token where an item, a number or a mark of a path text must be. The text ends
    /// outside a table only after a minus sign.</summary>
    private static string PathPart(ThnLuaTokens tokens, bool inTable) => tokens.Kind switch
    {
        ThnTokenKind.End => inTable ? PathUnfinished : "The path text ends after a minus sign. Add a number after it.",
        ThnTokenKind.Text or ThnTokenKind.Open => PathTexts,
        // A bad escape is in a text. The other bad token is a number that the lexer refuses.
        ThnTokenKind.Bad when tokens.Token[0] is '"' or '\'' => PathTexts,
        ThnTokenKind.Bad => $"The path text holds '{tokens.Token}', which is not a number. {PathRule}",
        _ when inTable => NotPathNumber(tokens.Token),
        ThnTokenKind.Name => NotPathName(tokens.Token),
        _ => NotPathPart(tokens.Token),
    };

    /// <summary>The items of a path text: the text runs as a Lua table, with the names OPEN and CLOSED. A text with a
    /// <see cref="TextProblem"/> does not run.</summary>
    /// <exception cref="FormatException">The text has a problem, it does not run, or it is not a list.</exception>
    private static ThnTable Items(string text, IThnEvaluator evaluator)
    {
        if (TextProblem(text) is { } problem)
            throw new FormatException(problem);
        ThnTable globals;
        try
        {
            var script = $"{OpenName} = 1\n{ClosedName} = 0\n{Global} = {{{text}}}\n";
            globals = evaluator.Evaluate(Encoding.ASCII.GetBytes(script), "path_data");
        }
        catch (Exception e) when (e is ThnEvaluationException or ArgumentException)
        {
            throw new FormatException($"The path text does not run: {e.Message}", e);
        }
        return globals[Global] is ThnTable { Record.IsEmpty: true } path
            ? path
            : throw new FormatException($"A path text starts with {OpenName} or {ClosedName}, then the keys.");
    }

    /// <summary>The path text of keys, with the number format of <see cref="ThnWriter"/>, for example
    /// <c>OPEN, {1,2,3}, {1,0,0,0}, </c>. Each item ends with a comma and a space, as retail writes it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A number is not finite.</exception>
    public static string Write(ThnPathKeys path)
    {
        var text = new StringBuilder(path.Closed ? ClosedName : OpenName).Append(", ");
        foreach (var key in path.Keys)
        {
            Item(text, key.Point.X, key.Point.Y, key.Point.Z);
            if (key.Orientation is { } q)
                Item(text, q.W, q.X, q.Y, q.Z);
        }
        return text.ToString();
    }

    private static void Item(StringBuilder text, params ReadOnlySpan<float> numbers)
    {
        text.Append('{');
        for (var i = 0; i < numbers.Length; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(ThnWriter.Value(new ThnNumber(numbers[i])));
        }
        text.Append("}, ");
    }

    /// <summary>The numbers of an item of a path, at least <paramref name="count"/>.</summary>
    private static float[] Numbers(ThnValue item, int count, int index)
    {
        if (item is not ThnTable { Record.IsEmpty: true } list || list.List.Length < count)
            throw new FormatException($"The item {index + 1} of the path is not a list of {count} numbers.");
        var numbers = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (list.List[i] is not ThnNumber number)
                throw new FormatException($"The item {index + 1} of the path is not a list of {count} numbers.");
            numbers[i] = number.Value;
        }
        return numbers;
    }
}
