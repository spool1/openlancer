using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The working copy of one file: the active mod's patch, its pinned lines, and the edits.
/// </summary>
public sealed class EditDocumentTests
{
    private const string FilePath = @"DATA\test.ini";

    private const string Goods = """
        [Good]
        nickname = g
        price = 5600
        ids_name = 1

        [Good]
        nickname = h
        price = 10
        """;

    /// <summary>
    /// The patch sets prices that equal B (a lower mod can set the same value). The writer keeps these lines when
    /// another line changes, so the mod keeps its values when the lower mod changes.
    /// </summary>
    private const string Pins = """
        [Good]
        nickname = g
        price = 5600

        [Good]
        nickname = h
        price = 10
        ol.remove = volume
        """;

    [Fact]
    public void WIsBWithTheActivePatchAndThePlaceholdersStayText()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("m", new() { [@"patches\" + FilePath] = "[Good]\nnickname = h\nids_name = $str:h_name\nids_info = $ifc:h_info" });
        var diagnostics = new List<Diagnostic>();

        var document = EditDocument.Open(FilePath, PatchWriterTests.Text(Goods), mod, diagnostics);

        Assert.Empty(diagnostics);
        var h = document.Working[1];
        Assert.Equal("$str:h_name", Assert.IsAssignableFrom<StringValue>(h["ids_name"]![0]).ToString());
        Assert.Equal("$ifc:h_info", h["ids_info"]![0].ToString());
        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = h", "ids_name = $str:h_name", "ids_info = $ifc:h_info"),
            document.Write().Text);
    }

    [Fact]
    public void PinnedLinesStayWhenTheirFileChanges()
    {
        var document = Open(Pins);

        Assert.Equal(["price"], document.Pins(new SectionIdentity("Good", "g")));
        Assert.Equal(["price", "volume"], document.Pins(new SectionIdentity("Good", "H")).Order());
        Edit(document, 0, "ids_name", 2);

        Assert.Equal(PatchWriterTests.Lines(
            "[Good]", "nickname = g", "price = 5600", "ids_name = 2", "",
            "[Good]", "nickname = h", "price = 10", "ol.remove = volume"), document.Write().Text);
    }

    [Fact]
    public void AChildSectionKeepsItsPin()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("m", new() { [@"patches\" + FilePath] = "[GF_NPC]\nnickname = n1\nroom = bar" });
        var diagnostics = new List<Diagnostic>();
        var bases = PatchWriterTests.Text("[MBase]\nnickname = b1\n[GF_NPC]\nnickname = n1\nroom = bar\nbody = 1\n[MBase]\nnickname = b2");

        var document = EditDocument.Open(FilePath, bases, mod, diagnostics);
        Edit(document, 1, "body", 5);

        Assert.Empty(diagnostics);
        Assert.Equal(["room"], document.Pins(new SectionIdentity("GF_NPC", "n1", new SectionIdentity("MBase", "b1"))));
        Assert.Equal(PatchWriterTests.Lines("[GF_NPC]", "nickname = n1", "room = bar", "body = 5"), document.Write().Text);
    }

    [Fact]
    public void AnEditOfAPinnedNameRemovesItsPin()
    {
        var document = Open(Pins);

        // Set the price to the value of B: the line is no longer written.
        Edit(document, 0, "price", 5600);

        Assert.Empty(document.Pins(new SectionIdentity("Good", "g")));
        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = h", "price = 10", "ol.remove = volume"),
            document.Write().Text);
    }

    [Fact]
    public void ADeletedSectionLosesItsPins()
    {
        var document = Open(Pins);

        document.Remove(1, 1);

        Assert.Empty(document.Pins(new SectionIdentity("Good", "h")));
        Assert.Equal(PatchWriterTests.Lines(
            "[Good]", "nickname = g", "price = 5600", "", "[Good]", "nickname = h", "ol.delete = true"),
            document.Write().Text);
    }

    [Fact]
    public void AnEditKeepsTheOldSectionUnchanged()
    {
        var document = Open(Pins);
        var before = document.Working;
        var old = before[0];

        Edit(document, 0, "ids_name", 2);

        Assert.Same(old, before[0]);
        Assert.Equal(1, old["ids_name"]![0].ToInt32());
        Assert.Equal(2, document.Working[0]["ids_name"]![0].ToInt32());
        Assert.Equal(new SectionIdentity("Good", "g"), document.IdentityAt(0));
        Assert.Equal(1, document.IndexOf(new SectionIdentity("Good", "h")));
        Assert.Equal(-1, document.IndexOf(new SectionIdentity("Good", "x")));
    }

    /// <summary>
    /// A hand-written patch with LF line ends, inline comments, letter case that differs from B, and bytes outside
    /// ASCII in its comments.
    /// </summary>
    private static readonly byte[] HandWritten = PatchSourceTests.Bytes(
        "[Good] ; prices ~\nNICKNAME = G\nprice   = 5600   ; kept ~\nol.remove = Volume\n\n" +
        "[Good]\nnickname = n1\nol.new = TRUE\nprice = 7.50 ; new\n", 0x89, 0xE9);

    [Fact]
    public void AnUntouchedLoadedSectionKeepsItsBytes()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, HandWritten);

        Edit(document, 1, "price", 11);

        Assert.Equal(PatchSourceTests.Bytes(
            "[Good] ; prices ~\r\nNICKNAME = G\r\nprice   = 5600   ; kept ~\r\nol.remove = Volume\r\n\r\n" +
            "[Good]\r\nnickname = h\r\nprice = 11\r\n\r\n" +
            "[Good]\r\nnickname = n1\r\nol.new = TRUE\r\nprice = 7.50 ; new\r\n", 0x89, 0xE9), document.Write().ToBytes());
    }

    [Fact]
    public void AnEditedLoadedSectionKeepsTheTextOfItsUntouchedLines()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, HandWritten);

        Edit(document, 0, "ids_name", 2);
        Edit(document, 2, "price", 8);

        // The edited sections get the order of the writer. A line that the edit changed gets new text.
        Assert.Equal(PatchSourceTests.Bytes(
            "[Good] ; prices ~\r\nNICKNAME = G\r\nprice   = 5600   ; kept ~\r\nids_name = 2\r\nol.remove = Volume\r\n\r\n" +
            "[Good]\r\nol.new = TRUE\r\nnickname = n1\r\nprice = 8\r\n", 0x89, 0xE9), document.Write().ToBytes());
    }

    [Fact]
    public void ALoadedLineThatIsSetBackLeavesThePatch()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, PatchSourceTests.Bytes(
            "[Good] ; header\nnickname = g\nprice = 5600 ; kept\nids_name = 7 ; seven\nol.remove = volume, weight\n"));

        // An edit sets ids_name back to the value of B. The other lines keep their text.
        Edit(document, 0, "ids_name", 1);

        Assert.Equal(PatchWriterTests.Lines("[Good] ; header", "nickname = g", "price = 5600 ; kept",
            "ol.remove = volume, weight"), document.Write().Text);
    }

    [Fact]
    public void AnOlRemoveLineKeepsItsTextOnlyWhenItRemovesTheSameNames()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, PatchSourceTests.Bytes("[Good]\nnickname = g\nol.remove = volume, weight ; both\n"));
        var section = new Section("Good");
        foreach (var entry in document.Working[0])
            section.Add(entry);
        section.Add(new Entry(section, "weight") { new Int32Value(3) });

        document.Replace(0, section, ["weight"]);

        Assert.Equal(PatchWriterTests.Lines("[Good]", "nickname = g", "weight = 3", "ol.remove = volume"),
            document.Write().Text);
    }

    /// <summary>
    /// A hand-written patch with a file header, comment lines above sections and entries, an <c>@</c> line, a comment
    /// line after the last entry of a section, and a block at the end of the file.
    /// </summary>
    private static readonly string Commented = """
        ; Prices for the test mod.

        ; g matches the lower mod on purpose
        [Good]
        nickname = g
        ; the price stays
        price = 5600
        @note keep
        ; end of g

        [Good]
        nickname = h
        ; h is cheap
        price = 11
        ; set by hand
        volume = 2 ; two

        ; TODO: raise prices later
        """.ReplaceLineEndings("\n");

    [Fact]
    public void EveryLoadedLineStaysWhenNoEditTouchesIt()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, Encoding.ASCII.GetBytes(Commented));

        AssertBytes(Crlf(Commented), document.Write());
    }

    [Fact]
    public void TheCommentLinesOfAnEditedLineStay()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, Encoding.ASCII.GetBytes(Commented));

        Edit(document, 1, "price", 12);

        AssertBytes(Crlf(Commented.Replace("price = 11", "price = 12")), document.Write());
    }

    [Fact]
    public void TheCommentLinesOfALineThatAnEditRemovesGoWithIt()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, Encoding.ASCII.GetBytes(Commented));
        var h = new Section("Good");
        foreach (var entry in document.Working[1].Where(e => e.Name != "volume"))
            h.Add(entry);

        document.Replace(1, h, ["volume"]);

        AssertBytes(Crlf(Commented.Replace("; set by hand\nvolume = 2 ; two\n", "")), document.Write());
    }

    [Fact]
    public void ASectionThatAnEditTakesOutOfThePatchTakesItsCommentLines()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, Encoding.ASCII.GetBytes(Commented));

        // The price of B is 5600, so g leaves the patch. The file header and the end block stay.
        Edit(document, 0, "price", 5600);

        AssertBytes(Crlf("; Prices for the test mod.\n\n" + Commented[Commented.IndexOf("[Good]\nnickname = h", StringComparison.Ordinal)..]),
            document.Write());
    }

    [Fact]
    public void ALoadedSectionThatChangesNothingStays()
    {
        using var ws = new TestWorkspace();
        const string patch = "[Good]\nnickname = g\n;price = 6000\n\n[Good]\nnickname = h\nprice = 11\n";
        var document = Open(ws, Encoding.ASCII.GetBytes(patch));

        Edit(document, 1, "price", 12);

        AssertBytes(Crlf(patch.Replace("price = 11", "price = 12")), document.Write());
    }

    [Fact]
    public void AnEntryWithoutAnEqualsSignKeepsItsLine()
    {
        using var ws = new TestWorkspace();
        const string patch = "[Good]\nnickname = g\nprice = 5600 ; keep this comment\nseparable\n\n[Good]\nnickname = h\nprice = 11\n";
        var document = Open(ws, Encoding.ASCII.GetBytes(patch));

        Edit(document, 1, "price", 12);

        AssertBytes(Crlf(patch.Replace("price = 11", "price = 12")), document.Write());
    }

    [Fact]
    public void TwoLoadedSectionsOfOneSectionKeepTheirLines()
    {
        using var ws = new TestWorkspace();
        var document = Open(ws, Encoding.ASCII.GetBytes(
            "[Good]\nnickname = g\nprice = 5600 ; first\n\n[Good]\nnickname = g\nids_name = 7 ; second\nprice = 5600 ; wins\n"));

        Edit(document, 1, "price", 12);

        // The writer writes one section. The header line and the key line of the second section go.
        AssertBytes(PatchWriterTests.Lines("[Good]", "nickname = g", "", "ids_name = 7 ; second", "price = 5600 ; wins", "",
            "[Good]", "nickname = h", "price = 12"), document.Write());
    }

    [Fact]
    public void ALoadedPatchThatDeletesAndAddsASectionAgainGivesTheSameText()
    {
        using var ws = new TestWorkspace();
        const string patch = "[Good]\nnickname = g\nol.delete = true\n\n[Good]\nnickname = g\nol.new = true ; again\nprice = 1\n";
        var document = Open(ws, Encoding.ASCII.GetBytes(patch));

        var unchanged = document.Write();
        // W has the new g after h. Without h, the merge can keep g at its position, but the patch keeps its form.
        Edit(document, 1, "price", 12);
        var edited = document.Write();
        document.Remove(0, 1);
        var withoutH = document.Write();

        AssertBytes(Crlf(patch), unchanged);
        AssertBytes(PatchWriterTests.Lines("[Good]", "nickname = g", "ol.delete = true", "",
            "[Good]", "ol.new = true ; again", "nickname = g", "price = 12"), edited);
        AssertBytes(PatchWriterTests.Lines("[Good]", "nickname = g", "ol.delete = true", "", "[Good]", "nickname = h",
            "ol.delete = true", "", "[Good]", "ol.new = true ; again", "nickname = g", "price = 12"), withoutH);
    }

    [Fact]
    public void AByteOrderMarkStaysAndTheLoadedLinesKeepTheirBytes()
    {
        using var ws = new TestWorkspace();
        const string patch = "[Good] ; ü\r\nnickname = g\r\nicon = café\r\n";
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
        {
            var document = Open(ws, [.. encoding.GetPreamble(), .. encoding.GetBytes(patch)]);

            Edit(document, 1, "price", 12);
            var result = document.Write();

            Assert.True(result.Succeeded, string.Join("\n", result.Problems));
            Assert.Equal([.. encoding.GetPreamble(), .. encoding.GetBytes(patch + "\r\n[Good]\r\nnickname = h\r\nprice = 12\r\n")],
                result.ToBytes());
        }
    }

    [Fact]
    public void AFileThatTheActiveModReplacesGivesNoPatch()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("m", new()
        {
            [@"files\" + FilePath] = "[Good]\nnickname = other",
            [@"patches\" + FilePath] = "[Good]\nnickname = other\nprice = 1",
        });
        var diagnostics = new List<Diagnostic>();

        var document = EditDocument.Open(FilePath, PatchWriterTests.Text(Goods), mod, diagnostics);
        var result = document.Write();

        var refusal = Assert.Single(diagnostics);
        Assert.Equal(mod.Files[FilePath], refusal.Source);
        Assert.Equal(refusal, Assert.Single(result.Problems));
        Assert.Empty(result.Sections);
        Assert.Equal(document.Base, document.Working);
    }

    [Fact]
    public void APatchWithAnErrorGivesNoPatch()
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("m", new() { [@"patches\" + FilePath] = "[Good]\nnickname = g\nprice = 1\n[Good]\nnickname = missing\nprice = 2" });
        var diagnostics = new List<Diagnostic>();

        var document = EditDocument.Open(FilePath, PatchWriterTests.Text(Goods), mod, diagnostics);
        var result = document.Write();

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(result.Sections);
        Assert.Equal(mod.Patches[FilePath], Assert.Single(result.Problems).Source);
    }

    /// <summary>
    /// The engine reads the lines of a mission <c>[Trigger]</c> in order. A patch cannot keep a line before the key
    /// line of such a section, so the editor takes no edit of it. Otherwise the edit stops each save until an undo.
    /// </summary>
    [Fact]
    public void AnOrderSensitiveSectionWithALineBeforeItsKeyIsReadOnly()
    {
        var document = EditDocument.Open(FilePath, PatchWriterTests.Text(
            "[Trigger]\nsystem = Li01\nnickname = t1\nAct_A = 1\n[Trigger]\nnickname = t2\nsystem = Li01\nAct_A = 1"), null, []);

        Assert.Equal("the engine reads its lines in order, and a patch keeps that order only when the 'nickname' line " +
                     "is the first line.", document.ReadOnlyReasonAt(0));
        Assert.Null(document.ReadOnlyReasonAt(1));

        // The self-check of the writer refuses each patch of such a change.
        Edit(document, 0, "Act_A", 2);
        var result = document.Write();

        Assert.Empty(result.Sections);
        var problem = Assert.Single(result.Problems);
        Assert.Equal("[Trigger] nickname = t1: the patch does not give the edited section: the merge gives other lines.",
            problem.Message);
        Assert.Equal(new SectionRef(FilePath, new SectionIdentity("Trigger", "t1")), problem.Target);
    }

    /// <summary>The editor edits a whole file of the active mod under <c>DATA\MISSIONS</c>, and an INI file in the
    /// folder of the active mod, in any letter case (decision O2). Each other whole file stays read-only.</summary>
    [Fact]
    public void TheEditorEditsAMissionFileAndAnIniFileOfTheFolderOfTheActiveMod()
    {
        Assert.True(EditDocument.EditsWholeFile(@"DATA\MISSIONS\M01A\m01a.ini", "m"));
        Assert.True(EditDocument.EditsWholeFile(@"DATA\MISSIONS\npcships.ini", "m"));
        Assert.True(EditDocument.EditsWholeFile(@"DATA\MODS\m\SYSTEMS\Ol01\Ol01.ini", "m"));
        Assert.True(EditDocument.EditsWholeFile(@"data/mods/M/x.INI", "m"));
        Assert.False(EditDocument.EditsWholeFile(@"DATA\MODS\other\x.ini", "m"));
        Assert.False(EditDocument.EditsWholeFile(@"DATA\MODS\m\CUTSCENES\shot.thn", "m"));
        Assert.False(EditDocument.EditsWholeFile(@"DATA\UNIVERSE\SYSTEMS\Li01\li01.ini", "m"));
        Assert.False(EditDocument.EditsWholeFile(FilePath, "m"));
    }

    /// <summary>A whole INI file in the folder of the active mod opens as an editable document of the kind "whole file",
    /// also without a file of the lower layer. The same file of a mod with another id replaces a lower file, so it gives
    /// no patch.</summary>
    [Fact]
    public void AWholeFileOfTheFolderOfTheActiveModOpensAsAnEditableWholeFile()
    {
        const string own = @"DATA\MODS\m\x.ini";
        using var ws = new TestWorkspace();
        var mod = ws.Mod("m", new() { [@"files\" + own] = "; A test file.\n[Thing]\nnickname = a\ncount = 1\n" });
        var other = ws.Mod("other", new() { [@"files\" + own] = "[Thing]\nnickname = a\n" });
        var diagnostics = new List<Diagnostic>();

        var document = EditDocument.Open(own, null, mod, diagnostics);
        var replaced = EditDocument.Open(own, PatchWriterTests.Text("[Thing]\nnickname = lower"), other, diagnostics);

        Assert.True(document.IsWholeFile);
        Assert.Null(document.ReadOnlyReason);
        Assert.Null(document.ReadOnlyReasonAt(0));
        Edit(document, 0, "count", 2);
        var text = Encoding.ASCII.GetString(document.ChangedWholeFile()!.Bytes!);
        Assert.StartsWith("; A test file.", text);
        Assert.Contains("count = 2", text);
        Assert.False(replaced.IsWholeFile);
        Assert.Equal($"The active mod 'other' replaces {own} with this file. The editor cannot write a patch for a file " +
                     "that the active mod replaces.", replaced.ReadOnlyReason);
        Assert.Equal(replaced.ReadOnlyReason, Assert.Single(diagnostics).Message);
    }

    /// <summary>Opens <see cref="Goods"/> with a patch file that holds exactly <paramref name="patch"/>.</summary>
    private static EditDocument Open(TestWorkspace ws, byte[] patch)
    {
        var mod = ws.Mod("m", new() { [@"patches\" + FilePath] = "" });
        File.WriteAllBytes(mod.Patches[FilePath], patch);
        var diagnostics = new List<Diagnostic>();
        var document = EditDocument.Open(FilePath, PatchWriterTests.Text(Goods), mod, diagnostics);
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        return document;
    }

    private static EditDocument Open(string patch)
    {
        using var ws = new TestWorkspace();
        var mod = ws.Mod("m", new() { [@"patches\" + FilePath] = patch });
        var diagnostics = new List<Diagnostic>();
        var document = EditDocument.Open(FilePath, PatchWriterTests.Text(Goods), mod, diagnostics);
        // The pinned ol.remove line of the patch removes nothing from B.
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        return document;
    }

    /// <summary>Checks that the patch succeeds and that its bytes are the ASCII bytes of the text.</summary>
    private static void AssertBytes(string expected, PatchText patch)
    {
        Assert.True(patch.Succeeded, string.Join("\n", patch.Problems));
        Assert.Equal(expected, patch.Text);
        Assert.Equal(Encoding.ASCII.GetBytes(expected), patch.ToBytes());
    }

    /// <summary>The text with CRLF line ends and a line end after the last line, as the writer writes it.</summary>
    private static string Crlf(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", "\r\n") + "\r\n";

    /// <summary>Sets the one line of a name to one Int32 value, as a new section.</summary>
    private static void Edit(EditDocument document, int index, string name, int value)
    {
        var section = new Section(document.Working[index].Name);
        foreach (var entry in document.Working[index])
            section.Add(entry.Name == name ? new Entry(section, name) { new Int32Value(value) } : entry);
        document.Replace(index, section, [name]);
    }
}
