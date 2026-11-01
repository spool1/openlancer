using System;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The texts of the Welcome page (#56): the editor makes mods, and the page names the first action.</summary>
public sealed class WelcomeScreenTests
{
    /// <summary>The name of a control in a sentence: its label without the dots of a control that opens a window.</summary>
    private static string Name(string label) => label.TrimEnd('.');

    [Fact]
    public void ThePageSaysThatTheEditorMakesMods()
    {
        Assert.Contains("makes mods", WelcomeScreen.Purpose, StringComparison.Ordinal);
        Assert.DoesNotContain("Look at", WelcomeScreen.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutALinkedGameTheFirstActionIsToLinkTheGame()
    {
        var text = WelcomeScreen.FirstAction(false);
        Assert.StartsWith("To start, link your game", text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.GameLinkLabel), text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.OpenProfileLabel), text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithALinkedGameTheFirstActionIsToOpenOrMakeAProfile()
    {
        var text = WelcomeScreen.FirstAction(true);
        Assert.StartsWith("To start, click Open profile", text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.OpenProfileLabel), text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.NewProfileLabel), text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WelcomeScreen.Purpose)]
    [InlineData(WelcomeScreen.ProfileNote)]
    [InlineData(WelcomeScreen.FirstActionWithoutGame)]
    [InlineData(WelcomeScreen.FirstActionWithGame)]
    public void EachTextIsSimplifiedTechnicalEnglish(string text) => Assert.Empty(SteLint.Problems(text));
}
