using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
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
        map.Changed(Wave, RowMap.OfInsert(6, 2), 6, Six);
        map.Changed(Wave, RowMap.OfRemoval(7, [5]), 7, Six);

        Assert.Equal([("ol_wave", 2, 3), ("ol_wave", 3, 4)], Moves(map));
    }

    [Fact]
    public void TheMapOfALaterRequestJoinsWithTheRenameOfTheTrigger()
    {
        var first = new RowMap();
        first.Changed(Wave, RowMap.OfMove(6, 5, 3), 6, Six);
        first.Renamed(Wave, Renamed);
        var second = new RowMap();
        second.Changed(Renamed, RowMap.OfRemoval(6, [3]), 6, Six);

        // Line 5 moved to row 3 and then went. Lines 3 and 4 moved to rows 4 and 5, and then up one row.
        Assert.Equal([("ol_wave_2", 3, 4), ("ol_wave_2", 4, 5), ("ol_wave_2", 5, 3)], Moves(first));
        Assert.Empty(Moves(first.Then(second)));
        var third = new RowMap();
        third.Changed(Renamed, RowMap.OfInsert(5, 0), 5, _ => 5);
        Assert.Equal([("ol_wave_2", 0, 1), ("ol_wave_2", 1, 2), ("ol_wave_2", 2, 3), ("ol_wave_2", 3, 4), ("ol_wave_2", 4, 5)],
            Moves(first.Then(second).Then(third)));
    }

    [Fact]
    public void AnEditThatTheMapDoesNotFollowGivesNoMoves()
    {
        var unknownStart = new RowMap();
        unknownStart.Changed(Wave, RowMap.OfInsert(7, 0), 7, Six);
        var newTrigger = new RowMap();
        newTrigger.Changed(Wave, RowMap.OfInsert(3, 0), 3, _ => -1);
        var changedLater = new RowMap();
        changedLater.Changed(Wave, RowMap.OfInsert(6, 0), 6, Six);
        changedLater.Check(_ => 9);

        Assert.Empty(unknownStart.Moves);
        Assert.Empty(newTrigger.Moves);
        Assert.Empty(changedLater.Moves);
    }

    /// <summary>The origins of an insert, a removal and a move give the row before the change of each line.</summary>
    [Fact]
    public void TheOriginsOfAnInsertARemovalAndAMoveGiveTheRowOfEachLineBefore()
    {
        Assert.Equal([0, 1, -1, -1, 2, 3], RowMap.OfInsert(4, 2, 2));
        Assert.Equal([0, 2, 4], RowMap.OfRemoval(5, [1, 3]));
        Assert.Equal([0, 3, 1, 2, 4], RowMap.OfMove(5, 3, 1));
        Assert.Equal([0, 2, 3, 1, 4], RowMap.OfMove(5, 1, 3));
    }

    /// <summary>The compare of two lists of lines keeps the row of a same line and of a changed line. An extra line is
    /// new or gone. So a command that does not give the origins still gives the moves of the other lines.</summary>
    [Fact]
    public void TheCompareOfTheLinesKeepsTheRowOfASameLineAndOfAChangedLine()
    {
        var before = Lines("nickname = ol_wave", "system = ol_system", "Cnd_True = no_params", "Act_ActTrig = ol_end",
            "Act_DeactTrig = ol_end");

        // A new line after the system line moves the three later lines one row down.
        Assert.Equal([0, 1, -1, 2, 3, 4], LineDiff.Origins(before, Lines("nickname = ol_wave", "system = ol_system",
            "InitState = ACTIVE", "Cnd_True = no_params", "Act_ActTrig = ol_end", "Act_DeactTrig = ol_end")));
        // A changed line keeps its row. A removed line moves the later lines up.
        Assert.Equal([0, 1, 2, 3, 4], LineDiff.Origins(before, Lines("nickname = ol_wave", "system = ol_system",
            "Cnd_Timer = 5", "Act_ActTrig = ol_end", "Act_DeactTrig = ol_end")));
        Assert.Equal([0, 2, 3, 4], LineDiff.Origins(before, Lines("nickname = ol_wave", "Cnd_True = no_params",
            "Act_ActTrig = ol_end", "Act_DeactTrig = ol_end")));
        // Two changed lines and one extra new line between the same lines.
        Assert.Equal([0, 1, 2, -1, 3, 4], LineDiff.Origins(before, Lines("nickname = ol_wave", "system = ol_other",
            "Cnd_Timer = 5", "Act_SpawnShip = ol_ship", "Act_ActTrig = ol_end", "Act_DeactTrig = ol_end")));
        Assert.Empty(LineDiff.Origins(before, []));
        Assert.Equal([-1, -1], LineDiff.Origins([], Lines("nickname = ol_wave", "Cnd_True = no_params")));
    }

    private static List<Entry> Lines(params string[] lines)
    {
        var section = new Section("Trigger");
        foreach (var line in lines)
        {
            var parts = line.Split(" = ");
            var entry = new Entry(section, parts[0]);
            foreach (var value in parts[1].Split(", "))
                entry.Add(new StringValue(value));
            section.Add(entry);
        }
        return [.. section];
    }
}
