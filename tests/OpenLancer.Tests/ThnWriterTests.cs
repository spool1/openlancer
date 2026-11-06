using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The canonical THN writer. Each writer case writes a tree, evaluates the text with the engine VM, compares the result
/// exactly (float bits and key order), and checks that the text is a fixed point. The round trip check itself gets a
/// fake evaluator, which gives a tree that differs.
/// </summary>
public sealed class ThnWriterTests
{
    /// <summary>The tree of the round trip check tests. The engine reads the number 3 as the name CAMERA.</summary>
    private static readonly ThnTable Written = Record(F("x", Record(F("a", N(1.5f)), F("type", N(3)))));

    /// <summary>Writes the value as the global <c>x</c>, and checks the round trip and the fixed point.</summary>
    /// <returns>The text of the script.</returns>
    private static string RoundTrip(ThnValue value)
    {
        var globals = Record(F("x", value));
        Assert.Null(ThnWriter.RoundTripProblem(globals, Evaluator, "test.thn"));
        var text = ThnWriter.Write(globals);
        Assert.Equal(globals, Evaluate(text));
        Assert.Equal(text, ThnWriter.Write(Evaluate(text)));
        return text;
    }

    /// <summary>Floats whose shortest text has 9 digits, small and large exponents, and the extremes. The stock format
    /// keeps about 7 digits, so it changes the first three, and it writes the smallest float as 0.</summary>
    [Theory]
    [InlineData(0x3DCCCCD0, "0.100000024")]
    [InlineData(0x42F6E97C, "123.456024")]
    [InlineData(0x33D6BF97, "1.00000015E-07")]
    [InlineData(0x33D6BF95, "1E-07")]
    [InlineData(0x7F7FC99E, "3.4E+38")]
    [InlineData(0x7F7FFFFF, "3.4028235E+38")]
    [InlineData(unchecked((int)0xFF7FFFFF), "-3.4028235E+38")]
    [InlineData(0x00000001, "1E-45")]
    [InlineData(0x00800000, "1.1754944E-38")]
    [InlineData(unchecked((int)0xC0200000), "-2.5")]
    [InlineData(0x4B800000, "16777216")]
    [InlineData(0x00000000, "0")]
    public void NumbersWriteTheShortestTextThatGivesTheSameFloat(int bits, string expected)
    {
        var number = N(BitConverter.Int32BitsToSingle(bits));

        Assert.Equal(expected, ThnWriter.Value(number));
        Assert.Equal($"x = {expected}\n", RoundTrip(number));
    }

    /// <summary>Quotes, backslashes and newlines are escaped. <c>&amp;</c> stays as it is: the stock path writes
    /// <c>\u0026</c>, which Lua 3.2 reads as <c>u0026</c>. Other control characters write as three digits, so a digit
    /// after them stays a digit.</summary>
    [Theory]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"DATA\SCRIPTS\x.thn", @"""DATA\\SCRIPTS\\x.thn""")]
    [InlineData("two\nlines", "\"two\\nlines\"")]
    [InlineData("Tom & Jerry", "\"Tom & Jerry\"")]
    [InlineData("tab\there\r\u0001\u007F9", "\"tab\\009here\\013\\001\\1279\"")]
    [InlineData("", "\"\"")]
    public void TextsAreEscaped(string text, string expected)
    {
        Assert.Equal(expected, ThnWriter.Value(T(text)));
        Assert.Equal($"x = {expected}\n", RoundTrip(T(text)));
    }

    [Fact]
    public void NamesAndBooleansWriteTheirNames()
    {
        var value = List(S("CAMERA"), S("SET_CAMERA"), S("LIT_DYNAMIC"), S("Y_AXIS"), new ThnBool(true), new ThnBool(false));

        Assert.Equal("{ CAMERA, SET_CAMERA, LIT_DYNAMIC, Y_AXIS, Y, N }", ThnWriter.Value(value));
        RoundTrip(value);
    }

    /// <summary>A number stays a number and a name stays a name, so <c>type = 3</c> does not become
    /// <c>type = CAMERA</c>.</summary>
    [Fact]
    public void ANumberStaysANumberAndANameStaysAName()
    {
        var numbers = Record(F("type", N(3)), F("flags", N(6)));
        var names = Record(F("type", S("CAMERA")), F("flags", S("LIT_DYNAMIC")));

        Assert.Equal("{ type = 3, flags = 6 }", ThnWriter.Value(numbers));
        Assert.Equal("{ type = CAMERA, flags = LIT_DYNAMIC }", ThnWriter.Value(names));
        RoundTrip(List(numbers, names));
    }

    [Fact]
    public void ATableWithBothPartsWritesTheListThenTheRecord()
    {
        var table = new ThnTable([N(1), N(2)], [F("a", N(3)), F("b", List(N(4)))]);

        Assert.Equal("{ 1, 2; a = 3, b = { 4 } }", ThnWriter.Value(table));
        Assert.Equal("x = {\n\t1,\n\t2;\n\ta = 3,\n\tb = { 4 }\n}\n", RoundTrip(table));
        RoundTrip(List(table, table));
    }

    /// <summary>A key that is not a Lua name, or that is a reserved word of Lua 3.2, writes in brackets.</summary>
    [Fact]
    public void KeysThatAreNotNamesWriteInBrackets()
    {
        var value = Record(F("end", N(1)), F("nil", N(2)), F("a b", N(3)), F("1x", N(4)), F("x.y", N(5)), F("", N(6)),
            F("q\"", N(7)), F("_ok9", N(8)), F("ends", N(9)));

        Assert.Equal("{ [\"end\"] = 1, [\"nil\"] = 2, [\"a b\"] = 3, [\"1x\"] = 4, [\"x.y\"] = 5, [\"\"] = 6, " +
                     "[\"q\\\"\"] = 7, _ok9 = 8, ends = 9 }", ThnWriter.Value(value));
        RoundTrip(value);
    }

    /// <summary>Each reserved word of Lua 3.2 (llex.c:30-32) writes in brackets as a key. A global cannot have it as its
    /// name.</summary>
    [Theory]
    [InlineData("and")]
    [InlineData("do")]
    [InlineData("else")]
    [InlineData("elseif")]
    [InlineData("end")]
    [InlineData("function")]
    [InlineData("if")]
    [InlineData("local")]
    [InlineData("nil")]
    [InlineData("not")]
    [InlineData("or")]
    [InlineData("repeat")]
    [InlineData("return")]
    [InlineData("then")]
    [InlineData("until")]
    [InlineData("while")]
    public void AReservedWordWritesInBracketsAsAKey(string word)
    {
        var value = Record(F(word, N(1)));

        Assert.False(ThnWriter.IsName(word));
        Assert.Equal($"{{ [\"{word}\"] = 1 }}", ThnWriter.Value(value));
        RoundTrip(value);
        Assert.Throws<ArgumentException>(() => ThnWriter.Write(value));
    }

    [Fact]
    public void EmptyTablesRoundTrip()
    {
        Assert.Equal("x = {}\n", RoundTrip(ThnTable.Empty));
        Assert.Equal("{ psysprops = {}, events = { {}, { {} } } }",
            ThnWriter.Value(Record(F("psysprops", ThnTable.Empty), F("events", List(ThnTable.Empty, List(ThnTable.Empty))))));
        RoundTrip(Record(F("psysprops", ThnTable.Empty), F("events", List(ThnTable.Empty, List(ThnTable.Empty)))));
    }

    /// <summary>The globals write in their order, and each item of a table global writes on its own line. The text of
    /// the evaluated text is the same text.</summary>
    [Fact]
    public void AScriptWritesOneItemOnEachLineAndIsAFixedPoint()
    {
        const string text =
            "duration = 12.5\n" +
            "entities = {\n" +
            "\t{ entity_name = \"Cam_A\", type = CAMERA, spatialprops = { pos = { 0.1, 0, -2.75 } }, " +
            "cameraprops = { fovh = 30, hvaspect = 1.5 } },\n" +
            "\t{ entity_name = \"Mon\", type = 4, userprops = { Priority = \"1\" } }\n" +
            "}\n" +
            "events = {\n" +
            "\t{ 0, SET_CAMERA, { \"Mon\", \"Cam_A\" } },\n" +
            "\t{ 2.125, START_SPATIAL_PROP_ANIM, { \"Cam_A\", \"Mk_B\" }, { duration = 4.25, " +
            "spatialprops = { q_orient = { 1, 0, 0, 0 } } } }\n" +
            "}\n" +
            "psys = {}\n";

        var globals = Evaluate(text);

        Assert.Equal(["duration", "entities", "events", "psys"], globals.Record.Select(f => f.Key));
        Assert.Equal(text, ThnWriter.Write(globals));
        Assert.Null(ThnWriter.RoundTripProblem(globals, Evaluator, "test.thn"));
    }

    /// <summary>The round trip check gives the written text to the evaluator, compares the result exactly, and names the
    /// first value that differs. A fake evaluator gives each kind of difference.</summary>
    [Fact]
    public void TheRoundTripCheckNamesTheFirstDifferentValue()
    {
        var same = new FixedEvaluator(() => Written);
        Assert.Null(ThnWriter.RoundTripProblem(Written, same, "test.thn"));
        Assert.Equal(ThnWriter.Write(Written), same.Text);
        Assert.Equal("test.thn", same.Name);

        // A number one unit in the last place higher.
        AssertDifference("x.a", Record(F("x", Record(F("a", N(MathF.BitIncrement(1.5f))), F("type", N(3))))));
        // The same keys in another order.
        AssertDifference("x", Record(F("x", Record(F("type", N(3)), F("a", N(1.5f))))));
        // A name in place of the number that the engine reads as the same value.
        AssertDifference("x.type", Record(F("x", Record(F("a", N(1.5f)), F("type", S("CAMERA"))))));
        // One more global.
        AssertDifference("the globals", Record(F("x", Written["x"]!), F("y", N(0))));

        var error = new FixedEvaluator(() => throw new ThnEvaluationException("test.thn", "The script does not compile."));
        Assert.Equal("The written text does not evaluate: test.thn: The script does not compile.",
            ThnWriter.RoundTripProblem(Written, error, "test.thn"));
    }

    /// <summary>Equal trees write the same text, so an exact round trip also gives a fixed point text. The claim needs
    /// the -0 rule: the float -0 equals +0, but its shortest text is <c>-0</c>.</summary>
    [Fact]
    public void EqualTreesWriteTheSameText()
    {
        static ThnTable Tree(float zero, string text) => Record(F("x", N(zero)),
            F("y", List(T(text), S("CAMERA"), new ThnBool(true), Record(F("z", N(0.1f))))));

        var a = Tree(0f, "Cam_A");
        var b = Tree(-0f, new string("Cam_A".AsSpan()));

        Assert.Equal(a, b);
        Assert.Equal(ThnWriter.Write(a), ThnWriter.Write(b));
    }

    [Fact]
    public void TextThatIsNotAsciiIsRefused()
    {
        var e = Assert.Throws<ArgumentException>(() => ThnWriter.Value(T("Café")));
        Assert.Contains("U+00E9", e.Message);
        Assert.Throws<ArgumentException>(() => ThnWriter.Value(Record(F("clé", N(1)))));
        // The VM drops U+0000 at the end of a text (Undump.cs:69).
        Assert.Contains("U+0000", Assert.Throws<ArgumentException>(() => ThnWriter.Value(T("a\0"))).Message);
        // TextProblem gives the reason of the writer without a write.
        Assert.Equal(e.Message, ThnWriter.TextProblem("Café"));
        Assert.Contains("U+0000", ThnWriter.TextProblem("a\0"));
        Assert.Null(ThnWriter.TextProblem("Cafe \"1\"\n\x7F"));
        Assert.Contains("cannot be written", ThnWriter.RoundTripProblem(Record(F("x", T("é"))), Evaluator, "t"));
    }

    [Fact]
    public void GlobalsAndNamesThatLuaCannotReadAreRefused()
    {
        Assert.Throws<ArgumentException>(() => ThnWriter.Write(List(N(1))));
        Assert.Throws<ArgumentException>(() => ThnWriter.Write(Record(F("end", N(1)))));
        Assert.Throws<ArgumentException>(() => ThnWriter.Write(Record(F("a b", N(1)))));
        Assert.Throws<ArgumentException>(() => ThnWriter.Value(S("NOT A NAME")));
    }

    private static void AssertDifference(string place, ThnTable result) =>
        Assert.Equal($"The written text gives a different value at {place}.",
            ThnWriter.RoundTripProblem(Written, new FixedEvaluator(() => result), "test.thn"));

    /// <summary>An evaluator that records the script that it gets and gives a fixed result.</summary>
    private sealed class FixedEvaluator(Func<ThnTable> result) : IThnEvaluator
    {
        public string? Text { get; private set; }

        public string? Name { get; private set; }

        public IReadOnlyDictionary<string, ThnSymbolInfo> Symbols { get; } = new Dictionary<string, ThnSymbolInfo>();

        public ThnTable Evaluate(ReadOnlySpan<byte> script, string name)
        {
            Text = Encoding.ASCII.GetString(script);
            Name = name;
            return result();
        }
    }
}
