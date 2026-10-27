using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>A value of an element of the open cutscene, as a row of the Inspector shows it.</summary>
/// <param name="Target">The element.</param>
/// <param name="Path">The path of the value in the element.</param>
/// <param name="Value">The value in W, or null when the element has no value there.</param>
/// <param name="ReadOnly">Why the row cannot change the value, or null.</param>
/// <param name="Changed">True when the value differs from the value of the lower cutscene. The label is blue
/// then.</param>
/// <param name="CanRevert">True when Revert can put back the value of the lower cutscene.</param>
internal sealed record ThnFieldState(ThnRef Target, ThnPath Path, ThnKeyKind Kind, ThnValue? Value, string? ReadOnly,
    bool Changed, bool CanRevert)
{
    /// <summary>The engine lines that read the value, for the tooltip of the label, or null.</summary>
    public string? Evidence { get; init; }

    /// <summary>The part of the value that the row edits, when more rows edit one value, for example the keys of a
    /// motion path. Else <see cref="FieldKey.AllLines"/>.</summary>
    public int Line { get; init; } = FieldKey.AllLines;

    /// <summary>The key of the field edits of the row.</summary>
    public FieldKey Key => new(Target, Path.Text, Line);
}

/// <summary>
/// The rows of the values of a cutscene (section 7.5 of the M8 design). A row binds to an element and a path. Each text
/// field is one edit of the history, as a bound INI row is (see <see cref="ThnFieldGesture"/>). A blue label and a bar
/// mark a value that differs from the lower cutscene. The menu of the label has Revert, Remove and Copy value.
/// </summary>
internal sealed partial class PropertyGrid
{
    /// <summary>A row of a cutscene value with its own widgets. <paramref name="value"/> draws them in the value
    /// column.</summary>
    /// <param name="note">The note of the row, or null.</param>
    public void ThnRow(string label, ThnFieldState field, string? note, Action value)
    {
        ThnLabel(label, field, note);
        value();
        ThnError(field);
        EndRow();
    }

    /// <summary>A row with a text field. Each change of the text shows at once, and the end of the field ends the
    /// edit.</summary>
    /// <param name="parse">Gives the value of a text.</param>
    /// <param name="hint">The text of an empty field.</param>
    /// <param name="command">The edit of a value, or null for <see cref="ThnFieldCommand.Set"/> of the path of the
    /// field.</param>
    public void ThnTextRow(string label, ThnFieldState field, string? note, Func<string, ThnFieldParse> parse, string hint,
        ThnFieldCommand? command = null)
    {
        ThnRow(label, field, note, () =>
        {
            var text = ThnFieldEdit.Format(field.Kind, field.Value);
            if (field.ReadOnly != null)
            {
                Field(text, -1, hint);
                return;
            }

            ImGui.SetNextItemWidth(-1);
            var changed = ImGui.InputTextWithHint("##value", hint, ref text, Capacity(text));
            ThnRoute(field, changed, parse, _ => text, command);
        });
    }

    /// <summary>
    /// A row with one field for each value of a list, each with its label in front of it, for example the X, Y and Z of
    /// a position. Each field edits one value, and the other values come from the text at the start of the edit.
    /// </summary>
    /// <remarks>A row whose value is a finite number for each label gets the field helpers of its kinds: a label drag
    /// on each label, and a color swatch with the picker at the right of the fields. A row without a value, or with
    /// another value, gets no helper, because a helper has no start to change.</remarks>
    /// <param name="drag">The kind of the label drag (see <see cref="FieldDrag.KindOf(ThnKeyKind, string)"/>), or null
    /// for no label drag.</param>
    /// <param name="color">The form of a color (see <see cref="ThnKeyRow.Color"/>), or null for no picker.</param>
    public void ThnComponentRow(string label, ThnFieldState field, string? note, IReadOnlyList<ComponentLabel> labels,
        Func<string, ThnFieldParse> parse, DragKind? drag = null, ColorForm? color = null)
    {
        ThnRow(label, field, note, () =>
        {
            var text = ThnFieldEdit.Format(field.Kind, field.Value);
            var values = FieldEdit.Values(text);
            if (field.ReadOnly != null)
            {
                ComponentFields(values, labels, (_, value, width) => Field(value, width));
                return;
            }

            // The numbers of the value, which the helpers start from.
            var helpers = field.Value != null && FieldDrag.TryParse(text, labels.Count, out var numbers) ? numbers : null;
            var kind = helpers != null ? drag : null;
            var form = helpers != null ? color : null;
            var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            ComponentFields(values, labels, (index, value, width) =>
                {
                    var edited = value;
                    ImGui.SetNextItemWidth(width);
                    var changed = ImGui.InputText("##value", ref edited, Capacity(edited));
                    ThnRoute(field, changed, parse, start => ComponentText(start, labels.Count, index, edited));
                }, form == null ? 0 : SwatchWidth + spacing,
                kind is { } k
                    ? (index, size) => DragLabel(field.Key, k, labels[index].Text, helpers, null, index,
                        () => ThnBegin(field, parse), size)
                    : null);
            if (form is { } f)
            {
                ImGui.SameLine(0, spacing);
                var (picker, reason) = ColorForms.ToPicker(helpers!, f);
                ColorSwatch(field.Key, f, picker, reason, () => ThnBegin(field, parse));
            }
        });
    }

    /// <summary>
    /// The popup of the list picker for a cutscene value. Call it after <see cref="PickerField"/>. One opening is one
    /// field edit, so one entry of the edit history (section 12.1 of the M8b design). A click and an arrow key set the
    /// value at once, and Esc puts back the value of the opening. A click on the current row makes no change.
    /// </summary>
    /// <param name="open">Opens the popup in this frame, for example after a click on the field.</param>
    /// <param name="model">Makes the rows when the popup opens.</param>
    /// <param name="kind">The kind of the entries, for their glyph.</param>
    /// <param name="parse">Gives the value of the text of a row.</param>
    /// <param name="pane">The side pane of the popup, or null for the footer.</param>
    public void ThnListPopup(ThnFieldState field, bool open, Func<ListPickerModel> model, ObjectKind kind,
        Func<string, ThnFieldParse> parse, ListPickerPane? pane = null) =>
        listPicker.Draw("##thnlist", open, model, kind, _ => 0, fieldEdits.IsOpenFor, (popup, events, selected) =>
        {
            picked = selected;
            RouteEvents(popup, field.Key, events, () => ThnBegin(field, parse), _ => picked);
        }, pane);

    /// <summary>The labels X, Y and Z, in the axis colors.</summary>
    internal static IReadOnlyList<ComponentLabel> Axes => AxisLabels;

    /// <summary>The text of all values of a list with one value replaced. A missing value is 0, so that a field of a
    /// list that the element does not have yet gives a whole list.</summary>
    internal static string ComponentText(string start, int count, int index, string value)
    {
        var values = FieldEdit.Values(start);
        var all = new string[count];
        for (var i = 0; i < count; i++)
            all[i] = i < values.Length && values[i].Length > 0 ? values[i] : "0";
        all[index] = value.Trim();
        return string.Join(", ", all);
    }

    /// <summary>Gives the events of the widget that drew last to the field edits, for a field of a cutscene
    /// value.</summary>
    /// <param name="text">Gives the text of the value after a change, from the text at the start of the edit.</param>
    /// <param name="command">The edit of a value, or null for <see cref="ThnFieldCommand.Set"/> of the path of the
    /// field.</param>
    private void ThnRoute(ThnFieldState field, bool changed, Func<string, ThnFieldParse> parse, Func<string, string> text,
        ThnFieldCommand? command = null) =>
        Route(field.Key, changed, () => ThnBegin(field, parse, command), text);

    /// <summary>Starts the field edit of a cutscene value, from the text of the value in this frame.</summary>
    /// <param name="command">The edit of a value, or null for <see cref="ThnFieldCommand.Set"/> of the path of the
    /// field.</param>
    private ThnFieldGesture ThnBegin(ThnFieldState field, Func<string, ThnFieldParse> parse,
        ThnFieldCommand? command = null) =>
        new(window.Session!.Edits, field.Key, ThnFieldEdit.Format(field.Kind, field.Value), parse,
            command ?? ThnFieldCommand.Set(field.Target, field.Path));

    /// <summary>The error of the open edit of a field below the field.</summary>
    private void ThnError(ThnFieldState field)
    {
        if (fieldEdits.ErrorOf(field.Key) is { } error)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    /// <summary>Starts a row of a cutscene value: the label with its marks and its menu, then the value column.</summary>
    private void ThnLabel(string label, ThnFieldState field, string? note)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        // The row has the id of its path, so that an active field stays with its value when rows come or go.
        ImGui.PushID($"{field.Path.Text}|{field.Line}");
        row++;
        ImGui.AlignTextToFramePadding();
        var labelMin = ImGui.GetCursorScreenPos();
        ImGui.PushStyleColor(ImGuiCol.Text, field.Changed ? EditorTheme.AccentText : EditorTheme.Label);
        ImGui.Text(label);
        ImGui.PopStyleColor();
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        ThnMenu(field);
        if (field.Changed)
        {
            ChangedBar(labelMin);
        }

        if (field.ReadOnly != null)
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextDisabled(EditorIcons.Lock.ToString());
            hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        }

        if (note != null)
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextDisabled(EditorIcons.InfoCircle.ToString());
            hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        }

        if (hovered)
        {
            var tooltip = new StringBuilder(label).Append("\nPath: ").Append(field.Path.Text);
            tooltip.Append("\n\n").Append(field.Changed
                ? "The active mod changes this value."
                : field.Value == null ? "The element has no value here." : "The lower cutscene sets this value.");
            if (field.Evidence != null)
            {
                tooltip.Append("\nThe engine reads it in ").Append(field.Evidence).Append('.');
            }

            if (field.ReadOnly != null)
            {
                tooltip.Append('\n').Append(EditorIcons.Lock).Append(' ').Append(field.ReadOnly);
            }

            if (note != null)
            {
                tooltip.Append("\n\n").Append(note);
            }

            ImGui.SetTooltip(tooltip.ToString());
        }

        ImGui.TableNextColumn();
    }

    /// <summary>The menu of the label of a cutscene row: Revert to the lower cutscene, Remove, and Copy value.</summary>
    private void ThnMenu(ThnFieldState field)
    {
        if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup("##thn-row-menu");
        }

        if (!ImGui.BeginPopup("##thn-row-menu"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        var editable = field.ReadOnly == null && window.Session?.NoEditReason == null;
        if (Theme.IconMenuItem(EditorIcons.Undo, "Revert to the lower cutscene", editable && field.CanRevert))
        {
            window.Edits.Execute(() => ThnEdits.Revert(field.Target.Path, field.Target, field.Path));
        }

        ImGui.SetItemTooltip("Puts back the value of the cutscene as the base data and the mods before the active mod have it.");
        if (Theme.IconMenuItem(EditorIcons.TrashAlt, "Remove", editable && field.Value != null))
        {
            window.Edits.Execute(() => ThnEdits.Remove(field.Target, field.Path));
        }

        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy value", field.Value != null))
        {
            ImGui.SetClipboardText(ThnWriter.Value(field.Value!));
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }
}
