using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The synthetic data of the prop group tests (section 14.1 of the M23 design): <see cref="AssetWorkspace"/> with a
/// station of Li01 that has two children with positions, as the retail group of <c>Li01_14</c>.
/// </summary>
internal static class GroupWorkspace
{
    public const string Li01Group = """

        [Object]
        nickname = Li01_14
        pos = 1000, 0, 0
        archetype = station1
        base = Li01_01_Base
        dock_with = Li01_01_Base
        [Object]
        nickname = Li01_14_large_transport_1
        pos = 1286, 10, 3
        archetype = station_nodock
        parent = Li01_14
        [Object]
        nickname = Li01_large_transport_2
        pos = 715, -10, 20
        archetype = station_nodock
        parent = Li01_14
        loadout = lo_station
        """;

    public static TestWorkspace Create(Dictionary<string, string>? lowerFiles = null,
        Dictionary<string, string>? activeFiles = null)
    {
        var ws = AssetWorkspace.Create(lowerFiles, activeFiles);
        ws.WriteBase(SceneWorkspace.Li01Path, SceneWorkspace.Li01Text + Li01Group);
        return ws;
    }

    public static string PropsFolder(TestWorkspace ws, string mod = "act") => Path.Combine(ws.ModFolder(mod), Mod.PropsFolder);

    /// <summary>Writes a group file into the props folder of a mod, as another program or a hand edit does.</summary>
    public static string WriteGroup(TestWorkspace ws, string nickname, string text, string mod = "act",
        string? texts = null)
    {
        var folder = PropsFolder(ws, mod);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, nickname + ".ini");
        File.WriteAllText(file, text.ReplaceLineEndings("\r\n"));
        if (texts != null)
            File.WriteAllText(Path.Combine(folder, nickname + ".strings.json"), texts);
        return file;
    }

    public static PropGroup Group(EditSession session, string nickname) =>
        PropGroups.Of(session).Single(g => g.Nickname == nickname);

    public static string[] Lines(PropMember member) =>
        member.Lines.Select(l => $"{l.Name} = {string.Join(", ", l.Values.Select(v => v.ToString()))}").ToArray();
}

/// <summary>The file of a prop group (section 9.2 of the M23 design, decision D284).</summary>
public sealed class PropGroupFileTests
{
    private static readonly PropMember[] Members =
    [
        new("14", "Object", new Vector3(286, 0, 3), [Line("pos", "286", "0", "3"), Line("archetype", "station1")]),
        new("large transport_1", "Object", new Vector3(-285, 0, 20),
            [Line("pos", "-285", "0", "20"), Line("archetype", "station_nodock"), Line("parent", "14"),
             Line("ids_name", "$str:large_transport_1")]),
        new("Zone_ring", "zone", Vector3.Zero, [Line("pos", "0", "0", "0"), Line("shape", "SPHERE"), Line("size", "1000")]),
        new("light1", "LightSource", Vector3.One, [Line("pos", "1", "1", "1"), Line("type", "POINT")]),
    ];

    private static readonly Dictionary<StringPlaceholder, string> Texts = new()
    {
        [new StringPlaceholder(StringKind.String, "large_transport_1")] = "Big Ship",
    };

    private static EntryView Line(string name, params string[] values) =>
        new(name, values.Select(v => IniDocument.ParseTextLine($"x = {v}")![0]).ToList());

    private static string WriteGroup(string folder, string nickname, string name)
    {
        var file = Path.Combine(folder, nickname + ".ini");
        File.WriteAllBytes(file, PropGroupFile.Write(nickname, name, Members));
        File.WriteAllBytes(PropGroupFile.TextsPath(file), PropGroupFile.WriteTexts(PropGroupFile.TextsPath(file), Texts)!);
        return file;
    }

    /// <summary>Write and read give the same group. A member id with a space and a name with a comma keep their
    /// text.</summary>
    [Fact]
    public void WriteAndReadGiveTheSameGroup()
    {
        using var ws = new TestWorkspace();
        var file = WriteGroup(ws.Root, "trio", "Three transports, big");

        var group = PropGroupFile.Read(file, null, true);

        Assert.Empty(group.Problems);
        Assert.Equal(("trio", "Three transports, big"), (group.Nickname, group.Name));
        Assert.Equal(Members.Select(m => (m.Member, m.Type, m.Position)), group.Members.Select(m => (m.Member, m.Type, m.Position)));
        Assert.Equal(Members.Select(GroupWorkspace.Lines), group.Members.Select(GroupWorkspace.Lines));
        Assert.Equal("Big Ship", group.Texts[new StringPlaceholder(StringKind.String, "large_transport_1")]);
        Assert.Equal(Path.Combine(ws.Root, "trio.strings.json"), group.TextsFile);
    }

    [Fact]
    public void TheFileStartsWithItsCommentAndItsHeader()
    {
        var text = Encoding.ASCII.GetString(PropGroupFile.Write("trio", "Trio", Members[..1]));

        Assert.StartsWith("""
            ; OpenLancer prop group, format 1. The game does not read this file.
            ; The editor places each member as a separate object, zone or light.
            [PropGroup]
            nickname = trio
            name = Trio
            format = 1

            [Object]
            member = 14
            pos = 286, 0, 3
            archetype = station1
            """.ReplaceLineEndings("\r\n"), text);
    }

    /// <summary>The reader reports each problem with its line (section 9.2).</summary>
    [Fact]
    public void TheReaderReportsEachProblemWithItsLine()
    {
        using var ws = new TestWorkspace();
        var file = Path.Combine(ws.Root, "bad.ini");
        File.WriteAllText(file, """
            [Object]
            member = a
            [PropGroup]
            nickname = bad
            [Object]
            member = A
            [Base]
            nickname = x
            [Object]
            member = b
            parent = nobody
            ring = nozone
            ids_name = $str:missing
            pos = 1, 2
            [zone]
            pos = 0, 0, 0
            """);

        var group = PropGroupFile.Read(file, null, true);

        Assert.Equal([
            "bad.ini, line 1: The file has no [PropGroup] section first.",
            "bad.ini, line 3: The [PropGroup] section has no 'format = 1' line.",
            "bad.ini, line 6: The member id 'A' repeats. Each member needs an id of its own.",
            "bad.ini, line 7: [Base] is not an object, a zone or a light. A group holds [Object], [zone] and [LightSource] sections.",
            "bad.ini, line 14: The 'pos' line of member 'b' needs three numbers.",
            "bad.ini, line 15: The [zone] section has no 'member' line.",
            "bad.ini, line 11: 'parent = nobody' of member 'b' names no object of the group.",
            "bad.ini, line 12: 'ring = nozone' of member 'b' names no zone of the group.",
            "bad.ini, line 13: The text key 'missing' of member 'b' is not in bad.strings.json.",
        ], group.Problems.Select(p => p.Message));
        Assert.Equal(["a", "b"], group.Members.Select(m => m.Member));
    }

    /// <summary>The parser reads "01" as the number 1. A member id and a link to a member keep their text, so the id of
    /// <c>Li01_01</c> stays "01".</summary>
    [Fact]
    public void AMemberIdKeepsItsText()
    {
        using var ws = new TestWorkspace();
        var file = Path.Combine(ws.Root, "zero.ini");
        File.WriteAllText(file, "[PropGroup]\nnickname = zero\nformat = 1\n[Object]\nmember = 01\npos = 0, 0, 0\n" +
                                "[Object]\nmember = 1\npos = 1, 0, 0\nparent = 01\n");

        var group = PropGroupFile.Read(file, null, true);

        Assert.Empty(group.Problems);
        Assert.Equal(["01", "1"], group.Members.Select(m => m.Member));
        Assert.Equal("01", group.Members[1].Lines.Single(l => l.Name == "parent").Values[0].ToString());
        Assert.Equal("parent = 01", PropGroupFile.LineText("Object", group.Members[1].Lines[1]));
    }

    [Fact]
    public void ALaterFormatStopsTheRead()
    {
        using var ws = new TestWorkspace();
        var file = Path.Combine(ws.Root, "next.ini");
        File.WriteAllText(file, "[PropGroup]\nnickname = next\nformat = 2\n[Object]\nmember = a\n");

        var group = PropGroupFile.Read(file, null, true);

        Assert.Equal("next.ini, line 3: OpenLancer cannot read prop group format 2. It reads format 1.",
            Assert.Single(group.Problems).Message);
        Assert.Empty(group.Members);
    }

    /// <summary>LF and CRLF files read the same, and a file with comments reads.</summary>
    [Fact]
    public void LineEndsAndCommentsDoNotChangeTheGroup()
    {
        using var ws = new TestWorkspace();
        const string text = """
            ; A group that a person wrote.
            [PropGroup]
            nickname = hand ; the nickname
            name = Hand made
            format = 1

            ; The first member.
            [Object]
            member = a
            pos = 1, 2, 3 ; relative
            archetype = station1
            """;
        var lf = Path.Combine(ws.Root, "lf.ini");
        var crlf = Path.Combine(ws.Root, "crlf.ini");
        File.WriteAllText(lf, text.ReplaceLineEndings("\n"));
        File.WriteAllText(crlf, text.ReplaceLineEndings("\r\n"));

        var a = PropGroupFile.Read(lf, null, true);
        var b = PropGroupFile.Read(crlf, null, true);

        Assert.Empty(a.Problems);
        Assert.Equal("Hand made", a.Name);
        Assert.Equal(new Vector3(1, 2, 3), a.Members.Single().Position);
        Assert.Equal(a.Members.Select(GroupWorkspace.Lines), b.Members.Select(GroupWorkspace.Lines));
        Assert.Equal(a.Name, b.Name);
    }
}

/// <summary>The draft of "Save as prop group" (section 9.3 of the M23 design, decisions D285 to D287).</summary>
public sealed class PropGroupDraftTests
{
    /// <summary>The children join, the member ids lose the system, and the anchor is the center of the box of the
    /// positions. The <c>base</c> line goes, <c>parent</c> names a member id, and <c>dock_with</c> stays.</summary>
    [Fact]
    public void TheChildrenJoinWithTheirMemberIdsAndTheAnchor()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");

        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]);

        Assert.Empty(draft.Refusals);
        Assert.Equal(["14", "14_large_transport_1", "large_transport_2"], draft.Members.Select(m => m.Member));
        Assert.Equal(new Vector3(1000, 0, 10), draft.Anchor);
        Assert.Equal([new Vector3(0, 0, -10), new Vector3(286, 10, -7), new Vector3(-285, -10, 10)],
            draft.Members.Select(m => m.Position));
        Assert.Equal(["pos = 0, 0, -10", "archetype = station1", "dock_with = Li01_01_Base"],
            GroupWorkspace.Lines(draft.Members[0]));
        Assert.Equal(["pos = 286, 10, -7", "archetype = station_nodock", "parent = 14"], GroupWorkspace.Lines(draft.Members[1]));
        Assert.Equal("14: the 'base = Li01_01_Base' line goes. A base has one object in space, and the group copies are " +
                     "new objects.", Assert.Single(draft.LeftOut).Message);
        // A nickname starts with a letter (decision D294), so the editor puts a "g" before the digits.
        Assert.Equal("g14_group", draft.SuggestedNickname);
    }

    /// <summary>Decision D286: a <c>parent</c> or a <c>ring</c> line that names an item outside the group goes, and one
    /// that names a member gets its member id.</summary>
    [Fact]
    public void AParentOrARingOutsideTheGroupGoes()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");

        var child = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_large_transport_2")]);
        Assert.Equal("large_transport_2: the 'parent = Li01_14' line goes. Li01_14 is not a member of the group.",
            Assert.Single(child.LeftOut).Message);
        Assert.DoesNotContain(child.Members[0].Lines, l => l.Name == "parent");

        var planet = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_planet")]);
        Assert.StartsWith("planet: the 'ring = Zone_ring, solar\\rings\\ring.ini' line goes.", Assert.Single(planet.LeftOut).Message);

        var withRing = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_planet"), SceneWorkspace.Zone("Zone_ring")]);
        Assert.Empty(withRing.LeftOut);
        Assert.Contains(@"ring = Zone_ring, solar\rings\ring.ini", GroupWorkspace.Lines(withRing.Members[0]));
        Assert.Equal(["planet", "Zone_ring"], withRing.Members.Select(m => m.Member));
    }

    /// <summary>A text of the active mod and a text of a lower mod go into the texts of the group (rule S8).</summary>
    [Fact]
    public void TheTextsOfTheModsGoIntoTheGroup()
    {
        using var ws = GroupWorkspace.Create(
            lowerFiles: new()
            {
                [@"patches\" + SceneWorkspace.Li01Path] = "[Object]\nnickname = Li01_14\nids_name = $str:low_name\n",
                ["strings.json"] = """{ "strings": { "low_name": "Low Station" } }""",
            },
            activeFiles: new()
            {
                [@"patches\" + SceneWorkspace.Li01Path] = "[Object]\nnickname = Li01_large_transport_2\n" +
                                                         "ids_info = $ifc:transport_info\n",
                ["strings.json"] = """{ "infocards": { "transport_info": "<RDL/>" } }""",
            });
        var session = ws.Session("act");

        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]);

        Assert.Contains("ids_name = $str:14", GroupWorkspace.Lines(draft.Members[0]));
        Assert.Contains("ids_info = $ifc:large_transport_2.info", GroupWorkspace.Lines(draft.Members[2]));
        var group = PropGroups.Write(session, draft, "texts", "Texts");
        Assert.Empty(group.Problems);
        Assert.Equal("Low Station", group.Texts[new StringPlaceholder(StringKind.String, "14")]);
        Assert.Equal("<RDL/>", group.Texts[new StringPlaceholder(StringKind.Infocard, "large_transport_2.info")]);
    }

    /// <summary>A member that names an entry that the mod did not save gets a warning (section 9.3).</summary>
    [Fact]
    public void AMemberThatNamesAnUnsavedEntryGetsAWarning()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(ListWorkspace.SolarLoadoutsPath, "Loadout", [ListWorkspace.Line("nickname", "lo_new")]));
        session.Execute(EditCommand.SetLinesFromText(SceneWorkspace.Object("Li01_dock"), "loadout", ["lo_new"]));

        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_dock")]);

        Assert.Equal("dock names lo_new, which the mod did not save. Save the mod, or the group names an entry that the " +
                     "mod does not have.", Assert.Single(draft.Warnings).Message);
    }

    [Fact]
    public void ConnectionsVolumesAndTwoFilesAreRefused()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");

        Assert.Equal("Li01_to_Li02 is a jump gate. A prop group cannot hold jump gates or jump holes, because each one " +
                     "needs a partner. To make a pair, use the jump connection tool connection_create of the agent server.",
            PropGroups.Draft(session, [SceneWorkspace.Object("Li01_to_Li02")]).Refusals[0].Message);
        Assert.Equal("ring2 is a trade lane ring. This version cannot put trade lane rings into a prop group.",
            PropGroups.Draft(session, [SceneWorkspace.Object("ring2")]).Refusals[0].Message);
        const string noVolume = "this version cannot put nebulae, asteroid fields or their zones into a prop group";
        Assert.Contains(noVolume, PropGroups.Draft(session, [SceneWorkspace.Li01("Nebula", "Zone_cloud")]).Refusals[0].Message);
        Assert.Contains(noVolume, PropGroups.Draft(session, [SceneWorkspace.Li01("Asteroids", "Zone_cloud")]).Refusals[0].Message);
        Assert.Contains(noVolume, PropGroups.Draft(session, [SceneWorkspace.Zone("Zone_cloud")]).Refusals[0].Message);
        Assert.Equal("A prop group takes the items of one system file.", Assert.Single(PropGroups.Draft(session,
            [SceneWorkspace.Object("Li01_dock"), SceneWorkspace.Li02("Object", "Li02_01")]).Refusals).Message);
        Assert.Throws<InvalidOperationException>(() => PropGroups.Write(session,
            PropGroups.Draft(session, [SceneWorkspace.Object("Li01_to_Li02")]), "gate", "Gate"));
    }

    /// <summary>Only an object with an archetype of the type <c>TRADELANE_RING</c> is a trade lane ring (#90). So the
    /// prop group save takes a sun with a stray <c>next_ring</c> line. A real ring is still refused.</summary>
    [Fact]
    public void ASunWithAStrayRingLineIsNotRefusedAsARing()
    {
        using var ws = GroupWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li01Path, SceneWorkspace.Li01Text + GroupWorkspace.Li01Group +
                                              "\n[Object]\nnickname = Li01_sun\npos = 0, 0, 0\narchetype = sun1\n" +
                                              "next_ring = ring2\n");
        var session = ws.Session("act");

        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_sun")]);
        var group = PropGroups.Write(session, draft, "sun", "Sun");

        Assert.Empty(draft.Refusals);
        Assert.Equal(["sun"], draft.Members.Select(m => m.Member));
        Assert.Empty(group.Problems);
        Assert.True(File.Exists(Path.Combine(GroupWorkspace.PropsFolder(ws), "sun.ini")));
        Assert.Equal("ring2 is a trade lane ring. This version cannot put trade lane rings into a prop group.",
            Assert.Single(PropGroups.Draft(session, [SceneWorkspace.Object("ring2")]).Refusals).Message);
    }
}

/// <summary>The prop groups of the mods (section 9.1 of the M23 design, decisions D283, D289 and D294).</summary>
public sealed class PropGroupStoreTests
{
    [Fact]
    public void AWriteNeedsAnEditableSessionAndAFreeNickname()
    {
        using var ws = GroupWorkspace.Create();
        var readOnly = ws.Session(null);
        Assert.Throws<InvalidOperationException>(() =>
            PropGroups.Write(readOnly, PropGroups.Draft(readOnly, [SceneWorkspace.Object("Li01_dock")]), "dock", "Dock"));

        var session = ws.Session("act");
        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_dock")]);
        var group = PropGroups.Write(session, draft, "dock", "");
        Assert.Equal(("dock", "dock", true, null), (group.Nickname, group.Name, group.Editable, group.ModId));
        Assert.True(File.Exists(Path.Combine(GroupWorkspace.PropsFolder(ws), "dock.ini")));

        Assert.Equal("A prop group with this nickname exists. Type another nickname.",
            Assert.Throws<ArgumentException>(() => PropGroups.Write(session, draft, "dock", "x")).Message.Split(" (")[0]);
        Assert.Equal("A nickname uses lowercase letters, digits, '-' and '_', and starts with a letter.",
            PropGroups.NicknameProblem(session, "1dock"));
        Assert.Equal("A nickname uses lowercase letters, digits, '-' and '_', and starts with a letter.",
            PropGroups.NicknameProblem(session, "Dock"));
        Assert.Equal("Type a nickname.", PropGroups.NicknameProblem(session, ""));
        Assert.Null(PropGroups.NicknameProblem(session, "dock-2_b"));
        Assert.NotNull(PropGroups.NameProblem("a;b"));
        Assert.Null(PropGroups.NameProblem("Three transports, big"));
    }

    /// <summary>Rename moves the group file and the file of its texts, and the nickname line follows. Delete removes
    /// both. The texts of a group stay with it.</summary>
    [Fact]
    public void RenameAndDeleteMoveOrRemoveBothFiles()
    {
        using var ws = GroupWorkspace.Create(activeFiles: new()
        {
            [@"patches\" + SceneWorkspace.Li01Path] = "[Object]\nnickname = Li01_dock\nids_name = $str:dock_name\n",
            ["strings.json"] = """{ "strings": { "dock_name": "Dock" } }""",
        });
        var session = ws.Session("act");
        var folder = GroupWorkspace.PropsFolder(ws);
        var group = PropGroups.Write(session, PropGroups.Draft(session, [SceneWorkspace.Object("Li01_dock")]), "dock", "Dock");
        Assert.True(File.Exists(Path.Combine(folder, "dock.strings.json")));

        var renamed = PropGroups.Rename(session, group, "quay");

        Assert.Equal(["quay.ini", "quay.strings.json"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal(("quay", "Dock"), (renamed.Nickname, renamed.Name));
        Assert.Contains("nickname = quay\r\n", File.ReadAllText(renamed.File));
        Assert.Empty(renamed.Problems);

        PropGroups.Delete(session, renamed);
        Assert.Empty(Directory.GetFiles(folder));
        Assert.Empty(PropGroups.Of(session));
    }

    /// <summary>Decision D289: the groups of the active mod and of the lower mods show, not of a later mod. Only a group
    /// of the active mod is editable.</summary>
    [Fact]
    public void TheGroupsOfTheActiveAndTheLowerModsShow()
    {
        using var ws = GroupWorkspace.Create();
        ws.Mod("later", new());
        ws.WriteProfile("lower", "act", "later");
        const string text = "[PropGroup]\nnickname = {0}\nformat = 1\n[Object]\nmember = a\narchetype = station1\n";
        GroupWorkspace.WriteGroup(ws, "low_group", string.Format(text, "low_group"), "lower");
        GroupWorkspace.WriteGroup(ws, "act_group", string.Format(text, "act_group"));
        GroupWorkspace.WriteGroup(ws, "late_group", string.Format(text, "late_group"), "later");
        var session = ws.Session("act");

        Assert.Equal([("low_group", "lower", false), ("act_group", null, true)],
            PropGroups.Of(session).Select(g => (g.Nickname, g.ModId, g.Editable)));
        Assert.Throws<InvalidOperationException>(() => PropGroups.Delete(session, GroupWorkspace.Group(session, "low_group")));
        // A lower mod can have a group with the nickname of a new group of the active mod.
        Assert.Null(PropGroups.NicknameProblem(session, "low_group"));
    }

    [Fact]
    public void EachWriteAndEachChangeOnDiskChangesTheVersion()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        Assert.Empty(PropGroups.Of(session));
        var version = PropGroups.Version(session);

        PropGroups.Write(session, PropGroups.Draft(session, [SceneWorkspace.Object("Li01_dock")]), "dock", "Dock");
        Assert.NotEqual(version, version = PropGroups.Version(session));

        PropGroups.Refresh(session);
        Assert.Equal(version, PropGroups.Version(session));

        // Another program writes a group: a refresh finds it, as Unity refreshes its assets.
        GroupWorkspace.WriteGroup(ws, "other", "[PropGroup]\nnickname = other\nformat = 1\n");
        Assert.Single(PropGroups.Of(session));
        PropGroups.Refresh(session);
        Assert.NotEqual(version, PropGroups.Version(session));
        Assert.Equal(["dock", "other"], PropGroups.Of(session).Select(g => g.Nickname));
    }
}

/// <summary>The placement of a prop group (section 9.4 of the M23 design, decisions D288 and D290).</summary>
public sealed class PropGroupPlaceTests
{
    private static PropGroup Trio(EditSession session)
    {
        var draft = PropGroups.Draft(session, [SceneWorkspace.Object("Li01_14")]);
        return PropGroups.Write(session, draft, "trio", "Three transports");
    }

    /// <summary>The names are <c>&lt;system&gt;_&lt;member&gt;</c> in lowercase, the positions are the anchor plus the
    /// relative positions, the parents come first, and the placement is one entry of the edit history.</summary>
    [Fact]
    public void APlacementIsOneEntryWithTheNamesAndThePositions()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var trio = Trio(session);

        var plan = PropGroups.Place(session, "Li02", trio, new Vector3(5000, 0, 0.4f), null);
        session.Execute(plan.Command!);

        Assert.Equal(SceneAction.Place, plan.Action);
        Assert.Empty(plan.PlacementErrors);
        Assert.Equal(["li02_14", "li02_14_large_transport_1", "li02_large_transport_2"], plan.Adds.Select(a => a.Id.Key));
        Assert.Equal(plan.Adds[0], plan.SelectAfter);
        var station = SceneWorkspace.Li02("Object", "li02_14");
        Assert.Equal(["nickname = li02_14", "pos = 5000, 0, -10", "archetype = station1"],
            ScenePlansDeleteTests.Lines(session, station));
        Assert.Equal(["nickname = li02_14_large_transport_1", "pos = 5286, 10, -7", "archetype = station_nodock",
            "parent = li02_14"], ScenePlansDeleteTests.Lines(session, plan.Adds[1]));
        Assert.Equal("Place Three transports", session.UndoLabel);
        // The base of Li01_14 is in Li01, so the copy in Li02 docks with no base.
        Assert.Equal("14: the base Li01_01_Base is in Li01. The copy docks with no base. Add the Dockable station " +
                     "component to pick a base.", Assert.Single(plan.Warnings).Message);

        session.Undo();
        Assert.All(plan.Adds, a => Assert.Null(session.Get(a)));
        Assert.False(session.CanUndo);
    }

    /// <summary>Decision D262: a name that an item has gets the numbering of a copy. A <c>dock_with</c> of a base of the
    /// same system stays.</summary>
    [Fact]
    public void ATakenNameGetsTheNumberingOfACopy()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");
        var trio = Trio(session);
        session.Execute(PropGroups.Place(session, "Li02", trio, Vector3.Zero, null).Command!);

        var second = PropGroups.Place(session, "Li02", trio, Vector3.Zero, null);
        session.Execute(second.Command!);

        // Li02_01 is an object of Li02, so the copy of member 14 takes li02_02.
        Assert.Equal(["li02_02", "li02_14_large_transport_2", "li02_large_transport_1"], second.Adds.Select(a => a.Id.Key));
        Assert.Contains("parent = li02_02", ScenePlansDeleteTests.Lines(session, second.Adds[1]));

        var home = PropGroups.Place(session, "Li01", trio, Vector3.Zero, null);
        session.Execute(home.Command!);
        Assert.Empty(home.Warnings);
        Assert.Contains("dock_with = Li01_01_Base", ScenePlansDeleteTests.Lines(session, home.Adds[0]));
    }

    /// <summary>Decision D234: a drop on a Hierarchy object row makes each root object a child of the row
    /// object.</summary>
    [Fact]
    public void ARootObjectGetsTheParentOfTheRow()
    {
        using var ws = GroupWorkspace.Create();
        var session = ws.Session("act");

        var plan = PropGroups.Place(session, "Li02", Trio(session), Vector3.Zero, SceneWorkspace.Li02("Object", "Li02_01"));
        session.Execute(plan.Command!);

        Assert.Contains("parent = Li02_01", ScenePlansDeleteTests.Lines(session, plan.Adds[0]));
        Assert.Contains("parent = li02_14", ScenePlansDeleteTests.Lines(session, plan.Adds[1]));
    }

    /// <summary>Each text of the group becomes a new text of the active mod.</summary>
    [Fact]
    public void TheTextsBecomeNewTextsOfTheActiveMod()
    {
        using var ws = GroupWorkspace.Create(activeFiles: new()
        {
            [@"patches\" + SceneWorkspace.Li01Path] = "[Object]\nnickname = Li01_14\nids_name = $str:station_name\n",
            ["strings.json"] = """{ "strings": { "station_name": "Trio Base" } }""",
        });
        var session = ws.Session("act");

        var plan = PropGroups.Place(session, "Li02", Trio(session), Vector3.Zero, null);
        session.Execute(plan.Command!);

        Assert.Contains("ids_name = $str:li02_14", ScenePlansDeleteTests.Lines(session, plan.Adds[0]));
        Assert.Equal("Trio Base", session.Text(new StringPlaceholder(StringKind.String, "li02_14")));
        session.Undo();
        Assert.Null(session.Text(new StringPlaceholder(StringKind.String, "li02_14")));
    }

    /// <summary>Decision D290: a line with an error never reaches W. The plan lists it and has no command. "Place without
    /// these lines" leaves it out.</summary>
    [Fact]
    public void ALineWithAnErrorIsListedAndCanStayOut()
    {
        using var ws = GroupWorkspace.Create();
        GroupWorkspace.WriteGroup(ws, "bad", """
            [PropGroup]
            nickname = bad
            name = Bad
            format = 1
            [Object]
            member = a
            pos = 0, 0, 0
            archetype = station1
            loadout = lo_nowhere
            [zone]
            member = z
            pos = 0, 0, 0
            shape = SPHERE
            size = 100
            encounter = area_nowhere, 1, 0.5
            [Object]
            member = b
            pos = 10, 0, 0
            archetype = station_nodock
            parent = a
            """);
        var session = ws.Session("act");
        var group = GroupWorkspace.Group(session, "bad");

        var plan = PropGroups.Place(session, "Li02", group, Vector3.Zero, null);

        Assert.Null(plan.Command);
        Assert.Empty(plan.Refusals);
        Assert.Equal([("a", "loadout", "lo_nowhere"), ("z", "encounter", "area_nowhere")],
            plan.PlacementErrors.Select(e => (e.Member, e.Key, e.Value)));
        Assert.Equal("'loadout = lo_nowhere': no loadout has the name 'lo_nowhere'. The object gets no equipment.",
            plan.PlacementErrors[0].Message);

        var without = PropGroups.Place(session, "Li02", group, Vector3.Zero, null, leaveOutErrorLines: true);
        session.Execute(without.Command!);
        Assert.Equal(["nickname = li02_a", "pos = 0, 0, 0", "archetype = station1"],
            ScenePlansDeleteTests.Lines(session, without.Adds[0]));
        Assert.DoesNotContain("encounter", string.Join("\n", ScenePlansDeleteTests.Lines(session, without.Adds[1])));
        // The parent names a member of the plan, so the check takes it as present.
        Assert.Contains("parent = li02_a", ScenePlansDeleteTests.Lines(session, without.Adds[2]));
        Assert.Equal(2, without.Warnings.Count(w => w.Message.Contains("line goes.")));
        Assert.Empty(session.CheckLists(without.Adds[2]));
    }

    /// <summary>A required line cannot go, so an unknown archetype refuses the whole placement.</summary>
    [Fact]
    public void AnUnknownArchetypeRefusesThePlacement()
    {
        using var ws = GroupWorkspace.Create();
        GroupWorkspace.WriteGroup(ws, "ghost", "[PropGroup]\nnickname = ghost\nformat = 1\n[Object]\nmember = a\n" +
                                               "pos = 0, 0, 0\narchetype = no_such_station\n");
        var session = ws.Session("act");

        var plan = PropGroups.Place(session, "Li02", GroupWorkspace.Group(session, "ghost"), Vector3.Zero, null,
            leaveOutErrorLines: true);

        Assert.Null(plan.Command);
        Assert.Equal("a: the archetype no_such_station is not in this profile. The game drops this object. Add the mod that " +
                     "has no_such_station to the profile.", Assert.Single(plan.Refusals).Message);
    }

    [Fact]
    public void AGroupWithAProblemAndAReadOnlySystemAreRefused()
    {
        using var ws = GroupWorkspace.Create(activeFiles: new()
        {
            [@"files\" + SceneWorkspace.Li02Path] = SceneWorkspace.Li02Text,
        });
        GroupWorkspace.WriteGroup(ws, "broken", "[PropGroup]\nnickname = broken\nformat = 1\n[Object]\nmember = a\nparent = b\n");
        var session = ws.Session("act");

        Assert.Contains("has a problem, so it does not place", Assert.Single(PropGroups.Place(session, "Li01",
            GroupWorkspace.Group(session, "broken"), Vector3.Zero, null).Refusals).Message);
        Assert.StartsWith("li02.ini: ", Assert.Single(PropGroups.Place(session, "Li02", Trio(session), Vector3.Zero,
            null).Refusals).Message);
    }
}
