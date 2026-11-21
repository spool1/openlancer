using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The save card of the Scene (<see cref="SaveCard"/>). A new system that the mod has not saved opens with the card. The
/// card asks for a save, waits while the game data loads, and gives the open of the system when the loaded game data has
/// it. An undo of the create closes the card. The data is invented.
/// </summary>
public sealed class SaveCardTests
{
    [Fact]
    public void TheCardOfANewSystemAsksForASaveWaitsForTheLoadAndThenOpensTheSystem()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "Av01", busy: false, loaded: false));
        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "AV01", busy: false, loaded: false));
        Assert.True(session.Save().Saved);
        // The reload of the game data after the save runs. The card waits, also when the old data has the system.
        Assert.Equal(SaveCardState.Reloading, SaveCard.StateOf(session, "Av01", busy: true, loaded: false));
        Assert.Equal(SaveCardState.Reloading, SaveCard.StateOf(session, "Av01", busy: true, loaded: true));
        // The data of the save has the system, so the Scene opens it.
        Assert.Equal(SaveCardState.Open, SaveCard.StateOf(session, "Av01", busy: false, loaded: true));
    }

    /// <summary>The edit view has no system after an undo of its create, so the card closes. A redo shows the card
    /// again.</summary>
    [Fact]
    public void AnUndoOfTheCreateClosesTheCard()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        session.Undo();
        Assert.Equal(SaveCardState.Gone, SaveCard.StateOf(session, "Av01", busy: false, loaded: false));
        session.Redo();
        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "Av01", busy: false, loaded: false));
        Assert.Equal(SaveCardState.Gone, SaveCard.StateOf(session, "Av09", busy: false, loaded: false));
    }

    /// <summary>A rename of the new system before the save closes the card of the old nickname, because the edit view
    /// has no system of that nickname.</summary>
    [Fact]
    public void ARenameBeforeTheSaveClosesTheCardOfTheOldNickname()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        session.Execute(SystemPlans.Rename(session, "Av01", "Av02").Command!);

        Assert.Equal(SaveCardState.Gone, SaveCard.StateOf(session, "Av01", busy: false, loaded: false));
        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "Av02", busy: false, loaded: false));
    }

    [Fact]
    public void TheTextsOfTheCardAreSimplifiedTechnicalEnglish()
    {
        Assert.Equal("This system is new. Save the mod to open it in the Scene.",
            $"{SaveCard.NewSystem.Message} {SaveCard.NewSystem.Next}");
        Assert.Empty(SteLint.Problems(SaveCard.Reloading));
        Assert.Equal("The editor loads the game data.", SaveCard.Reloading);
    }
}
