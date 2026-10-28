using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The patch writer: which sections and lines it writes for an edit, and which edits it refuses.
/// </summary>
public sealed class PatchWriterTests
{
    private const string FilePath = @"DATA\test.ini";

    private const string Ships = """
        [Ship]
        nickname = s1
        mass = 100
        hit_pts = 50

        [Ship]
        nickname = s2
        mass = 5
        """;

    [Fact]
    public void ASingleNameChangeWritesTheKeyAndThatName()
    {
        var patch = Patch(Ships, Ships.Replace("mass = 100", "mass = 120"));

        Assert.Equal(Lines("[Ship]", "nickname = s1", "mass = 120"), patch);
    }

    [Fact]
    public void TheChangedNamesFollowTheOrderOfW()
    {
        var patch = Patch(
            "[Good]\nnickname = g\na = 1\nb = 2\nc = 3\nc = 4",
            "[Good]\nnickname = g\nc = 9\nc = 4\na = 1\nb = 7");

        // The merge writes the block at the first line that it replaces. The order within each name stays.
        Assert.Equal(Lines("[Good]", "nickname = g", "c = 9", "c = 4", "b = 7"), patch);
    }

    [Fact]
    public void ANameThatWDoesNotHaveGoesToOlRemove()
    {
        var patch = Patch("[Good]\nnickname = g\na = 1\nb = 2\nc = 3", "[Good]\nnickname = g\nb = 2");

        Assert.Equal(Lines("[Good]", "nickname = g", "ol.remove = a, c"), patch);
    }

    [Fact]
    public void AChangeInALinkedGroupWritesTheWholeGroup()
    {
        const string zone = """
            [Zone]
            nickname = z1
            faction_weight = fw0, 1
            encounter = e1, 5, 0.4
            faction = f1, 1
            faction_weight = fw1, 2
            encounter = e2, 5, 0.2
            faction = f2, 1
            shape = SPHERE
            """;

        var patch = Patch(zone, zone.Replace("faction = f2, 1", "faction = f2, 3"));

        // faction_weight lines belong to the encounter before them, as faction lines do.
        Assert.Equal(Lines("[Zone]", "nickname = z1", "faction_weight = fw0, 1", "encounter = e1, 5, 0.4",
            "faction = f1, 1", "faction_weight = fw1, 2", "encounter = e2, 5, 0.2", "faction = f2, 3"), patch);
    }

    /// <summary>The merge writes the lines of a patch as one block at the first line that they replace. So a patch of a
    /// linked group also writes each line between the lines of the group, here the density_restriction line of a zone.
    /// Then a save and a reload keep the order of the lines (milestone MCP1-2).</summary>
    [Fact]
    public void AChangeInALinkedGroupAlsoWritesTheLinesBetweenTheLinesOfTheGroup()
    {
        const string zone = """
            [Zone]
            nickname = z1
            faction_weight = fw0, 1
            density_restriction = 2, lawful
            encounter = e1, 5, 0.4
            faction = f1, 1
            toughness = 4
            """;

        var patch = Patch(zone, zone.Replace("faction = f1, 1", "faction = f1, 3"));

        Assert.Equal(Lines("[Zone]", "nickname = z1", "faction_weight = fw0, 1", "density_restriction = 2, lawful",
            "encounter = e1, 5, 0.4", "faction = f1, 3"), patch);
    }

    /// <summary>A new line before the lines of a linked group: without the group, the merge adds the line after them. So
    /// the patch also writes the group, and the merge gives the order of W (milestone MCP1-2).</summary>
    [Fact]
    public void ANewLineBeforeALinkedGroupAlsoWritesTheGroup()
    {
        const string b = """
            [Zone]
            nickname = z1
            shape = SPHERE
            encounter = e1, 5, 0.4
            faction = f1, 1
            toughness = 4
            """;
        var w = b.Replace("encounter =", "density_restriction = 2, lawful\nencounter =");

        var patch = Patch(b, w);

        Assert.Equal(Lines("[Zone]", "nickname = z1", "density_restriction = 2, lawful", "encounter = e1, 5, 0.4",
            "faction = f1, 1"), patch);
        Assert.Equal(Names(w), Names(Merged(b, patch)));
    }

    /// <summary>A line between the lines of one linked group can be a line of another group. Then the patch writes
    /// both groups, because the merge needs all names of a group or none (milestone MCP1-2).</summary>
    [Fact]
    public void ALineOfAnotherGroupBetweenTheLinesOfAGroupAddsThatGroupToo()
    {
        const string b = """
            [MBase]
            nickname = b1
            [GF_NPC]
            nickname = n1
            rumor = r1, 1
            know = k1, 1
            rumor = r2, 1
            rumor_type2 = t2, 1
            knowdb = d1
            """;
        var w = b.Replace("rumor = r2, 1", "rumor = r2, 2");

        var patch = Patch(b, w);

        Assert.Equal(Lines("[GF_NPC]", "nickname = n1", "rumor = r1, 1", "know = k1, 1", "rumor = r2, 2",
            "rumor_type2 = t2, 1", "knowdb = d1"), patch);
        Assert.Equal(Names(w), Names(Merged(b, patch)));
    }

    /// <summary>A change of a line that is not in a linked group, at the same place in B and W, writes only that line,
    /// also in a section with a group.</summary>
    [Fact]
    public void AChangeOutsideTheLinkedGroupsWritesOnlyItsLine()
    {
        const string b = """
            [Zone]
            nickname = z1
            pos = 1, 2, 3
            shape = SPHERE
            rotate = 0, 0, 0
            encounter = e1, 5, 0.4
            faction = f1, 1
            """;

        var patch = Patch(b, b.Replace("pos = 1, 2, 3", "pos = 4, 5, 6").Replace("rotate = 0, 0, 0", "rotate = 0, 90, 0"));

        // The merge moves rotate after pos, but the order of these lines does not change what the game reads.
        Assert.Equal(Lines("[Zone]", "nickname = z1", "pos = 4, 5, 6", "rotate = 0, 90, 0"), patch);
    }

    [Fact]
    public void AChangeInAnOrderSensitiveSectionWritesAllLines()
    {
        const string trigger = "[Trigger]\nnickname = t1\nCnd_True = no_params\nAct_A = 1\nAct_B = 2";

        var patch = Patch(trigger, trigger.Replace("Act_B = 2", "Act_B = 3"));

        Assert.Equal(Lines("[Trigger]", "nickname = t1", "Cnd_True = no_params", "Act_A = 1", "Act_B = 3"), patch);
    }

    [Fact]
    public void TheOrderSensitiveTypesComeFromTheUpstreamSchema()
    {
        Assert.True(SectionKeys.IsOrderSensitive("trigger"));
        Assert.True(SectionKeys.IsOrderSensitive("ObjList"));
        Assert.True(SectionKeys.IsOrderSensitive("Icons"));
        Assert.False(SectionKeys.IsOrderSensitive("Ship"));
        Assert.False(SectionKeys.IsOrderSensitive("Zone"));
    }

    [Fact]
    public void ANewSectionIsFollowedByItsNewChildren()
    {
        const string ships = "[Ship]\nnickname = a\n[CollisionGroup]\nobj = x";

        var patch = Patch(ships, ships + "\n[Ship]\nnickname = b\nmass = 5.5\n[CollisionGroup]\nobj = y\n[CollisionGroup]\nobj = z");

        Assert.Equal(Lines("[Ship]", "ol.new = true", "nickname = b", "mass = 5.5", "",
            "[CollisionGroup]", "obj = y", "",
            "[CollisionGroup]", "obj = z"), patch);
    }

    [Fact]
    public void ANewChildOfASectionOfBGetsOlParent()
    {
        var patch = Patch(
            "[MBase]\nnickname = b1\n[GF_NPC]\nnickname = n1\n[MBase]\nnickname = b2",
            "[MBase]\nnickname = b1\n[GF_NPC]\nnickname = n1\n[GF_NPC]\nnickname = n2\n[MVendor]\nnum_offers = 1\n[MBase]\nnickname = b2");

        Assert.Equal(Lines("[GF_NPC]", "ol.new = true", "ol.parent = b1", "nickname = n2", "",
            "[MVendor]", "ol.new = true", "ol.parent = b1", "num_offers = 1"), patch);
    }

    [Fact]
    public void OlParentNeedsAKeyThatNoOtherSectionOfTheFileHas()
    {
        // ol.parent finds the parent among the sections of all types.
        var result = Write("[MBase]\nnickname = b1\n[Room]\nnickname = b1",
            "[MBase]\nnickname = b1\n[GF_NPC]\nnickname = n2\n[Room]\nnickname = b1");

        var problem = Assert.Single(result.Problems);
        Assert.Contains("ol.parent needs a key of the parent section [MBase] nickname = b1", problem.Message);
        Assert.Equal(new SectionRef(FilePath, new SectionIdentity("GF_NPC", "n2", new SectionIdentity("MBase", "b1"))), problem.Target);
        Assert.Empty(result.Sections);
    }

    [Fact]
    public void ASingletonCanChangeAndBeDeleted()
    {
        var patch = Patch(
            "[SystemInfo]\nspace_color = 0, 0, 0\nlocal_faction = li_n_grp\n[Music]\nspace = music_a",
            "[SystemInfo]\nspace_color = 1, 2, 3\nlocal_faction = li_n_grp");

        Assert.Equal(Lines("[SystemInfo]", "space_color = 1, 2, 3", "", "[Music]", "ol.delete = true"), patch);
    }

    [Fact]
    public void OnlyNewBaseGoodSectionsCanBeWritten()
    {
        const string market = "[BaseGood]\nbase = b1\nmarketgood = g1, 0, -1, 1, 1, 0, 1";
        const string added = "\n[BaseGood]\nbase = b1\nmarketgood = g2, 0, -1, 1, 1, 0, 1";

        var patch = Patch(market, market + added);
        var changed = Write(market + added, market.Replace("g1", "g3") + added);

        Assert.Equal(Lines("[BaseGood]", "base = b1", "marketgood = g2, 0, -1, 1, 1, 0, 1"), patch);
        var problem = Assert.Single(changed.Problems);
        Assert.Equal("[BaseGood] #1: a patch cannot change this section, because the merge only adds [BaseGood] " +
                     "sections. Add a new section.", problem.Message);
    }

    [Fact]
    public void ADeletedSectionTakesItsChildrenWithIt()
    {
        var patch = Patch("[Ship]\nnickname = a\n[CollisionGroup]\nobj = x\n[Ship]\nnickname = b", "[Ship]\nnickname = b");

        Assert.Equal(Lines("[Ship]", "nickname = a", "ol.delete = true"), patch);
    }

    [Fact]
    public void AChildWithoutAKeyCannotBeDeleted()
    {
        var result = Write("[Ship]\nnickname = a\n[CollisionGroup]\nobj = x\n[Ship]\nnickname = b",
            "[Ship]\nnickname = a\n[Ship]\nnickname = b");

        var problem = Assert.Single(result.Problems);
        Assert.Equal("[CollisionGroup] #1 of [Ship] nickname = a: a patch cannot delete this section, because it has " +
                     "no single 'nickname' entry with one value.", problem.Message);
    }

    [Fact]
    public void ASectionWhoseKeyAnotherSectionAlsoHasCannotChange()
    {
        var result = Write("[Good]\nnickname = a\nprice = 1\n[Good]\nnickname = a\nprice = 2",
            "[Good]\nnickname = a\nprice = 5\n[Good]\nnickname = a\nprice = 2");

        Assert.Equal("[Good] nickname = a: a patch cannot change this section, because another [Good] section also " +
                     "has nickname = a.", Assert.Single(result.Problems).Message);
    }

    [Fact]
    public void ANewSectionGetsItsNewKeyAndADuplicateKeyIsRefused()
    {
        const string goods = "[Good]\nnickname = a\nprice = 1";

        var renamed = Patch(goods, goods + "\n[Good]\nnickname = copy_renamed\nprice = 1");
        var duplicate = Write(goods, goods + "\n[Good]\nnickname = A\nprice = 2");

        Assert.Equal(Lines("[Good]", "ol.new = true", "nickname = copy_renamed", "price = 1"), renamed);
        Assert.Equal("[Good] nickname = A (#2): another [Good] section has nickname = A.",
            Assert.Single(duplicate.Problems).Message);
    }

    [Fact]
    public void AKeyThatChangesOnlyItsLetterCaseIsRefused()
    {
        var result = Write("[Good]\nnickname = abc\nprice = 1", "[Good]\nnickname = ABC\nprice = 1");

        Assert.Equal("[Good] nickname = abc: the nickname 'ABC' differs only in letter case. A patch cannot change that.",
            Assert.Single(result.Problems).Message);
    }

    [Fact]
    public void AValueSetBackToBGivesAnEmptyPatch()
    {
        // W is a separate parse, so no section of W is a section of B.
        var result = Write(Ships, Ships);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Sections);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void ASectionThatIsAddedAndDeletedAgainGivesAnEmptyPatch()
    {
        var document = EditDocument.Open(FilePath, Text(Ships), null, []);
        var added = Text("[Ship]\nnickname = s3\nmass = 1").Sections.Single();

        document.Insert(2, added);
        Assert.Single(document.Write().Sections);
        document.Remove(2, 1);

        Assert.Equal("", document.Write().Text);
    }

    [Fact]
    public void AChangeOfOnlyTheOrderOfALinkedGroupWritesTheWholeSection()
    {
        const string zone = "[Zone]\nnickname = z1\nshape = SPHERE\n";

        // f1 now belongs to e2. Each name has the same lines, so the first patch is empty, and the check finds the
        // change. The writer then writes all lines.
        var patch = Patch(
            zone + "encounter = e1, 5, 0.4\nfaction = f1, 1\nencounter = e2, 5, 0.2\nfaction = f2, 1",
            zone + "encounter = e1, 5, 0.4\nencounter = e2, 5, 0.2\nfaction = f1, 1\nfaction = f2, 1");

        Assert.Equal(Lines("[Zone]", "nickname = z1", "shape = SPHERE", "encounter = e1, 5, 0.4",
            "encounter = e2, 5, 0.2", "faction = f1, 1", "faction = f2, 1"), patch);
    }

    [Fact]
    public void ALoadedSectionWhoseOrderTheCheckRefusesIsWrittenAgain()
    {
        const string zone = "[Zone]\nnickname = z1\nshape = SPHERE\n";
        const string group = "encounter = e1, 5, 0.4\nfaction = f1, 1\nencounter = e2, 5, 0.2\nfaction = f2, 1";
        var source = PatchSource.Parse("patch.ini",
            Encoding.ASCII.GetBytes("; zones\n[Zone]\nnickname = z1\n; the encounters\n" + group.Replace("0.4", "0.4 ; first")));
        string[] pinned = ["encounter", "faction"];

        // Each line of W has the text of a loaded line, but the loaded order gives f1 to e1, not to e2. The second
        // pass writes all lines with new text. The comment lines stay.
        var result = PatchWriter.Write(FilePath, Text(zone + group),
            Text(zone + "encounter = e1, 5, 0.4\nencounter = e2, 5, 0.2\nfaction = f1, 1\nfaction = f2, 1").Sections,
            _ => pinned, source);

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("; zones", "[Zone]", "nickname = z1", "shape = SPHERE", "; the encounters", "encounter = e1, 5, 0.4",
            "encounter = e2, 5, 0.2", "faction = f1, 1", "faction = f2, 1"), result.Text);
    }

    [Fact]
    public void ASectionOfBAtAnotherPositionIsDeletedAndAddedAgain()
    {
        // The merge keeps the sections of B in their order, so b first needs a again after it.
        var patch = Patch("[Good]\nnickname = a\nprice = 1\n[Good]\nnickname = b", "[Good]\nnickname = b\n[Good]\nnickname = a\nprice = 1");

        Assert.Equal(Lines("[Good]", "nickname = a", "ol.delete = true", "",
            "[Good]", "ol.new = true", "nickname = a", "price = 1"), patch);
    }

    [Fact]
    public void ASectionOfBAfterAMovedSectionMovesToo()
    {
        // The merge adds a again at the end, so c must also come again after it.
        var patch = Patch("[Good]\nnickname = a\n[Good]\nnickname = b\n[Good]\nnickname = c",
            "[Good]\nnickname = b\n[Good]\nnickname = a\n[Good]\nnickname = c");

        Assert.Equal(Lines("[Good]", "nickname = a", "ol.delete = true", "", "[Good]", "nickname = c", "ol.delete = true", "",
            "[Good]", "ol.new = true", "nickname = a", "", "[Good]", "ol.new = true", "nickname = c"), patch);
    }

    [Fact]
    public void ASectionThatAnEditDeletesAndAddsAgainWithTheSameKeyIsWritten()
    {
        var document = EditDocument.Open(FilePath, Text(Ships), null, []);
        var again = Text("[Ship]\nnickname = s1\nmass = 7").Sections.Single();

        document.Remove(0, 1);
        document.Insert(1, again);
        var result = document.Write();

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("[Ship]", "nickname = s1", "ol.delete = true", "", "[Ship]", "ol.new = true", "nickname = s1", "mass = 7"),
            result.Text);
    }

    [Fact]
    public void AMovedSectionTakesItsChildren()
    {
        var patch = Patch("[Ship]\nnickname = a\n[CollisionGroup]\nobj = x\n[Ship]\nnickname = b",
            "[Ship]\nnickname = b\n[Ship]\nnickname = a\n[CollisionGroup]\nobj = x");

        Assert.Equal(Lines("[Ship]", "nickname = a", "ol.delete = true", "",
            "[Ship]", "ol.new = true", "nickname = a", "", "[CollisionGroup]", "obj = x"), patch);
    }

    [Fact]
    public void ANewChildBeforeTheChildrenOfItsParentMovesThem()
    {
        // The merge adds a new child after the children of its parent. So n1 is deleted and added again after n0.
        var patch = Patch("[MBase]\nnickname = b1\n[GF_NPC]\nnickname = n1\n[MBase]\nnickname = b2",
            "[MBase]\nnickname = b1\n[GF_NPC]\nnickname = n0\n[GF_NPC]\nnickname = n1\n[MBase]\nnickname = b2");

        Assert.Equal(Lines("[GF_NPC]", "nickname = n1", "ol.delete = true", "",
            "[GF_NPC]", "ol.new = true", "ol.parent = b1", "nickname = n0", "",
            "[GF_NPC]", "ol.new = true", "ol.parent = b1", "nickname = n1"), patch);
    }

    [Fact]
    public void AnEditThatNoPatchCanGiveIsAnError()
    {
        // The vendor must come after the new child, but a patch cannot delete a section without a key.
        var result = Write("[MBase]\nnickname = b1\n[MVendor]\nnum_offers = 1\n[MBase]\nnickname = b2",
            "[MBase]\nnickname = b1\n[GF_NPC]\nnickname = n0\n[MVendor]\nnum_offers = 1\n[MBase]\nnickname = b2");

        Assert.False(result.Succeeded);
        Assert.Empty(result.Sections);
        var problem = Assert.Single(result.Problems);
        Assert.Equal("[MVendor] #1 of [MBase] nickname = b1: the merge cannot keep this section at its position in the " +
                     "edit. A patch must delete it and add it again, but it cannot delete it, because it has no single " +
                     "'nickname' entry with one value.", problem.Message);
        Assert.Equal(new SectionRef(FilePath, new SectionIdentity("MVendor", null, new SectionIdentity("MBase", "b1"))),
            problem.Target);
    }

    [Fact]
    public void ALastSectionThatAnEditDeletesAndAddsAgainIsANewSection()
    {
        // A new line of a binary file stores a boolean as 'true', and a change keeps the old text 'TRUE'. So only a
        // new section can give the copy that a template makes.
        var b = Binary(new Section("Ship") { Line("nickname", "s1") }, new Section("Ship") { Line("nickname", "s2"), Line("flag", "TRUE") });
        var document = EditDocument.Open(FilePath, b, null, []);

        document.Remove(1, 1);
        document.Insert(1, new Section("Ship") { Line("nickname", "s2"), Line("flag", "true") });
        var result = document.Write();

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("[Ship]", "nickname = s2", "ol.delete = true", "", "[Ship]", "ol.new = true", "nickname = s2", "flag = true"),
            result.Text);
    }

    [Fact]
    public void ANameThatAPatchCannotHoldIsRefused()
    {
        var b = Text(Ships);
        var edited = Copy(b.Sections[0]);
        edited.Add(new Entry(edited, "ol.extra") { new Int32Value(1) });

        var result = PatchWriter.Write(FilePath, b, [edited, b.Sections[1]], _ => []);

        Assert.Equal("[Ship] nickname = s1: 'ol.extra': an entry name cannot start with 'ol.', ';', '@' or '['.",
            Assert.Single(result.Problems).Message);
    }

    [Fact]
    public void ARemovedNameThatAPatchCannotHoldIsRefused()
    {
        // A binary file can hold any name, but ol.remove names it in a patch line.
        var b = Binary(new Section("Ship") { Line("nickname", "s1"), Line("bad;name", new Int32Value(1)) });

        var result = PatchWriter.Write(FilePath, b, [new Section("Ship") { Line("nickname", "s1") }], _ => []);

        Assert.Equal("[Ship] nickname = s1: 'bad;name': an entry name cannot contain '=', ';' or a control character " +
                     "such as a line break.", Assert.Single(result.Problems).Message);
    }

    [Fact]
    public void AnEntryWithoutValuesIsWrittenWithAnEqualsSign()
    {
        var patch = Patch(Ships, Ships.Replace("hit_pts = 50", "hit_pts = 50\nseparable"));

        Assert.Equal(Lines("[Ship]", "nickname = s1", "separable ="), patch);
    }

    [Fact]
    public void ABinaryFileKeepsTheValueTypesOfB()
    {
        var b = Binary(new Section("Ship")
        {
            Line("nickname", "s1"),
            Line("mass", new SingleValue(100f, null)),
            Line("flag", "TRUE"),
        });
        var edited = new Section("Ship")
        {
            Line("nickname", "s1"),
            Line("mass", new SingleValue(120f, null)),
            Line("flag", "TRUE"),
            Line("hash", new Int32Value(unchecked((int)2314954114u), unsigned: true)),
        };

        var result = PatchWriter.Write(FilePath, b, [edited], _ => []);

        // A float keeps its dot. The binary file stores the 32 bits of a hash, so the signed form is enough.
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("[Ship]", "nickname = s1", "mass = 120.0", "hash = -1980013182"), result.Text);
    }

    /// <summary>A section of B under a new key in W gets <c>ol.rename</c> and its changed lines only, and no
    /// <c>ol.delete</c> (decision D231 of the M3 design). The merge of the patch gives W.</summary>
    [Fact]
    public void ARenamedSectionWritesOlRenameAndItsChangedLines()
    {
        var w = Text(Ships.Replace("nickname = s1", "nickname = s9").Replace("mass = 100", "mass = 120"));

        var result = PatchWriter.Write(FilePath, Text(Ships), w.Sections, _ => [], renamed: Renamed("Ship", "s9", "s1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("[Ship]", "nickname = s1", "ol.rename = s9", "mass = 120"), result.Text);
    }

    /// <summary>After a rename, a new section can take the old key. The merge renames first and adds the new section at
    /// the end.</summary>
    [Fact]
    public void ANewSectionCanTakeTheOldKeyOfARenamedSection()
    {
        var w = Text(Ships.Replace("nickname = s1", "nickname = s9") + "\n\n[Ship]\nnickname = s1\nmass = 1");

        var result = PatchWriter.Write(FilePath, Text(Ships), w.Sections, _ => [], renamed: Renamed("Ship", "s9", "s1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("[Ship]", "nickname = s1", "ol.rename = s9", "", "[Ship]", "ol.new = true", "nickname = s1",
            "mass = 1"), result.Text);
    }

    /// <summary>The child sections of a renamed section stay its children, so a change of a child is a change of a kept
    /// section. A loaded patch can rename such a parent.</summary>
    [Fact]
    public void TheChildrenOfARenamedSectionAreKept()
    {
        const string b = "[Ship]\nnickname = s1\n[CollisionGroup]\nobj = a\nmass = 1\n[Ship]\nnickname = s2";
        var w = Text(b.Replace("nickname = s1", "nickname = s9"));

        var result = PatchWriter.Write(FilePath, Text(b), w.Sections, _ => [], renamed: Renamed("Ship", "s9", "s1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        Assert.Equal(Lines("[Ship]", "nickname = s1", "ol.rename = s9"), result.Text);
    }

    /// <summary>A change of one <c>locked_gate</c> value writes all lines of the name, each hash as its exact decimal
    /// text (decision D257 of the M3 design).</summary>
    [Fact]
    public void AChangeOfALockedGateWritesAllItsLinesWithExactNumbers()
    {
        const string b = "[locked_gates]\nlocked_gate = 2926089285\nlocked_gate = 100\nnpc_locked_gate = 7";

        var patch = Patch(b, b.Replace("2926089285", "3000000000"));

        Assert.Equal(Lines("[locked_gates]", "locked_gate = 3000000000", "locked_gate = 100"), patch);
    }

    /// <summary>The renames of a document: the identity in W to the identity in B.</summary>
    private static Dictionary<SectionIdentity, SectionIdentity> Renamed(string type, string to, string from) =>
        new() { [new SectionIdentity(type, to)] = new SectionIdentity(type, from) };

    /// <summary>Writes the patch from B to W, which must succeed.</summary>
    private static string Patch(string b, string w)
    {
        var result = Write(b, w);
        Assert.True(result.Succeeded, string.Join("\n", result.Problems));
        return result.Text;
    }

    private static PatchText Write(string b, string w) => PatchWriter.Write(FilePath, Text(b), Text(w).Sections, _ => []);

    /// <summary>The document that the merge of a patch text gives from B. The merge must give no diagnostics.</summary>
    private static IniDocument Merged(string b, string patch)
    {
        var merged = Text(b);
        var diagnostics = new List<Diagnostic>();
        IniPatcher.Apply(merged, Text(patch).Sections,
            new PatchContext("act", FilePath, FilePath, null, new PatchLedger(), diagnostics));
        Assert.Empty(diagnostics);
        return merged;
    }

    /// <summary>The names of the lines of each section, in order.</summary>
    private static string[] Names(string text) => Names(Text(text));

    private static string[] Names(IniDocument document) =>
        document.Sections.Select(s => $"[{s.Name}] " + string.Join(" ", s.Select(e => e.Name))).ToArray();

    /// <summary>The expected patch text: the lines with CRLF ends.</summary>
    internal static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + "\r\n"));

    internal static IniDocument Text(string text) =>
        IniDocument.Parse(FilePath, Encoding.ASCII.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n")));

    private static IniDocument Binary(params Section[] sections)
    {
        using var stream = new MemoryStream();
        IniWriter.WriteBini(stream, sections);
        return IniDocument.Parse(FilePath, stream.ToArray());
    }

    private static Section Copy(Section section)
    {
        var copy = new Section(section.Name);
        foreach (var entry in section)
        {
            var line = new Entry(copy, entry.Name);
            foreach (var value in entry)
                line.Add(value);
            copy.Add(line);
        }
        return copy;
    }

    private static Entry Line(string name, params ValueBase[] values)
    {
        var entry = new Entry(new Section("Test"), name);
        foreach (var value in values)
            entry.Add(value);
        return entry;
    }
}
