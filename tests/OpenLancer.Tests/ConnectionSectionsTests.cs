using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The Jump connection and Trade lane ring sections of the Inspector without a window: the models of a page, the lock
/// dropdown, the ring buttons and the "Lane name" row. Each control runs the plan of an agent tool through the command
/// runner as one entry of the edit history with the label of the plan. The data is invented.
/// </summary>
public sealed class ConnectionSectionsTests
{
    private static readonly SectionRef Gate = SceneWorkspace.Object("Li01_to_Li02");
    private static readonly SectionRef Partner = SceneWorkspace.Li02("Object", "Li02_to_Li01");

    private static SectionRef Ring(int n) => LaneWorkspace.Ring(n);

    /// <summary>
    /// The lock dropdown offers the four locks, and a pick runs the lock plan as one entry of the user with the label of
    /// the plan. The pair gets the lock, and one undo gives back the locks of both ends.
    /// </summary>
    [Fact]
    public void ThePickOfALockIsOneEntryWithTheLabelOfThePlan()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var runner = FakeCommandHost.Runner(session);
        Assert.Equal([JumpLock.None, JumpLock.Player, JumpLock.Npc, JumpLock.Both],
            ConnectionSections.LockChoices.Select(c => c.Lock));
        Assert.Equal("Locked for NPCs", ConnectionSections.LockLabel(JumpLock.Npc));
        Assert.Equal(JumpLock.Both, ConnectionSections.Page.Of(session, Gate).Jump!.Lock);

        var result = runner.Run(ConnectionPlans.SetLock(session, Gate, JumpLock.None));

        Assert.Equal(EditStatus.Applied, result.Status);
        Assert.Equal("Unlock Li01_to_Li02", session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.Equal(JumpLock.None, ConnectionSections.Page.Of(session, Gate).Jump!.Lock);
        Assert.Equal(JumpLock.None, ConnectionSections.Page.Of(session, Partner).Jump!.Lock);
        session.Undo();
        Assert.Equal(JumpLock.Both, ConnectionSections.Page.Of(session, Gate).Jump!.Lock);
        Assert.Equal(JumpLock.Npc, ConnectionSections.Page.Of(session, Partner).Jump!.Lock);
        Assert.False(session.CanUndo);

        runner.Run(ConnectionPlans.SetLock(session, Partner, JumpLock.Player));
        Assert.Equal("Lock Li02_to_Li01", session.UndoLabel);
    }

    /// <summary>
    /// "Insert ring after", "Repair lane" and "Delete ring" each run their plan as one entry of the user with the label
    /// of the plan. The page then shows the new place of the ring, and one undo gives back the lane.
    /// </summary>
    [Fact]
    public void EachRingButtonIsOneEntryWithTheLabelOfThePlan()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");
        var runner = FakeCommandHost.Runner(session);

        var insert = LanePlans.InsertRing(session, Ring(2), LanePlace.After);
        Assert.Equal(EditStatus.Applied, runner.Run(insert).Status);
        Assert.Equal("Insert ring after Li01_Trade_Lane_Ring_2", session.UndoLabel);
        Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Assert.Equal("Ring 3 of 5", ConnectionSections.Page.Of(session, insert.Ring!).Ring!.Place);
        session.Undo();
        Assert.Equal("Ring 3 of 4", ConnectionSections.Page.Of(session, Ring(3)).Ring!.Place);

        session.Execute(EditCommand.RemoveName(Ring(3), "prev_ring"));
        Assert.Equal(EditStatus.Applied, runner.Run(LanePlans.Repair(session, Ring(2))).Status);
        Assert.Equal("Repair trade lane of Li01_Trade_Lane_Ring_2", session.UndoLabel);
        Assert.True(ConnectionSections.Page.Of(session, Ring(2)).Completeness!.Complete);
        session.Undo();
        session.Undo();

        var delete = SceneCommands.Delete(session, [Ring(2)]);
        Assert.False(delete.NeedsConfirmation);
        Assert.Equal(EditStatus.Applied, runner.Run(delete).Status);
        Assert.Equal("Delete Li01_Trade_Lane_Ring_2", session.UndoLabel);
        Assert.Equal(Ring(1), ConnectionSections.Page.Of(session, Ring(3)).Ring!.Previous);
        session.Undo();
        Assert.Equal(Ring(2), ConnectionSections.Page.Of(session, Ring(3)).Ring!.Previous);
        Assert.False(session.CanUndo);
    }

    /// <summary>The "Lane name" row shows only on the two end rings, because the HUD reads only their names. Without a
    /// ring model, the bound rows follow the lines of the section.</summary>
    [Fact]
    public void TheLaneNameRowShowsOnlyOnAnEndRing()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal([true, false, false, true],
            Enumerable.Range(1, 4).Select(n => ConnectionSections.ShowsLaneName(ConnectionSections.Page.Of(session, Ring(n)).Ring)));
        Assert.True(ConnectionSections.ShowsLaneName(null));
    }

    /// <summary>The help texts and the tooltips of the sections pass the STE lint.</summary>
    [Fact]
    public void TheTextsPassTheSteLint()
    {
        string[] texts =
        [
            ConnectionSections.LockHelp, ConnectionSections.LaneNameHelp, ConnectionSections.InsertTip,
            ConnectionSections.RepairTip, ConnectionSections.DeleteTip, ConnectionSections.PartnerTip,
            ConnectionSections.FieldEditReason, ConnectionSections.KindHelp, ConnectionSections.ExitHelp,
            ConnectionSections.TrafficZoneHelp, ConnectionSections.LaneZoneHelp, ConnectionSections.LeftNameHelp,
            ConnectionSections.RightNameHelp,
            .. ConnectionSections.LockChoices.Select(c => c.Label),
        ];

        Assert.All(texts, t => Assert.Empty(SteLint.Problems(t)));
    }

    /// <summary>A profile without an active mod gives its reason for each control of the page, and the runner runs
    /// nothing while the editor takes no edits.</summary>
    [Fact]
    public void AReadOnlyProfileGivesTheReasonForEachControl()
    {
        using var ws = LaneWorkspace.Create();
        var session = ws.Session(null);
        var reason = session.ReadOnlyReason!;

        var ring = ConnectionSections.Page.Of(session, Ring(2)).Ring!;

        Assert.Equal([reason, reason, reason], [ring.InsertUnavailable, ring.RepairUnavailable, ring.DeleteUnavailable]);
        var host = new FakeCommandHost(session, "The save runs.");
        Assert.Equal(EditStatus.Refused, new Editor.Editing.CommandRunner(host).Run(LanePlans.Repair(session, Ring(2))).Status);
        Assert.Equal("The save runs.", Assert.Single(host.Log.Entries).Message);
    }
}
