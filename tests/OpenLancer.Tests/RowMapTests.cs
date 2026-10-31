using System.Linq;
using OpenLancer.Data.Missions.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The net row moves of the trigger lines of a request and of a call (final finding 1 of MCP1-3).</summary>
public sealed class RowMapTests
{
    private const string Script = @"DATA\MISSIONS\OL_TEST\ol_test.ini";
    private static readonly TriggerRef Wave = new(Script, "ol_wave");
    private static readonly TriggerRef Renamed = new(Script, "ol_wave_2");

    private static int? Six(TriggerRef trigger) => 6;

    private static (string Trigger, int From, int To)[] Moves(RowMap map) =>
        map.Moves.Select(m => (m.Trigger.Name, m.From, m.To)).ToArray();

    [Fact]
    public void AnInsertAndARemovalGiveOneMoveForEachLine()
    {
        var map = new RowMap();
        map.Inserted(Wave, 2, 6, Six);
        map.Removed(Wave, 5, 7, Six);

        Assert.Equal([("ol_wave", 2, 3), ("ol_wave", 3, 4)], Moves(map));
    }

    [Fact]
    public void TheMapOfALaterRequestJoinsWithTheRenameOfTheTrigger()
    {
        var first = new RowMap();
        first.Moved(Wave, 5, 3, 6, Six);
        first.Renamed(Wave, Renamed);
        var second = new RowMap();
        second.Removed(Renamed, 3, 6, Six);

        // Line 5 moved to row 3 and then went. Lines 3 and 4 moved to rows 4 and 5, and then up one row.
        Assert.Equal([("ol_wave_2", 3, 4), ("ol_wave_2", 4, 5), ("ol_wave_2", 5, 3)], Moves(first));
        Assert.Empty(Moves(first.Then(second)));
        var third = new RowMap();
        third.Inserted(Renamed, 0, 5, _ => 5);
        Assert.Equal([("ol_wave_2", 0, 1), ("ol_wave_2", 1, 2), ("ol_wave_2", 2, 3), ("ol_wave_2", 3, 4), ("ol_wave_2", 4, 5)],
            Moves(first.Then(second).Then(third)));
    }

    [Fact]
    public void AnEditThatTheMapDoesNotFollowGivesNoMoves()
    {
        var unknownStart = new RowMap();
        unknownStart.Inserted(Wave, 0, 7, Six);
        var newTrigger = new RowMap();
        newTrigger.Inserted(Wave, 0, 3, _ => -1);
        var changedLater = new RowMap();
        changedLater.Inserted(Wave, 0, 6, Six);
        changedLater.Check(_ => 9);

        Assert.Empty(unknownStart.Moves);
        Assert.Empty(newTrigger.Moves);
        Assert.Empty(changedLater.Moves);
    }
}
