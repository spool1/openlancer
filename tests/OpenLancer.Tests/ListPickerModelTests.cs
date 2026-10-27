using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ListWorkspace;

namespace OpenLancer.Tests;

/// <summary>The rows of the list picker, without ImGui (section 12.2 of the M21 design).</summary>
public sealed class ListPickerModelTests
{
    private static ListEntry Entry(string value, ValueOrigin origin = ValueOrigin.Base, bool unsaved = false) =>
        new(value, null, origin, origin == ValueOrigin.ActiveMod ? "act" : null, "type = STATION") { Unsaved = unsaved };

    private static ListPickerRow Row(string value, string? group = null, string note = "") =>
        new(ListRowKind.Entry, value, value, null) { Group = group, Note = note };

    private static readonly (ListEntry, string)[] Entries =
    [
        (Entry("li_b"), "Bravo"),
        (Entry("li_a"), "Alpha"),
        (Entry("li_c"), ""),
        (Entry("li_twin1"), "Twin"),
        (Entry("li_twin2"), "Twin"),
        (Entry("li_new", ValueOrigin.ActiveMod, unsaved: true), "New one"),
    ];

    [Fact]
    public void TheRowsAreNoneThenTheMissingValueThenTheEntriesByNameAndNickname()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_typo");

        Assert.Equal([ListRowKind.None, ListRowKind.Missing], model.Rows.Take(2).Select(r => r.Kind));
        Assert.Equal("Missing: li_typo", model.Rows[1].Text);
        // A name without text shows the nickname, and repeated names keep their own rows.
        Assert.Equal(["Alpha", "Bravo", "li_c", "New one", "Twin", "Twin"], model.Rows.Skip(2).Select(r => r.Text));
        Assert.Equal(["li_twin1", "li_twin2"], model.Rows.Where(r => r.Name == "Twin").Select(r => r.Value));
        Assert.Equal(1, model.Current);
        Assert.Equal("new", model.Rows.Single(r => r.Value == "li_new").Tag);
        Assert.Equal("", model.Rows.Single(r => r.Value == "li_a").Tag);
    }

    /// <summary>
    /// A rule with the flag exists accepts a value outside the list of files when its file exists (decision D160 of the
    /// M22 design). Its row reads "value (other folder)", not "Missing: value", and it is the current row.
    /// </summary>
    [Fact]
    public void AnExistingFileOutsideTheListIsNotMissing()
    {
        var files = new (ListEntry, string)[] { (Entry(@"SOLAR\NEBULA\cloud.ini"), "cloud") };
        var model = new ListPickerModel(files, none: false, current: @"solar\more\far_cloud.ini", currentExists: true);

        var row = model.Rows[0];
        Assert.Equal((ListRowKind.Other, @"solar\more\far_cloud.ini (other folder)"), (row.Kind, row.Text));
        Assert.Equal(0, model.Current);
        Assert.Contains("exists", ListPickerModel.Footer(row, "Nebula file", _ => 0).Single());
        // A filter shows only the entries.
        Assert.Equal([1], model.Visible("cloud"));

        // Without the file, the value is missing.
        var missing = new ListPickerModel(files, none: false, current: @"solar\more\far_cloud.ini");
        Assert.Equal("Missing: solar\\more\\far_cloud.ini", missing.Rows[0].Text);
    }

    [Fact]
    public void NoneShowsOnlyForAKeyThatIsNotRequired()
    {
        var required = new ListPickerModel(Entries, none: false, current: "");
        Assert.DoesNotContain(required.Rows, r => r.Kind == ListRowKind.None);
        Assert.Equal(-1, required.Current);

        var optional = new ListPickerModel(Entries, none: true, current: "");
        Assert.Equal(0, optional.Current);
        Assert.Equal("", optional.Rows[0].Value);
    }

    /// <summary>The current value is selected, and the list scrolls to it. The name compares without letter
    /// case.</summary>
    [Fact]
    public void TheCurrentValueIsSelected()
    {
        var model = new ListPickerModel(Entries, none: true, current: "LI_B");

        Assert.Equal("li_b", model.Rows[model.Current].Value);
        Assert.DoesNotContain(model.Rows, r => r.Kind == ListRowKind.Missing);
    }

    [Fact]
    public void TheFilterFindsNamesAndNicknamesAndHidesNoneAndMissing()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_typo");

        Assert.Equal(model.Rows.Count, model.Visible("").Count);
        Assert.Equal(["li_twin1", "li_twin2"], model.Visible("TWIN").Select(i => model.Rows[i].Value));
        Assert.Equal(["li_a"], model.Visible("alp").Select(i => model.Rows[i].Value));
        Assert.Empty(model.Visible("nothing"));
    }

    /// <summary>Enter assigns the selected row, or the first row when the filter hides the selection. Up and Down move
    /// in the visible rows.</summary>
    [Fact]
    public void EnterAndTheArrowKeysMoveInTheVisibleRows()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_a");
        var all = model.Visible("");
        var twins = model.Visible("twin");

        Assert.Equal(model.Current, ListPickerModel.EnterRow(all, model.Current));
        Assert.Equal(twins[0], ListPickerModel.EnterRow(twins, model.Current));
        Assert.Equal(-1, ListPickerModel.EnterRow([], model.Current));
        Assert.Equal("li_b", model.Rows[ListPickerModel.Step(all, model.Current, 1)].Value);
        Assert.Equal(0, ListPickerModel.Step(all, model.Current, -1));
        Assert.Equal(0, ListPickerModel.Step(all, 0, -1));
        Assert.Equal(twins[0], ListPickerModel.Step(twins, model.Current, 1));
        Assert.Equal(twins[^1], ListPickerModel.Step(twins, model.Current, -1));
    }

    [Fact]
    public void TheFooterTellsTheEntry()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_typo");
        var row = model.Rows.Single(r => r.Value == "li_new");

        var footer = ListPickerModel.Footer(row, "Loadout", e => e.Value == "li_new" ? 3 : 0);

        Assert.Equal(["New one (li_new)", "Loadout", "The active mod 'act' adds it. It is not saved.", "Used by 3 lines",
            "type = STATION"], footer);
        Assert.Equal(["li_typo: no loadout has this name."], ListPickerModel.Footer(model.Rows[1], "Loadout", _ => 0));
    }

    /// <summary>The rows of a list that the caller makes keep its order and its groups. A value outside the list gets
    /// the "Missing" row first, and the list has no "None" (section 12.1 of the M8b design).</summary>
    [Fact]
    public void ACallerListKeepsItsOrderAndGetsTheMissingRow()
    {
        ListPickerRow[] rows = [Row("Sc_b", "Mine"), Row("Sc_a", "Mine"), Row("Sc_c", "Other")];

        var missing = new ListPickerModel(rows, "Sc_typo");
        Assert.Equal([ListRowKind.Missing, ListRowKind.Entry, ListRowKind.Entry, ListRowKind.Entry],
            missing.Rows.Select(r => r.Kind));
        Assert.Equal(["Sc_typo", "Sc_b", "Sc_a", "Sc_c"], missing.Rows.Select(r => r.Value));
        Assert.Equal(0, missing.Current);

        var known = new ListPickerModel(rows, "SC_A");
        Assert.Equal(1, known.Current);
        Assert.DoesNotContain(known.Rows, r => r.Kind is ListRowKind.Missing or ListRowKind.None);
        Assert.Equal(-1, new ListPickerModel(rows, "").Current);
        // A row without an entry gets its name and the kind in the footer.
        Assert.Equal(["Sc_a", "Animation"], ListPickerModel.Footer(known.Rows[1], "Animation", _ => 0));
    }

    /// <summary>A caller list keeps the spaces of the value. So a value with a space at the start or at the end gets the
    /// "Missing" row. An INI list trims the value, as the INI reader does.</summary>
    [Fact]
    public void ACallerListKeepsTheSpacesOfTheValue()
    {
        var spaced = new ListPickerModel([Row("Sc_a"), Row("Sc_b")], " Sc_a ");
        Assert.Equal(ListRowKind.Missing, spaced.Rows[0].Kind);
        Assert.Equal(" Sc_a ", spaced.Rows[0].Value);
        Assert.Equal(0, spaced.Current);

        var ini = new ListPickerModel(Entries, none: true, current: " li_a ");
        Assert.DoesNotContain(ini.Rows, r => r.Kind == ListRowKind.Missing);
        Assert.Equal("li_a", ini.Rows[ini.Current].Value);
    }

    /// <summary>The dim text of a row has the nickname when the row shows another name, the note and the tag, with one
    /// space between them.</summary>
    [Fact]
    public void TheDimTextOfARowHasOneSpaceBetweenItsParts()
    {
        var model = new ListPickerModel(Entries, none: true, current: "li_typo");

        Assert.Equal("li_new [new]", ListPickerModel.Note(model.Rows.Single(r => r.Value == "li_new")));
        Assert.Equal("li_a", ListPickerModel.Note(model.Rows.Single(r => r.Value == "li_a")));
        Assert.Equal("", ListPickerModel.Note(model.Rows.Single(r => r.Value == "li_c")));
        Assert.Equal("", ListPickerModel.Note(model.Rows[0]));
        Assert.Equal("Walk", ListPickerModel.Note(Row("Sc_a", note: "Walk")));
        Assert.Equal("li_x Walk [mod]", ListPickerModel.Note(
            new ListPickerRow(ListRowKind.Entry, "li_x", "Name", Entry("li_x", ValueOrigin.ActiveMod)) { Note = "Walk" }));
    }

    /// <summary>
    /// The side pane shows the row that the mouse hovered last, or the row that the arrow keys selected last. A row
    /// under a mouse that did not move does not take back the pane after an arrow key.
    /// </summary>
    [Fact]
    public void AnArrowKeyKeepsThePaneUnderAStillMouse()
    {
        // The mouse moves onto row 4.
        var pane = ListPicker.PaneRow(2, 4, mouseMoved: true);
        Assert.Equal(4, pane);
        // Down selects row 5 and gives it to the pane. The mouse stays on row 4.
        Assert.Equal(5, ListPicker.PaneRow(5, 4, mouseMoved: false));
        // The mouse moves again, or it leaves the list.
        Assert.Equal(4, ListPicker.PaneRow(5, 4, mouseMoved: true));
        Assert.Equal(5, ListPicker.PaneRow(5, -1, mouseMoved: true));
    }

    /// <summary>Esc cancels the pick, but while a dropdown of the pane is open, Esc closes only the dropdown.</summary>
    [Fact]
    public void EscInADropdownOfThePaneKeepsThePick()
    {
        Assert.True(ListPicker.Cancels(escape: true, dropdown: false));
        Assert.False(ListPicker.Cancels(escape: true, dropdown: true));
        Assert.False(ListPicker.Cancels(escape: false, dropdown: false));
    }

    /// <summary>Up and Down step the selection, but while a dropdown of the pane is open, they belong to the dropdown and
    /// the selection stays.</summary>
    [Fact]
    public void ArrowKeysInADropdownOfThePaneKeepTheSelection()
    {
        Assert.Equal(1, ListPicker.ArrowStep(down: true, up: false, dropdown: false));
        Assert.Equal(-1, ListPicker.ArrowStep(down: false, up: true, dropdown: false));
        Assert.Equal(0, ListPicker.ArrowStep(down: false, up: false, dropdown: false));
        Assert.Equal(0, ListPicker.ArrowStep(down: true, up: false, dropdown: true));
        Assert.Equal(0, ListPicker.ArrowStep(down: false, up: true, dropdown: true));
    }

    /// <summary>The list filters its rows again only for new rows, a new search text or a change of a filter of the pane.
    /// A frame without a change reuses the rows and the lines.</summary>
    [Fact]
    public void TheListFiltersAgainOnlyAfterAChange()
    {
        var calls = 0;
        bool Shows(ListPickerRow row)
        {
            calls++;
            return row.Value != "b";
        }

        var model = new ListPickerModel([Row("a", "Mine"), Row("b", "Mine"), Row("c", "Other")], "");
        var filter = new ListPickerFilter();

        var first = filter.Get(model, "", false, Shows);
        Assert.Equal(3, calls);
        Assert.Equal([0, 2], first.Rows);
        Assert.Equal([~0, 0, ~2, 2], first.Lines);
        Assert.Same(first.Rows, filter.Get(model, "", false, Shows).Rows);
        Assert.Equal(3, calls);

        Assert.Equal([2], filter.Get(model, "c", false, Shows).Rows);
        Assert.Equal(6, calls);
        Assert.Equal([2], filter.Get(model, "c", true, _ => true).Rows);
        var reopened = new ListPickerModel([Row("a"), Row("d")], "");
        Assert.Empty(filter.Get(reopened, "c", false, Shows).Rows);
        Assert.Equal([1], filter.Get(reopened, "d", false, Shows).Rows);
    }

    /// <summary>The search also finds the note of a row, and a second filter hides entries but not the "Missing"
    /// row.</summary>
    [Fact]
    public void TheSearchFindsTheNoteAndASecondFilterHidesEntries()
    {
        var model = new ListPickerModel([Row("Sc_one", note: "Walk"), Row("Sc_two", note: "Talk"), Row("Sc_three")], "Sc_typo");

        Assert.Equal(["Sc_one"], model.Visible("walk").Select(i => model.Rows[i].Value));
        Assert.Equal(["Sc_typo", "Sc_two", "Sc_three"],
            model.Visible("", r => r.Value != "Sc_one").Select(i => model.Rows[i].Value));
        Assert.Empty(model.Visible("walk", r => r.Value != "Sc_one"));
    }

    /// <summary>A header line comes before the first visible row of each group, so a filter that hides the first row of
    /// a group keeps its header. A row without a group gets no header.</summary>
    [Fact]
    public void AGroupGetsAHeaderAboveItsFirstVisibleRow()
    {
        var model = new ListPickerModel([Row("a", "Mine"), Row("b", "Mine"), Row("c", "Other"), Row("d", "Other")], "x");

        Assert.Equal([0, ~1, 1, 2, ~3, 3, 4], ListPicker.Lines(model.Rows, model.Visible("")));
        Assert.Equal([~2, 2, ~4, 4], ListPicker.Lines(model.Rows, [2, 4]));
        Assert.Equal([0, 1], ListPicker.Lines([Row("a"), Row("b")], [0, 1]));
    }

    /// <summary>A popup that does not fit below its field moves left and up into the work area. A popup that is larger
    /// than the area starts at the top left corner of the area.</summary>
    [Fact]
    public void APopupMovesToStayInsideTheWindow()
    {
        var min = new Vector2(0, 20);
        var max = new Vector2(1000, 800);

        Assert.Equal(new Vector2(100, 300), ListPicker.PopupPos(new Vector2(100, 300), new Vector2(400, 400), min, max));
        Assert.Equal(new Vector2(600, 400), ListPicker.PopupPos(new Vector2(900, 700), new Vector2(400, 400), min, max));
        Assert.Equal(new Vector2(600, 700), ListPicker.PopupPos(new Vector2(900, 700), new Vector2(400, 0), min, max));
        Assert.Equal(new Vector2(0, 20), ListPicker.PopupPos(new Vector2(900, 700), new Vector2(1200, 900), min, max));
    }
}

/// <summary>The list widgets of the bound rows (section 12 of the M21 design).</summary>
public sealed class ListWidgetTests
{
    [Fact]
    public void AKeyWithOneRuleOnItsFirstValueGetsThePicker()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var lists = session.Lists;
        var loadout = lists.Rules(Li01("Li01_01"), "loadout");
        var gotos = lists.Rules(Li01("Li01_to_Li02"), "goto");
        var key = session.Schema(Li01("Li01_01"), "loadout");

        Assert.True(PropertyGrid.IsListed(loadout));
        Assert.False(PropertyGrid.IsListed(gotos));
        Assert.Equal(PropertyGrid.BoundWidget.List, PropertyGrid.WidgetOf(key, 1, 1, null, listed: true));
        Assert.Equal(PropertyGrid.BoundWidget.List, PropertyGrid.WidgetOf(key, 0, 0, null, listed: true));
        // More than one line keeps the text field, and a key without a list keeps the widget of its type.
        Assert.Equal(PropertyGrid.BoundWidget.Text, PropertyGrid.WidgetOf(key, 2, 1, null, listed: true));
        Assert.Equal(PropertyGrid.BoundWidget.Text, PropertyGrid.WidgetOf(key, 1, 1, null));
    }

    /// <summary>A one-line key with labeled rules on its values gets a slot for each value, with the label of the
    /// rule.</summary>
    [Fact]
    public void AKeyWithLabeledRulesGetsSlots()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var gotos = session.Lists.Rules(Li01("Li01_to_Li02"), "goto");
        var labels = PropertyGrid.SlotLabels(gotos)!;

        Assert.Equal(["System", "Exit object", "Tunnel"], labels(3)!.Select(l => l.Text));
        Assert.Equal(["System", "Exit object", "Tunnel", "Value 4"], labels(4)!.Select(l => l.Text));
        Assert.Null(PropertyGrid.SlotLabels(session.Lists.Rules(Li01("Li01_01"), "loadout")));
        Assert.Equal(PropertyGrid.BoundWidget.Components,
            PropertyGrid.WidgetOf(session.Schema(Li01("Li01_to_Li02"), "goto"), 1, 3, labels));
    }

    /// <summary>A pick of a line of a multiline key or of a slot keeps the other lines and values.</summary>
    [Fact]
    public void APickKeepsTheOtherLinesAndValues()
    {
        Assert.Equal("gun1, HpWeapon01\ngun2, HpWeapon02",
            PropertyGrid.PickedLine("gun1, HpWeapon01\ngun_typo, HpWeapon02", 1, 0, "gun2"));
        Assert.Equal("Li02, Li02_to_Li01, gate_x", PropertyGrid.PickedLine("Li02, typo, gate_x", 0, 1, "Li02_to_Li01"));
        Assert.Equal("a", PropertyGrid.PickedLine("a", 3, 0, "b"));
    }

    /// <summary>Decision D123: a section of a file whose schema the editor does not know is read-only in the
    /// Inspector, for example the [Solar] that the follow button of an archetype selects.</summary>
    [Fact]
    public void ASectionOfAFileWithoutASchemaIsReadOnly()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var solar = new SectionRef(SolarPath, new SectionIdentity("Solar", "station1"));

        var field = FieldBinding.Create(session, solar, session.Get(solar)!, "loadout");

        Assert.Equal(FieldBinding.NoSchemaReason, field.ReadOnlyReason);
        Assert.False(session.KnowsSchema(SolarPath));
        Assert.True(session.KnowsSchema(Li01Path));
        Assert.True(session.KnowsSchema(EquipmentPath));
        Assert.Null(FieldBinding.Create(session, Li01("Li01_01"), session.Get(Li01("Li01_01"))!, "loadout").ReadOnlyReason);
    }

    [Fact]
    public void AnEnumListHoldsTheNamesOfTheEnum()
    {
        var list = ListView.Of(typeof(LibreLancer.Data.Schema.Solar.DockSphereType));

        Assert.True(list.IsEnum);
        Assert.NotNull(list.Find("BERTH"));
        Assert.Null(list.Find("no_such_type"));
        Assert.Same(list, ListView.Of(typeof(LibreLancer.Data.Schema.Solar.DockSphereType)));

        // The picker selects the current name of the enum. A name outside the enum gets no "Missing" row.
        var rows = list.Entries.Select(e => (e, e.Value)).ToList();
        var current = new ListPickerModel(rows, none: true, PropertyGrid.PickerValue(list, "BERTH"));
        Assert.Equal("berth", current.Rows[current.Current].Value, ignoreCase: true);
        var outside = new ListPickerModel(rows, none: true, PropertyGrid.PickerValue(list, "no_such_type"));
        Assert.DoesNotContain(outside.Rows, r => r.Kind == ListRowKind.Missing);
        Assert.Equal(ListRowKind.None, outside.Rows[outside.Current].Kind);
    }
}
