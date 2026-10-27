using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Studio.Commands;

/// <summary>The value that the text of a cutscene field gives, or why the text gives no value.</summary>
internal sealed record ThnFieldParse(ThnValue? Value, string? Error)
{
    public static ThnFieldParse Of(ThnValue value) => new(value, null);

    public static ThnFieldParse Fail(string error) => new(null, error);
}

/// <summary>The edit of a field of a cutscene value: the label of its entry in the edit history, and the command that
/// gives the element the value of the field.</summary>
internal sealed record ThnFieldCommand(string Label, Func<ThnValue, EditCommand> Make)
{
    /// <summary>The edit that sets the value at a path of an element. Its label is <c>Set</c> and the path.</summary>
    public static ThnFieldCommand Set(ThnRef target, ThnPath path) =>
        new($"Set {path}", value => ThnEdits.Set(target, path, value));

    /// <summary>The edit of the time of an event: a move, as a drag on the Timeline makes it. The time rounds to 1 ms,
    /// and a time below 0 is an error (see <see cref="ThnEdits.MoveEvents"/>).</summary>
    /// <param name="start">The time of the event at the start of the edit.</param>
    public static ThnFieldCommand Time(ThnRef ev, float start) =>
        new("Move event", value => Move(ev, Change(value, start), 0));

    /// <summary>The edit of the length (<c>props.duration</c>) of an event, as a drag of the end of a bar makes it. The
    /// length rounds to 1 ms, and a length below 0 is an error.</summary>
    /// <param name="start">The length of the event at the start of the edit. An event without a length has the length
    /// 0.</param>
    public static ThnFieldCommand Length(ThnRef ev, float start) =>
        new("Change the length of event", value => Move(ev, 0, Change(value, start)));

    /// <summary>The move of one event. No change gives a command that changes nothing.</summary>
    private static EditCommand Move(ThnRef ev, float time, float length) =>
        time == 0 && length == 0 ? EditCommand.Group("", []) : ThnEdits.MoveEvents([ev], time, length);

    /// <summary>The change from the start value to a number.</summary>
    /// <exception cref="ArgumentException">The value is not a number.</exception>
    private static float Change(ThnValue value, float start) => value is ThnNumber number
        ? (float)((double)number.Value - start)
        : throw new ArgumentException("The value must be a number.", nameof(value));
}

/// <summary>
/// The text of a value of a cutscene in an Inspector field, and the value that the text of a field gives back (section
/// 7.5 of the M8 design). A number has the text of <see cref="ThnWriter"/>, so a field shows the float exactly. A
/// vector has its numbers separated by ", ". A rotation matrix shows as Euler angles in degrees.
/// </summary>
/// <remarks>A text that is not a value of the kind gives the error, and the field keeps the last valid value. A number
/// that is not finite is an error (section 5.2).</remarks>
internal static class ThnFieldEdit
{
    /// <summary>The name of the global that holds the value of a Lua value text.</summary>
    private const string ValueGlobal = "ol_value";

    /// <summary>The text of a value in a field of a kind. Null gives the empty text.</summary>
    public static string Format(ThnKeyKind kind, ThnValue? value) => (kind, value) switch
    {
        (_, null) => "",
        (ThnKeyKind.Number, ThnNumber number) => Number(number.Value),
        (ThnKeyKind.Text or ThnKeyKind.PathData, ThnText text) => text.Value,
        (ThnKeyKind.Vector or ThnKeyKind.Quaternion, ThnTable { Record.IsEmpty: true } list)
            when list.List.All(v => v is ThnNumber) =>
            string.Join(", ", list.List.Select(v => Number(((ThnNumber)v).Value))),
        (ThnKeyKind.Orient, _) when ThnPose.OrientMatrix(value) is { } matrix =>
            string.Join(", ", Components(Degrees(matrix)).Select(Number)),
        _ => ThnWriter.Value(value),
    };

    /// <summary>The value that the text of a field of a kind gives.</summary>
    /// <param name="evaluator">Runs the Lua text of a value that has no field of its own.</param>
    public static ThnFieldParse Parse(ThnKeyKind kind, string text, IThnEvaluator evaluator)
    {
        switch (kind)
        {
            case ThnKeyKind.Number:
                return ParseNumbers(text, 1) is { } one ? Numbers(one, n => new ThnNumber(n[0])) : NotNumbers(text, 1);
            case ThnKeyKind.Text:
            case ThnKeyKind.PathData:
                return ThnFieldParse.Of(new ThnText(text));
            case ThnKeyKind.Vector:
                return ParseNumbers(text, 3) is { } vector
                    ? Numbers(vector, n => ThnTable.FromList([.. n.Select(v => (ThnValue)new ThnNumber(v))]))
                    : NotNumbers(text, 3);
            case ThnKeyKind.Quaternion:
                return ParseNumbers(text, 4) is { } quaternion
                    ? Numbers(quaternion, n => ThnTable.FromList([.. n.Select(v => (ThnValue)new ThnNumber(v))]))
                    : NotNumbers(text, 4);
            case ThnKeyKind.Orient:
                return ParseNumbers(text, 3) is { } degrees
                    ? Numbers(degrees, n => ThnPose.Orient(MathHelper.MatrixFromEulerDegrees(new Vector3(n[0], n[1], n[2]))))
                    : NotNumbers(text, 3);
            default:
                return ParseValue(text, evaluator);
        }
    }

    /// <summary>The value of a checkbox of <c>Y</c> or <c>N</c>. A stored number stays a number, 0 or 1, as the rule of
    /// the flags keeps it (section 5.2).</summary>
    public static ThnValue BoolValue(ThnValue? stored, bool on) =>
        stored is ThnNumber ? new ThnNumber(on ? 1 : 0) : new ThnBool(on);

    /// <summary>The value of a choice of a THN name from a list. A stored number stays a number: the number of the
    /// name.</summary>
    /// <exception cref="KeyNotFoundException">The name is not a THN name.</exception>
    public static ThnValue SymbolValue(ThnValue? stored, string name, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        stored is ThnNumber ? new ThnNumber(symbols[name].Value) : new ThnSymbol(name);

    /// <summary>The targets of an event with one target replaced by a name from the list of the target.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The event has no target at the index.</exception>
    public static ThnTable TargetsValue(ThnTable targets, int index, string name) =>
        new(targets.List.SetItem(index, new ThnText(name)), targets.Record);

    /// <summary>The Euler angles in degrees of a rotation matrix, in the engine form, nearest to 0.</summary>
    public static Vector3 Degrees(Matrix4x4 rotation) => TransformValues.EulerNear(rotation, Vector3.Zero);

    /// <summary>The value of a Lua value text, as the engine reads it, for example <c>{ 1, 2 }</c> or
    /// <c>"text"</c>.</summary>
    public static ThnFieldParse ParseValue(string text, IThnEvaluator evaluator)
    {
        if (text.Trim().Length == 0)
            return ThnFieldParse.Fail("The field needs a value. Use Remove in the menu of the label to remove the key.");
        try
        {
            var globals = evaluator.Evaluate(Encoding.ASCII.GetBytes($"{ValueGlobal} = {text}\n"), "field");
            return globals[ValueGlobal] is { } value
                ? ThnFieldParse.Of(value)
                : ThnFieldParse.Fail("The text gives no value.");
        }
        catch (Exception e) when (e is ThnEvaluationException or ArgumentException)
        {
            return ThnFieldParse.Fail(ErrorText.Of(e));
        }
    }

    /// <summary>The text of a float: the shortest text that gives the same float.</summary>
    public static string Number(float value) => ThnWriter.Value(new ThnNumber(value + 0f));

    private static float[] Components(Vector3 v) => [v.X, v.Y, v.Z];

    /// <summary>The numbers of a text with <paramref name="count"/> numbers separated by commas, or null.</summary>
    private static float[]? ParseNumbers(string text, int count)
    {
        var parts = text.Split(',');
        if (parts.Length != count)
            return null;
        var numbers = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }
        return numbers;
    }

    /// <summary>The value of numbers, or the error of a number that is not finite.</summary>
    private static ThnFieldParse Numbers(float[] numbers, Func<float[], ThnValue> value) =>
        numbers.All(float.IsFinite) ? ThnFieldParse.Of(value(numbers)) : ThnFieldParse.Fail(ThnNumber.NotFiniteMessage);

    private static ThnFieldParse NotNumbers(string text, int count) => ThnFieldParse.Fail(count == 1
        ? $"'{text.Trim()}' is not a number."
        : $"'{text.Trim()}' is not {count} numbers separated by commas.");
}
