using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The JSON form of the cutscene values of <c>get_cutscene</c> and <c>set_fields</c> (see <see cref="ThnJson"/>): the
/// rules of the stored value, the compare of two forms, and the refusal of a form with a key that it does not name.
/// </summary>
public sealed class ThnJsonTests
{
    private static ThnKeyRow Row(ThnElementKind kind, string? type, string path) =>
        ThnSchema.Find(kind, type, new ThnPath(path))!;

    private static ThnJsonInput Read(ThnKeyRow? row, string json, ThnValue? stored) =>
        ThnJson.Read(row, row?.Path.Text ?? "x", JsonNode.Parse(json)!, stored, Evaluator.Symbols, Evaluator);

    /// <summary>A stored number of a checkbox or of a THN name stays a number, as the Inspector keeps it.</summary>
    [Fact]
    public void AStoredNumberOfACheckboxOrANameStaysANumber()
    {
        var on = Row(ThnElementKind.Entity, "LIGHT", "lightprops.on");
        var type = Row(ThnElementKind.Entity, "LIGHT", "lightprops.type");
        var direct = Evaluator.Symbols["L_DIRECT"].Value;

        Assert.Equal(new ThnNumber(0), Read(on, "false", new ThnNumber(1)).Value);
        Assert.Equal(new ThnBool(false), Read(on, "false", null).Value);
        Assert.Equal(new ThnNumber(direct), Read(type, "\"L_DIRECT\"", new ThnNumber(9)).Value);
        Assert.Equal(new ThnSymbol("L_DIRECT"), Read(type, "\"L_DIRECT\"", new ThnSymbol("L_POINT")).Value);
        Assert.Equal("\"L_DIRECT\"", ThnJson.Write(type, new ThnNumber(direct), Evaluator.Symbols, Evaluator).ToJsonString());
    }

    /// <summary>The form of the stored value gives the stored value, also when the form does not give the same floats
    /// back. A stored value that does not fit its row is Lua text.</summary>
    [Fact]
    public void TheFormOfTheStoredValueGivesTheStoredValue()
    {
        var orient = Row(ThnElementKind.Entity, "CAMERA", "spatialprops.orient");
        var stored = ThnPose.Orient(System.Numerics.Quaternion.CreateFromYawPitchRoll(0.3f, 0.2f, 0.1f));
        var form = ThnJson.Write(orient, stored, Evaluator.Symbols, Evaluator);
        var fovh = Row(ThnElementKind.Entity, "CAMERA", "cameraprops.fovh");
        var text = new ThnText("wide");

        Assert.Same(stored, Read(orient, form.ToJsonString(), stored).Value);
        Assert.Equal("\"\\u0022wide\\u0022\"", ThnJson.Write(fovh, text, Evaluator.Symbols, Evaluator).ToJsonString());
        Assert.Same(text, Read(fovh, "\"\\\"wide\\\"\"", text).Value);
        Assert.NotNull(Read(fovh, "\"other\"", text).Error);
    }

    /// <summary>Two forms are the same when their keys and items are the same and their numbers give the same float.
    /// The order of the keys does not count.</summary>
    [Fact]
    public void TwoFormsCompareByKeysItemsAndFloats()
    {
        Assert.True(ThnJson.Same(JsonNode.Parse("""{ "a": 1.0, "b": [0.1] }"""), JsonNode.Parse("""{ "b": [0.100000001], "a": 1 }""")));
        Assert.False(ThnJson.Same(JsonNode.Parse("""{ "a": 1 }"""), JsonNode.Parse("""{ "a": 1, "b": 2 }""")));
        Assert.False(ThnJson.Same(JsonNode.Parse("[1, 2]"), JsonNode.Parse("[2, 1]")));
        Assert.False(ThnJson.Same(JsonNode.Parse("\"1\""), JsonNode.Parse("1")));
    }

    /// <summary>A curve keeps the other keys of the stored curve in their places, a turn can leave out its axis, and
    /// the bits of a flags key take a whole number too. A form with a key that it does not name refuses.</summary>
    [Fact]
    public void TheObjectFormsKeepTheStoredKeysAndRefuseOtherKeys()
    {
        var curve = Row(ThnElementKind.Event, "START_PSYS", "props.param_curve");
        var stored = ThnTable.FromRecord(new ThnField("CLSID", new ThnText("A")), new ThnField("note", new ThnNumber(1)),
            new ThnField("points", ThnTable.Empty));
        var axis = Row(ThnElementKind.Event, "START_SPATIAL_PROP_ANIM", "props.spatialprops.axisrot");
        var flags = Row(ThnElementKind.Event, "START_MOTION", "props.event_flags");

        var changed = (ThnTable)Read(curve, """{ "clsid": "B", "points": [ { "time": 0, "value": 1, "in": 0, "out": 0 } ] }""", stored).Value!;

        Assert.Equal(["CLSID", "note", "points"], changed.Record.Select(f => f.Key));
        Assert.Equal(new ThnText("B"), changed["CLSID"]);
        Assert.Equal(ThnTable.FromList(new ThnNumber(30)), Read(axis, """{ "degrees": 30 }""", null).Value);
        Assert.Equal(new ThnNumber(130), Read(flags, "130", null).Value);
        Assert.Equal(["Loop"], Read(flags, """["Loop"]""", null).Bits!);
        Assert.NotNull(Read(axis, """{ "degrees": 30, "speed": 1 }""", null).Error);
        Assert.NotNull(Read(flags, "1.5", null).Error);
        Assert.NotNull(Read(curve, """{ "points": [] }""", stored).Error);
    }
}
