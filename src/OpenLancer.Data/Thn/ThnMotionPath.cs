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

    /// <summary>The path of the text in a MOTION_PATH entity.</summary>
    public static readonly ThnPath Path = new("pathprops.path_data");

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
    /// Why the evaluator must not read a path text, or null when it can (section 8.7, item 2 of the MCP1 design). The
    /// text must be the items of a table: numbers, tables of numbers and the names OPEN and CLOSED. The evaluator would
    /// also run a statement, a call or an operator (see <see cref="ThnValueText"/>). A text without a problem can still
    /// be a text that the engine does not read as a path (see <see cref="Problem"/>).
    /// </summary>
    internal static string? TextProblem(string text) =>
        ThnValueText.ItemsProblem(text, name => name is OpenName or ClosedName
            ? null
            : $"The path text names {name}. A path text can name only {OpenName} and {ClosedName}.");

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
