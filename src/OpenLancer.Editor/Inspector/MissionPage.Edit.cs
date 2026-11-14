using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The edit widgets of the mission pages (section 8.4 of the M6b design). Each widget draws the binding of its field
/// (<see cref="FieldEdit"/>), and one gesture runs one batch through the mission editor of the session. A drag of a
/// number and a pick in an open picker are one transaction, so one history entry (section 4.11 of the M6b design). A
/// field whose edit can need a confirmation runs one batch at the end of its gesture, so the dialog can ask. The row
/// menus, the page menu, the "+" of the action groups and "Pick in Scene" are here too.
/// </summary>
internal sealed partial class MissionPage
{
    private const string PickTip = "Click a point in the Scene to set this position. Esc cancels.";

    private readonly Dictionary<string, ValuePicker> pickers = [];

    // The frame in which the page drew each picker last. A picker that the page does not draw is closed.
    private readonly Dictionary<string, int> pickerFrames = [];
    private readonly QuickAdd quickAdd = new();
    private readonly RenamePopup rename = new("##rename");
    private readonly NewFilePopup newFile = new("##new-file");
    private (string Field, MissionTransaction Transaction)? gesture;
    private string? lastRefusal;
    private readonly Dictionary<string, string> texts = [];

    // The values of the number fields after a change, until the page reads the next model. A step of a transaction does
    // not make a new model, so a drag adds each step to these values, not to the value of the model (review finding 2
    // of M6b-2).
    private readonly Dictionary<string, double[]> numbers = [];

    // The number field that the user drags or types into now, or null.
    private string? activeNumber;

    // The last pick of an open picker of a field that asks first: it runs when the picker closes.
    private readonly Dictionary<string, string> pendingPicks = [];

    /// <summary>Runs the batch of one gesture. Returns true when the batch changed W.</summary>
    private bool Run(MissionBatch? batch) => window.Missions.Run(batch);

    /// <summary>
    /// True while the user works with the widget of the open gesture: an item is active, or the picker of the gesture is
    /// open and the page drew it in the last frame. The page does not draw the picker of another item, so a gesture
    /// ends when the selection changes (review finding 6 of M6b-2).
    /// </summary>
    private bool GestureShows() =>
        gesture is var (owner, _) && (ImGui.IsAnyItemActive() ||
                                      pickers.TryGetValue(owner, out var picker) && picker.IsOpen &&
                                      pickerFrames.GetValueOrDefault(owner) >= ImGui.GetFrameCount() - 1);

    /// <summary>Ends the open gesture and forgets the states of the widgets, for example for the page of another
    /// item.</summary>
    private void ForgetWidgets()
    {
        EndGesture();
        pickers.Clear();
        pickerFrames.Clear();
        pendingAdd.Clear();
        pendingPicks.Clear();
        numbers.Clear();
        activeNumber = null;
        texts.Clear();
        newFile.Close();
    }

    /// <summary>Forgets the values of the number fields for a new model. The field that the user drags keeps its
    /// values.</summary>
    private void ForgetNumbers()
    {
        foreach (var id in numbers.Keys.Where(id => id != activeNumber).ToList())
        {
            numbers.Remove(id);
        }
    }

    /// <summary>One step of a gesture with many steps: the first step opens a transaction, and each step takes the place
    /// of the step before it (section 4.11 of the M6b design).</summary>
    private void Step(string field, MissionBatch? batch)
    {
        if (batch == null || window.Session?.Edits is not { } edits)
        {
            return;
        }

        if (gesture is not var (owner, transaction) || owner != field || !transaction.IsOpen)
        {
            EndGesture();
            try
            {
                transaction = edits.Missions.Begin(batch.Label);
            }
            catch (EditException e)
            {
                window.Log.Add(ConsoleSeverity.Warning, MissionChecks.Source, e.Error.Message);
                return;
            }

            gesture = (field, transaction);
        }

        var result = transaction.Apply(batch);
        if (result.Status == EditStatus.Refused && result.Summary is { } message && message != lastRefusal)
        {
            lastRefusal = message;
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, message);
        }
    }

    /// <summary>Ends the open gesture: one history entry when its steps changed data.</summary>
    private void EndGesture()
    {
        if (gesture is not var (_, transaction))
        {
            return;
        }

        gesture = null;
        lastRefusal = null;
        if (!transaction.IsOpen)
        {
            return;
        }

        var result = transaction.Commit();
        if (result.Status != EditStatus.Applied)
        {
            // The fields show the values of the model again.
            activeNumber = null;
            ForgetNumbers();
        }

        foreach (var error in result.Errors)
        {
            window.Log.Add(ConsoleSeverity.Error, MissionChecks.Source, error.Message);
        }

        foreach (var note in result.Notes)
        {
            window.Log.Add(ConsoleSeverity.Info, MissionChecks.Source, note);
        }
    }

    /// <summary>The edit widget of a field in its row. Returns false for a field without a widget that the session can
    /// change now, so the page draws it read-only.</summary>
    private bool EditField(string id, PageField field)
    {
        if (field.Edit is not { ReadOnly: null } edit || window.Missions.ReadOnlyNote != null || edit.Widget == EditWidget.List)
        {
            return false;
        }

        // The menu of the field comes after its widget, for example "Revert" of an override (section 8.6 of the M6b
        // design). The note of the field comes under them.
        void Row(Action widget) => grid.Row(field.Label, () =>
        {
            widget();
            RowMenu(id, field.Actions);
            Note(edit);
        });

        switch (edit.Widget)
        {
            case EditWidget.Picker:
                Row(() => Picker(id, field, edit));
                break;
            case EditWidget.Words:
                Row(() => Words(field, edit));
                break;
            case EditWidget.Number or EditWidget.WholeNumber:
                Row(() => Number(id, edit));
                break;
            case EditWidget.Position or EditWidget.Rotation:
                Row(() => Numbers(id, field, edit));
                break;
            case EditWidget.Bool:
                Row(() =>
                {
                    var on = edit.Value == "true";
                    if (ImGui.Checkbox("##bool", ref on))
                    {
                        Run(edit.Make?.Invoke(MissionValue.Of(on)));
                    }
                });
                break;
            case EditWidget.TextId:
                Row(() => TextId(id, field, edit));
                break;
            case EditWidget.Text:
                Row(() => Text(id, edit));
                break;
            case EditWidget.AddPicker:
                Row(() => AddPicker(id, edit));
                break;
        }

        return true;
    }

    /// <summary>
    /// A name: the reference field with the kind glyph, the name text and the button that goes to the item (UI rule 6),
    /// and the list picker with the "Missing" and "None" rows. A click on the field opens the picker. Each pick of the
    /// open picker is a step of one transaction. A field that asks first runs the last pick when the picker closes.
    /// </summary>
    private void Picker(string id, PageField field, FieldEdit edit)
    {
        var (kind, nickname, text) = field.Reference is { } reference
            ? (new ObjectKind(reference.Kind, reference.Glyph, EditorTheme.Label), (string?)reference.Nickname,
                field.Missing ? $"Missing: {reference.Nickname}"
                : reference.Name is { } name ? $"{reference.Nickname} ({name})"
                : reference.Nickname)
            : (new ObjectKind(edit.Spec?.Label ?? field.Label, MissionGlyphs.OfValue(edit.Spec?.Kind ?? MissionValueKind.Text),
                    EditorTheme.Label), edit.Value.Length > 0 ? edit.Value : null,
                edit.Value.Length > 0 ? field.Value
                : field.Value == MissionPageModel.NotSet ? MissionPageModel.NotSet
                : "None");
        var link = field.Link;
        var open = grid.PickField(kind, nickname, text, link != null ? () => GoTo(link) : null, field.Missing);
        if (!pickers.TryGetValue(id, out var picker))
        {
            pickers[id] = picker = new ValuePicker();
        }

        pickerFrames[id] = ImGui.GetFrameCount();
        var picked = picker.Draw("##picker", open, edit.Value, () => Entries(edit), edit.NoneWord != null, kind);
        if (picked != null)
        {
            var value = picked.Length == 0 && edit.NoneWord is { Length: > 0 } none ? none : picked;
            if (edit.Confirms)
            {
                pendingPicks[id] = value;
            }
            else
            {
                Step(id, edit.Make?.Invoke(MissionValue.Of(value)));
            }
        }

        if (picker.IsOpen)
        {
            return;
        }

        if (pendingPicks.Remove(id, out var last) && !string.Equals(last, edit.Value, StringComparison.Ordinal))
        {
            Run(edit.Make?.Invoke(MissionValue.Of(last)));
        }

        if (gesture is var (owner, _) && owner == id)
        {
            EndGesture();
        }
    }

    /// <summary>The data lists of the ids of a picker, joined with "+". A mission list has the entries of the script of
    /// the scope. Null for a list that is not available.</summary>
    private IEnumerable<ListView?> ListsOf(string ids, string? scope)
    {
        var lists = window.Session?.Edits.Lists;
        foreach (var id in ids.Split('+'))
        {
            yield return lists == null ? null
                : id.StartsWith("mission_", StringComparison.Ordinal) && scope != null ? lists.List(id, scope)
                : lists.List(id);
        }
    }

    /// <summary>The entries of the lists of a picker, with their display names.</summary>
    private IReadOnlyList<(ListEntry Entry, string Name)> Entries(FieldEdit edit)
    {
        if (window.Session is not { } session || edit.List == null)
        {
            return [];
        }

        var result = new List<(ListEntry, string)>();
        foreach (var entry in ListsOf(edit.List, edit.Scope).SelectMany(list => list?.Entries ?? []))
        {
            var name = entry.Section != null && session.SectionName(entry.Section) is { Length: > 0 } text
                ? text
                : entry.Name ?? entry.Value;
            result.Add((entry, name));
        }

        return result;
    }

    /// <summary>True when a list of the lists of a picker has a value. A list that is not available checks
    /// nothing.</summary>
    private bool Resolves(string ids, string? scope, string value) =>
        window.Session == null || ListsOf(ids, scope).Any(list => list == null || list.Find(value) != null);

    /// <summary>The note under the widget of a field: a problem of the checks in the color of its severity, or a dim
    /// note.</summary>
    private static void Note(FieldEdit edit)
    {
        if (edit.Note is not { } note)
        {
            return;
        }

        ImGui.PushTextWrapPos(0);
        if (edit.NoteSeverity is { } severity)
        {
            ImGui.TextColored(severity == DiagnosticSeverity.Error ? EditorTheme.Error : EditorTheme.Warning, note);
        }
        else
        {
            ImGui.TextDisabled(note);
        }

        ImGui.PopTextWrapPos();
    }

    /// <summary>A dropdown of the words of a set, or of the conditions of the field "Event" by event group. A choice
    /// with a note or a warning shows it under the choice. The item "New file..." at the end opens its popup.</summary>
    private void Words(PageField field, FieldEdit edit)
    {
        var current = edit.Choices.FirstOrDefault(c => string.Equals(c.Value, edit.Value, StringComparison.OrdinalIgnoreCase));
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##words", current.Label ?? (edit.Value.Length > 0 ? edit.Value : field.Value)))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        string? group = null;
        foreach (var (value, label, choiceGroup) in edit.Choices)
        {
            if (choiceGroup != null && choiceGroup != group)
            {
                group = choiceGroup;
                ImGui.SeparatorText(group);
            }

            if (ImGui.Selectable($"{label}##{value}", string.Equals(value, edit.Value, StringComparison.OrdinalIgnoreCase)))
            {
                Run(edit.Make?.Invoke(MissionValue.Of(value)));
            }

            var note = edit.ChoiceNotes.GetValueOrDefault(value);
            var warning = edit.ChoiceWarnings.GetValueOrDefault(value);
            if (!string.Equals(label, value, StringComparison.Ordinal) || note != null || warning != null)
            {
                ImGui.SetItemTooltip(string.Join("\n", new[] { label != value ? value : null, note, warning }.OfType<string>()));
            }

            if (note != null || warning != null)
            {
                ImGui.Indent();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360 * ImGuiHelper.Scale);
                if (note != null)
                {
                    ImGui.TextDisabled(note);
                }

                if (warning != null)
                {
                    ImGui.TextColored(EditorTheme.Warning, warning);
                }

                ImGui.PopTextWrapPos();
                ImGui.Unindent();
            }
        }

        if (edit.NewFile is { } item)
        {
            ImGui.Separator();
            if (ImGui.Selectable(NewFileItem.Label))
            {
                newFile.Open(item);
            }

            ImGui.SetItemTooltip(item.Tooltip);
        }

        EditorTheme.PopMenuColors();
        ImGui.EndCombo();
    }

    /// <summary>A number drag (M20). The drag is one transaction, and typed text ends with the field.</summary>
    private void Number(string id, FieldEdit edit)
    {
        ImGui.SetNextItemWidth(-1);
        var value = numbers.TryGetValue(id, out var shown) ? shown[0] : Parse(edit.Value);
        bool changed;
        if (edit.Widget == EditWidget.WholeNumber)
        {
            var whole = (int)value;
            changed = ImGui.DragInt("##number", ref whole, 1);
            value = whole;
        }
        else
        {
            var single = (float)value;
            changed = ImGui.DragFloat("##number", ref single, Math.Max(0.01f, Math.Abs(single) / 100), 0, 0, "%g");
            value = single;
        }

        NumberGesture(id, edit, changed, [value]);
    }

    /// <summary>Three numbers of a position with "Pick in Scene", or four numbers of a rotation.</summary>
    private void Numbers(string id, PageField field, FieldEdit edit)
    {
        var count = edit.Widget == EditWidget.Rotation ? 4 : 3;
        var parts = numbers.TryGetValue(id, out var shown) ? shown.ToList()
            : edit.Value.Length == 0 ? []
            : edit.Value.Split(',').Select(Parse).ToList();
        while (parts.Count < count)
        {
            parts.Add(edit.Widget == EditWidget.Rotation && parts.Count == 0 ? 1 : 0);
        }

        var pick = edit.PickInScene ? ImGui.CalcTextSize("Pick in Scene").X + ImGui.GetStyle().FramePadding.X * 2 +
                                      ImGui.GetStyle().ItemSpacing.X : 0;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - pick);
        bool changed;
        double[] values;
        if (count == 3)
        {
            var vector = new Vector3((float)parts[0], (float)parts[1], (float)parts[2]);
            changed = ImGui.DragFloat3("##position", ref vector, 1, 0, 0, "%g");
            values = [vector.X, vector.Y, vector.Z];
        }
        else
        {
            var vector = new Vector4((float)parts[0], (float)parts[1], (float)parts[2], (float)parts[3]);
            changed = ImGui.DragFloat4("##rotation", ref vector, 0.01f, 0, 0, "%g");
            values = [vector.X, vector.Y, vector.Z, vector.W];
        }

        NumberGesture(id, edit, changed, values);
        if (edit.PickInScene)
        {
            PickInScene(id, field, edit);
        }
    }

    /// <summary>
    /// The end of the frame of a number field. The field shows its own values after a change, so each step of a drag
    /// adds to the step before it (review finding 2 of M6b-2). Each change is a step of one transaction, and the end of
    /// the field ends the transaction. A field that asks first runs one batch when it ends.
    /// </summary>
    private void NumberGesture(string id, FieldEdit edit, bool changed, double[] values)
    {
        var active = ImGui.IsItemActive();
        if (active)
        {
            activeNumber = id;
        }
        else if (activeNumber == id)
        {
            activeNumber = null;
        }

        if (changed || active)
        {
            numbers[id] = values;
        }

        if (changed && !edit.Confirms)
        {
            Step(id, edit.Make?.Invoke(MissionValue.Of(values)));
        }

        if (!ImGui.IsItemDeactivated())
        {
            return;
        }

        if (!edit.Confirms)
        {
            EndGesture();
        }
        else if (!ImGui.IsItemDeactivatedAfterEdit() || !Run(edit.Make?.Invoke(MissionValue.Of(values))))
        {
            numbers.Remove(id);
        }
    }

    /// <summary>The button "Pick in Scene" of a position: the next click in the Scene sets the position, while the
    /// Scene shows the system of the item (section 8.9 of the M6b design).</summary>
    private void PickInScene(string id, PageField field, FieldEdit edit)
    {
        ImGui.SameLine();
        var missions = window.Missions;
        var waiting = missions.PendingPick is { } pending && pending.Key == id;
        if (ImGui.Button(waiting ? "Picking...##pick" : "Pick in Scene##pick"))
        {
            if (waiting)
            {
                missions.CancelPick();
            }
            else if (window.Selection is { } owner)
            {
                missions.StartPick(new ScenePick(id, field.Label, edit.PickSystem, owner,
                    point => Run(edit.Make?.Invoke(MissionValue.Of(point.X, point.Y, point.Z)))));
            }
        }

        ImGui.SetItemTooltip(PickTip);
    }

    /// <summary>A text id: the number, and own text that the editor writes into the text of the mod.</summary>
    private void TextId(string id, PageField field, FieldEdit edit)
    {
        var number = (int)Parse(edit.Value);
        ImGui.SetNextItemWidth(Math.Min(120 * ImGuiHelper.Scale, ImGui.GetContentRegionAvail().X));
        ImGui.InputInt("##id", ref number, 0);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Run(edit.Make?.Invoke(MissionValue.Of(number)));
        }

        ImGui.SetItemTooltip(field.Value);
        if (edit.MakeText == null)
        {
            return;
        }

        ImGui.SameLine();
        var own = texts.GetValueOrDefault(id + ".own") ?? "";
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##own", "Own text", ref own, 1024, ImGuiInputTextFlags.EnterReturnsTrue) &&
            own.Trim().Length > 0)
        {
            Run(edit.MakeText(own.Trim()));
            own = "";
        }

        ImGui.SetItemTooltip("Type the text of this line. The editor writes it into the text of the mod. Enter sets it.");
        texts[id + ".own"] = own;
    }

    /// <summary>A text: the edit runs when the field ends with a change.</summary>
    private void Text(string id, FieldEdit edit)
    {
        // The field keeps the typed text while it is active. Else it shows the value of the line.
        var text = texts.TryGetValue(id, out var typed) ? typed : edit.Value;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##text", ref text, 512);
        if (ImGui.IsItemActive())
        {
            texts[id] = text;
            return;
        }

        texts.Remove(id);
        if (ImGui.IsItemDeactivatedAfterEdit() && text.Trim() != edit.Value)
        {
            Run(edit.Make?.Invoke(MissionValue.Of(text.Trim())));
        }
    }

    /// <summary>The "Add" of a list of triggers or members: a picker whose pick adds the item.</summary>
    private void AddPicker(string id, FieldEdit edit)
    {
        var open = ImGui.Button($"{EditorIcons.Plus}  Add##add");
        if (!pickers.TryGetValue(id, out var picker))
        {
            pickers[id] = picker = new ValuePicker();
        }

        var picked = picker.Draw("##add-picker", open, "", () => Entries(edit), false,
            new ObjectKind("Item", EditorIcons.Bolt, EditorTheme.Label));
        // Each pick of the open picker moves the selection. The last pick adds its item when the picker closes. Esc
        // gives the empty value of the opening, so it adds nothing.
        if (picked != null)
        {
            pendingAdd[id] = picked;
        }

        if (!picker.IsOpen && pendingAdd.Remove(id, out var last) && last.Length > 0)
        {
            Run(edit.Add?.Invoke(last));
        }
    }

    // The last pick of an open "Add" picker: the item goes in when the picker closes.
    private readonly Dictionary<string, string> pendingAdd = [];

    /// <summary>A list field of the page: one gesture of the widget gives one batch.</summary>
    private void ListEdit(string key, PageField field, ListFieldModel list)
    {
        if (!lists.TryGetValue(key, out var widget))
        {
            lists[key] = widget = new ListField();
        }

        var edit = field.Edit;
        var readOnly = window.Missions.ReadOnlyNote ?? edit?.ReadOnly ?? (edit?.MakeList == null ? MissionPageModel.FieldNote : null);
        // The cell checks show at once (section 8.5 of the M6b design): a name outside its lists, a number outside its
        // range, a fraction in a whole number and a word in the wrong letter case.
        var problems = readOnly == null ? list.Check((ids, value) => Resolves(ids, edit?.Scope, value)) : null;
        grid.FullRow(() =>
        {
            var changed = widget.Draw(key, list, null, readOnly, problems,
                entries: id => Entries(new FieldEdit(EditWidget.Picker, "") { List = id, Scope = edit?.Scope }),
                changed: field.Changed, overridden: field.Override);
            if (changed != null && edit?.MakeList is { } make)
            {
                Run(make(changed));
            }

            if (readOnly == null)
            {
                RowMenu(key, field.Actions);
            }
        });
    }

    /// <summary>The small menu button of a row with actions, for example "Move up" of an action line.</summary>
    private void RowMenu(string id, IReadOnlyList<PageAction> actions)
    {
        if (actions.Count == 0)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"{EditorIcons.EllipsisV}##menu-{id}"))
        {
            ImGui.OpenPopup($"##row-menu-{id}");
        }

        ImGui.SetItemTooltip("Line menu");
        Actions($"##row-menu-{id}", actions);
    }

    /// <summary>The popup of a menu of actions.</summary>
    private void Actions(string popup, IReadOnlyList<PageAction> actions)
    {
        if (!ImGui.BeginPopup(popup))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        MenuItems(actions);
        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The items of a menu of actions. An item runs its batch or goes to its link. An item without both is a
    /// heading, for example "Overrides (2)".</summary>
    private void MenuItems(IReadOnlyList<PageAction> actions)
    {
        foreach (var action in actions)
        {
            if (action.IsHeading)
            {
                ImGui.SeparatorText(action.Label);
                continue;
            }

            if (ImGui.MenuItem(action.Label))
            {
                Do(action);
            }

            if (action.Tooltip != null)
            {
                ImGui.SetItemTooltip(action.Tooltip);
            }
        }
    }

    /// <summary>Runs the batch of an action, or goes to its link.</summary>
    private void Do(PageAction action)
    {
        if (action.Batch != null)
        {
            Run(action.Batch);
        }
        else if (action.Link != null)
        {
            GoTo(action.Link);
        }
    }

    /// <summary>The page menu at the right of the header: "Rename" and the actions of the page. F2 renames the item while
    /// the Inspector has the focus, as in the graph and the Hierarchy.</summary>
    private void PageMenu(MissionPageModel model)
    {
        if (model.PageActions.Count == 0 && model.Rename == null || window.Missions.ReadOnlyNote != null)
        {
            return;
        }

        if (model.Rename != null && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) &&
            !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F2, false))
        {
            rename.Open(model.Title);
        }

        if (ImGui.SmallButton($"{EditorIcons.EllipsisV}##page-menu"))
        {
            ImGui.OpenPopup("##page-menu");
        }

        ImGui.SetItemTooltip("Page menu");
        if (ImGui.BeginPopup("##page-menu"))
        {
            EditorTheme.PushMenuColors();
            if (model.Rename != null && ImGui.MenuItem("Rename", "F2"))
            {
                rename.Open(model.Title);
            }

            MenuItems(model.PageActions);

            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        if (rename.Draw() is { } name)
        {
            Run(model.Rename?.Invoke(name));
        }
    }

    /// <summary>The button "Add action" at the end of a Trigger page, and quick add of the page. Quick add of the "+" of
    /// a group shows only the actions of the group.</summary>
    private void AddAction(MissionPageModel model)
    {
        if (model.PageTrigger is not { } trigger || model.ReadOnly != null || window.Missions.ReadOnlyNote != null)
        {
            return;
        }

        if (ImGui.Button($"{EditorIcons.Plus}  Add action"))
        {
            quickAdd.Open(new QuickAddContext(null, trigger, QuickAddFilter.Actions), ImGui.GetItemRectMin());
        }

        ImGui.SetItemTooltip("Add an action at the end of the trigger.");
        if (quickAdd.Draw(MissionLibrary.Studio(window.Session?.Edits.HidesOpenLancer != true)) is var (_, item))
        {
            Run(GraphEditModel.AddTo(trigger, item));
        }
    }

    /// <summary>Opens quick add of the actions of one group, for the "+" of a group of the Trigger page.</summary>
    private void AddToGroup(MissionTrigger trigger, string group) =>
        quickAdd.Open(new QuickAddContext(null, trigger, QuickAddFilter.Actions, group), ImGui.GetMousePos());

    /// <summary>The buttons of the actions of a section, for example "This ship only".</summary>
    private void SectionActions(PageSection section)
    {
        // A read-only session keeps the buttons that go to a link, for example "Open prefab".
        var actions = window.Missions.ReadOnlyNote == null ? section.Actions : section.Actions.Where(a => a.Batch == null).ToList();
        if (actions.Count == 0)
        {
            return;
        }

        grid.FullRow(() =>
        {
            foreach (var action in actions)
            {
                if (ImGui.Button(action.Label))
                {
                    Do(action);
                }

                if (action.Tooltip != null)
                {
                    ImGui.SetItemTooltip(action.Tooltip);
                }

                ImGui.SameLine();
            }

            ImGui.NewLine();
        });
    }

    private static double Parse(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
