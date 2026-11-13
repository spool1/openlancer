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
    /// A path text that holds more than numbers, tables of numbers, OPEN and CLOSED does not run (section 8.7, item 2 of
    /// the MCP1 design). The first text closes the table and assigns the path again with an operator: the evaluator
    /// would give the point 5. The check of the items refuses each text before the evaluator runs it, with the reason.
    /// </summary>
    [Theory]
    [InlineData("OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0} } ol_path = { OPEN, {2+3,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}",
        "The text holds '}', which a value cannot hold.")]
    [InlineData("OPEN, {x,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}", "The path text names x. A path text can name only OPEN and CLOSED.")]
    [InlineData("OPEN, {0,0,0}, {1,0,0,0}, {1,1,1}, {1,0,0,0}, f()", "The path text names f.")]
    [InlineData("OPEN, [[\n$if nil\n]], {1,1,1}", ThnValueText.Pragma)]
    [InlineData("OPEN, {0,0,0", ThnValueText.Unfinished)]
    public void APathTextWithMoreThanValuesDoesNotRun(string text, string expected)
    {
        var evaluator = new CountingEvaluator();

        var error = Assert.Throws<FormatException>(() => ThnMotionPath.Parse(text, evaluator));

        Assert.StartsWith(expected, error.Message);
        Assert.Equal(error.Message, ThnMotionPath.TextProblem(text));
        Assert.Equal(0, evaluator.Runs);
        Assert.Null(ThnMotionPath.TextProblem(Retail));
    }

    /// <summary>A MOTION_PATH entity of the engine with a path text.</summary>
    private static ThnEntity Entity(string pathData)
    {
        var text = $"duration = 1\nentities = {{ {{ entity_name = \"P\", type = MOTION_PATH, pathprops = {{ path_data = \"{pathData}\" }} }} }}\nevents = {{}}\n";
        return new ThnScript(Encoding.ASCII.GetBytes(text), _ => null!, "path.thn").Entities["P"];
    }
}
