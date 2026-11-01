using System;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The texts of the Welcome page (#56): the editor makes mods, and the page names the first action. The first
/// action and the reason of an off New profile use the count of the linked games that can be a base.</summary>
public sealed class WelcomeScreenTests
{
    /// <summary>The name of a control in a sentence: its label without the dots of a control that opens a
    /// window.</summary>
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
        var text = WelcomeScreen.FirstAction(0, 0);
        Assert.StartsWith("To start, link your game", text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.GameLinkLabel), text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.OpenProfileLabel), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Name(WelcomeScreen.NewProfileLabel), text, StringComparison.Ordinal);
        Assert.Equal(WelcomeScreen.NoGameReason, WelcomeScreen.NewProfileReason(0, 0));
    }

    [Fact]
    public void WithALinkedGameTheFirstActionIsToOpenOrMakeAProfile()
    {
        var text = WelcomeScreen.FirstAction(2, 1);
        Assert.StartsWith("To start, click Open profile", text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.OpenProfileLabel), text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.NewProfileLabel), text, StringComparison.Ordinal);
        Assert.Null(WelcomeScreen.NewProfileReason(2, 1));
    }

    /// <summary>The only linked game cannot be a base, for example because its folder is not there or because it is a
    /// source build. New profile is then off, so the first action does not name it. The reason of New profile names the
    /// real cause, not a missing link.</summary>
    [Fact]
    public void WhenNoLinkedGameCanBeABaseTheFirstActionIsToCheckTheGame()
    {
        var text = WelcomeScreen.FirstAction(1, 0);
        Assert.StartsWith("To start, check your game", text, StringComparison.Ordinal);
        Assert.Contains(Name(WelcomeScreen.GameLinkLabel), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Name(WelcomeScreen.NewProfileLabel), text, StringComparison.Ordinal);
        Assert.Equal(WelcomeScreen.NoBaseReason, WelcomeScreen.NewProfileReason(1, 0));
        Assert.Contains(Name(WelcomeScreen.GameLinkLabel), WelcomeScreen.NoBaseReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WelcomeScreen.Purpose)]
    [InlineData(WelcomeScreen.ProfileNote)]
    [InlineData(WelcomeScreen.FirstActionWithoutGame)]
    [InlineData(WelcomeScreen.FirstActionWithGame)]
    [InlineData(WelcomeScreen.FirstActionWithoutBase)]
    [InlineData(WelcomeScreen.NewProfileTip)]
    [InlineData(WelcomeScreen.NoGameReason)]
    [InlineData(WelcomeScreen.NoBaseReason)]
    public void EachTextIsSimplifiedTechnicalEnglish(string text) => Assert.Empty(SteLint.Problems(text));
}
