using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The save card of the Scene (<see cref="SaveCard"/>). A new system that the mod has not saved opens with the card. The
/// card asks for a save, waits while the game data loads, and gives the open of the system when the loaded game data has
/// it. The open waits for the end of a field edit or a drag. The card follows a rename of its system, and an undo of the
/// create or a delete closes it. The data is invented.
/// </summary>
public sealed class SaveCardTests
{
    [Fact]
    public void TheCardOfANewSystemAsksForASaveWaitsForTheLoadAndThenOpensTheSystem()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "Av01", busy: false, loaded: false, waiting: false));
        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "AV01", busy: false, loaded: false, waiting: false));
        // A field edit before the save does not change the card.
        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "Av01", busy: false, loaded: false, waiting: true));
        Assert.True(session.Save().Saved);
        // The reload of the game data after the save runs. The card waits, also when the old data has the system.
        Assert.Equal(SaveCardState.Reloading, SaveCard.StateOf(session, "Av01", busy: true, loaded: false, waiting: false));
        Assert.Equal(SaveCardState.Reloading, SaveCard.StateOf(session, "Av01", busy: true, loaded: true, waiting: false));
        // The data of the save has the system, but a field edit or a drag runs, so the open waits for its end.
        Assert.Equal(SaveCardState.Waiting, SaveCard.StateOf(session, "Av01", busy: false, loaded: true, waiting: true));
        // Then the Scene opens the system.
        Assert.Equal(SaveCardState.Open, SaveCard.StateOf(session, "Av01", busy: false, loaded: true, waiting: false));
    }

    /// <summary>The edit view has no system after an undo of its create or after its delete, so the card closes. A redo
    /// shows the card again.</summary>
    [Fact]
    public void AnUndoOfTheCreateOrADeleteClosesTheCard()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        session.Undo();
        Assert.Equal(SaveCardState.Gone, SaveCard.StateOf(session, "Av01", busy: false, loaded: false, waiting: false));
        session.Redo();
        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, "Av01", busy: false, loaded: false, waiting: false));
        Assert.Equal(SaveCardState.Gone, SaveCard.StateOf(session, "Av09", busy: false, loaded: false, waiting: false));

        session.Execute(SystemPlans.Delete(session, "Av01").Command!);
        Assert.Equal(SaveCardState.Gone, SaveCard.StateOf(session, "Av01", busy: false, loaded: false, waiting: false));
    }

    /// <summary>
    /// A rename of the new system before the save keeps the card, with the new nickname, as Unity keeps a renamed item in
    /// view. The rename of an agent does the same, also with follow mode off. An undo and a redo of the rename move the
    /// card back and forth. A rename of the section that waits for the open moves it too. A rename of another system
    /// changes nothing.
    /// </summary>
    [Fact]
    public void TheCardFollowsARenameOfItsSystemBeforeTheSave()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        var sun = NewSystemWorkspace.New("Object", "Av01_sun");
        var target = new SaveCardTarget("Av01", sun);
        session.Changed += change => target = target.After(change, session);

        session.Execute(SystemPlans.Rename(session, "Av01", "Av02").Command!);
        Assert.Equal(new SaveCardTarget("Av02", sun), target);
        Assert.Equal(SaveCardState.New, SaveCard.StateOf(session, target.System, busy: false, loaded: false, waiting: false));

        session.Undo();
        Assert.Equal("Av01", target.System);
        session.Redo();
        Assert.Equal("Av02", target.System);

        session.Execute(EditCommand.Rename(sun, "Av01_star"));
        Assert.Equal(new SaveCardTarget("Av02", NewSystemWorkspace.New("Object", "Av01_star")), target);

        session.Execute(SystemPlans.Create(session, "Av05", "Li02", (6, 6), "Bravo", "A second system.").Command!);
        session.Execute(SystemPlans.Rename(session, "Av05", "Av06").Command!);
        Assert.Equal("Av02", target.System);
    }

    /// <summary>
    /// An open of a system that the loaded game data does not have shows the card of its <c>[system]</c>, and the
    /// <c>[system]</c> gets the selection. Else the Console tells why: the first load of the game data runs, more than one
    /// <c>[system]</c> has the nickname, or the mod has no such system.
    /// </summary>
    [Fact]
    public void AnOpenShowsTheCardOfTheSystemOrTellsWhyNot()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);

        Assert.Equal((NewSystemWorkspace.System("Av01"), null), SaveCard.EntryOf(session, "av01", dataLoaded: true));
        var (entry, loading) = SaveCard.EntryOf(session, "Av01", dataLoaded: false);
        Assert.Null(entry);
        Assert.Equal("The editor loads the game data. Open the system Av01 after the load.", loading);
        Assert.Equal((null, "The game data and the mod have no system Av09."),
            SaveCard.EntryOf(session, "Av09", dataLoaded: true));

        using var systems = SystemWorkspace.Create();
        var (twice, why) = SaveCard.EntryOf(systems.Session("act"), "twice", dataLoaded: true);
        Assert.Null(twice);
        Assert.Equal("More than one [system] has the nickname twice. Give each system a different nickname to open it " +
                     "in the Scene.", why);
        Assert.Empty(SteLint.Problems(loading!));
        Assert.Empty(SteLint.Problems(why!));
    }

    [Fact]
    public void TheTextsOfTheCardAreSimplifiedTechnicalEnglish()
    {
        Assert.Equal("This system is new. Save the mod to open it in the Scene.",
            $"{SaveCard.NewSystem.Message} {SaveCard.NewSystem.Next}");
        Assert.Equal("The editor loads the game data.", SaveCard.Reloading);
        Assert.All([SaveCard.Reloading, SaveCard.Waiting, SaveCard.Opening], t => Assert.Empty(SteLint.Problems(t)));
    }
}
