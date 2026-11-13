using System;
using System.Numerics;
using System.Text;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The path keys of a MOTION_PATH (section 7.5 of the M8 design): the editor reads <c>path_data</c> with the evaluator,
/// as the engine reads it, and writes an edited path with the number format of the writer.
/// </summary>
public sealed class ThnMotionPathTests
{
    /// <summary>The text of a retail path: OPEN, then a point and an orientation <c>{ w, x, y, z }</c> for each key, with
    /// a comma at the end.</summary>
    private const string Retail = "OPEN, {-0.088292,-0.069038,0.000021}, {0.999109,0.042209,0.000000,0.000000}, " +
                                  "{-0.000003,0.143674,0.000004}, {1.000000,0.000000,0.000000,0.000000}, ";

    [Fact]
    public void AResultOfTheEvaluatorGivesTheKeysAsTheEngineReadsThem()
    {
        var keys = ThnMotionPath.Parse(Retail, Evaluator);

        Assert.False(keys.Closed);
        Assert.True(keys.HasOrientation);
        Assert.Equal(2, keys.Keys.Length);
        Assert.Equal(new Vector3(-0.088292f, -0.069038f, 0.000021f), keys.Keys[0].Point);
        Assert.Equal(new Quaternion(0.042209f, 0, 0, 0.999109f), keys.Keys[0].Orientation);
        // The engine reads the same points from the same text (MotionPath.cs:54-85).
        var path = Entity(Retail).Path!;
        Assert.Equal(keys.Keys[0].Point, path.GetPosition(0));
        Assert.Equal(keys.Keys[1].Point, path.GetPosition(1));
    }

    /// <summary>An edited path has the number format of the writer, and the text gives the same keys back. The engine
    /// reads it.</summary>
    [Fact]
    public void AWrittenPathGivesTheSameKeysBack()
    {
        var keys = ThnMotionPath.Parse(Retail, Evaluator);
        var moved = keys.With(1, keys.Keys[1] with { Point = new Vector3(1.5f, -0f, 0.1f) });

        var text = ThnMotionPath.Write(moved);

        Assert.StartsWith("OPEN, {-0.088292,-0.069038,", text);
        Assert.EndsWith("}, {0.999109,0.042209,0,0}, {1.5,0,0.1}, {1,0,0,0}, ", text);
        Assert.Equal(moved.Keys, ThnMotionPath.Parse(text, Evaluator).Keys);
        Assert.Equal(new Vector3(1.5f, 0, 0.1f), Entity(text).Path!.GetPosition(1));
    }

    [Fact]
    public void APathWithoutOrientationsAndAClosedPathKeepTheirForm()
    {
        var plain = ThnMotionPath.Parse("CLOSED, {0,0,0}, {0,0,4}, {1,2,3}, ", Evaluator);

        Assert.True(plain.Closed);
        Assert.False(plain.HasOrientation);
        Assert.Equal(3, plain.Keys.Length);
        Assert.Null(plain.Keys[2].Orientation);
        Assert.Equal("CLOSED, {0,0,0}, {0,0,4}, {1,2,3}, ", ThnMotionPath.Write(plain));
    }

    [Theory]
    [InlineData("{0,0,0}, {1,1,1}, ")]
    [InlineData("OPEN, {0,0}, ")]
    [InlineData("OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, ")]
    [InlineData("OPEN, {0,0,0,")]
    public void ATextThatIsNotAPathIsAFormatException(string text) =>
        Assert.Throws<FormatException>(() => ThnMotionPath.Parse(text, Evaluator));

    /// <summary>
    /// A path text that holds more than OPEN, CLOSED, numbers and tables of numbers does not run (section 8.7, item 2 of
    /// the MCP1 design). The first text closes the table and assigns the path again with an operator: the evaluator
    /// would give the point 5. The check refuses each text before the evaluator runs it, with a reason about paths. A
    /// text, a table in a table, a field, a comment and a second minus sign refuse too, also when the VM could read
    /// them as values.
    /// </summary>
    [Theory]
    [InlineData("OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0} } ol_path = { OPEN, {2+3,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}",
        "The path text holds '}', which a path text cannot hold. A path text holds OPEN or CLOSED, then tables of numbers")]
    [InlineData("OPEN, {0,0,0}, {2+3,0,0}", "The path text holds '+' in a table. A table of a path text holds only numbers")]
    [InlineData("OPEN, {x,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}", "The path text holds 'x' in a table.")]
    [InlineData("OPEN, {OPEN,0,0}", "The path text holds 'OPEN' in a table.")]
    [InlineData("OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}, f()", "The path text names f. A path text can name only OPEN and CLOSED.")]
    [InlineData("OPEN, \"a\", {1,1,1}, {1,0,0,0}", ThnMotionPath.PathTexts)]
    [InlineData("OPEN, [[\n$if nil\n]], {1,1,1}", ThnMotionPath.PathTexts)]
    [InlineData("OPEN, \"a\\200\", {1,1,1}", ThnMotionPath.PathTexts)]
    [InlineData("OPEN, \"a", ThnMotionPath.PathTexts)]
    [InlineData("OPEN, {{1,2,3}}", "The path text holds '{' in a table.")]
    [InlineData("OPEN, {x = 1}", "The path text holds 'x' in a table.")]
    [InlineData("OPEN, {[1] = 1}", "The path text holds '[' in a table.")]
    [InlineData("OPEN; {0,0,0}", "The path text holds ';', which a path text cannot hold.")]
    [InlineData("OPEN,, {0,0,0}", "The path text holds ',', which a path text cannot hold.")]
    [InlineData("OPEN, {0,0,0} -- x", ThnMotionPath.PathComment)]
    [InlineData("OPEN, {0,0,0}\n$debug\n", "The path text holds '$', which a path text cannot hold.")]
    [InlineData("OPEN, {- -1,0,0}", "The path text holds '-' before '-'. A minus sign can come only before a number.")]
    [InlineData("OPEN, -OPEN", "The path text holds '-' before 'OPEN'.")]
    [InlineData("OPEN, -", "The path text ends after a minus sign.")]
    [InlineData("OPEN, {1..2,0,0}", "The path text holds '1..', which is not a number.")]
    [InlineData("OPEN, {0,0,0", ThnMotionPath.PathUnfinished)]
    [InlineData("OPEN, {0,0,0}, {1,0,0,0}, \u00fc", "The text \"OPEN, {0,0,0}, {1,0,0,0}, \u00fc\" has the character U+00FC.")]
    public void APathTextWithMoreThanValuesDoesNotRun(string text, string expected)
    {
        var evaluator = new CountingEvaluator();

        var error = Assert.Throws<FormatException>(() => ThnMotionPath.Parse(text, evaluator));

        Assert.StartsWith(expected, error.Message);
        Assert.Equal(error.Message, ThnMotionPath.TextProblem(text));
        // A path cannot name L_POINT, so the hint of a value text does not apply.
        Assert.DoesNotContain("L_POINT", error.Message);
        Assert.Equal(0, evaluator.Runs);
    }

    /// <summary>A path text holds OPEN or CLOSED, numbers with an optional minus sign and tables of numbers, with a
    /// comma between two items and an optional comma at the end. Each such text passes the check, also a retail path
    /// and a path of the writer with exponents.</summary>
    [Theory]
    [InlineData(Retail)]
    [InlineData("CLOSED, {0,0,0}, {0,0,4}, {1,2,3}, ")]
    [InlineData("OPEN, { - 1, 2e-5, .5, 1., 1E+3 }, {1,0,0,0}")]
    [InlineData("OPEN,\r\n{1,2,3},\n\t{1,0,0,0},")]
    [InlineData("OPEN, {}, {1,}")]
    [InlineData("")]
    public void APathTextOfNumbersPassesTheCheck(string text) => Assert.Null(ThnMotionPath.TextProblem(text));

    [Fact]
    public void APathOfTheWriterPassesTheCheck()
    {
        var keys = new ThnPathKeys(false, [new ThnPathKey(new Vector3(1e-10f, -3e38f, 0.1f), Quaternion.Identity),
            new ThnPathKey(new Vector3(float.MaxValue, float.Epsilon, -0f), new Quaternion(0.5f, -0.5f, 0.5f, -0.5f))]);

        var text = ThnMotionPath.Write(keys);

        Assert.Null(ThnMotionPath.TextProblem(text));
        Assert.Equal(keys.Keys, ThnMotionPath.Parse(text, Evaluator).Keys);
    }

    /// <summary>
    /// The engine reads the path text of an entity of each type and runs it as Lua (ThnScript.cs:476-484). So the check
    /// of a script for a build reads each entity, also an entity with a text key in <c>entities</c>, and names the
    /// entity of the first path text with a problem. A script of paths passes.
    /// </summary>
    [Fact]
    public void TheCheckOfAScriptNamesTheEntityOfABadPathText()
    {
        const string bad = "OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0} } ol_hit = 1 ol_path = { OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}";
        static ThnTable Globals(string entities) => Evaluate($"duration = 1\nentities = {{ {entities} }}\nevents = {{}}\n");
        var good = $$"""{ entity_name = "P", type = MOTION_PATH, pathprops = { path_data = "{{Retail}}" } }""";

        var marker = ThnMotionPath.ScriptProblem(Globals(
            $$"""{{good}}, { entity_name = "Mk", type = MARKER, pathprops = { path_data = "{{bad}}" } }"""));
        var keyed = ThnMotionPath.ScriptProblem(Globals(
            $$"""{{good}}; cam = { entity_name = "Cam", type = CAMERA, pathprops = { path_data = "{{bad}}" } }"""));
        var unnamed = ThnMotionPath.ScriptProblem(Globals($$"""{ type = MARKER, pathprops = { path_data = "{{bad}}" } }"""));

        Assert.Equal("The entity Mk has a pathprops.path_data text that is not only a path. The engine would run it as a " +
                     $"script. {ThnMotionPath.NotPathPart("}")}", marker);
        Assert.StartsWith("The entity Cam has a pathprops.path_data text", keyed);
        Assert.StartsWith("An entity without a name has a pathprops.path_data text", unnamed);
        // The engine reads path_data from a pathprops table with list items too, before or after the key.
        Assert.StartsWith("The entity L1 has a pathprops.path_data text", ThnMotionPath.ScriptProblem(Globals(
            $$"""{{good}}, { entity_name = "L1", type = MARKER, pathprops = { 1; path_data = "{{bad}}" } }""")));
        Assert.StartsWith("The entity L2 has a pathprops.path_data text", ThnMotionPath.ScriptProblem(Globals(
            $$"""{{good}}, { entity_name = "L2", type = CAMERA, pathprops = { path_data = "{{bad}}"; 0 } }""")));
        Assert.Null(ThnMotionPath.ScriptProblem(Globals($$"""{{good}}, { entity_name = "N", type = MARKER, pathprops = { path_data = 1 } }""")));
        Assert.Null(ThnMotionPath.ScriptProblem(Evaluate("duration = 1\n")));
    }

    /// <summary>
    /// The editor reads the path text of an entity as the engine reads it: from the record part of the
    /// <c>pathprops</c> table, also when the table has list items. A path of <see cref="ThnPath"/> does not go into a
    /// table with list items. So each check reads the text with <see cref="ThnMotionPath.DataOf"/>.
    /// </summary>
    [Theory]
    [InlineData("pathprops = { path_data = \"@\" }", true)]
    [InlineData("pathprops = { 1; path_data = \"@\" }", true)]
    [InlineData("pathprops = { path_data = \"@\"; 0 }", true)]
    [InlineData("pathprops = { 1, 2 }", false)]
    [InlineData("pathprops = {}", false)]
    [InlineData("", false)]
    public void ThePathTextOfAnEntityIsTheTextThatTheEngineReads(string pathprops, bool hasPath)
    {
        pathprops = pathprops.Replace("@", Retail, StringComparison.Ordinal);
        var script = $"duration = 1\nentities = {{ {{ entity_name = \"Mk\", type = MARKER, {pathprops} }} }}\nevents = {{}}\n";

        var entity = Assert.IsType<ThnTable>(Assert.IsType<ThnTable>(Evaluate(script)["entities"]).List[0]);
        var engine = new ThnScript(Encoding.ASCII.GetBytes(script), _ => null!, "path.thn").Entities["Mk"];

        Assert.Equal(hasPath, engine.Path != null);
        Assert.Equal(hasPath ? new ThnText(Retail) : null, ThnMotionPath.DataOf(entity));
        if (hasPath && pathprops.Contains(';', StringComparison.Ordinal))
            Assert.Null(ThnMotionPath.Path.ValueIn(ThnElementKind.Entity, entity));
    }

    /// <summary>A MOTION_PATH entity of the engine with a path text.</summary>
    private static ThnEntity Entity(string pathData)
    {
        var text = $"duration = 1\nentities = {{ {{ entity_name = \"P\", type = MOTION_PATH, pathprops = {{ path_data = \"{pathData}\" }} }} }}\nevents = {{}}\n";
        return new ThnScript(Encoding.ASCII.GetBytes(text), _ => null!, "path.thn").Entities["P"];
    }
}
