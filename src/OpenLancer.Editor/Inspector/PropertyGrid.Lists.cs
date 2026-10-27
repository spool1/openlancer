using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The data lists of the bound rows (milestone M21): a key with a list rule gets a picker that works like the Unity
/// object picker. A value outside its list shows as "Missing (Type)", and the row below offers the fixes.
/// </summary>
/// <remarks>
/// <para>A one-line key with one rule on its first value gets one picker field (see <see cref="BoundWidget.List"/>). A
/// one-line key with labeled rules on its values, for example <c>goto</c>, gets a slot for each value: a picker for a
/// value with a rule, and a text field for the others. A multiline key keeps its text field, and the check marks each
/// bad line with its number.</para>
/// <para>The checks never refuse an edit. A value outside a closed list is an error that blocks the save (decision D96),
/// and the row shows it below the field with Pick, None and Revert.</para>
/// </remarks>
internal sealed partial class PropertyGrid
{
    private const string WOnlyNote = "The Scene shows this value after the save and the game data reload.";
    /// <summary>The arrow at the right of a picker field.</summary>
    private static readonly string DropDown = EditorIcons.CaretDown.ToString();

    private readonly ListPicker listPicker = new();

    /// <summary>The value of the selected row of the open list popup.</summary>
    private string picked = "";

    /// <summary>The entries of the last list that a picker opened, with their names (see <see cref="Named"/>).</summary>
    private (ListView? List, object? Data, IReadOnlyList<(ListEntry Entry, string Name)> Rows) named = (null, null, []);

    /// <summary>The list rules of the key of a bound row.</summary>
    private IReadOnlyList<ListRule> ListRules(FieldBinding field) =>
        window.Session?.Edits.Lists.Rules(field.Section, field.Name) ?? [];

    /// <summary>True when the key gets one picker field: one rule, on the first value, without a slot label. The rule of
    /// a scene line reads a value by its place, so its key keeps the text field.</summary>
    internal static bool IsListed(IReadOnlyList<ListRule> rules) => rules is [{ Index: 0, Label: null, Scene: false }];

    /// <summary>The labels of the slots of a key with labeled rules on its values (see <see cref="ListRule.Label"/>),
    /// or null for a key without such rules. A value without a rule gets "Value N".</summary>
    internal static Func<int, IReadOnlyList<ComponentLabel>?>? SlotLabels(IReadOnlyList<ListRule> rules)
    {
        if (!rules.Any(r => r.Label != null))
        {
            return null;
        }

        return count => Enumerable.Range(0, Math.Max(count, rules.Max(r => r.Index) + 1))
            .Select(i => new ComponentLabel(rules.FirstOrDefault(r => r.Index == i)?.Label ?? $"Value {i + 1}",
                EditorTheme.Label))
            .ToList();
    }

    /// <summary>The text of a key after a pick of value <paramref name="index"/> of line <paramref name="line"/>, from
    /// the text at the start of the edit. The other lines and values stay.</summary>
    internal static string PickedLine(string start, int line, int index, string value)
    {
        var lines = start.Replace("\r\n", "\n").Split('\n').ToList();
        if (line >= lines.Count)
        {
            return start;
        }

        lines[line] = FieldEdit.WithValue(lines[line], index, value);
        return string.Join("\n", lines);
    }

    /// <summary>The list of a rule for the first line of a bound key, or null when the list is not available. A key
    /// whose list is not available keeps its text field (section 8.3 of the M21 design).</summary>
    private ListView? ListOf(FieldBinding field, ListRule rule) =>
        window.Session?.Edits.Lists.List(rule, field.Section, field.Lines.Count > 0 ? field.Lines[0].Values : null);

    /// <summary>The list problems of the key of a bound row.</summary>
    private IReadOnlyList<ListProblem> ListProblems(FieldBinding field) =>
        window.Session?.Edits.Lists.Problems(field.Section)
            .Where(p => field.Name.Equals(p.Diagnostic.Key, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];

    /// <summary>The picker field of a one-line key with a list (see <see cref="BoundWidget.List"/>).</summary>
    /// <param name="hint">The text of the field without a line.</param>
    private void ListField(FieldBinding field, ListRule rule, string hint)
    {
        var line = field.Lines.Count > 0 ? field.Lines[0].Values : null;
        var list = window.Session!.Edits.Lists.List(rule, field.Section, line);
        var value = line is [var first, ..] ? first.ToString().Trim() : "";
        var problem = ListProblems(field).FirstOrDefault(p => p.Rule == rule && p.Line <= 0);
        var other = IsOtherFile(rule, list, value);
        var clicked = ListValue(list, value, problem, FillWidth, hint, other);
        // A tile of the Assets panel drops on the field, as Unity drops an asset on an object field (decision D281 of the
        // M23 design). The drop sets the value as a pick does.
        window.AssetDrag.Field(rule.Lists, list?.Label ?? "value",
            dropped => window.Commands.Execute(EditCommand.SetLinesFromText(field.Section, field.Name, [dropped])));
        ListPopup("##list", clicked, field, list, value, !rule.Required, _ => picked, other);
    }

    /// <summary>
    /// The asset of the value of the <c>archetype</c> or the <c>loadout</c> line of an object, for the button "Show in
    /// Assets" (section 6.3 of the M23 design), as Unity pings an asset from an object field. Null for another key, and
    /// for a value that no tile has.
    /// </summary>
    private (AssetKind Kind, string Id)? AssetOf(FieldBinding field, IReadOnlyList<ListRule> rules)
    {
        if (!field.Section.Id.Type.Equals("Object", StringComparison.OrdinalIgnoreCase) ||
            !(field.Name.Equals("archetype", StringComparison.OrdinalIgnoreCase) ||
              field.Name.Equals("loadout", StringComparison.OrdinalIgnoreCase)) ||
            !IsListed(rules) || window.Session is not { } session ||
            field.Lines is not [{ Values: [var value, ..] } line])
        {
            return null;
        }

        var list = session.Edits.Lists.List(rules[0], field.Section, line.Values);
        return list?.Find(value.ToString()) is { } entry && session.Edits.Assets.KindOfList(list.Id) is { } kind
            ? (kind, entry.Value)
            : null;
    }

    /// <summary>The button "Show in Assets": it shows the tile of the value in the Assets panel.</summary>
    private void ShowInAssetsButton((AssetKind Kind, string Id) asset)
    {
        if (ImGui.Button($"{EditorIcons.Images}##show-in-assets", new Vector2(ToolbarItems.IconWidth, ImGui.GetFrameHeight())))
        {
            window.Assets.Show(asset.Kind, asset.Id);
        }

        ImGui.SetItemTooltip($"Show {asset.Id} in Assets");
    }

    /// <summary>
    /// True when a value outside a list of files names a file that exists in the edit view. Only a rule with the flag
    /// exists accepts such a value (milestone M22). The engine reads the file, so the value is valid.
    /// </summary>
    private bool IsOtherFile(ListRule rule, ListView? list, string value) =>
        rule.Exists && list != null && value.Length > 0 && list.Find(value) == null &&
        window.Session?.Edits.Lists.FileExists(value) == true;

    /// <summary>The slots of a one-line key with labeled rules on its values. Each slot is its own field edit.</summary>
    private void ListSlots(FieldBinding field, IReadOnlyList<ListRule> rules, IReadOnlyList<ComponentLabel> labels)
    {
        var line = field.Lines.Count > 0 ? field.Lines[0].Values : null;
        var problems = ListProblems(field);
        ComponentFields(FieldEdit.Values(field.Text), labels, (index, text, width) =>
        {
            // The list of a scoped value follows the other values of the line, for example the exit object of a jump.
            var rule = rules.FirstOrDefault(r => r.Index == index);
            var list = rule == null ? null : window.Session!.Edits.Lists.List(rule, field.Section, line);
            if (rule == null || list == null)
            {
                // A value without a rule, or whose list is not available, keeps its text field.
                var edited = text;
                ImGui.SetNextItemWidth(width);
                var changed = ImGui.InputText("##value", ref edited, Capacity(edited));
                Route(field, changed, start => FieldEdit.WithValue(start, index, edited));
                return;
            }

            var problem = problems.FirstOrDefault(p => p.Rule == rule && p.Line <= 0);
            var clicked = ListValue(list, text, problem, width, "None");
            ListPopup("##list", clicked, field, list, text, rule.Empty, start => FieldEdit.WithValue(start, index, picked));
        }, reserve);
        FieldError(field);
    }

    /// <summary>
    /// Draws the closed field of a listed value: the kind glyph, the name and the nickname dim at the right. A value
    /// outside a closed list shows "Missing (Type): value" in the error color with a red frame, as Unity shows a missing
    /// reference. Returns true when the user clicks the field.
    /// </summary>
    /// <param name="width">The width of the field, or a negative width that leaves that room at the right.</param>
    /// <param name="other">True for a valid value outside a list of files (see <see cref="IsOtherFile"/>).</param>
    private bool ListValue(ListView? list, string value, ListProblem? problem, float width, string hint,
        bool other = false)
    {
        var entry = value.Length == 0 ? null : list?.Find(value);
        var (text, dim, color, error) = other
            ? (value, "other folder", EditorTheme.Text, false)
            : ListText(list, value, entry, problem, hint);
        var clicked = PickerField(text, dim, color, error, entry != null ? ObjectKinds.OfList(list?.Kind ?? "") : null, width);
        if (list != null && ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(other
                ? string.Join("\n", ListPickerModel.Footer(new ListPickerRow(ListRowKind.Other, value, value, null),
                    list.Label, UsedBy(list)))
                : ListTooltip(list, value, entry, problem));
        }

        return clicked;
    }

    /// <summary>
    /// Draws the closed field of a picker: a frame with the text, a dim text at the right, and the arrow. Returns true
    /// when the user clicks the field. The field is the last item, so a tooltip after it belongs to the field.
    /// </summary>
    /// <param name="error">True for the red frame of an error.</param>
    /// <param name="glyph">The kind of the value, for the glyph in front of the text, or null for no glyph.</param>
    /// <param name="width">The width of the field, or a negative width that leaves that room at the right.</param>
    internal static bool PickerField(string text, string dim, Vector4 color, bool error, ObjectKind? glyph, float width)
    {
        var style = ImGui.GetStyle();
        var size = new Vector2(width < 0 ? Math.Max(1, ImGui.GetContentRegionAvail().X + width + 1) : width,
            ImGui.GetFrameHeight());
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var clicked = ImGui.InvisibleButton("##listfield", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg),
            style.FrameRounding);
        drawList.AddRect(min, max, error ? ImGui.GetColorU32(EditorTheme.Error) : ImGui.GetColorU32(ImGuiCol.Border),
            style.FrameRounding, ImDrawFlags.None, error ? 1.5f : 1);
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var position = min + style.FramePadding;
        var arrow = ImGui.CalcTextSize(DropDown).X;
        var end = max.X - style.FramePadding.X - arrow - style.ItemInnerSpacing.X;
        var clip = new Vector4(min.X, min.Y, end, max.Y);
        if (glyph != null)
        {
            font.RenderChar(drawList, fontSize, position, glyph.Abgr, glyph.Icon, ref clip);
            position.X += font.GetFontBaked(fontSize).FindGlyph(glyph.Icon).AdvanceX + style.ItemInnerSpacing.X;
        }

        var room = Math.Max(0, end - position.X);
        var dimWidth = dim.Length == 0 ? 0 : Math.Min(ImGui.CalcTextSize(dim).X + style.ItemInnerSpacing.X, room / 2);
        var textWidth = TextFit.AddText(drawList, font, fontSize, position, ImGui.GetColorU32(color), text,
            room - dimWidth);
        if (dim.Length > 0)
        {
            var dimX = Math.Max(position.X + textWidth + style.ItemInnerSpacing.X, end - dimWidth + style.ItemInnerSpacing.X);
            TextFit.AddText(drawList, font, fontSize, new Vector2(dimX, position.Y),
                ImGui.GetColorU32(ImGuiCol.TextDisabled), dim, Math.Max(0, end - dimX));
        }

        drawList.AddText(new Vector2(max.X - style.FramePadding.X - arrow, position.Y), ImGui.GetColorU32(ImGuiCol.Text),
            DropDown);
        return clicked;
    }

    /// <summary>The text of the closed field of a listed value, its dim nickname, its color, and true for the red
    /// frame of an error.</summary>
    private (string Text, string Dim, Vector4 Color, bool Error) ListText(ListView? list, string value, ListEntry? entry,
        ListProblem? problem, string hint)
    {
        var label = list?.Label ?? "List";
        var error = problem?.IsError == true;
        if (value.Length == 0)
        {
            // As in Unity: "None (Type)". A required key without a line is red.
            return list == null || list.IsEnum ? (hint, "", EditorTheme.TextDim, false)
                : ($"None ({label})", "", error ? EditorTheme.Error : EditorTheme.TextDim, error);
        }

        if (list == null)
        {
            // The list is not available: the field shows the value.
            return (value, "", EditorTheme.Text, false);
        }

        if (entry == null)
        {
            return problem is { Kind: ListProblemKind.Missing or ListProblemKind.Removed }
                ? ($"Missing ({label}): {value}", "", error ? EditorTheme.Error : EditorTheme.Warning, error)
                : ($"Not listed ({label}): {value}", "", EditorTheme.Warning, false);
        }

        var name = EntryName(entry);
        var color = problem == null ? EditorTheme.Text : error ? EditorTheme.Error : EditorTheme.Warning;
        return string.Equals(name, entry.Value, StringComparison.OrdinalIgnoreCase)
            ? (entry.Value, "", color, error)
            : (name, entry.Value, color, error);
    }

    /// <summary>The tooltip of a listed value: the nickname, the file of the entry, its layer and the number of lines
    /// that name it.</summary>
    private string ListTooltip(ListView list, string value, ListEntry? entry, ListProblem? problem)
    {
        var kind = list.Label.ToLowerInvariant();
        if (entry == null)
        {
            return value.Length == 0 ? $"No line. Click to pick a {kind}."
                : problem is { IsError: true } ? $"No {kind} has the name '{value}'. Click to pick a {kind}."
                : $"'{value}' is not in the {kind} list. Unlisted values allowed.";
        }

        return string.Join("\n", ListPickerModel.Footer(
            new ListPickerRow(ListRowKind.Entry, entry.Value, EntryName(entry), entry), list.Label, UsedBy(list)));
    }

    /// <summary>The number of lines that name an entry of a list, for the footer and the tooltip.</summary>
    private Func<ListEntry, int> UsedBy(ListView list) => entry =>
        window.Session is { } session && !list.IsEnum
            ? list.Id.Split('+').Sum(id => session.Edits.Lists.CountReferences(id, entry.Value, entry.Section?.Path))
            : 0;

    /// <summary>
    /// The entries of a list with their display names, for the rows of a picker opening. The names stay for the list and
    /// the game data, so a second opening of the same list reads no section (budget of section 16.2 of the M21 design).
    /// An edit of a file of the list gives a new list, so its names are made again.
    /// </summary>
    private IReadOnlyList<(ListEntry Entry, string Name)> Named(ListView list)
    {
        var data = window.Session?.GameData;
        if (!ReferenceEquals(named.List, list) || !ReferenceEquals(named.Data, data))
        {
            named = (list, data, list.Entries.Select(e => (e, EntryName(e))).ToList());
        }

        return named.Rows;
    }

    /// <summary>The display name of an entry: the text of its <c>ids_name</c>, its own name (for example the file name
    /// of a cutscene), or its nickname.</summary>
    private string EntryName(ListEntry entry) =>
        entry.Section != null && window.Session?.SectionName(entry.Section) is { Length: > 0 } name ? name
        : entry.Name ?? entry.Value;

    /// <summary>
    /// The popup of a list field. One opening is one field edit of the key, with the label "Set key". Each click and
    /// each arrow key assigns at once, so the Scene shows each value.
    /// </summary>
    /// <param name="value">The value now, for the "Missing" row.</param>
    /// <param name="none">True when the picker offers "None": the value can be empty.</param>
    /// <param name="text">Gives the text of the key after a pick, from the text at the start of the edit.</param>
    /// <param name="other">True for a valid value outside a list of files (see <see cref="IsOtherFile"/>).</param>
    private void ListPopup(string id, bool open, FieldBinding field, ListView? list, string value, bool none,
        Func<string, string> text, bool other = false)
    {
        if (list == null || window.Session is not { } session)
        {
            return;
        }

        listPicker.Draw(id, open, () => new ListPickerModel(Named(list), none, PickerValue(list, value), other),
            ObjectKinds.OfList(list.Kind), UsedBy(list), fieldEdits.IsOpenFor, (popup, events, selected) =>
            {
                picked = selected;
                RouteEvents(popup, field.FieldKey, events, () => new LineGesture(session.Edits, field), text);
            });
    }

    /// <summary>The value of the field for the rows of a picker opening. An enum has no "Missing" row, so a name outside
    /// the enum gives no value. The picker then selects no name of the enum. A value of a list of paths that names an
    /// entry in another spelling gives the value of the entry, so the picker selects it.</summary>
    internal static string PickerValue(ListView list, string value) =>
        list.Find(value) is { } entry ? entry.Value : list.IsEnum ? "" : value;

    /// <summary>
    /// The problems of the lists below the row of their key, with the fixes: Pick opens the picker for the first bad
    /// line, None removes the line of a key that is not required, Revert puts back the lower lines, and Select selects an
    /// entry that the engine cannot use. A list problem of a key takes the place of the problems of the scene.
    /// </summary>
    /// <returns>False when the key has no list problem.</returns>
    private bool ListRowProblems(FieldBinding field, string? reason)
    {
        var problems = ListProblems(field);
        if (problems.Count == 0)
        {
            return false;
        }

        foreach (var problem in problems)
        {
            ProblemText(problem.Diagnostic, problem.IsError ? EditorTheme.Error : EditorTheme.Warning);
        }

        if (reason != null || window.Session is not { } session || problems.FirstOrDefault(p => p.Rule != null) is not
                { Rule: { } rule } first)
        {
            return true;
        }

        var lineIndex = Math.Max(first.Line, 0);
        var values = lineIndex < field.Lines.Count ? field.Lines[lineIndex].Values : null;
        var list = session.Edits.Lists.List(rule, field.Section, values);
        var whole = field.Lines.Count <= 1 && field.Key?.Multiline != true && IsListed(ListRules(field));
        // The rule of a scene line reads its value by its place, so a pick goes to that place.
        var valueIndex = values != null && rule.IndexIn(values) is >= 0 and var at ? at : rule.Index;
        var pick = false;
        if (list != null)
        {
            pick = ImGui.SmallButton("Pick...");
            ImGui.SetItemTooltip(whole ? $"Pick a {list.Label.ToLowerInvariant()}."
                : $"Pick a {list.Label.ToLowerInvariant()} for line {lineIndex + 1}.");
            ListPopup("##listfix", pick, field, list, first.Value ?? "", whole && !rule.Required,
                start => whole ? picked : PickedLine(start, lineIndex, valueIndex, picked));
        }

        if (whole && !rule.Required && field.Lines.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("None"))
            {
                window.Commands.Execute(EditCommand.RemoveName(field.Section, field.Name));
            }

            ImGui.SetItemTooltip("Removes the line. The engine then uses its default.");
        }

        if (field.CanRevert)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Revert"))
            {
                window.Commands.Execute(field.Revert());
            }

            ImGui.SetItemTooltip("Puts back the value of the base data and the mods that load before the active mod.");
        }

        if (first.Kind == ListProblemKind.Unusable && first.Value is { } named && list?.Find(named)?.Section is { } target)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Select"))
            {
                window.Selection = target;
            }

            ImGui.SetItemTooltip($"Selects the {list!.Label.ToLowerInvariant()} '{named}'.");
        }

        return true;
    }

    /// <summary>True when the key has a rule on its first value and that value is in the list. The Scene then waits for
    /// the save and the game data reload to show it (review finding R15).</summary>
    private bool HasListedValue(FieldBinding field)
    {
        if (window.Session is not { } session || field.Lines is not [{ Values: [var value, ..] } line, ..])
        {
            return false;
        }

        return ListRules(field).FirstOrDefault(r => r.Index == 0) is { } rule &&
               session.Edits.Lists.List(rule, field.Section, line.Values)?.Find(value.ToString()) != null;
    }

    /// <summary>The follow button of a listed value: it selects the section of the entry. Null when the key has no
    /// single list, or the entry has no section.</summary>
    private Follow? ListFollow(FieldBinding field, IReadOnlyList<ListRule> rules)
    {
        if (!IsListed(rules) || window.Session is not { } session || field.Lines is not [{ Values: [var value, ..] } line])
        {
            return null;
        }

        var list = session.Edits.Lists.List(rules[0], field.Section, line.Values);
        return list?.Find(value.ToString()) is { Section: { } section } entry
            ? new Follow(section, $"{list.Label}: {entry.Value}")
            : null;
    }

    /// <summary>
    /// A bound enum key: the same popup as a list, over the names of its C# enum, with no "Missing" state. A value that
    /// the enum does not have stays an error of the field checks.
    /// </summary>
    private void EnumField(FieldBinding field, Type type, string hint)
    {
        var list = ListView.Of(type);
        var value = field.Lines.Count > 0 ? field.Text : "";
        var clicked = ListValue(list, value, null, FillWidth, hint);
        ListPopup("##list", clicked, field, list, value, field.Key?.Required != true, _ => picked);
    }
}
