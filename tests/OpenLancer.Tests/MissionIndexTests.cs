using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The raw layer, the shared trigger rules and the index of mission scripts (sections 14.2, 14.3 and 12.1 of the M6a
/// design), on synthetic scripts.
/// </summary>
public sealed class MissionIndexTests
{
    [Fact]
    public void KeysBeforeTheNicknameStayInTheRawLayerAndDoNotCount()
    {
        var index = Index("""
            [Trigger]
            Cnd_True = no_params
            repeatable = true
            nickname = t1
            Cnd_Timer = 5
            """);

        var trigger = Assert.Single(index.Triggers);
        Assert.Equal(4, trigger.Section.Section.Count);
        Assert.Equal([0, 1], trigger.LinesBeforeNickname);
        Assert.False(trigger.Repeats);
        Assert.Equal("Cnd_Timer", Assert.Single(trigger.Conditions).Key);
    }

    [Fact]
    public void TheLastConditionAfterTheNicknameIsEffective()
    {
        var index = Index("""
            [Trigger]
            nickname = t1
            Cnd_True = no_params
            Cnd_Timer = 5
            """);

        var trigger = Assert.Single(index.Triggers);
        Assert.Equal(2, trigger.Conditions.Count);
        Assert.Equal("Cnd_Timer", trigger.Condition!.Key);
        Assert.Equal(2, TriggerRules.EffectiveCondition(trigger.Section.Section));
        Assert.Equal([1, 2], TriggerRules.ConditionLines(trigger.Section.Section));
    }

    [Fact]
    public void AConditionBeforeTheNicknameDoesNotCount()
    {
        var index = Index("[Trigger]\nCnd_True = no_params\nnickname = t1\nAct_ActTrig = t1\n");

        Assert.Equal(-1, TriggerRules.EffectiveCondition(index.Triggers[0].Section.Section));
        Assert.Null(index.Triggers[0].Condition);
    }

    [Fact]
    public void TheSectionTypesCompareWithoutLetterCaseAndUnknownSectionsStay()
    {
        var document = Document("[Objlist]\nnickname = ol1\nGotoVec = goto, 1, 2, 3, 100\n\n[Unknown]\nkey = value\n" +
                                "\n[MsnShip]\nnickname = s1\nBreakFormation\n");

        Assert.Equal(MissionSectionKind.OrderList, document.Sections[0].Kind);
        Assert.Equal(MissionSectionKind.Other, document.Sections[1].Kind);
        // A line without '=' stays as a key without a value.
        Assert.Empty(document.Sections[2].Section[1]);
        Assert.Equal("BreakFormation", document.Sections[2].Section[1].Name);
    }

    [Fact]
    public void ARepeatedTriggerNicknameKeepsTheFirstTrigger()
    {
        var index = Index("[Trigger]\nnickname = t1\nCnd_True = no_params\n\n[Trigger]\nnickname = T1\nCnd_Timer = 2\n");

        Assert.Equal(2, index.Triggers.Count);
        Assert.False(index.Triggers[0].IsDuplicate);
        Assert.True(index.Triggers[1].IsDuplicate);
        Assert.Same(index.Triggers[0], index.Trigger("t1"));
    }

    [Fact]
    public void ARepeatedOrderListKeepsTheFirstAndRepeatedShipsKeepBoth()
    {
        var index = Index("[ObjList]\nnickname = ol1\nDelay = 1\n\n[ObjList]\nnickname = ol1\nDelay = 2\n\n" +
                          "[MsnShip]\nnickname = s1\n\n[MsnShip]\nnickname = s1\n");

        Assert.Equal(2, index.Items(MissionSectionKind.OrderList, "ol1").Count);
        Assert.Same(index.Document.Sections[0], index.Item(MissionSectionKind.OrderList, "OL1"));
        var ships = index.Items(MissionSectionKind.Ship, "s1");
        Assert.Equal(2, ships.Count);
        Assert.NotEqual(ships[0].Ref, ships[1].Ref);
    }

    [Fact]
    public void TheLinksHaveTheirKindsAndFacts()
    {
        var index = Index("""
            [Trigger]
            nickname = t1
            InitState = ACTIVE
            Cnd_True = no_params
            Act_DeactTrig = t2
            Act_ActTrig = t2
            Act_ActTrig = t3
            Act_DeactTrig = t3
            Act_ActTrig = t1
            Act_Save = t4

            [Trigger]
            nickname = t2
            Cnd_WatchTrigger = t3, ON

            [Trigger]
            nickname = t3
            Cnd_True = no_params

            [Trigger]
            nickname = t4
            Cnd_True = no_params
            """);

        var kinds = index.Links.GroupBy(l => l.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, kinds[MissionLinkKind.Start]);
        Assert.Equal(2, kinds[MissionLinkKind.Stop]);
        Assert.Equal(1, kinds[MissionLinkKind.Load]);
        Assert.Equal(1, kinds[MissionLinkKind.Watch]);
        Assert.Equal("ON", index.Links.Single(l => l.Kind == MissionLinkKind.Watch).State);
        Assert.Equal("t2", Assert.Single(index.Restarts).TargetName);
        Assert.Equal("t3", Assert.Single(index.Cancels).TargetName);
        Assert.Equal("t1", Assert.Single(index.SelfStarts).Nickname);
        // The start trigger reaches t2 and t3 over Start links and t4 over the Load link.
        Assert.Equal(4, index.Reached.Count);
    }

    [Fact]
    public void NamesResolveWithoutLetterCase()
    {
        var index = Index(Story + "\n[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_GiveObjList = SHIP1, OL1\n" +
                          "\n[MsnShip]\nnickname = ship1\n\n[ObjList]\nnickname = ol1\n");

        var line = index.Lines.Single(l => l.Key == "Act_GiveObjList");
        Assert.All(line.References, r => Assert.Equal(MissionReferenceState.Found, r.State));
        Assert.Contains(index.ReferencesTo(MissionValueKind.Ship, "ship1"), r => r.Line == line);
    }

    [Fact]
    public void TokensThatTheGameComparesWithLetterCaseGetTheRightWord()
    {
        var index = Index("[Trigger]\nnickname = t1\nCnd_HasMsn = Yes\nAct_LockDock = player, gate1, lock\n" +
                          "\n[Trigger]\nnickname = t2\nCnd_MsnResponse = ACCEPT\n");

        var wrong = index.Lines.SelectMany(l => l.References).Where(r => r.State == MissionReferenceState.WrongCase)
            .Select(r => (r.Name, r.Expected)).ToList();
        Assert.Equal([("Yes", "yes"), ("player", "Player")], wrong);
        // accept compares without letter case.
        Assert.Contains(index.Lines.SelectMany(l => l.References),
            r => r.Name == "ACCEPT" && r.State == MissionReferenceState.Token);
    }

    [Fact]
    public void TheSystemOfAShipComesFromItsSpawnTrigger()
    {
        var index = Index("""
            [Trigger]
            nickname = t1
            system = sys_a
            Cnd_True = no_params
            Act_SpawnShip = ship1

            [MsnShip]
            nickname = ship1

            [MsnShip]
            nickname = ship2
            system = sys_b
            """);

        Assert.Equal("sys_a", index.SystemOf(index.Item(MissionSectionKind.Ship, "ship1")!));
        Assert.Equal("sys_b", index.SystemOf(index.Item(MissionSectionKind.Ship, "ship2")!));
    }

    [Fact]
    public void AnObjectOfAnySystemFileResolves()
    {
        var world = World(new() { [MissionValueKind.SystemObject] = ["gate_of_any_system"] });
        var index = Index("[Trigger]\nnickname = t1\nCnd_True = no_params\nAct_LockDock = Player, gate_of_any_system, lock\n",
            world);

        var reference = index.Lines.Single(l => l.Key == "Act_LockDock").References.Single(r => r.ValueIndex == 1);
        Assert.Equal(MissionReferenceState.Found, reference.State);
    }

    [Fact]
    public void TheReverseReferencesOfAShipALabelAndABase()
    {
        var world = World(new() { [MissionValueKind.Base] = ["base_a"] });
        var index = Index("""
            [Trigger]
            nickname = t1
            Cnd_BaseEnter = base_a
            Act_SpawnShip = ship1
            Act_SetVibe = wing, Player, -1

            [MsnShip]
            nickname = ship1
            label = wing
            """, world);

        Assert.Single(index.ReferencesTo(MissionValueKind.Ship, "ship1"));
        Assert.Single(index.ReferencesTo(MissionValueKind.Object, "wing"));
        Assert.Single(index.ReferencesTo(MissionValueKind.Base, "base_a"));
        Assert.Single(index.Labels["wing"]);
    }

    [Fact]
    public void AFormationTakesAnEarlierShipOnly()
    {
        var index = Index("""
            [MsnShip]
            nickname = early

            [MsnFormation]
            nickname = form1
            formation = fighter_wedge
            ship = early
            ship = late

            [MsnShip]
            nickname = late
            """);

        Assert.Equal("form1", index.TakenShips["early"].Nickname);
        Assert.False(index.TakenShips.ContainsKey("late"));
        Assert.Equal("late", Assert.Single(index.LaterMembers).Ship);
    }

    /// <summary>The last <c>repeatable</c> line after the nickname counts, as for
    /// <c>InitState</c> (TR10).</summary>
    [Fact]
    public void TheLastRepeatableLineCounts()
    {
        var index = Index("""
            [Trigger]
            nickname = t1
            repeatable = true
            repeatable = false
            Cnd_True = no_params

            [Trigger]
            nickname = t2
            repeatable = false
            repeatable = true
            Cnd_True = no_params
            """);

        Assert.False(index.Trigger("t1")!.Repeats);
        Assert.True(index.Trigger("t2")!.Repeats);
    }

    /// <summary>A start after a stop restarts the target, so a stop between two starts is not
    /// a cancel (TR9).</summary>
    [Fact]
    public void AStartAfterACancelRestartsTheTarget()
    {
        var index = Index("""
            [Trigger]
            nickname = t1
            Cnd_True = no_params
            Act_ActTrig = t2
            Act_DeactTrig = t2
            Act_ActTrig = t2

            [Trigger]
            nickname = t2
            Cnd_True = no_params
            """);

        Assert.Empty(index.Cancels);
        Assert.Equal(4, Assert.Single(index.Restarts).Line.Entry);
    }

    /// <summary>The game skips a trigger without a nickname value. The mission checks and the
    /// target checks use one rule for it (decision D556).</summary>
    [Fact]
    public void ATriggerWithoutANicknameValueIsSkipped()
    {
        var index = Index("""
            [Trigger]
            nickname =
            Cnd_True = no_params

            [Trigger]
            nickname = t1
            Cnd_True = no_params
            """);

        Assert.Equal("t1", Assert.Single(index.Triggers).Nickname);
        Assert.Null(TriggerRules.Nickname(index.Document.Sections[0].Section));
        Assert.Equal("t1", TriggerRules.Nickname(index.Document.Sections[1].Section));
    }

    /// <summary>One rule names a line in the checks and in the source text of a start place (issue #10): the plain name
    /// of a condition or an action, else the label of a section key, else the key.</summary>
    [Fact]
    public void ALineHasThePlainNameOfItsConditionActionOrKey()
    {
        var index = Index("""
            [MsnLoot]
            nickname = ol_loot
            archetype = ol_crate

            [Trigger]
            nickname = t1
            Cnd_SpaceEnter = ol_sys_a
            act_moveplayer = 1, 2, 3, 0
            Act_NoSuchAction = 1
            """);

        var trigger = Assert.Single(index.Triggers);
        Assert.Equal("On enter space", trigger.Condition!.Plain);
        Assert.Equal(["Move player", "Act_NoSuchAction"], trigger.Actions.Select(a => a.Plain));
        Assert.Equal(["Nickname", "Archetype"], index.LinesOf(index.Document.Sections[0]).Select(l => l.Plain));
    }
}
