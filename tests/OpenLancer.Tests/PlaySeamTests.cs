using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Play;
using Xunit;
using static OpenLancer.Tests.EditSessionTests;

namespace OpenLancer.Tests;

/// <summary>
/// The values that the editor gives Play through its seam (section 9.12 of the M4 design). <c>PlayControllerTests</c>
/// give them with a fake editor, so these tests check that the editor makes them in the same way. The validation state
/// is in <c>ValidationTests</c>.
/// </summary>
public sealed class PlaySeamTests
{
    /// <summary>A save that is not complete keeps that fact, so Play starts no game for it (section 9.2).</summary>
    [Fact]
    public void TheSaveOutcomeKeepsWhetherTheSaveSavedAndIsComplete()
    {
        var saved = new SaveResult(true, [@"C:\mods\act\patches\DATA\EQUIPMENT\goods.ini"], [], [], []);
        var notSaved = new SaveResult(false, [], [], [Diagnostic.Error("goods.ini", "changed outside the editor")], []);

        Assert.Equal(new SaveOutcome(true, 3, false) { Result = saved }, SaveOutcome.Of(saved, 3));
        var incomplete = saved with { Incomplete = true };
        Assert.Equal(new SaveOutcome(true, 4, true) { Result = incomplete }, SaveOutcome.Of(incomplete, 4));
        Assert.Equal(new SaveOutcome(false, 0, false) { Result = notSaved }, SaveOutcome.Of(notSaved, 0));
    }

    /// <summary>A save that did not save tells why, so a play request of an agent gives save_blocked or changed_on_disk
    /// (section 7.4 of the M6c design).</summary>
    [Fact]
    public void TheSaveOutcomeTellsWhyASaveDidNotSave()
    {
        var notSaved = new SaveResult(false, [], [], [], []);

        Assert.Equal(SaveFailure.None, SaveOutcome.Of(notSaved, 0).Failure);
        Assert.Equal(SaveFailure.ChangedOnDisk,
            SaveOutcome.Of(notSaved with { ChangedOnDisk = ["goods.ini"] }, 0).Failure);
        Assert.Equal(SaveFailure.Blocked, SaveOutcome.Blocked.Failure);
        Assert.False(SaveOutcome.Blocked.Saved);
    }

    /// <summary>
    /// The snapshot has the active mod, so Play saves its edits and plays the mods through it. The later mods are only
    /// for the warning of D38. Without an active mod, Play plays all mods.
    /// </summary>
    [Fact]
    public void ThePlaySessionHasTheActiveModAndTheLaterMods()
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(GoodsPath, Goods);
        ws.Mod("low", new());
        ws.Mod("act", new());
        ws.Mod("late1", new());
        ws.Mod("late2", new());
        ws.WriteProfile("low", "act", "late1", "late2");

        var session = ws.Session("act");
        var active = PlaySession.Of(session, 3, 2, "a reason");
        var all = PlaySession.Of(ws.Session(null), 0, 0, null);
        // The snapshot tells whether the active mod has unsaved edits, for the agent play rule.
        Assert.Equal(session.IsDirty, active.Unsaved);
        Assert.False(all.Unsaved);

        Assert.Equal((ws.ProfilePath, "act", 3, 2, "a reason"),
            (active.ProfilePath, active.ActiveMod, active.Saves, active.IncompleteSave, active.NoEditReason));
        Assert.Equal(["late1", "late2"], active.LaterMods);
        Assert.Equal(ws.ProfilePath, all.ProfilePath);
        Assert.Null(all.ActiveMod);
        Assert.Null(all.NoEditReason);
        Assert.Empty(all.LaterMods);
    }
}
