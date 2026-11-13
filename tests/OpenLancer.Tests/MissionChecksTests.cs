using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Missions;
using Xunit;
using static OpenLancer.Tests.MissionTestData;

namespace OpenLancer.Tests;

/// <summary>
/// The mission checks (sections 12 and 13.6 of the M6a design): each rule fires on a synthetic script and gives its
/// message and its severity.
/// </summary>
public sealed class MissionChecksTests
{
    private static string With(string text) => Story + "\n" + text;

    [Fact]
    public void ACleanScriptHasNoProblem() => Assert.Empty(Check(Story));

    [Fact]
    public void M01ATriggerWithoutAConditionIsAnError()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nAct_ActTrig = t_end\n")), "M01"));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("Trigger 't2' has no condition. The game stops when this trigger starts. Add one condition, for " +
                     "example Cnd_True = no_params.", problem.Message);
        Assert.Equal(TriggerRules.NoConditionMessage("t2"), problem.Message);
        Assert.Equal("M01|t2", problem.Subject);
    }

    [Fact]
    public void M02ARepeatedTriggerNameIsAnError()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t_end\nCnd_True = no_params\n")), "M02"));

        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.StartsWith("Two triggers have the name 't_end'. The game uses the first one", problem.Message);
    }

    [Fact]
    public void M03TheFirstOfTwoConditionsGetsAWarning()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nCnd_Timer = 5\n")), "M03"));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("Trigger 't2' has 2 conditions. The game uses only the last one, Cnd_Timer. Keep one condition.",
            problem.Message);
        Assert.Equal(1, problem.EntryIndex);
    }

    [Fact]
    public void M04EachLineBeforeTheNicknameGetsAWarning()
    {
        var problems = Rule(Check(With("[Trigger]\nsystem = sys_a\nrepeatable = true\nnickname = t2\nCnd_True = no_params\n")), "M04");

        Assert.Equal([0, 1], problems.Select(p => p.EntryIndex));
        Assert.Equal("Line 'system' comes before the nickname of trigger 't2'. The game ignores this line. Move it after " +
                     "the nickname.", problems[0].Message);
    }

    [Fact]
    public void M05AStartOfAMissingTriggerIsAnError()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_ActTrig = no_such\n")), "M05"));

        Assert.Equal("'Start trigger' names trigger 'no_such'. This script has no such trigger. The game ignores this line. " +
                     "Pick a trigger of this script.", problem.Message);
        Assert.Equal("M05|t2|no_such", problem.Subject);
    }

    [Fact]
    public void M06AMissingOrderListIsAnError()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_GiveObjList = Player, ol_x\n")), "M06"));

        Assert.Equal("'Give orders' names order list 'ol_x'. This script has no such order list. The game ignores this " +
                     "line. Pick an order list of this script.", problem.Message);
    }

    [Fact]
    public void M07ASpawnOfAShipThatAFormationTakesIsAnError()
    {
        var problems = Check(With("""
            [MsnShip]
            nickname = ship1

            [MsnFormation]
            nickname = form1
            ship = ship1

            [Trigger]
            nickname = t2
            Cnd_True = no_params
            Act_SpawnShip = ship1
            """));

        Assert.Equal("Formation 'form1' takes ship 'ship1', so this spawn spawns nothing. Spawn formation 'form1' instead.",
            Assert.Single(Rule(problems, "M07")).Message);
    }

    [Fact]
    public void M08AndM09TheOrderOfTheShipLines()
    {
        var problems = Check(With("""
            [MsnFormation]
            nickname = form1
            formation = fighter_wedge
            ship = ship1
            label = late_label

            [MsnShip]
            nickname = ship1
            """));

        Assert.Equal("Formation 'form1' names ship 'ship1', which comes later in the file. The game does not put the ship " +
                     "in the formation. Move ship 'ship1' before the formation.", Assert.Single(Rule(problems, "M08")).Message);
        Assert.Equal("Line 'label' comes after the 'formation' line in 'form1'. The game can stop with this order. Move " +
                     "'label' before 'formation'.", Assert.Single(Rule(problems, "M09")).Message);
    }

    [Fact]
    public void M10AStartAndThenAStopOfOneTargetIsAWarning()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_ActTrig = t_end\n" +
                                                     "Act_DeactTrig = t_end\n")), "M10"));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("Trigger 't2' starts 't_end' and then stops it. The game does not start 't_end'. Remove one of the " +
                     "two lines.", problem.Message);
    }

    [Fact]
    public void M11IsAProblemOnlyWhenTheTargetsHoldOpenLancer()
    {
        var text = With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_GiveMB = Player, mb1\n\n[MetaBehavior]\nnickname = mb1\n");
        var coverage = new TestCoverage("Act_GiveMB");

        Assert.Empty(Rule(Check(text, coverage: coverage), "M11"));
        var problem = Assert.Single(Rule(Check(text, options: new MissionCheckOptions(true), coverage: coverage), "M11"));
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal("OpenLancer cannot load 'Give meta behavior'. The mission does not start in OpenLancer. Remove the " +
                     "line, or remove OpenLancer from the targets of the mod.", problem.Message);
    }

    [Fact]
    public void M12ANameAboveItsBufferIsAnError()
    {
        var name = new string('a', 48);
        var problem = Assert.Single(Rule(Check(With($"[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_Destroy = {name}\n")), "M12"));

        Assert.Equal($"The object name '{name}' has 48 characters. The game reads at most 47 characters, so it does not " +
                     "find the item. Use a shorter object name.", problem.Message);
    }

    [Fact]
    public void M13AWordInTheWrongLetterCaseIsAnError()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_BaseEnter = any\n")), "M13"));

        Assert.Equal("'any' must be 'ANY'. The game compares this word with letter case, so the line does nothing. " +
                     "Write 'ANY'.", problem.Message);
    }

    [Fact]
    public void M14AnotherBooleanIsAWarningWithTheTextOfTheTargetChecks()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_Cloak = Player, yes\n")), "M14"));

        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal(TriggerRules.BooleanMessage("yes"), problem.Message);
        Assert.Equal("'yes' is not true or false. The game reads only true, 1, false and 0. For another value, it uses " +
                     "false. Pick true or false.", problem.Message);
    }

    [Fact]
    public void M15AFractionInAWholeNumberIsAWarning()
    {
        var problem = Assert.Single(Rule(Check(With("[ObjList]\nnickname = ol1\nGotoVec = goto, 1.5, 2, 3, 100\n")), "M15"));

        Assert.Equal("The game reads only the whole part of the point. So 1.5 becomes 1. Use a whole number.",
            problem.Message);
    }

    [Fact]
    public void M16AndM17TheTypeOfAnObjective()
    {
        var problems = Check(With("[NNObjective]\nnickname = o1\nstate = HIDDEN\n\n[NNObjective]\nnickname = o2\ntype = 9\n" +
                                  "\n[NNObjective]\nnickname = o3\ntype = ids, 100\n"));

        Assert.Equal("Objective 'o1' has no valid type. The game drops this objective. Pick a type.",
            Assert.Single(Rule(problems, "M16"), p => p.Subject == "M16|o1").Message);
        Assert.Equal("Objective 'o2' has type 9. The game stops with a type outside 1 to 8. Pick a type from 1 to 8.",
            Assert.Single(Rule(problems, "M17")).Message);
        Assert.DoesNotContain(problems, p => p.Subject?.Contains("o3") == true);
    }

    [Fact]
    public void M18AndM19TheStartAndTheSpeakersOfAConversation()
    {
        var problems = Check(With("""
            [Dialog]
            system = sys_a
            nickname = d1

            [Dialog]
            nickname = d2
            system = sys_a
            line = ship1, Player, line_x

            [MsnShip]
            nickname = ship1
            system = sys_b
            """));

        Assert.Equal("Conversation 'd1' does not start with its nickname, or it has no system. The game drops this " +
                     "conversation. Put the nickname first, and add a system.", Assert.Single(Rule(problems, "M18")).Message);
        Assert.Equal("Speaker 'ship1' of conversation 'd2' is not in system sys_a. The conversation does not start. " +
                     "Spawn the speaker in sys_a.", Assert.Single(Rule(problems, "M19")).Message);
    }

    [Fact]
    public void M20ADelayOutsideTheRangeIsAWarning()
    {
        var problem = Assert.Single(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_StartDialog = d1, 0\n" +
                                                     "\n[Dialog]\nnickname = d1\nsystem = sys_a\n")), "M20"));

        Assert.Equal("The delay 0 is not -1 and not a value from 1 to 10. The effect in the game is not known. Use -1 or " +
                     "a value from 1 to 10.", problem.Message);
    }

    [Fact]
    public void M21M22AndM23NamesOutsideTheScript()
    {
        var world = World(new()
        {
            [MissionValueKind.CommLine] = ["line_known"],
            [MissionValueKind.System] = ["sys_a"],
        }, @"MISSIONS\known.thn");
        var problems = Check(With("""
            [Trigger]
            nickname = t2
            Cnd_SystemEnter = sys_x
            Act_SendComm = Player, Player, line_x
            Act_CallThorn = missions\\missing.thn
            Act_CallThorn = missions\\known.thn
            """), world);

        Assert.Equal("Comm line 'line_x' is in no voices file. The game plays no sound for this line. Pick a comm line " +
                     "from the list.", Assert.Single(Rule(problems, "M21")).Message);
        Assert.Equal("File 'missions\\\\missing.thn' does not exist. The game loads nothing for this line. Pick a file " +
                     "that exists.", Assert.Single(Rule(problems, "M22")).Message);
        Assert.Equal("'On enter system' names system 'sys_x'. The game data has no system with this name. The game ignores " +
                     "this line. Pick a system from the list.", Assert.Single(Rule(problems, "M23")).Message);
    }

    [Fact]
    public void M24AndM25UnknownKeysAndSections()
    {
        var problems = Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_NoSuchAction = 1\n\n[NoSuchSection]\nkey = 1\n"));

        Assert.Equal("Line 'Act_NoSuchAction' is not a key of [Trigger]. The game ignores this line. Check the name of the " +
                     "key.", Assert.Single(Rule(problems, "M24")).Message);
        Assert.Equal("Section type [NoSuchSection] is not a known type of a mission script. The editor does not check " +
                     "this section. Make sure that the type name is correct.", Assert.Single(Rule(problems, "M25")).Message);
    }

    [Fact]
    public void TheGraphRules()
    {
        var problems = Check("""
            [Trigger]
            nickname = t1
            Cnd_True = no_params
            Act_ChangeState = FAIL
            Act_CallThorn = missions\\clip.thn

            [Trigger]
            nickname = t2
            Cnd_True = no_params
            Act_ChangeState = SUCCEED
            Act_ChangeState = SUCCEED
            """);

        Assert.Equal("This mission has no start trigger. Nothing in the mission runs. Make one trigger start with the " +
                     "mission.", Assert.Single(Rule(problems, "G01")).Message);
        Assert.Single(Rule(problems, "G02"));
        Assert.Equal("This mission has 2 success lines. The story moves on at the first one that runs. Keep one success " +
                     "line.", Assert.Single(Rule(problems, "G03")).Message);
        Assert.Equal("Trigger 't1' fails the mission without a text. The player sees no reason for the fail. Add a fail " +
                     "text.", Assert.Single(Rule(problems, "G04")).Message);
        Assert.Equal(["G05|t1", "G05|t2"], Rule(problems, "G05").Select(p => p.Subject));
        Assert.Equal("No trigger after 't1' ends the camera of cutscene 'missions\\\\clip.thn'. The camera does not come " +
                     "back to the player. Add \"End camera\" to a later trigger.", Assert.Single(Rule(problems, "G06")).Message);
    }

    [Fact]
    public void ALaterEndCameraEndsTheCutscene()
    {
        var problems = Check(With("""
            [Trigger]
            nickname = t2
            Cnd_True = no_params
            Act_CallThorn = missions\\clip.thn
            Act_ActTrig = t3

            [Trigger]
            nickname = t3
            Cnd_Timer = 5
            Act_RevertCam = no_params
            """));

        Assert.Empty(Rule(problems, "G06"));
    }

    [Fact]
    public void EachRuleHasItsSeverity()
    {
        Assert.Equal(DiagnosticSeverity.Error, MissionChecks.SeverityOf("M05"));
        Assert.Equal(DiagnosticSeverity.Warning, MissionChecks.SeverityOf("G05"));
        Assert.Equal(DiagnosticSeverity.Warning, MissionChecks.SeverityOf("M26"));
        Assert.Equal(DiagnosticSeverity.Error, MissionChecks.SeverityOf("M27"));
        Assert.Equal(DiagnosticSeverity.Warning, MissionChecks.SeverityOf("M28"));
        Assert.Equal(37, MissionChecks.Rules.Count);
    }

    [Fact]
    public void TheArticleOfAKindWordFollowsItsSound()
    {
        Assert.Equal(["an order list", "an NPC", "an objective", "an equipment item", "a ship build", "a system"],
            new[] { "order list", "NPC", "objective", "equipment item", "ship build", "system" }.Select(MissionChecks.WithArticle));
    }

    /// <summary>A word outside a closed set of words gives a problem and a raw mark. The
    /// game reads a word outside an open set as its default, so such a word gives none.</summary>
    [Fact]
    public void M26AWordOutsideAClosedSetIsAProblem()
    {
        var problems = Check(With("[Trigger]\nnickname = t2\nCnd_PopUpDialog = BUTTONX\nAct_ActTrig = t1\n\n" +
                                  "[Trigger]\nnickname = t3\nCnd_True = no_params\nAct_LockDock = Player, x, lockx\n" +
                                  "Act_ActTrig = t2\n"));

        var words = Rule(problems, "M26");
        Assert.Equal(2, words.Count);
        Assert.StartsWith("'BUTTONX' is not a word of '", words[0].Message);
        Assert.Contains("The effect in the game is not known. Write one of these words: YES, NO, LATER, CLOSE.",
            words[0].Message);
        Assert.All(words, w => Assert.Equal(DiagnosticSeverity.Warning, w.Severity));
        Assert.Empty(Rule(Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_NNIds = 5, OTHER\n")), "M26"));
    }

    /// <summary>The game finds a trigger by the FLHash id of its name. Two triggers with different names and one id
    /// give M27 on the second trigger.</summary>
    [Fact]
    public void M27TwoTriggersWithOneGameIdAreAnError()
    {
        var (first, second) = DataListsTests.FlHashPair();
        var problems = Check(With($"[Trigger]\nnickname = {first}\nCnd_True = no_params\n\n" +
                                  $"[Trigger]\nnickname = {second}\nCnd_True = no_params\n"));

        var problem = Assert.Single(Rule(problems, "M27"));
        Assert.Equal(DiagnosticSeverity.Error, problem.Severity);
        Assert.Equal($"Triggers '{first}' and '{second}' have the same game id. The game keeps only the first of the two. " +
                     "Rename one of them.", problem.Message);
        Assert.Equal(second, problem.Target!.Id.Key);
        Assert.Equal($"M27|{first}|{second}", problem.Subject);
        // Two triggers with one name give M02, not M27.
        Assert.Empty(Rule(Check(With($"[Trigger]\nnickname = {first}\nCnd_True = no_params\n\n" +
                                     $"[Trigger]\nnickname = {first.ToUpperInvariant()}\nCnd_True = no_params\n")), "M27"));
    }

    /// <summary>A name that no retail script uses gives M28 (decision D425).</summary>
    [Fact]
    public void M28ANameWithNoRetailExampleIsAWarning()
    {
        var problems = Check(With("[Trigger]\nnickname = t2\nCnd_RumorHeard = rumor_a, room_a, base_a\nAct_ActTrig = t_end\n"));

        var problem = Assert.Single(Rule(problems, "M28"));
        Assert.Equal(DiagnosticSeverity.Warning, problem.Severity);
        Assert.Equal("The game data has no example of 'On rumor heard'. Its effect is not checked. Test the mission in the " +
                     "game.", problem.Message);
        Assert.Equal(1, problem.EntryIndex);
        Assert.Empty(Rule(Check(Story), "M28"));
    }

    /// <summary>A name of the label rule that names no item of the script and no
    /// object of a system gives M06 with a text for both places.</summary>
    [Fact]
    public void M06AMissingObjectNamesTheScriptAndTheSystems()
    {
        var world = World(new() { [MissionValueKind.SystemObject] = ["gate_a"] });
        var problems = Check(With("[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_LockDock = Player, gate_a, lock\n" +
                                  "Act_LockDock = Player, gate_x, lock\n"), world);

        var problem = Assert.Single(Rule(problems, "M06"));
        Assert.Equal("'Lock or unlock dock' names 'gate_x'. This script has no ship, formation, solar, loot or label with " +
                     "this name, and no system has an object with this name. The game ignores this line. Pick a name from " +
                     "the list.", problem.Message);
    }

    /// <summary>G02 names the success line that no start trigger reaches.</summary>
    [Fact]
    public void G02NamesASuccessThatNoStartTriggerReaches()
    {
        var problems = Check("[Mission]\n\n[Trigger]\nnickname = t1\nInitState = ACTIVE\nCnd_True = no_params\n\n" +
                             "[Trigger]\nnickname = t2\nCnd_True = no_params\nAct_ChangeState = SUCCEED\n");

        var problem = Assert.Single(Rule(problems, "G02"));
        Assert.Equal("No start trigger leads to the success line of trigger 't2'. The story cannot go to the next mission. " +
                     "Start trigger 't2' from a trigger that runs.", problem.Message);
        Assert.Equal(2, problem.EntryIndex);
    }

    /// <summary>Coverage with names that OpenLancer cannot load.</summary>
    private sealed class TestCoverage(params string[] cannotLoad) : IEngineCoverage
    {
        public EngineMark Of(string name) =>
            cannotLoad.Contains(name, System.StringComparer.OrdinalIgnoreCase) ? EngineMark.CannotLoad : EngineMark.Runs;

        public IReadOnlyList<RuleDifference> RuleDifferences(MissionIndex index, MissionTrigger trigger) => [];
    }
}
