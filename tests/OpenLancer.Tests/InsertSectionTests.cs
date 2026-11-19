using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// <see cref="EditCommand.InsertSection"/> adds a section directly before another section, only in a whole file of the
/// active mod. A patch adds a new section at the end of the file, so the command refuses a patch file. The data is
/// invented.
/// </summary>
public sealed class InsertSectionTests
{
    private static readonly SectionRef Zone =
        new(NewModFileTests.NewPath, new SectionIdentity("zone", "Zone_Ol01_sun_death"));

    private static EditCommand Panels() => EditCommand.InsertSection(NewModFileTests.NewPath, "TexturePanels",
        [ListWorkspace.Line("file", @"solar\nebula\shapes.ini")], NewModFileTests.Sun);

    [Fact]
    public void ASectionGoesDirectlyBeforeTheOtherSectionOfAWholeFile()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(NewModFileTests.New());
        var changes = new List<EditChange>();
        session.Changed += changes.Add;

        session.Execute(Panels());

        Assert.Equal(["SystemInfo", "TexturePanels", "Object", "zone"],
            session.Sections(NewModFileTests.NewPath).Select(s => s.Id.Type));
        Assert.Equal("Add [TexturePanels]", session.UndoLabel);
        var added = session.Sections(NewModFileTests.NewPath)[1];
        Assert.True(changes[^1].Structural);
        Assert.Equal([added], changes[^1].Sections);
        Assert.Equal([@"solar\nebula\shapes.ini"], EditSessionTests.Values(session, added, "file"));

        session.Undo();
        Assert.Equal(["SystemInfo", "Object", "zone"], session.Sections(NewModFileTests.NewPath).Select(s => s.Id.Type));
        session.Redo();
        Assert.Equal(["SystemInfo", "TexturePanels", "Object", "zone"],
            session.Sections(NewModFileTests.NewPath).Select(s => s.Id.Type));

        // The save writes the whole file in the order of W, and a later insert keeps that order.
        Assert.True(session.Save().Saved);
        session.Execute(EditCommand.InsertSection(NewModFileTests.NewPath, "LightSource",
            [ListWorkspace.Line("nickname", "Ol01_light")], Zone));
        Assert.True(session.Save().Saved);
        var text = ws.ReadModFile(@"files\" + NewModFileTests.NewPath);
        var order = new[] { "[SystemInfo]", "[TexturePanels]", "[Object]", "[LightSource]", "[zone]" }
            .Select(h => text.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
    }

    /// <summary>A patch of a lower file refuses with the reason. A section in the middle of a patch would move each
    /// later section of the lower file.</summary>
    [Fact]
    public void APatchFileRefusesWithTheReason()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");

        var e = Assert.Throws<InvalidOperationException>(() => session.Execute(EditCommand.InsertSection(
            ListWorkspace.Li01Path, "zone", [ListWorkspace.Line("nickname", "Zone_new")], ListWorkspace.Li01("ring1"))));

        Assert.Equal($"{ListWorkspace.Li01Path} is a patch of a file of the game or of a lower mod. The editor puts a " +
                     "section before another section only in a whole file of the active mod. In a patch, a new section " +
                     "goes at the end of the file, because a section in the middle moves each later section of the lower " +
                     "file.", e.Message);
        Assert.False(session.IsDirty);
        Assert.DoesNotContain(session.Sections(ListWorkspace.Li01Path), s => s.Id.Key == "Zone_new");
    }

    [Fact]
    public void TheCommandRefusesAChildSectionAndAPlaceInsideAParent()
    {
        using var ws = ListWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(NewModFileTests.New());
        const string bases = @"DATA\MODS\act\BASES\bases.ini";
        session.Execute(EditCommand.NewFile(bases, ContentPathsTests.Sections("""
            [MBase]
            nickname = ol01_base
            [MRoom]
            nickname = bar
            [MBase]
            nickname = ol02_base
            """)));
        var room = session.Sections(bases)[1];
        void Refused(EditCommand command, string start) =>
            Assert.StartsWith(start, Assert.Throws<InvalidOperationException>(() => session.Execute(command)).Message);

        Refused(EditCommand.InsertSection(bases, "MRoom", [ListWorkspace.Line("nickname", "deck")],
                new SectionRef(bases, new SectionIdentity("MBase", "ol02_base"))),
            "A [MRoom] section belongs to the section before it.");
        Refused(EditCommand.InsertSection(bases, "MBase", [ListWorkspace.Line("nickname", "ol03_base")], room),
            $"{room.Id} is a child section.");
        Refused(EditCommand.InsertSection(bases, "MBase", [ListWorkspace.Line("nickname", "ol03_base")], NewModFileTests.Sun),
            $"{NewModFileTests.Sun.Id} is not in {bases}.");
        Assert.Throws<ArgumentException>(() => session.Execute(EditCommand.InsertSection(bases, "MBase",
            [ListWorkspace.Line("nickname", "ol03_base")], new SectionRef(bases, new SectionIdentity("MBase", "none")))));
        Assert.Throws<ArgumentException>(() => EditCommand.InsertSection(bases, "Bad]", [], room));
        Assert.Equal(["MBase", "MRoom", "MBase"], session.Sections(bases).Select(s => s.Id.Type));
    }
}
