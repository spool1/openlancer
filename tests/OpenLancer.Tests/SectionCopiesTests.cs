using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// <see cref="SectionCopies.CopyTo"/> copies sections into a new file or into an existing file, with new keys.
/// A text of a mod gets a copy with a new key, and a text id of the base game stays. The data is invented.
/// </summary>
public sealed class SectionCopiesTests
{
    private static readonly SectionRef Station = ListWorkspace.Li01("Li01_01");
    private static readonly SectionRef Child = ListWorkspace.Li01("Li01_child");

    private static SectionRef Object(string path, string nickname) => ListWorkspace.Object(path, nickname);

    /// <summary>The sections of an INI text, as a template file gives them.</summary>
    private static IReadOnlyList<Section> Template(string text) =>
        Data.Ini.IniDocument.Parse("template.ini", System.Text.Encoding.ASCII.GetBytes(text)).Sections;

    /// <summary>
    /// A copy into a new file of the mod. The copies get their keys, and the <c>parent</c> of the child names the copy
    /// of its parent. A light keeps its key, because a light name is unique only in its file. The text of the active
    /// mod gets a copy with a key from the new nickname. The id of a text of the base game stays. One undo takes
    /// the file and the copied text back.
    /// </summary>
    [Fact]
    public void ACopyIntoANewFileGetsNewKeysAndNewTextKeys()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.Group("Names",
        [
            EditCommand.SetLinesFromText(Station, "ids_name", ["$str:li01_name"]),
            EditCommand.SetText(StringKind.String, "li01_name", "Planet One"),
            EditCommand.SetLinesFromText(Child, "ids_name", ["196609"]),
        ]));
        var problems = new List<string>();
        var texts = new SectionCopies(session, problems.Add);

        var copy = texts.CopyTo(NewModFileTests.NewPath,
        [
            new SectionCopy(session.WorkingSection(Station)!, "Ol01_01"),
            new SectionCopy(session.WorkingSection(Child)!, "Ol01_child"),
            new SectionCopy(session.WorkingSection(ListWorkspace.Li01Section("LightSource", "light1"))!),
        ], newFile: true);
        session.Execute(EditCommand.Group("Copy the template", [copy, .. texts.Commands]));

        Assert.Empty(problems);
        Assert.Equal([new SectionIdentity("Object", "Ol01_01"), new SectionIdentity("Object", "Ol01_child"),
            new SectionIdentity("LightSource", "light1")], session.Sections(NewModFileTests.NewPath).Select(s => s.Id));
        var station = Object(NewModFileTests.NewPath, "Ol01_01");
        var child = Object(NewModFileTests.NewPath, "Ol01_child");
        Assert.Equal(["Ol01_01"], EditSessionTests.Values(session, child, "parent"));
        Assert.Equal(["Li01_01_Base"], EditSessionTests.Values(session, station, "base"));
        Assert.Equal(["$str:ol01_01"], EditSessionTests.Values(session, station, "ids_name"));
        Assert.Equal("Planet One", session.Text(new StringPlaceholder(StringKind.String, "ol01_01")));
        Assert.Equal(["196609"], EditSessionTests.Values(session, child, "ids_name"));
        // The template does not change.
        Assert.Equal(["$str:li01_name"], EditSessionTests.Values(session, Station, "ids_name"));
        Assert.Equal(["Li01_01"], EditSessionTests.Values(session, Child, "parent"));

        Assert.Equal("Copy the template", session.UndoLabel);
        session.Undo();
        Assert.Empty(session.Sections(NewModFileTests.NewPath));
        Assert.Null(session.Text(new StringPlaceholder(StringKind.String, "ol01_01")));
        Assert.Equal("Names", session.UndoLabel);
        session.Redo();
        Assert.Equal(3, session.Sections(NewModFileTests.NewPath).Count);
        Assert.Equal("Planet One", session.Text(new StringPlaceholder(StringKind.String, "ol01_01")));
    }

    /// <summary>
    /// A copy into an existing file adds the copies at the end. Child copies go under the parent that the call names.
    /// A copy keeps only the lines that it keeps, and always its key. The <c>fixture</c> of a copied room names the
    /// copy of the character. A copy into a patch of a lower file is a new section of the patch.
    /// </summary>
    [Fact]
    public void ACopyIntoAnExistingFileAddsTheCopiesAtTheEnd()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        const string bases = @"DATA\MODS\act\BASES\bases.ini";
        session.Execute(EditCommand.NewFile(bases, ContentPathsTests.Sections("""
            [MBase]
            nickname = ol01_base
            [MBase]
            nickname = ol02_base
            """)));
        var template = Template("""
            [GF_NPC]
            nickname = li01_bartender
            body = li_bartender_body
            rumor = base_0_rank, mission_end, 1, 66000
            room = bar
            [MRoom]
            nickname = bar
            fixture = li01_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\li_host_fidget.thn, bartender
            """);
        var texts = new SectionCopies(session, _ => { });

        session.Execute(texts.CopyTo(bases,
        [
            new SectionCopy(template[0], "ol01_bartender", ["body", "room"]),
            new SectionCopy(template[1]),
        ], newFile: false, parent: new SectionRef(bases, new SectionIdentity("MBase", "ol01_base"))));

        Assert.Equal(["MBase", "GF_NPC", "MRoom", "MBase"], session.Sections(bases).Select(s => s.Id.Type));
        var npc = session.Sections(bases)[1];
        Assert.Equal(["ol01_bartender"], EditSessionTests.Values(session, npc, "nickname"));
        Assert.Equal(["li_bartender_body"], EditSessionTests.Values(session, npc, "body"));
        Assert.Empty(EditSessionTests.Values(session, npc, "rumor"));
        Assert.Equal(["bar"], EditSessionTests.Values(session, npc, "room"));
        Assert.Equal([@"ol01_bartender, Zs/NPC/Bartender/01/A/Stand, scripts\vendors\li_host_fidget.thn, bartender"],
            EditSessionTests.Values(session, session.Sections(bases)[2], "fixture"));
        Assert.Equal("Copy 2 sections to bases.ini", session.UndoLabel);

        // A copy into a patch of a lower file, with a section whose key the copy adds.
        var zone = Template("[zone]\npos = 1, 2, 3\nshape = SPHERE\n")[0];
        session.Execute(new SectionCopies(session, _ => { }).CopyTo(ListWorkspace.Li02Path,
            [new SectionCopy(zone, "Zone_Li02_copy")], newFile: false));
        var copied = session.Sections(ListWorkspace.Li02Path)[^1];
        Assert.Equal(new SectionIdentity("zone", "Zone_Li02_copy"), copied.Id);
        Assert.Equal(["Zone_Li02_copy"], EditSessionTests.Values(session, copied, "nickname"));
        Assert.Equal(["1, 2, 3"], EditSessionTests.Values(session, copied, "pos"));
        Assert.True(session.Get(copied)!.IsNew);
        Assert.Equal("Copy 1 section to li02.ini", session.UndoLabel);
    }

    [Fact]
    public void TheCopyRefusesAKeyWithoutAKeyEntryTwoNewKeysAndAChildWithoutAParent()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        var texts = new SectionCopies(session, _ => { });
        var template = Template("""
            [SystemInfo]
            space_color = 0, 0, 0
            [Object]
            nickname = a
            [zone]
            nickname = a
            [GF_NPC]
            nickname = npc
            """);

        Assert.Contains("[SystemInfo] section has no key",
            Assert.Throws<ArgumentException>(() => texts.CopyTo(NewModFileTests.NewPath,
                [new SectionCopy(template[0], "info")], newFile: true)).Message);
        Assert.Contains("Two copies give the key 'a' the new keys 'b' and 'c'.",
            Assert.Throws<ArgumentException>(() => texts.CopyTo(NewModFileTests.NewPath,
                [new SectionCopy(template[1], "b"), new SectionCopy(template[2], "c")], newFile: true)).Message);
        Assert.Contains("The copy of a [GF_NPC] section needs a parent section.",
            Assert.Throws<ArgumentException>(() => texts.CopyTo(ListWorkspace.Li02Path,
                [new SectionCopy(template[3])], newFile: false)).Message);
        // In a new file, a child copy needs no parent, because the whole file keeps the order.
        session.Execute(texts.CopyTo(NewModFileTests.NewPath, [new SectionCopy(template[3])], newFile: true));
        Assert.Equal(["GF_NPC"], session.Sections(NewModFileTests.NewPath).Select(s => s.Id.Type));
    }

    /// <summary>A copy in a binary file writes each bool string in lowercase, as New from template does. A copy in a
    /// new text file keeps the value.</summary>
    [Fact]
    public void ACopyInABinaryFileWritesItsBoolStringsInLowercase()
    {
        using var ws = ListWorkspace.Create();
        var gun = new Section("Gun");
        gun.Add(new Entry(gun, "nickname") { new StringValue("gun1") });
        gun.Add(new Entry(gun, "lootable") { new StringValue("True") });
        ws.WriteBaseBini(ListWorkspace.EquipmentPath, gun);
        var session = ws.Session("act");
        var source = session.WorkingSection(new SectionRef(ListWorkspace.EquipmentPath, new SectionIdentity("Gun", "gun1")))!;

        session.Execute(new SectionCopies(session, _ => { }).CopyTo(ListWorkspace.EquipmentPath,
            [new SectionCopy(source, "gun2")], newFile: false));
        session.Execute(new SectionCopies(session, _ => { }).CopyTo(@"DATA\MODS\act\EQUIPMENT\guns.ini",
            [new SectionCopy(source, "gun3")], newFile: true));

        Assert.Equal(["true"], EditSessionTests.Values(session,
            new SectionRef(ListWorkspace.EquipmentPath, new SectionIdentity("Gun", "gun2")), "lootable"));
        Assert.Equal(["True"], EditSessionTests.Values(session,
            new SectionRef(@"DATA\MODS\act\EQUIPMENT\guns.ini", new SectionIdentity("Gun", "gun3")), "lootable"));
    }
}
