using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreLancer;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>What the JSON value of a cutscene key gives (see <see cref="ThnJson.Read"/>): a value, the bits of a flags
/// key, or why the JSON value gives nothing.</summary>
/// <param name="Value">The new value, or null for bits or an error.</param>
/// <param name="Bits">The names of the bits that are on after the edit, for a flags key, else null.</param>
/// <param name="Error">Why the JSON value is not a value of the key, in strict Simplified Technical English, or
/// null.</param>
internal sealed record ThnJsonInput(ThnValue? Value, IReadOnlySet<string>? Bits, string? Error)
{
    public static ThnJsonInput Of(ThnValue value) => new(value, null, null);

    public static ThnJsonInput Fail(string error) => new(null, null, error);
}

/// <summary>
/// The JSON form of the values of cutscene elements, for <c>get_cutscene</c>, <c>get_item</c> and <c>set_fields</c>
/// (section 7.0 of the MCP1 design). The kind of the row of the key in <see cref="ThnSchema"/> gives the form, as it
/// gives the field of the Inspector (see <see cref="ThnFieldEdit"/>). A list is an array of rows, never a text with
/// commas (UI rule 3).
/// </summary>
/// <remarks>
/// <para>The forms: a number, a text, <c>true</c> or <c>false</c>, a vector <c>[x, y, z]</c>, a quaternion
/// <c>[w, x, y, z]</c>, a rotation matrix as Euler angles in degrees <c>[x, y, z]</c>, a THN name of the group of the
/// key, the names of the bits of a flags key, a motion path <c>{closed, keys}</c>, a curve <c>{clsid, points}</c>, and
/// a turn about an axis <c>{degrees, axis}</c>. A key without a row, and a key of the kind
/// <see cref="ThnKeyKind.Value"/>, is Lua text in a JSON text, as the engine reads it.</para>
/// <para>A stored value that does not fit the form of its row is Lua text in a JSON text. A JSON value that is the
/// form of the stored value keeps the stored value. So each value that <see cref="Write"/> gives goes back into
/// <see cref="Read"/> without a change, also a rotation, whose degrees do not give the same floats back.</para>
/// </remarks>
internal static class ThnJson
{
    private const string PointKey = "point";
    private const string OrientationKey = "orientation";

    /// <summary>The JSON form of a value of a key.</summary>
    /// <param name="row">The row of the key, or null when the schema does not know the key.</param>
    public static JsonNode Write(ThnKeyRow? row, ThnValue value, IReadOnlyDictionary<string, ThnSymbolInfo> symbols,
        IThnEvaluator evaluator) =>
        (row?.Kind ?? ThnKeyKind.Value) switch
        {
            ThnKeyKind.Number when value is ThnNumber number => Number(number.Value),
            ThnKeyKind.Text when value is ThnText text => JsonValue.Create(text.Value),
            ThnKeyKind.Bool when value is ThnBool flag => JsonValue.Create(flag.Value),
            ThnKeyKind.Bool when value is ThnNumber number => JsonValue.Create(number.Value != 0),
            ThnKeyKind.Vector or ThnKeyKind.Quaternion when ThnSchema.Fits(row!, value, symbols) =>
                Numbers(((ThnTable)value).List.Select(v => ((ThnNumber)v).Value)),
            ThnKeyKind.Orient when ThnPose.OrientMatrix(value) is { } matrix => Degrees(matrix),
            ThnKeyKind.Symbol when SymbolName(row!, value, symbols) is { } name => JsonValue.Create(name),
            ThnKeyKind.Symbol when value is ThnNumber number => Number(number.Value),
            ThnKeyKind.Flags when BitNames(row!, value, symbols) is { } bits => bits,
            ThnKeyKind.PathData when value is ThnText text =>
                (JsonNode?)PathKeys(text.Value, evaluator) ?? JsonValue.Create(text.Value),
            ThnKeyKind.Curve when value is ThnTable curve && Curve(curve) is { } form => form,
            ThnKeyKind.AxisRotation when ThnSchema.Fits(row!, value, symbols) && Axis((ThnTable)value, symbols) is { } form =>
                form,
            _ => JsonValue.Create(ThnWriter.Value(value)),
        };

    /// <summary>
    /// The value of a key that a JSON value gives. A JSON value that is the form of the stored value gives the stored
    /// value. A stored number of a checkbox or of a THN name stays a number, as the Inspector keeps it (see
    /// <see cref="ThnFieldEdit.BoolValue"/> and <see cref="ThnFieldEdit.SymbolValue"/>). A THN name key also takes the
    /// number of a name of its group, as a choice of that name. Another number refuses, because the Inspector offers
    /// only the names of the group.
    /// </summary>
    /// <param name="row">The row of the key, or null when the schema does not know the key. The caller refuses a
    /// record and the flags of an entity before the read.</param>
    /// <param name="path">The path of the key, for the error.</param>
    /// <param name="json">The JSON value. The caller handles <c>null</c>, which removes the key.</param>
    /// <param name="stored">The value of the key now, or null.</param>
    /// <remarks>A new value must be a value that a save can write. Else the read gives the reason of the writer, for
    /// example for a text with a character outside ASCII. So the error names the key, and the plan of the edit does
    /// not refuse the value later.</remarks>
    public static ThnJsonInput Read(ThnKeyRow? row, string path, JsonNode json, ThnValue? stored,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols, IThnEvaluator evaluator)
    {
        if (stored != null && Same(Write(row, stored, symbols, evaluator), json))
            return ThnJsonInput.Of(stored);
        var input = ReadNew(row, path, json, stored, symbols, evaluator);
        return input.Value is { } value && WriteProblem(value) is { } problem ? ThnJsonInput.Fail(problem) : input;
    }

    /// <summary>Why the writer of a save cannot write a value, or null.</summary>
    private static string? WriteProblem(ThnValue value)
    {
        try
        {
            ThnWriter.Value(value);
            return null;
        }
        catch (ArgumentException e)
        {
            return e.Message;
        }
    }

    /// <summary>The value of a key that a JSON value gives, when the JSON value is not the form of the stored
    /// value.</summary>
    private static ThnJsonInput ReadNew(ThnKeyRow? row, string path, JsonNode json, ThnValue? stored,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols, IThnEvaluator evaluator)
    {
        var kind = row?.Kind ?? ThnKeyKind.Value;
        var bad = ThnJsonInput.Fail($"The value of {path} must be {Expected(row, symbols)}.");
        switch (kind)
        {
            case ThnKeyKind.Number:
                return NumberOf(json) is { } number ? Finite(() => new ThnNumber(number)) : bad;
            case ThnKeyKind.Text:
                return Text(json) is { } text ? ThnJsonInput.Of(new ThnText(text)) : bad;
            case ThnKeyKind.Bool:
                return json.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                    ? ThnJsonInput.Of(ThnFieldEdit.BoolValue(stored, json.GetValue<bool>()))
                    : bad;
            case ThnKeyKind.Vector or ThnKeyKind.Quaternion:
                return NumbersOf(json, kind == ThnKeyKind.Vector ? 3 : 4) is { } list
                    ? Finite(() => ThnTable.FromList([.. list.Select(n => (ThnValue)new ThnNumber(n))]))
                    : bad;
            case ThnKeyKind.Orient:
                return NumbersOf(json, 3) is [var x, var y, var z]
                    ? Finite(() => ThnPose.Orient(MathHelper.MatrixFromEulerDegrees(new Vector3(x, y, z))))
                    : bad;
            case ThnKeyKind.Symbol:
                var name = Text(json) ??
                           (NumberOf(json) is { } symbolNumber ? NameOf(row!.Group, symbolNumber, symbols) : null);
                return name != null && symbols.TryGetValue(name, out var info) && info.Group == row!.Group
                    ? ThnJsonInput.Of(ThnFieldEdit.SymbolValue(stored, name, symbols))
                    : bad;
            case ThnKeyKind.Flags:
                return Bits(row!, path, json) ?? bad;
            case ThnKeyKind.PathData:
                if (Text(json) is { } pathText)
                    return ThnJsonInput.Of(new ThnText(pathText));
                return json is JsonObject keys ? ReadPath(path, keys) : bad;
            case ThnKeyKind.Curve:
                return json is JsonObject curve ? ReadCurve(path, curve, stored) : bad;
            case ThnKeyKind.AxisRotation:
                return json is JsonObject axis ? ReadAxis(path, axis, symbols) : bad;
            default:
                if (Text(json) is not { } lua)
                    return bad;
                var parse = ThnFieldEdit.ParseValue(lua, evaluator);
                return parse.Value is { } value ? ThnJsonInput.Of(value) : ThnJsonInput.Fail(parse.Error!);
        }
    }

    /// <summary>True when two JSON values are equal: the same keys and items, and numbers that give the same float.
    /// The order of the keys of an object does not count.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count &&
                                        x.All(p => y.TryGetPropertyValue(p.Key, out var other) && Same(p.Value, other)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second)),
        (JsonValue x, JsonValue y) => x.GetValueKind() == y.GetValueKind() && x.GetValueKind() switch
        {
            JsonValueKind.Number => NumberOf(x) is { } m && NumberOf(y) is { } n && m.Equals(n),
            JsonValueKind.String => x.GetValue<string>() == y.GetValue<string>(),
            _ => true,
        },
        _ => false,
    };

    /// <summary>The form that a key takes, for an error, for example "a number" or "[x, y, z]".</summary>
    public static string Expected(ThnKeyRow? row, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        (row?.Kind ?? ThnKeyKind.Value) switch
        {
            ThnKeyKind.Number => "a number",
            ThnKeyKind.Text => "a text",
            ThnKeyKind.Bool => "true or false",
            ThnKeyKind.Vector => "three numbers [x, y, z]",
            ThnKeyKind.Quaternion => "four numbers [w, x, y, z]",
            ThnKeyKind.Orient => "three angles in degrees [x, y, z]",
            ThnKeyKind.Symbol => $"one of these names: {string.Join(", ", GroupNames(row!.Group, symbols))}",
            ThnKeyKind.Flags => $"a list of these bit names: {string.Join(", ", row!.Bits.Select(b => b.Name))}",
            ThnKeyKind.PathData => "an object with closed and keys",
            ThnKeyKind.Curve => "an object with clsid and points",
            ThnKeyKind.AxisRotation => "an object with degrees and axis",
            _ => "Lua text in a JSON text, for example \"{ 1, 2 }\"",
        };

    /// <summary>A JSON number of a float: the shortest decimal text that gives the float.</summary>
    private static JsonValue Number(float value) =>
        JsonValue.Create(double.Parse(ThnFieldEdit.Number(value), CultureInfo.InvariantCulture));

    private static JsonArray Numbers(IEnumerable<float> values) => new([.. values.Select(v => (JsonNode)Number(v))]);

    private static JsonArray Degrees(Matrix4x4 matrix)
    {
        var degrees = ThnFieldEdit.Degrees(matrix);
        return Numbers([degrees.X, degrees.Y, degrees.Z]);
    }

    /// <summary>The float of a JSON number, or null for another value. A number above the range of a float gives an
    /// infinity, and the caller refuses it.</summary>
    private static float? NumberOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out double number)
            ? (float)number
            : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>The floats of a JSON array of exactly <paramref name="count"/> numbers, or null.</summary>
    private static float[]? NumbersOf(JsonNode? node, int count)
    {
        if (node is not JsonArray array || array.Count != count)
            return null;
        var numbers = new float[count];
        for (var i = 0; i < count; i++)
        {
            if (NumberOf(array[i]) is not { } number)
                return null;
            numbers[i] = number;
        }
        return numbers;
    }

    /// <summary>A value of numbers, or the error of a number that is not finite.</summary>
    private static ThnJsonInput Finite(Func<ThnValue> make)
    {
        try
        {
            return ThnJsonInput.Of(make());
        }
        catch (ArgumentOutOfRangeException)
        {
            return ThnJsonInput.Fail(ThnNumber.NotFiniteMessage);
        }
    }

    /// <summary>The THN names of a group, in the order of their numbers.</summary>
    private static IEnumerable<string> GroupNames(string? group, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        symbols.Where(s => s.Value.Group == group).OrderBy(s => s.Value.Value).Select(s => s.Key);

    /// <summary>The THN name of a group that has a number, or null when no name of the group has it.</summary>
    private static string? NameOf(string? group, float number, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        number == MathF.Floor(number)
            ? symbols.FirstOrDefault(s => s.Value.Group == group && s.Value.Value == number).Key
            : null;

    /// <summary>The THN name of a stored value of a symbol key: the name, or the name of the number in the group of
    /// the key. Null for another value.</summary>
    private static string? SymbolName(ThnKeyRow row, ThnValue value, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        value switch
        {
            ThnSymbol symbol when symbols.TryGetValue(symbol.Name, out var info) && info.Group == row.Group => symbol.Name,
            ThnNumber number => symbols.FirstOrDefault(s => s.Value.Group == row.Group && s.Value.Value == (long)number.Value)
                .Key,
            _ => null,
        };

    /// <summary>The names of the bits of a flags key that are on, in the order of the row, or null when the engine
    /// cannot read the value as bits.</summary>
    private static JsonArray? BitNames(ThnKeyRow row, ThnValue value, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        try
        {
            var on = ThnFlags.BitsOn(value, row, symbols);
            return new JsonArray([.. row.Bits.Where(b => on.Contains(b.Name)).Select(b => (JsonNode)JsonValue.Create(b.Name))]);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The bits of a list of bit names, or the number of a whole number, or null for another value. A name
    /// that is not a bit of the row gives the error.</summary>
    private static ThnJsonInput? Bits(ThnKeyRow row, string path, JsonNode json)
    {
        if (NumberOf(json) is { } number)
        {
            return number >= 0 && number == MathF.Floor(number) && number <= int.MaxValue
                ? ThnJsonInput.Of(new ThnNumber(number))
                : null;
        }
        if (json is not JsonArray list || list.Any(n => Text(n) == null))
            return null;
        var names = list.Select(n => Text(n)!).ToList();
        if (names.FirstOrDefault(n => row.Bits.All(b => b.Name != n)) is { } other)
        {
            return ThnJsonInput.Fail($"'{other}' is not a bit of {path}. The bits are " +
                                     $"{string.Join(", ", row.Bits.Select(b => b.Name))}.");
        }
        return new ThnJsonInput(null, names.ToFrozenSet(StringComparer.Ordinal), null);
    }

    /// <summary>The form of a motion path text, or null when the engine cannot read the text as a path.</summary>
    private static JsonObject? PathKeys(string text, IThnEvaluator evaluator)
    {
        ThnPathKeys keys;
        try
        {
            keys = ThnMotionPath.Parse(text, evaluator);
        }
        catch (FormatException)
        {
            return null;
        }
        return new JsonObject
        {
            ["closed"] = keys.Closed,
            ["keys"] = new JsonArray([.. keys.Keys.Select(k =>
            {
                var row = new JsonObject { [PointKey] = Numbers([k.Point.X, k.Point.Y, k.Point.Z]) };
                if (k.Orientation is { } q)
                    row[OrientationKey] = Numbers([q.W, q.X, q.Y, q.Z]);
                return (JsonNode)row;
            })]),
        };
    }

    /// <summary>The path text of the form of a motion path. Each key has a point, and all keys or no key have an
    /// orientation.</summary>
    private static ThnJsonInput ReadPath(string path, JsonObject form)
    {
        var bad = ThnJsonInput.Fail($"The value of {path} must be {{\"closed\": true or false, \"keys\": [{{\"point\": " +
                                    "[x, y, z], \"orientation\": [w, x, y, z]}]}. All keys have an orientation, or no key has one.");
        if (Keys(form, ["closed", "keys"], ["closed", "keys"]) is false ||
            form["closed"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False) ||
            form["keys"] is not JsonArray rows)
            return bad;
        var keys = ImmutableArray.CreateBuilder<ThnPathKey>();
        bool? oriented = null;
        foreach (var item in rows)
        {
            if (item is not JsonObject row || Keys(row, [PointKey], [PointKey, OrientationKey]) is false ||
                NumbersOf(row[PointKey], 3) is not [var x, var y, var z])
                return bad;
            Quaternion? orientation = null;
            if (row.ContainsKey(OrientationKey))
            {
                if (NumbersOf(row[OrientationKey], 4) is not [var w, var qx, var qy, var qz])
                    return bad;
                orientation = new Quaternion(qx, qy, qz, w);
            }
            if ((oriented ??= orientation != null) != (orientation != null))
                return bad;
            keys.Add(new ThnPathKey(new Vector3(x, y, z), orientation));
        }
        try
        {
            return ThnJsonInput.Of(new ThnText(ThnMotionPath.Write(new ThnPathKeys(form["closed"]!.GetValue<bool>(),
                keys.ToImmutable()))));
        }
        catch (ArgumentOutOfRangeException)
        {
            return ThnJsonInput.Fail(ThnNumber.NotFiniteMessage);
        }
    }

    /// <summary>The form of a curve: its CLSID and its points. Null for a curve with other keys, a CLSID that is not
    /// a text, or points that are not lists of four numbers.</summary>
    private static JsonObject? Curve(ThnTable curve)
    {
        if (!curve.IsRecord || curve.Record.Length != 2 || curve[ThnCurve.TypeKey] is not ThnText type ||
            ThnCurve.Points(curve) is not { } points)
            return null;
        return new JsonObject
        {
            ["clsid"] = type.Value,
            ["points"] = new JsonArray([.. points.Select(p => (JsonNode)new JsonObject
            {
                ["time"] = Number(p.Time), ["value"] = Number(p.Value), ["in"] = Number(p.In), ["out"] = Number(p.Out),
            })]),
        };
    }

    /// <summary>The curve record of the form of a curve. A stored curve keeps the place of its keys.</summary>
    private static ThnJsonInput ReadCurve(string path, JsonObject form, ThnValue? stored)
    {
        var bad = ThnJsonInput.Fail($"The value of {path} must be {{\"clsid\": a text, \"points\": [{{\"time\", \"value\", " +
                                    "\"in\", \"out\"}]} with a number for each part of a point.");
        if (Keys(form, ["clsid", "points"], ["clsid", "points"]) is false || Text(form["clsid"]) is not { } type ||
            form["points"] is not JsonArray rows)
            return bad;
        var points = new List<ThnValue>();
        foreach (var item in rows)
        {
            string[] parts = ["time", "value", "in", "out"];
            if (item is not JsonObject row || Keys(row, parts, parts) is false)
                return bad;
            var numbers = parts.Select(p => NumberOf(row[p])).ToList();
            if (numbers.Any(n => n == null))
                return bad;
            var point = Finite(() => ThnTable.FromList([.. numbers.Select(n => (ThnValue)new ThnNumber(n!.Value))]));
            if (point.Value == null)
                return point;
            points.Add(point.Value);
        }
        var list = new ThnTable([.. points], []);
        var fields = stored is ThnTable { IsRecord: true } curve ? curve.Record : [];
        fields = Put(Put(fields, ThnCurve.TypeKey, new ThnText(type)), ThnCurve.PointsKey, list);
        return ThnJsonInput.Of(new ThnTable([], fields));
    }

    /// <summary>The fields of a record with the value of a key: a new value of a key keeps its place, and a new key
    /// goes at the end.</summary>
    private static ImmutableArray<ThnField> Put(ImmutableArray<ThnField> fields, string key, ThnValue value)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Key == key)
                return fields.SetItem(i, new ThnField(key, value));
        }
        return fields.Add(new ThnField(key, value));
    }

    /// <summary>The form of a turn about an axis, or null when its axis is not a name of the axis group or three
    /// numbers.</summary>
    private static JsonObject? Axis(ThnTable turn, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var form = new JsonObject { ["degrees"] = Number(((ThnNumber)turn.List[0]).Value) };
        if (turn.List.Length < 2)
            return form;
        switch (turn.List[1])
        {
            case ThnSymbol name when symbols.TryGetValue(name.Name, out var info) && info.Group == ThnSchema.AxisGroup:
                form["axis"] = name.Name;
                return form;
            case var axis when ThnPose.Vector(axis) is { } vector:
                form["axis"] = Numbers([vector.X, vector.Y, vector.Z]);
                return form;
            default:
                return null;
        }
    }

    /// <summary>The list <c>{ degrees, AXIS }</c> of the form of a turn. Without an axis, the list has only the
    /// degrees.</summary>
    private static ThnJsonInput ReadAxis(string path, JsonObject form, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var bad = ThnJsonInput.Fail($"The value of {path} must be {{\"degrees\": a number, \"axis\": one of " +
                                    $"{string.Join(", ", GroupNames(ThnSchema.AxisGroup, symbols))} or [x, y, z]}}.");
        if (Keys(form, ["degrees"], ["degrees", "axis"]) is false || NumberOf(form["degrees"]) is not { } degrees)
            return bad;
        ThnValue? axis = null;
        if (form.ContainsKey("axis"))
        {
            if (Text(form["axis"]) is { } name)
            {
                if (!symbols.TryGetValue(name, out var info) || info.Group != ThnSchema.AxisGroup)
                    return bad;
                axis = new ThnSymbol(name);
            }
            else if (NumbersOf(form["axis"], 3) is [var x, var y, var z])
            {
                var vector = Finite(() => ThnPose.Vector(new Vector3(x, y, z)));
                if (vector.Value == null)
                    return vector;
                axis = vector.Value;
            }
            else
            {
                return bad;
            }
        }
        return Finite(() => axis == null
            ? ThnTable.FromList(new ThnNumber(degrees))
            : ThnTable.FromList(new ThnNumber(degrees), axis));
    }

    /// <summary>True when an object has each necessary key and no key that the form does not name.</summary>
    private static bool Keys(JsonObject form, IReadOnlyList<string> required, IReadOnlyList<string> allowed) =>
        required.All(form.ContainsKey) && form.All(p => allowed.Contains(p.Key) && p.Value != null);
}
