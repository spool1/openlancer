using System;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Editor;
using OpenLancer.Editor.Universe;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The model of the read-only Universe panel without a window: the places and the names of the systems, the lines of
/// the jump connections, the red lock, a new system before the save, the map that keeps the model, the highlight of a
/// selection, the labels, and the fit of the map into the canvas. The install is <see cref="ConnectionWorkspace"/>.
/// Li01 at 7, 8 and Li02 at 6, 9 have a jump gate pair with a player lock on the Li01 gate. Li03 at 9, 8 has a gate and
/// a hole without a goto line. The data is invented.
/// </summary>
public sealed class UniverseModelTests
{
    private static readonly SectionRef Gate = SceneWorkspace.Object("Li01_to_Li02");

    // The loaded game data of the tests has the systems of the install, so a system that a test adds is new.
    private static readonly object Data = new();
    private static readonly StarSystem[] Loaded = new[] { "Li01", "Li02", "Li03" }
        .Select(n => new StarSystem { Nickname = n, SourceFile = "" })
        .ToArray();

    private static StarSystem? LoadedSystem(string nickname) =>
        Loaded.FirstOrDefault(s => s.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));

    private static ItemTexts Texts(EditSession session) => new(session, _ => "", _ => "");

    /// <summary>The model of a session as the map of the panel makes it.</summary>
    private static UniverseModel Model(EditSession session) =>
        new UniverseMap().Of(session, Texts(session), 0, Data, LoadedSystem);

    private static void Run(EditSession session, ConnectionPlan plan)
    {
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
    }

    /// <summary>Each system shows at its place on the nav map grid, with its nickname as its label when it has no name
    /// and its jump objects as "n of 10". The area to show is the grid.</summary>
    [Fact]
    public void EachSystemShowsAtItsPlaceWithItsLabelAndItsJumpObjects()
    {
        using var ws = ConnectionWorkspace.Create();
        var model = Model(ws.Session("act"));

        Assert.Equal(["Li01", "Li02", "Li03"], model.Systems.Select(s => s.Nickname));
        Assert.Equal([new Vector2(7, 8), new Vector2(6, 9), new Vector2(9, 8)], model.Systems.Select(s => s.Place!.Value));
        Assert.Equal(["Li01", "Li02", "Li03"], model.Systems.Select(s => s.Label));
        Assert.Equal(["1 of 10", "1 of 10", "0 of 10"], model.Systems.Select(s => s.Slots));
        Assert.All(model.Systems, s => Assert.False(s.IsNew));
        Assert.Equal((Vector2.Zero, new Vector2(UniverseModel.GridWidth, UniverseModel.GridHeight)), model.Bounds);
        Assert.Same(model.Systems[1], model.System("li02"));
        Assert.Equal("3 systems", UniversePanel.CountText(model));
    }

    /// <summary>
    /// A jump gate pair is one solid line. The player lock of one end makes it red, and the tooltip of each system tells
    /// its connection with its lock. Without the player lock, the line has the color of a jump.
    /// </summary>
    [Fact]
    public void AGatePairIsOneSolidLineThatAPlayerLockMakesRed()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");

        var model = Model(session);

        var line = Assert.Single(model.Lines);
        Assert.Equal(new UniverseMapLine("Li01", "Li02", false, true, false, 0), line);
        Assert.Equal(["Jump gate to Li02. Locked for the player and NPCs."], model.System("Li01")!.Connections);
        Assert.Equal(["Jump gate to Li01. Locked for NPCs."], model.System("Li02")!.Connections);
        Assert.Equal("Li01\nNickname: Li01\nJump objects: 1 of 10\nJump gate to Li02. Locked for the player and NPCs.",
            model.System("Li01")!.Tooltip);

        Run(session, ConnectionPlans.SetLock(session, Gate, JumpLock.Npc));
        Assert.False(Assert.Single(Model(session).Lines).PlayerLocked);
    }

    /// <summary>A jump hole pair is a dashed line. Beside the gate line between the same two systems, the two lines get
    /// offsets to two sides, so each one shows.</summary>
    [Fact]
    public void AHolePairIsADashedLineBesideTheGateLine()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Hole, new ConnectionEnd("Li02"),
            new ConnectionEnd("Li01")));

        var lines = Model(session).Lines;

        Assert.Equal(2, lines.Count);
        Assert.Equal((false, -0.5f), (lines[0].Dashed, lines[0].Offset));
        Assert.Equal((true, 0.5f, false, false), (lines[1].Dashed, lines[1].Offset, lines[1].PlayerLocked, lines[1].OneWay));
        Assert.Equal(("Li01", "Li02"), (lines[0].From, lines[0].To));
        Assert.Equal(("Li01", "Li02"), (lines[1].From, lines[1].To));
        var (a, b) = (new Vector2(0, 0), new Vector2(100, 0));
        var gate = UniverseFrame.Segment(lines[0], a, b, 4);
        var hole = UniverseFrame.Segment(lines[1], a, b, 4);
        Assert.Equal(-2, gate.Start.Y, 3);
        Assert.Equal(2, hole.Start.Y, 3);
    }

    /// <summary>
    /// Two gate pairs between the same two systems are two lines side by side. The ends of each pair find each other by
    /// their goto lines, so only the line of the locked pair is red.
    /// </summary>
    [Fact]
    public void TwoPairsOfOneKindAreTwoLinesWithTheirOwnLocks()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Li02"),
            new ConnectionEnd("Li01")));

        var lines = Model(session).Lines;

        Assert.Equal(
        [
            new UniverseMapLine("Li01", "Li02", false, true, false, -0.5f),
            new UniverseMapLine("Li01", "Li02", false, false, false, 0.5f),
        ], lines);
    }

    /// <summary>
    /// The ends of a pair find each other by their goto lines, not by their order in the files. Here the file of B lists
    /// the end of the second pair first. Only the end b1 of the first pair has a player lock, so only the first line is
    /// red.
    /// </summary>
    [Fact]
    public void TheEndsOfAPairFindEachOtherByTheirGotoLines()
    {
        static UniverseSystemRow System(string nickname, float x) =>
            new($"system:{nickname}", nickname, "", null, new ItemOrigin(ValueOrigin.Base, null, false))
            {
                MapPos = [x, 1], JumpObjects = new JumpSlots(2, 10),
            };
        static UniverseConnectionRow End(string self, string system, string other, string toSystem, JumpLock locked) =>
            new($"obj:{self}", JumpKind.JumpGate, $"system:{system}", $"system:{toSystem}", $"obj:{other}", true, locked);
        UniverseConnectionRow[] connections =
        [
            End("a1", "A", "b1", "B", JumpLock.None), End("a2", "A", "b2", "B", JumpLock.None),
            End("b2", "B", "a2", "A", JumpLock.None), End("b1", "B", "a1", "A", JumpLock.Player),
        ];

        var model = UniverseModel.Of([System("A", 1), System("B", 2)], connections, _ => false);

        Assert.Equal([true, false], model.Lines.Select(l => l.PlayerLocked));
        Assert.Equal([-0.5f, 0.5f], model.Lines.Select(l => l.Offset));
    }

    /// <summary>
    /// A new system shows at its place before the save, with the mark of a new system in its tooltip. Its new gate pair
    /// to Li02 is a line. The count of the toolbar names the new system. An undo takes the system and its line away.
    /// </summary>
    [Fact]
    public void AnUnsavedNewSystemAndItsNewPairShow()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        Run(session, ConnectionPlans.CreatePair(session, ConnectionKind.Gate, new ConnectionEnd("Av01"),
            new ConnectionEnd("Li02")));

        var model = Model(session);

        var av01 = model.System("Av01")!;
        Assert.True(av01.IsNew);
        Assert.Equal((new Vector2(4, 5), "Avalon", "1 of 10"), (av01.Place!.Value, av01.Label, av01.Slots));
        Assert.Contains(SystemEntry.NewMark, av01.Tooltip.Split('\n'));
        Assert.Contains(new UniverseMapLine("Li02", "Av01", false, false, false, 0), model.Lines);
        Assert.Contains("Jump gate to Avalon (Av01).", model.System("Li02")!.Connections);
        Assert.Equal("4 systems, 1 new", UniversePanel.CountText(model));

        session.Undo();
        session.Undo();
        model = Model(session);
        Assert.Null(model.System("Av01"));
        Assert.Single(model.Lines);
    }

    /// <summary>A jump whose other end does not go back is a line of its own with a direction. The tooltip tells that
    /// the player cannot jump back.</summary>
    [Fact]
    public void AOneWayJumpIsALineWithADirection()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ConnectionWorkspace.Li03Path, ConnectionWorkspace.Li03Text.Replace("archetype = jumpgate",
            "archetype = jumpgate\ngoto = Li01, Li01_to_Li02, gate_tunnel_bretonia", StringComparison.Ordinal));
        var model = Model(ws.Session("act"));

        Assert.Equal(new UniverseMapLine("Li03", "Li01", false, false, true, 0), model.Lines[1]);
        Assert.Equal(["Jump gate to Li01. The player cannot jump back."], model.System("Li03")!.Connections);
    }

    /// <summary>
    /// A place outside the grid widens the area to show, because the nav map of the game shows the system off the map. A
    /// system without a place is not on the map, and the note names it. A jump to a system that the universe does not
    /// have makes no line, and its tooltip names the nickname of the goto line.
    /// </summary>
    [Fact]
    public void APlaceOutsideTheGridWidensTheAreaAndASystemWithoutAPlaceIsNotOnTheMap()
    {
        using var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ListWorkspace.UniversePath, """
            [system]
            nickname = Li01
            file = systems\Li01\li01.ini
            pos = 7, 8
            [system]
            nickname = Li02
            file = systems\Li02\li02.ini
            [system]
            nickname = Li03
            file = systems\Li03\li03.ini
            pos = 17, -1
            """);
        ws.WriteBase(ConnectionWorkspace.Li03Path, ConnectionWorkspace.Li03Text.Replace("archetype = jumphole",
            "archetype = jumphole\ngoto = Zz09, Zz09_hole, gate_tunnel_bretonia", StringComparison.Ordinal));

        var model = Model(ws.Session("act"));

        Assert.Equal((new Vector2(0, -1), new Vector2(17, UniverseModel.GridHeight)), model.Bounds);
        Assert.Null(model.System("Li02")!.Place);
        Assert.Equal(["Li02"], model.Unplaced.Select(s => s.Nickname));
        Assert.DoesNotContain(model.Lines, l => l.From == "Li03" || l.To == "Li03");
        Assert.Equal(["Jump hole to Zz09. The player cannot jump back."], model.System("Li03")!.Connections);
        Assert.Equal("Not on the map because the [system] has no position: Li02.", UniversePanel.UnplacedText(["Li02"]));
    }

    /// <summary>The frame zooms the area with its margin to fit the canvas, with square cells in the middle of the
    /// canvas. A larger y of the grid is lower. A hit finds the nearest placed system within the radius.</summary>
    [Fact]
    public void TheFrameFitsTheAreaAndFindsTheSystemUnderTheMouse()
    {
        using var ws = ConnectionWorkspace.Create();
        var model = Model(ws.Session("act"));

        var frame = UniverseFrame.Fit(model.Bounds, new Vector2(10, 20), new Vector2(400, 310));

        // The area with its margin is 16.5 by 15.5 cells, so the height decides the scale.
        Assert.Equal(20, frame.Scale, 3);
        Assert.Equal(new Vector2(10 + (400 - 16.5f * 20) / 2 + 0.75f * 20, 20 + 0.75f * 20), frame.ToScreen(Vector2.Zero));
        Assert.True(frame.ToScreen(new Vector2(0, 9)).Y > frame.ToScreen(new Vector2(0, 8)).Y);
        var li01 = frame.ToScreen(new Vector2(7, 8));
        Assert.Same(model.System("Li01"), frame.Hit(model.Systems, li01 + new Vector2(3, 4), 10));
        Assert.Null(frame.Hit(model.Systems, li01 + new Vector2(0, -11), 10));
        Assert.Equal(0, UniverseFrame.Fit(model.Bounds, Vector2.Zero, Vector2.Zero).Scale);
    }

    /// <summary>A dashed line has dashes of the given length with the given space between them, and the last dash ends at
    /// the end of the line.</summary>
    [Fact]
    public void ADashedLineEndsAtItsEnd()
    {
        var dashes = UniverseFrame.Dashes(Vector2.Zero, new Vector2(25, 0), 6, 4).ToList();

        Assert.Equal([(0f, 6f), (10f, 16f), (20f, 25f)], dashes.Select(d => (d.Start.X, d.End.X)));
        Assert.Empty(UniverseFrame.Dashes(Vector2.One, Vector2.One, 6, 4));
    }

    /// <summary>The side of an offset comes from the order of the nicknames. So two lines between the same two systems
    /// with two directions go to two sides.</summary>
    [Fact]
    public void TwoLinesWithTwoDirectionsGoToTwoSides()
    {
        var pair = new UniverseMapLine("Li01", "Li02", false, false, false, -0.5f);
        var back = new UniverseMapLine("Li02", "Li01", false, false, true, 0.5f);
        var (a, b) = (new Vector2(0, 0), new Vector2(0, 100));

        var first = UniverseFrame.Segment(pair, a, b, 4);
        var second = UniverseFrame.Segment(back, b, a, 4);

        Assert.Equal(-first.Start.X, second.Start.X, 3);
        Assert.NotEqual(0, first.Start.X, 3);
        Assert.Equal((a, b), UniverseFrame.Segment(pair with { Offset = 0 }, a, b, 4));
    }

    /// <summary>
    /// The map marks a new system as the Database does. A click on a saved system selects the system of the loaded game
    /// data, so that its full page shows. A click on a new system selects its <c>[system]</c>.
    /// </summary>
    [Fact]
    public void TheMapMarksANewSystemAndAClickSelectsItsItem()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        var map = new UniverseMap();

        var model = map.Of(session, Texts(session), 0, Data, LoadedSystem);

        Assert.Equal([false, false, false, true], model.Systems.Select(s => s.IsNew));
        Assert.Same(Loaded[0], map.ItemOf("li01"));
        Assert.Equal(session.SystemSection("Av01"), map.ItemOf("Av01"));
        Assert.IsType<SectionRef>(map.ItemOf("Av01"));
        Assert.Null(map.ItemOf("Zz09"));
    }

    /// <summary>
    /// The map keeps its model until the edit view or the loaded game data change. While a gesture is open, it keeps the
    /// model of the start of the gesture, also when a step moves a system. The end of the gesture makes the model again
    /// with the new place.
    /// </summary>
    [Fact]
    public void TheMapMakesTheModelAgainAfterAnEditButNotDuringAGesture()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var texts = Texts(session);
        // The version of the editor session changes at each change of the edit view.
        var version = 0;
        session.Changed += _ => version++;
        var map = new UniverseMap();
        var first = map.Of(session, texts, version, Data, LoadedSystem);
        Assert.Same(first, map.Of(session, texts, version, Data, LoadedSystem));
        var li01 = session.SystemSection("Li01")!;

        var gesture = CommandTransaction.Begin(session, "Move Li01");
        foreach (var x in new[] { 1, 2 })
        {
            Assert.Equal(EditStatus.Applied, gesture.Apply(_ =>
                new CommandPlan(EditCommand.SetLinesFromText(li01, "pos", [$"{x}, 2"]), [li01])).Status);
            Assert.Same(first, map.Of(session, texts, version, Data, LoadedSystem));
        }

        Assert.Equal(2, version);
        gesture.Commit();

        var after = map.Of(session, texts, version, Data, LoadedSystem);
        Assert.NotSame(first, after);
        Assert.Equal(new Vector2(2, 2), after.System("Li01")!.Place);
        Assert.Same(after, map.Of(session, texts, version, Data, LoadedSystem));
        Assert.NotSame(after, map.Of(session, texts, version, new object(), LoadedSystem));
    }

    /// <summary>
    /// A selected system gets the ring, and its lines are wider. A selected object of a system file gives the ring to its
    /// system. A selected end of a jump pair also makes the line of the pair wider, and another object makes no line
    /// wider.
    /// </summary>
    [Fact]
    public void ASelectionPutsTheRingOnItsSystemAndWidensTheLineOfAJump()
    {
        using var ws = ConnectionWorkspace.Create();
        var session = ws.Session("act");
        var map = new UniverseMap();
        var model = map.Of(session, Texts(session), 0, Data, LoadedSystem);
        var line = Assert.Single(model.Lines);

        var system = map.Highlight(session, Loaded[0]);
        Assert.Equal(new UniverseHighlight("Li01", null, true), system);
        Assert.True(system.HasRing(model.System("Li01")!));
        Assert.True(system.IsWide(line));

        var section = map.Highlight(session, session.SystemSection("Li02"));
        Assert.True(section.HasRing(model.System("Li02")!));
        Assert.True(section.IsWide(line));

        Assert.Equal(new UniverseHighlight("Li01", line, false), map.Highlight(session, Gate));
        var otherEnd = ListWorkspace.Object(ListWorkspace.Li02Path, "Li02_to_Li01");
        Assert.Equal(new UniverseHighlight("Li02", line, false), map.Highlight(session, otherEnd));

        var station = map.Highlight(session, ListWorkspace.Object(ConnectionWorkspace.Li03Path, "Li03_station"));
        Assert.Equal(new UniverseHighlight("Li03", null, false), station);
        Assert.False(station.IsWide(line));
        Assert.False(station.HasRing(model.System("Li01")!));

        Assert.Equal(default, map.Highlight(session, null));
        Assert.Equal(default, map.Highlight(session, "text"));
    }

    /// <summary>
    /// A label shows under its dot, else over it. It shows only in the canvas, where it hits no label and no dot of
    /// another system. A label that has no space does not show. The labels of the selected and the hovered system always
    /// show under their dots, and the other labels give way to them.
    /// </summary>
    [Fact]
    public void ALabelShowsOnlyWhereItHasSpace()
    {
        Vector2?[] dots =
        [
            new Vector2(100, 100), new Vector2(110, 100), new Vector2(300, 100), new Vector2(200, 200),
            new Vector2(200, 215), null, new Vector2(390, 395),
        ];
        var sizes = Enumerable.Repeat(new Vector2(30, 10), dots.Length).ToList();
        var canvas = (Vector2.Zero, new Vector2(400, 400));

        var places = UniverseFrame.Labels(dots, sizes, 5, 11, canvas, []);

        Assert.Equal(new Vector2?[]
        {
            // Under A. B is too near, so its label goes over its dot.
            new Vector2(85, 111), new Vector2(95, 79),
            // C has space under its dot.
            new Vector2(285, 111),
            // Under D is the dot of E, so the label of D goes over its dot.
            new Vector2(185, 179), new Vector2(185, 226),
            // A system without a place has no label. At the corner of the canvas, neither place is in the canvas.
            null, null,
        }, places);

        var selected = UniverseFrame.Labels(dots, sizes, 5, 11, canvas, [1, 6]);

        Assert.Equal(new Vector2(95, 111), selected[1]);
        Assert.Equal(new Vector2(85, 79), selected[0]);
        Assert.Equal(new Vector2(375, 406), selected[6]);
    }

    /// <summary>The texts of the panel pass the STE lint.</summary>
    [Fact]
    public void TheTextsPassTheSteLint()
    {
        string[] texts =
        [
            UniversePanel.Help.WhatItIs, UniversePanel.Help.WhatItWrites, UniversePanel.NoSystems.Message,
            UniversePanel.NoSystems.Next, UniversePanel.UnplacedText(["Av01"]), SystemEntry.NewMark,
            UniverseModel.ConnectionText(new UniverseConnectionRow("obj:a", JumpKind.AirlockGate, "system:Li01",
                "system:Li02", null, false, JumpLock.Both), "New York (Li02)"),
        ];

        Assert.All(texts, t => Assert.Empty(SteLint.Problems(t)));
    }
}
