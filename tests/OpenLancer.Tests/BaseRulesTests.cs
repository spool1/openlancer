using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The list rules of the base files and of mBases.ini (section C.3 of the bases design), the delete action of the dock
/// lines of an object, and the pickers of the keys that name a part of a base.
/// </summary>
public sealed class BaseRulesTests
{
    private static readonly ListRules Rules = ListRules.Retail;

    /// <summary>The rule data has the roles, the list and the rules of the bases, each closed rule with its fork
    /// evidence (decision D109).</summary>
    [Fact]
    public void TheRuleDataHasTheBaseRolesListAndRules()
    {
        Assert.True(Rules.Roles["base_files"].BaseFiles);
        Assert.True(Rules.Roles["base_files"].Dynamic);
        Assert.Equal([@"MISSIONS\mBases.ini"], Rules.Roles["mbases"].Paths);
        var rooms = Rules.Lists["room_of_base"].Source!;
        Assert.Equal(("base_files", true, "Room"), (rooms.Role, rooms.Scoped, rooms.Sections.Single()));

        var start = Assert.Single(Rules.Rules("base_files", "BaseInfo", "start_room"));
        Assert.Equal((true, true, RuleEffect.Crash, "file"), (start.Closed, start.Required, start.Effect, start.Scope));
        Assert.Equal("The game crashes when the player docks here.", start.ConsequenceText);
        foreach (var (section, key, list) in new[]
                 {
                     ("MBase", "nickname", "base"), ("MBase", "local_faction", "faction"), ("BaseFaction", "faction", "faction"),
                     ("GF_NPC", "body", "body"), ("GF_NPC", "head", "head"), ("GF_NPC", "lefthand", "left_hand"),
                     ("GF_NPC", "righthand", "right_hand"), ("GF_NPC", "accessory", "accessory"),
                     ("GF_NPC", "base_appr", "costume"), ("GF_NPC", "affiliation", "faction"),
                 })
        {
            var rule = Assert.Single(Rules.Rules("mbases", section, key));
            Assert.Equal((list, true, RuleEffect.Degrade), (rule.Lists.Single(), rule.Closed, rule.Effect));
            Assert.False(string.IsNullOrWhiteSpace(rule.Evidence));
        }
        // The fork opens the voice bank by its name, not through the voice list. So the rule is open.
        var voice = Assert.Single(Rules.Rules("mbases", "GF_NPC", "voice"));
        Assert.Equal((false, RuleEffect.None), (voice.Closed, voice.Effect));
        Assert.Equal(DeleteAction.Clear, Assert.Single(Rules.Rules("system", "Object", "dock_with")).OnDelete);
        Assert.Equal(DeleteAction.Clear, Assert.Single(Rules.Rules("system", "Object", "base")).OnDelete);
    }

    /// <summary>The data of the fixture gives no list problem, and the list of the rooms holds the rooms of one base
    /// file.</summary>
    [Fact]
    public void TheBaseFilesAndMBasesOfTheFixtureGiveNoListProblem()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal(["Deck", "Bar"], session.Lists.List("room_of_base", BaseWorkspace.StationPath)!.Entries.Select(e => e.Value));
        Assert.Equal(["Cityscape", "ShipDealer"],
            session.Lists.List("room_of_base", BaseWorkspace.PlanetPath)!.Entries.Select(e => e.Value));
        foreach (var path in session.DataFiles.Paths("base_files").Concat(session.DataFiles.Paths("mbases")))
            Assert.All(session.Sections(path), s => Assert.Empty(session.Lists.Problems(s)));
        Assert.Empty(session.SaveProblems());
    }

    /// <summary>A start room that the base does not have crashes the game when the player docks. The match ignores the
    /// letter case, as the engine does. A changed [BaseInfo] without a start room is an error too.</summary>
    [Fact]
    public void TheStartRoomMustBeARoomOfTheSameBaseFile()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var info = BaseWorkspace.Section(session, BaseWorkspace.StationPath, "BaseInfo");

        session.Execute(EditCommand.SetLinesFromText(info, "start_room", ["deck"]));
        Assert.Empty(session.Lists.Problems(info));
        // A room of another base file is not a room of this base.
        session.Execute(EditCommand.SetLinesFromText(info, "start_room", ["Cityscape"]));
        var problem = Assert.Single(session.Lists.Problems(info));
        Assert.Equal((ListProblemKind.Missing, true, "start_room"), (problem.Kind, problem.IsError, problem.Diagnostic.Key));
        Assert.Equal("The game crashes when the player docks here.", problem.Consequence);
        Assert.Contains(session.SaveProblems(), p => p.Severity == DiagnosticSeverity.Error && p.Message.Contains("Cityscape"));

        session.Execute(EditCommand.RemoveName(info, "start_room"));
        var missing = Assert.Single(session.Lists.Problems(info));
        Assert.Equal((ListProblemKind.RequiredMissing, true), (missing.Kind, missing.IsError));
    }

    /// <summary>The look, the faction and the voice of a bar character, the faction of the base, and the base of an
    /// [MBase] name entries of their lists. A value of the active mod outside a closed list is an error. A voice outside
    /// the voice list is a warning. The edits of mBases.ini are patches, and a patch cannot find a [BaseFaction],
    /// because it has no nickname.</summary>
    [Fact]
    public void TheBarDataNamesItsListsAndTheBaseOfTheMBase()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var npc = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC", "ol01_01_npc_1");
        var faction = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "BaseFaction");
        var mbase = BaseWorkspace.Section(session, BaseWorkspace.MBasesPath, "MBase", BaseWorkspace.Station);

        session.Execute(EditCommand.SetLinesFromText(npc, "head", ["no_head"]));
        session.Execute(EditCommand.SetLinesFromText(npc, "base_appr", ["no_costume"]));
        session.Execute(EditCommand.SetLinesFromText(npc, "voice", ["no_voice"]));
        session.Execute(EditCommand.SetLinesFromText(mbase, "local_faction", ["no_grp"]));
        session.Execute(EditCommand.AddSection(BaseWorkspace.MBasesPath, "MBase",
            [new EntryView("nickname", [new StringValue("no_base")])]));

        var problems = session.Lists.Problems(npc);
        Assert.Equal([("base_appr", true), ("head", true), ("voice", false)],
            problems.Select(p => (p.Diagnostic.Key!, p.IsError)).Order());
        Assert.Contains(problems, p => p.Diagnostic.Key == "head" && p.Kind == ListProblemKind.Missing &&
                                       p.Consequence == "The game ignores this value.");
        Assert.NotNull(session.Get(faction)!.ReadOnlyReason);
        Assert.True(Assert.Single(session.Lists.Problems(mbase)).IsError);
        var added = BaseWorkspace.Section(session, BaseWorkspace.MBasesPath, "MBase", "no_base");
        var lost = Assert.Single(session.Lists.Problems(added));
        Assert.Equal(("nickname", "The game does not use the bar data of this block."), (lost.Diagnostic.Key, lost.Consequence));
    }

    /// <summary>A whole mBases.ini of the active mod takes each edit, also of a [BaseFaction]. Its values are values of
    /// the active mod, so a faction outside the list is an error.</summary>
    [Fact]
    public void AWholeMBasesOfTheActiveModGivesTheErrorsOfItsBaseFactions()
    {
        var copy = BaseWorkspace.MBases.Replace("faction = ol_grp\nweight = 100", "faction = no_grp\nweight = 100");
        using var ws = BaseWorkspace.Create(activeFiles: new() { ["files\\" + BaseWorkspace.MBasesPath] = copy });
        var session = ws.Session("act");
        var faction = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "BaseFaction");

        Assert.Null(session.Get(faction)!.ReadOnlyReason);
        var problem = Assert.Single(session.Lists.Problems(faction));
        Assert.Equal(("faction", true), (problem.Diagnostic.Key, problem.IsError));
        session.Execute(EditCommand.SetLinesFromText(faction, "faction", ["ol_pirates"]));
        Assert.Empty(session.Lists.Problems(faction));
    }

    /// <summary>A delete of a [Base] removes the dock_with and base lines of the objects that name it, as safe fixes.
    /// The objects stay, as Unity keeps a GameObject when its referenced asset goes.</summary>
    [Fact]
    public void ADeleteOfABaseClearsTheDockLinesOfItsObjects()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var entry = BaseWorkspace.Section(session, BaseWorkspace.UniversePath, "Base", BaseWorkspace.Planet);

        var plan = ScenePlans.Delete(session, new ScenePlan(SceneAction.Delete, "Delete base"), [], [], [entry]);

        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals));
        Assert.Equal([("Dock_Ring_Ol01_02", "dock_with"), ("Ol01_02", "base")],
            plan.Fixes.Select(f => (f.Section.Id.Key!, f.Key)).Order());
        Assert.Empty(plan.Deletes);
        Assert.DoesNotContain(plan.Missing, m => m.Reference.Section.Path == BaseWorkspace.Ol01Path);
    }

    /// <summary>The keys that name a part of a base get the entries of their pickers from the base view: the rooms of the
    /// base for the room of a character, and the characters of the base for a fixture and an npc line (editor UI rule
    /// 2). Another key has none.</summary>
    [Fact]
    public void TheBasePartKeysGetTheRoomsAndTheCharactersOfTheBase()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var npc = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC", "ol01_01_npc_1");
        var room = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "MRoom", "Deck");
        var faction = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "BaseFaction");

        var rooms = FieldBinding.Create(session, npc, session.Get(npc)!, "room").Parts!;
        Assert.Equal(["Deck", "Bar"], rooms.Entries.Select(e => e.Value));
        Assert.Equal(BaseWorkspace.Section(session, BaseWorkspace.StationPath, "Room", "Deck"), rooms.Entries[0].Section);
        Assert.Equal(("Room", "Room"), (rooms.Label, rooms.Kind));
        var characters = FieldBinding.Create(session, room, session.Get(room)!, "fixture").Parts!;
        Assert.Equal(["ol01_01_bartender", "ol01_01_trader", "ol01_01_npc_1"], characters.Entries.Select(e => e.Value));
        Assert.Equal(["bartender", "trader", "crowd"], characters.Entries.Select(e => e.Detail));
        Assert.Equal(characters.Entries.Select(e => e.Value),
            FieldBinding.Create(session, faction, session.Get(faction)!, "npc").Parts!.Entries.Select(e => e.Value));
        Assert.Null(FieldBinding.Create(session, npc, session.Get(npc)!, "head").Parts);
        var deck = BaseWorkspace.Section(session, BaseWorkspace.StationDeckPath, "Room_Info");
        Assert.Null(FieldBinding.Create(session, deck, session.Get(deck)!, "set_script").Parts);
        // The pirate base has its own rooms.
        var pirate = BaseWorkspace.MBasePart(session, BaseWorkspace.Pirate, "GF_NPC");
        Assert.Equal(["Deck", "Bar"], FieldBinding.Create(session, pirate, session.Get(pirate)!, "room").Parts!.Entries
            .Select(e => e.Value));
        Assert.Equal(BaseWorkspace.PiratePath, FieldBinding.Create(session, pirate, session.Get(pirate)!, "room").Parts!
            .Entries[0].Section!.Path, System.StringComparer.OrdinalIgnoreCase);
    }
}
