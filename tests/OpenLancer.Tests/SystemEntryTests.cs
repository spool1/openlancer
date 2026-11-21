using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The rows of the <c>[system]</c> of a star system (<see cref="SystemEntryRows"/>). The page of a new system before the
/// save and the page of a saved system show them. They bind only to the <c>[system]</c> of W, so they work without
/// loaded game data. The data is invented.
/// </summary>
public sealed class SystemEntryTests
{
    private static readonly SystemEntryRows.Row[] AllRows =
        [.. SystemEntryRows.IdentityRows, .. SystemEntryRows.NavMapRows, .. SystemEntryRows.AdvancedRows];

    private static FieldBinding Binding(EditSession session, SectionRef entry, string key) =>
        FieldBinding.Create(session, entry, session.Get(entry)!, key);

    /// <summary>
    /// Each row has a plain name and a key of the schema of a <c>[system]</c>. Before the save, the rows of a new system
    /// show its lines of W: the name of the text of the mod, the place on the nav map as two whole numbers and the visit
    /// choice. The nickname and the file are read-only, and the other rows take edits.
    /// </summary>
    [Fact]
    public void TheRowsOfANewSystemShowItsLinesOfWBeforeTheSave()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        var entry = NewSystemWorkspace.System("Av01");

        Assert.True(session.IsSystemSection(entry));
        Assert.Equal(["Nickname", "Name", "Infocard", "File"], SystemEntryRows.IdentityRows.Select(r => r.Label));
        Assert.Equal(["Nav map position", "Nav map scale", "Visit"], SystemEntryRows.NavMapRows.Select(r => r.Label));
        Assert.Equal(["Visit flags", "Visit value", "Message id prefix"], SystemEntryRows.AdvancedRows.Select(r => r.Label));
        Assert.All(AllRows, r =>
        {
            Assert.DoesNotContain("_", r.Label);
            Assert.True(char.IsUpper(r.Label[0]));
            Assert.NotNull(session.Schema(entry, r.Key));
        });

        var name = Binding(session, entry, "strid_name");
        Assert.Equal(StringKind.String, name.Key!.TextKind);
        Assert.True(StringPlaceholder.TryParse(name.Text, out var placeholder));
        Assert.Equal("Avalon", session.Text(placeholder));
        Assert.Equal(StringKind.Infocard, Binding(session, entry, "ids_info").Key!.TextKind);

        var place = Binding(session, entry, "pos");
        Assert.Equal([4, 5], PropertyGrid.WholeValues(place.Text, SystemEntryRows.PositionLabels.Length, (0, 15)));
        Assert.Equal(0ul, PropertyGrid.LineBits(Binding(session, entry, "visit").Lines));

        var file = Binding(session, entry, "file");
        Assert.NotNull(file.ReadOnlyReason);
        Assert.Equal(@"..\MODS\act\SYSTEMS\Av01\Av01.ini", file.Text, ignoreCase: true);
        Assert.NotNull(Binding(session, entry, "nickname").ReadOnlyReason);
        Assert.All(AllRows.Where(r => r.Kind is not (SystemEntryRows.RowKind.Nickname or SystemEntryRows.RowKind.File)),
            r => Assert.Null(Binding(session, entry, r.Key).ReadOnlyReason));
    }

    /// <summary>The place on the nav map shows whole-number fields only for two whole numbers from 0 to 15. Another line
    /// shows text fields, so the row never hides a value.</summary>
    [Theory]
    [InlineData("4, 5", new[] { 4, 5 })]
    [InlineData("0, 15", new[] { 0, 15 })]
    [InlineData(" 7 ,8 ", new[] { 7, 8 })]
    [InlineData("4.5, 5", null)]
    [InlineData("16, 1", null)]
    [InlineData("-1, 3", null)]
    [InlineData("4", null)]
    [InlineData("4, 5, 6", null)]
    [InlineData("", null)]
    public void ThePlaceShowsWholeNumbersOnlyInTheRange(string line, int[]? numbers)
    {
        Assert.Equal(numbers, PropertyGrid.WholeValues(line, 2, (0, 15)));
    }

    /// <summary>The Visit choice sets or clears only the flag that shows the system on the map at start. The other
    /// flags stay, and the Advanced section shows them.</summary>
    [Fact]
    public void TheVisitChoiceKeepsTheOtherFlags()
    {
        var bit = SystemEntryRows.OnMapAtStart;

        Assert.Equal(1ul, bit);
        Assert.Equal("1", PropertyGrid.WithBit(0, bit, true));
        Assert.Equal("0", PropertyGrid.WithBit(1, bit, false));
        Assert.Equal("129", PropertyGrid.WithBit(128, bit, true));
        Assert.Equal("128", PropertyGrid.WithBit(129, bit, false));
        Assert.Equal("129", PropertyGrid.WithBit(129, bit, true));
    }

    /// <summary>A choice of the Visit row is one edit of the line, and an undo takes it back.</summary>
    [Fact]
    public void AVisitChoiceIsOneEdit()
    {
        using var ws = NewSystemWorkspace.Create();
        var session = ws.Session("act");
        NewSystemWorkspace.CreateAv01(session);
        var entry = NewSystemWorkspace.System("Av01");
        var bits = PropertyGrid.LineBits(Binding(session, entry, "visit").Lines)!.Value;

        var runner = FakeCommandHost.Runner(session);
        Assert.Equal(EditStatus.Applied, runner.Run(s => FieldCommands.SetLines(s, entry, "visit",
            [PropertyGrid.WithBit(bits, SystemEntryRows.OnMapAtStart, true)])).Status);

        Assert.Equal("Set visit", session.UndoLabel);
        Assert.Equal(1ul, PropertyGrid.LineBits(Binding(session, entry, "visit").Lines));
        session.Undo();
        Assert.Equal(0ul, PropertyGrid.LineBits(Binding(session, entry, "visit").Lines));
    }

    /// <summary>The texts of the rows and of the page are Simplified Technical English.</summary>
    [Fact]
    public void TheTextsAreSimplifiedTechnicalEnglish()
    {
        string[] texts =
        [
            .. AllRows.Select(r => r.Help).OfType<string>(), SystemEntryRows.OpenTip, SystemEntryRows.OpenNewTip,
            SystemEntryRows.ShownTip, SystemEntryPage.NewNote,
        ];
        Assert.All(texts, t => Assert.Empty(SteLint.Problems(t)));
        Assert.All(AllRows.Where(r => r.Kind is not SystemEntryRows.RowKind.Nickname), r => Assert.NotNull(r.Help));
    }
}
