using System;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The text of a refused value for a user (decision D130, test 1 of section 8.1 of the M8b design): the message of an
/// exception without the parameter text that .NET adds.
/// </summary>
public sealed class ErrorTextTests
{
    /// <summary>Pins the text that .NET adds. A runtime update that changes it fails this test, not the user.</summary>
    [Fact]
    public void DotNetAddsTheParameterTextToTheMessage()
    {
        Assert.Equal("The value is wrong. (Parameter 'value')",
            new ArgumentException("The value is wrong.", "value").Message);
        Assert.Equal("The value is too big. (Parameter 'size')" + Environment.NewLine + "Actual value was 12.",
            new ArgumentOutOfRangeException("size", 12, "The value is too big.").Message);
    }

    [Fact]
    public void AnArgumentExceptionGivesItsMessageWithoutTheParameterText()
    {
        Assert.Equal("The value is wrong.", ErrorText.Of(new ArgumentException("The value is wrong.", "value")));
        Assert.Equal("The value is wrong.", ErrorText.Of(new ArgumentException("The value is wrong.")));
        Assert.Equal("Value cannot be null.", ErrorText.Of(new ArgumentNullException("name")));
    }

    [Fact]
    public void AnArgumentOutOfRangeExceptionAlsoLosesTheActualValue()
    {
        Assert.Equal("The value is too big.",
            ErrorText.Of(new ArgumentOutOfRangeException("size", 12, "The value is too big.")));
    }

    [Fact]
    public void AnotherExceptionKeepsItsMessage()
    {
        Assert.Equal("No edit is open. (Parameter 'x')",
            ErrorText.Of(new InvalidOperationException("No edit is open. (Parameter 'x')")));
    }

    /// <summary>The edit of a field shows the refusal of the data layer, which throws with the parameter name
    /// (ThnEdits.CheckValue), without the parameter text.</summary>
    [Fact]
    public void AFieldEditOfATextThatASaveCannotWriteGivesAnErrorWithoutTheParameterText()
    {
        using var ws = Workspace();
        var session = ws.Session("act");
        var trent = Entity(session, "Char_trent");
        var path = new ThnPath("userprops.category");
        var edit = new ThnFieldGesture(session, new FieldKey(trent, path.Text, FieldKey.AllLines), "Char",
            text => ThnFieldParse.Of(new ThnText(text)), ThnFieldCommand.Set(trent, path));

        edit.Change("é");

        Assert.NotNull(edit.Error);
        Assert.Contains("ASCII", edit.Error);
        Assert.DoesNotContain("(Parameter", edit.Error);
        edit.Dispose();
    }
}
