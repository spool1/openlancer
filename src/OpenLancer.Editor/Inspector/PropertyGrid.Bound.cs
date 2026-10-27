using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The bound rows of the grid. Inside a <see cref="Bind"/> scope, a row with an INI key that the schema of the section
/// has (or that the section has) shows the lines of the edit view, and the user can edit them. The page code keeps its
/// row calls: each row method asks <see cref="BoundRow"/> first.
/// </summary>
/// <remarks>
/// <para>Numbers and text edit as text with invariant parsing, so a float value stays exact. A bool gets a checkbox,
/// an enum a list, a vector three fields, and the size of a zone one labeled field for each value. A drag on the label
/// of such a field changes its value, and a color key gets a swatch with a color picker in front of its text field (see
/// <c>PropertyGrid.Helpers.cs</c>). A text key, for example <c>ids_name</c>, edits the text of the active mod.</para>
/// <para>A page draws the rows of the values of other files, for example the model of an archetype, in a
/// <c>Bind(null)</c> scope. A reference row shows the lines of its key with the button that selects the item. Below a
/// row, a problem of the scene shows, for example an archetype that does not exist.</para>
/// <para>Each text field is one edit of the history: the edit starts when the field becomes active, and the edit ends
/// when the field ends. Each change of the lines of a key shows at once. The text of a text key changes when the field
/// ends. Escape cancels the edit (see <see cref="FieldEditRouter"/>).</para>
/// <para>While a field edit is open, its row keeps the widgets that it had at the start of the edit, and a bound row
/// has the ImGui id of its key. So the active widget stays with its key when the edit changes the rows.</para>
/// <para>Each bound row has marks: a blue bar and a blue label when the active mod sets the value, a warning glyph
/// when a later mod changes it, and a lock when the row is read-only. The tooltip of the label tells which layer sets
/// the value. The menu of the label has Revert and Copy INI line.</para>
/// </remarks>
internal sealed partial class PropertyGrid
{
    // Sizes in unscaled points.
    private const int MultilineRows = 4;

    private readonly Dictionary<SectionRef, BoundSection?> boundSections = [];
    private readonly FieldEditRouter fieldEdits =
        new(message => window.Log.Add(Logs.ConsoleSeverity.Warning, EditController.Source, message));

    private static readonly HashSet<string> NoKeys = [];

    // The keys that bound rows showed in the frame shownFrame, by section.
    private readonly Dictionary<SectionRef, HashSet<string>> shown = [];
    private int shownFrame = -1;

    private (EditorSession? Session, int Version, FieldGesture? Edit) boundVersion;
    private BoundSection? bound;

    // The width that the value widgets of a bound row leave free at their right, for the button of a reference.
    private float reserve;

    /// <summary>True while a field edit is open. The edit history waits for its end. A save ends it first.</summary>
    public bool IsEditing => fieldEdits.Open != null;

    /// <summary>The open field edit, or null.</summary>
    public FieldGesture? OpenEdit => fieldEdits.Open;

    /// <summary>True in a scope that a section of the edit view binds (see <see cref="Bind"/>).</summary>
    public bool IsBound => bound != null;

    /// <summary>
    /// Binds the rows of a scope to a section of the edit view. Null ends the binding for the scope, for example for
    /// rows that show values of another section. Dispose the result to end the scope.
    /// </summary>
    public BindScope Bind(SectionRef? section)
    {
        var previous = bound;
        bound = section == null ? null : Section(section);
        return new BindScope(this, previous);
    }

    /// <summary>
    /// True when the page shows the rows of the keys. In a bound scope, that is when the section has a line of one of
    /// the keys (see <see cref="Shows(SectionView, bool, string[])"/>). Without a bound section, the answer is
    /// <paramref name="unbound"/>, for example because the game data sets the value.
    /// </summary>
    public bool Shows(bool unbound, params string[] keys) => Shows(Layout, unbound, keys);

    /// <summary>
    /// True when a page shows the rows of the keys. With a section to lay out from, that is when the section has a line
    /// of one of the keys, and never from the values of the scene or the game data. While a field edit of the section
    /// is open, that section is the section at the start of the edit. So no row comes or goes while a field edit is
    /// open: a row stays while its own edit removes its last line, and a key in "More keys" stays there while its edit
    /// changes the scene item (see <see cref="SectionExtras"/>).
    /// </summary>
    /// <param name="layout">The section that a bound page lays out its rows from (see <see cref="LayoutView"/>), or
    /// null without a bound section.</param>
    /// <param name="unbound">The answer without a bound section.</param>
    internal static bool Shows(SectionView? layout, bool unbound, params string[] keys) =>
        layout == null ? unbound : keys.Any(k => layout.Lines(k).Count > 0);

    /// <summary>The section that the rows of the bound scope lay out from (see <see cref="LayoutView"/>), or null
    /// without a bound section.</summary>
    internal SectionView? Layout => bound?.Layout;

    /// <summary>The name of a key that a section can also set with an alias, for the row of the key: the name of the
    /// last line of either name in the bound section, or the key (see <see cref="LastName"/>).</summary>
    public string KeyOrAlias(string key, string alias) => Layout is { } layout ? LastName(layout, key, alias) : key;

    /// <summary>
    /// Of a key and its alias, the name of the last line of either name in the section, or the key when the section has
    /// a line of neither. Each line of either name sets the value, so the engine uses the last line, and a row of the
    /// value edits that line.
    /// </summary>
    /// <param name="layout">The section that the page lays out its rows from, so the name stays while a field edit is
    /// open.</param>
    internal static string LastName(SectionView layout, string key, string alias) =>
        layout.Entries.LastOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                                          e.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)) is { } last &&
        last.Name.Equals(alias, StringComparison.OrdinalIgnoreCase)
            ? alias
            : key;

    /// <summary>The keys of a section that bound rows showed in this frame, with the schema names of the keys.</summary>
    public IReadOnlySet<string> Shown(SectionRef section) =>
        shownFrame == ImGui.GetFrameCount() && shown.TryGetValue(section, out var keys) ? keys : NoKeys;

    /// <summary>
    /// The section that a page lays out its rows from: the section at the start of an open line edit of it, or the
    /// section of the edit view. So the rows of a page stay while the edit changes the lines.
    /// </summary>
    /// <param name="open">The open field edit, or null.</param>
    internal static SectionView? LayoutView(EditSession edits, SectionRef section, FieldGesture? open) =>
        (open is LineEditGesture edit && edit.Key.Owner.Equals(section) ? edit.StartSection : null) ??
        edits.Get(section);

    /// <summary>The end of a <see cref="Bind"/> scope.</summary>
    internal readonly struct BindScope(PropertyGrid grid, BoundSection? previous) : IDisposable
    {
        public void Dispose() => grid.bound = previous;
    }

    /// <summary>Cancels an open field edit and forgets the bindings. Call it when the profile closes.</summary>
    public void Reset()
    {
        fieldEdits.End(keep: false);
        boundSections.Clear();
        boundVersion = default;
    }

    /// <summary>
    /// Ends a field edit whose field did not draw in the last frame, for example because the selection changed. Call
    /// it once each frame before the rows draw.
    /// </summary>
    public void EndLostEdit() => fieldEdits.EndLost(ImGui.GetFrameCount());

    /// <summary>Ends an open field edit and keeps its text. Call it when the window closes, so that the recovery file
    /// keeps the edit (see <see cref="FieldEditRouter.EndAtClose"/>).</summary>
    public void EndEdit() => fieldEdits.EndAtClose();

    /// <summary>The binding of a key of the current scope, or null when the row is not bound.</summary>
    private FieldBinding? Binding(string? iniKey) => iniKey == null ? null : bound?.Field(iniKey);

    /// <summary>The bound section of a reference for this edit version, or null when the edit view has no such
    /// section.</summary>
    private BoundSection? Section(SectionRef section)
    {
        if (window.Session is not { } session)
        {
            return null;
        }

        // An edit, an undo, a redo or a save changes the version. The bindings of the version before are old. The
        // start and the end of a field edit change which key stays bound (see BoundSection).
        var open = fieldEdits.Open;
        if (boundVersion != (session, session.Version, open))
        {
            boundSections.Clear();
            boundVersion = (session, session.Version, open);
        }

        if (!boundSections.TryGetValue(section, out var result))
        {
            var view = session.Edits.Get(section);
            var editing = open != null && open.Key.Owner.Equals(section) ? open.Key.Name : null;
            result = view == null
                ? null
                : new BoundSection(session.Edits, section, view, LayoutView(session.Edits, section, open) ?? view, editing);
            boundSections[section] = result;
        }

        return result;
    }

    /// <summary>
    /// Draws a bound row when the key of the row is bound. Returns false for an unbound row, which the caller then
    /// draws as before.
    /// </summary>
    /// <param name="unset">The value that the engine uses when no line sets the key, as text.</param>
    /// <param name="labels">Gives the labels of the values of a line with a number of values, or null when a line with
    /// that number of values gets one text field. Null for the default: X, Y and Z for a vector of three values.</param>
    /// <param name="follow">The button that selects the item of a reference, or null for no button.</param>
    private bool BoundRow(string label, string? iniKey, string unset,
        Func<int, IReadOnlyList<ComponentLabel>?>? labels = null, Follow? follow = null)
    {
        if (Binding(iniKey) is not { } field)
        {
            return false;
        }

        var reason = field.ReadOnlyReason ?? window.Session!.NoEditReason;
        BeginRow(label, iniKey, field, reason);
        var hint = $"Not set. The engine uses {unset}.";
        // While a field edit of the key is open, the row draws the widgets of the key at the start of the edit. So the
        // active widget stays, also when the edit changes the number of lines.
        var shape = fieldEdits.StartOf(field) ?? field;
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        // A key with a data list asks the lists before the widget of its type (milestone M21). A reference row keeps
        // its own follow target, and a listed value gets a button that selects its entry.
        var rules = field.Key?.TextKind == null ? ListRules(field) : [];
        follow ??= ListFollow(field, rules);
        var asset = reason == null ? AssetOf(field, rules) : null;
        reserve = (follow == null ? 0 : ToolbarItems.IconWidth + spacing) + (asset == null ? 0 : ToolbarItems.IconWidth + spacing);
        if (field.Key?.TextKind is { } kind)
        {
            TextKeyField(field, kind, reason);
        }
        else if (reason != null)
        {
            // A read-only field has one line, so a bar separates the lines of the key.
            Field(field.Text.Replace("\n", " | "), FillWidth, field.Lines.Count == 0 ? hint : "None");
        }
        else
        {
            // A multiline key keeps its text field. Pickers for each line come with M28 (decision D122).
            var single = field.Key?.Multiline != true;
            var slots = single ? SlotLabels(rules) : null;
            var labelsOf = slots ?? labels ?? DefaultLabels(field.Key);
            var values = FieldEdit.Values(shape.Text).Length;
            var listed = single && IsListed(rules) && ListOf(field, rules[0]) != null;
            switch (WidgetOf(field.Key, shape.Lines.Count, values, labelsOf, listed))
            {
                case BoundWidget.Bool:
                    BoolField(field, unset);
                    break;
                case BoundWidget.Enum:
                    EnumField(field, field.Key!.ClrType!, hint);
                    break;
                case BoundWidget.List:
                    ListField(field, rules[0], hint);
                    break;
                case BoundWidget.Components when slots != null:
                    ListSlots(field, rules, slots(values)!);
                    break;
                case BoundWidget.Components:
                    // An edit of one value keeps the number of values, because an empty value is not valid.
                    ComponentField(field, labelsOf!(values)!);
                    break;
                case BoundWidget.Color:
                    ColorField(field, ColorForms.Of(field.Key)!.Value, hint);
                    break;
                default:
                    TextField(field, shape.Lines.Count, hint);
                    break;
            }
        }

        reserve = 0;
        if (follow is { } button)
        {
            ImGui.SameLine(0, spacing);
            FollowButton(button);
        }

        if (asset is { } shown)
        {
            ImGui.SameLine(0, spacing);
            ShowInAssetsButton(shown);
        }

        RowProblems(field, reason);
        EndRow();
        NoteShown(field);
        return true;
    }

    /// <summary>
    /// The widget of an editable bound row: a picker for a key with a data list, a checkbox for a bool, a list for an
    /// enum, a labeled field for each value of a line with component labels, a color swatch with the text field for a
    /// one-line color key, else a text field.
    /// </summary>
    /// <param name="lines">The number of lines of the key at the start of an open edit of it, or now.</param>
    /// <param name="values">The number of values of the first line, in the same state.</param>
    /// <param name="labels">The labels of the values of a line with a number of values (see <see cref="BoundRow"/>),
    /// with the default already applied.</param>
    /// <param name="listed">True for a key with one list rule on its first value (see <see cref="IsListed"/>).</param>
    internal static BoundWidget WidgetOf(KeySchema? key, int lines, int values,
        Func<int, IReadOnlyList<ComponentLabel>?>? labels, bool listed = false)
    {
        if (listed && lines <= 1)
            return BoundWidget.List;
        if (key?.ClrType == typeof(bool) && lines <= 1)
            return BoundWidget.Bool;
        if (key?.ClrType is { IsEnum: true } && lines <= 1)
            return BoundWidget.Enum;
        if (lines == 1 && labels?.Invoke(values) != null)
            return BoundWidget.Components;
        return lines == 1 && ColorForms.Of(key) != null ? BoundWidget.Color : BoundWidget.Text;
    }

    /// <summary>The labels of the values of a line of a key without its own labels: X, Y and Z for a line of three
    /// values of a vector.</summary>
    internal static Func<int, IReadOnlyList<ComponentLabel>?>? DefaultLabels(KeySchema? key) =>
        key is { Multiline: false } && key.ClrType == typeof(Vector3) ? count => count == 3 ? AxisLabels : null : null;

    /// <summary>
    /// The bits that the chips or the checkboxes of a flags key show: in a bound scope, the value of the lines of the
    /// key in W, else <paramref name="unbound"/>. So they follow an edit of the value row at once, also for a key that
    /// the scene does not apply, for example <c>visit</c> of a system.
    /// </summary>
    private ulong FlagBits(string? iniKey, ulong unbound) =>
        Binding(iniKey) is { } field && LineBits(field.Lines) is { } bits ? bits : unbound;

    /// <summary>
    /// The bits of a flags key as the engine reads its lines: the first value of the last line as a whole number, or 0
    /// without a line. Null when that value is not a whole number. Then the check of the section fails, and the row
    /// shows the value of the scene or the game data.
    /// </summary>
    internal static ulong? LineBits(IReadOnlyList<EntryView> lines) =>
        lines.Count == 0 ? 0
        : lines[^1].Values is [var value, ..] && value.TryToInt32(out var number) ? unchecked((ulong)(long)number)
        : null;

    /// <summary>The width argument of a value widget that fills the cell, less the width of a reference button.</summary>
    private float FillWidth => reserve > 0 ? -reserve : -1;

    /// <summary>Notes that a bound row of the key showed in this frame (see <see cref="Shown"/>).</summary>
    private void NoteShown(FieldBinding field)
    {
        var frame = ImGui.GetFrameCount();
        if (frame != shownFrame)
        {
            shown.Clear();
            shownFrame = frame;
        }

        if (!shown.TryGetValue(field.Section, out var keys))
        {
            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            shown[field.Section] = keys;
        }

        keys.Add(field.Name);
    }

    /// <summary>
    /// The problems below a row of their key: the problems of the data lists with their fixes (see
    /// <see cref="ListRowProblems"/>), or else the problems of the scene, for example the model of an archetype that
    /// does not load (see <see cref="Runtime.Scene.SceneApplier.Problems"/>). A value that only W has is in its list,
    /// so its scene problem gives the note that the Scene shows it after the save (review finding R15).
    /// </summary>
    /// <param name="reason">Why the row cannot change the key, or null. A read-only row shows no fix.</param>
    private void RowProblems(FieldBinding field, string? reason)
    {
        if (ListRowProblems(field, reason))
        {
            return;
        }

        var scene = window.Scene.Problems(field.Section)
            .Where(p => field.Name.Equals(p.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (scene.Count > 0 && HasListedValue(field))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
            ImGui.TextWrapped($"{EditorIcons.InfoCircle} {WOnlyNote}");
            ImGui.PopStyleColor();
            return;
        }

        scene.ForEach(ProblemText);
    }

    /// <summary>The tooltip of a base-game problem of a section (decision D512 of the console filter design).</summary>
    internal const string BaseGameProblemTip =
        "Base game: the base game without mods has the same problem. No mod changes this line.";

    /// <summary>
    /// A problem that the base game without mods has too (the key rule, decision D512 of the console filter design):
    /// muted, with the lock glyph, and with the tooltip that tells why. It never marks a field, because no mod causes it.
    /// The look does not depend on the base-game toggle of the Console, because the Inspector shows one section on
    /// request.
    /// </summary>
    internal static void BaseGameProblemText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        ImGui.TextWrapped($"{EditorIcons.Lock} {text}");
        ImGui.PopStyleColor();
        ImGui.SetItemTooltip(BaseGameProblemTip);
    }

    /// <summary>A problem of the scene as a warning, without the section at the start of its message.</summary>
    internal static void ProblemText(Diagnostic problem) => ProblemText(problem, EditorTheme.Warning);

    /// <summary>A problem in a color, without the section at the start of its message.</summary>
    internal static void ProblemText(Diagnostic problem, Vector4 color)
    {
        var prefix = problem.Target == null ? null : $"{problem.Target.Id}: ";
        var message = prefix != null && problem.Message.StartsWith(prefix, StringComparison.Ordinal)
            ? problem.Message[prefix.Length..]
            : problem.Message;
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped($"{EditorIcons.ExclamationTriangle} {message}");
        ImGui.PopStyleColor();
    }

    /// <summary>A text field for the lines of a key: one line, or a field with more lines for a key that the engine
    /// reads more than one time.</summary>
    /// <param name="startLines">The number of lines of the key at the start of an open edit, or now.</param>
    private void TextField(FieldBinding field, int startLines, string hint)
    {
        var text = field.Text;
        var multiline = field.Key?.Multiline == true || startLines > 1;
        bool changed;
        ImGui.SetNextItemWidth(FillWidth);
        if (multiline)
        {
            var lines = Math.Clamp(field.Lines.Count + 1, 2, MultilineRows);
            var height = lines * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
            changed = ImGui.InputTextMultiline("##value", ref text, Capacity(text), new Vector2(FillWidth, height));
        }
        else
        {
            changed = ImGui.InputTextWithHint("##value", hint, ref text, Capacity(text));
        }

        Route(field, changed, _ => text);
        FieldError(field);
    }

    /// <summary>One field for each value of the line, each with its label in front of it. Each field edits one value
    /// of the line, and each field is its own field edit.</summary>
    /// <remarks>A key with a drag kind (see <see cref="FieldDrag.KindOf(KeySchema, string)"/>) also gets a label drag
    /// on each label.</remarks>
    private void ComponentField(FieldBinding field, IReadOnlyList<ComponentLabel> labels)
    {
        var kind = FieldDrag.KindOf(field.Key, field.Section.Id.Type);
        ComponentFields(FieldEdit.Values(field.Text), labels, (index, text, width) =>
        {
            var value = text;
            ImGui.SetNextItemWidth(width);
            var changed = ImGui.InputText("##value", ref value, Capacity(value));
            // The other values come from the line at the start of the edit.
            Route(field, changed, start => FieldEdit.WithValue(start, index, value));
        }, reserve, kind is { } drag ? (index, size) => BoundDragLabel(field, drag, labels, index, size) : null);
        FieldError(field);
    }

    private void BoolField(FieldBinding field, string unset)
    {
        // A line without a value is true, for example 'separable ='.
        var value = field.Lines is [{ Values: var values }]
            ? values.Count == 0 || values[0].TryToBoolean(out var truth) && truth
            : unset == "true";
        if (ImGui.Checkbox("##value", ref value))
        {
            window.Commands.Run(session => FieldCommands.SetLines(session, field.Section, field.Name,
                [value ? "true" : "false"]));
        }

        if (field.Lines.Count == 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Not set");
        }
    }

    /// <summary>
    /// A key that holds a text id, for example <c>ids_name</c>: the field shows the text and edits the text of the
    /// active mod. The edit runs when the field ends, because it reads the text of the mod when it is made (see
    /// <see cref="CommitGesture"/>).
    /// </summary>
    private void TextKeyField(FieldBinding field, StringKind kind, string? reason)
    {
        var (text, id) = ShownText(field, kind);
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var idWidth = id.Length == 0 ? 0 : ImGui.CalcTextSize(id).X + spacing;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X - idWidth - reserve);
        if (reason != null)
        {
            Field(text.Replace('\n', ' '), width, field.Lines.Count == 0 ? "Not set" : "No text for this id");
        }
        else
        {
            var edited = text;
            bool changed;
            if (kind == StringKind.Infocard)
            {
                var height = MultilineRows * ImGui.GetTextLineHeight() + 2 * ImGui.GetStyle().FramePadding.Y;
                changed = ImGui.InputTextMultiline("##value", ref edited, Capacity(edited), new Vector2(width, height));
            }
            else
            {
                ImGui.SetNextItemWidth(width);
                changed = ImGui.InputTextWithHint("##value", "Not set", ref edited, Capacity(edited));
            }

            CommitField(field.FieldKey, changed, text, edited, EditController.TextKeyCommit(window.Session!.Edits,
                field, make => window.Commands.Run(make),
                (title, message, choices) => window.Choice.Show(title, message, choices)));
            ImGui.SetItemTooltip("The text of the active mod. A new text gets a key in the strings.json of the mod.");
        }

        if (id.Length > 0)
        {
            ImGui.SameLine(0, spacing);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(id);
        }
    }

    /// <summary>The text that a text key shows, and its id or placeholder.</summary>
    private (string Text, string Id) ShownText(FieldBinding field, StringKind kind)
    {
        if (field.Lines is not [{ Values: [var value, ..] }, ..])
        {
            return ("", "");
        }

        var session = window.Session!;
        var id = FieldEdit.FormatValue(value);
        if (StringPlaceholder.TryParse(id, out var placeholder))
        {
            var text = session.Edits.Text(placeholder) ?? "";
            return (kind == StringKind.Infocard && text.Length > 0 ? ItemTexts.PlainText(text) : text, id);
        }

        return value.TryToInt32(out var number) && number > 0
            ? (kind == StringKind.Infocard ? session.InfoText(number) : session.Name(number), id)
            : ("", id);
    }

    /// <summary>
    /// Gives the events of the widget that drew last to the field edits, for a field whose edit runs when the field
    /// ends (see <see cref="CommitGesture"/>).
    /// </summary>
    /// <param name="start">The text of the field when it is not active.</param>
    /// <param name="text">The text of the widget in this frame.</param>
    /// <param name="commit">Runs the edit with the last text. Its second argument is false when the editor cannot ask
    /// a question. It returns why the text cannot be the value, or null.</param>
    public void CommitField(FieldKey key, bool changed, string start, string text, Func<string, bool, string?> commit) =>
        Route(key, changed, () => new CommitGesture(key, start, commit), _ => text);

    /// <summary>Gives the events of the widget that drew last to the field edits, for a field that changes the lines
    /// of a bound key (see <see cref="LineGesture"/>).</summary>
    /// <param name="text">Gives the text of the key after a change, from the text at the start of the edit.</param>
    private void Route(FieldBinding field, bool changed, Func<string, string> text) =>
        Route(field.FieldKey, changed, () => LineGesture.Open(window.Commands, field), text);

    private void Route(FieldKey key, bool changed, Func<FieldGesture> begin, Func<string, string> text)
    {
        var deactivated = ImGui.IsItemDeactivated();
        var events = new FieldEvents(ImGui.IsItemActivated(), ImGui.IsItemActive(), changed, deactivated,
            deactivated && ImGui.IsKeyPressed(ImGuiKey.Escape, false));
        RouteEvents(ImGui.GetItemID(), key, events, begin, text);
    }

    /// <summary>Gives the events of a widget to the field edits. A label drag and a color picker make their events
    /// themselves (see <see cref="LabelDrag"/> and <see cref="PopupSession"/>).</summary>
    /// <param name="id">The ImGui id of the widget.</param>
    private void RouteEvents(uint id, FieldKey key, FieldEvents events, Func<FieldGesture> begin,
        Func<string, string> text) =>
        fieldEdits.Route(id, key, events, begin, text, ImGui.GetFrameCount());

    /// <summary>The error of the open edit of a field, below the field.</summary>
    private void FieldError(FieldBinding field)
    {
        if (fieldEdits.ErrorOf(field.FieldKey) is { } error)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    /// <summary>
    /// Draws the marks of a bound row in the label cell, after the label: a later-mod warning and a lock. The blue bar
    /// at the left edge shows that the active mod sets the value.
    /// </summary>
    private bool RowMarks(FieldBinding field, string? reason, Vector2 labelMin)
    {
        if (field.Origin == ValueOrigin.ActiveMod)
        {
            ChangedBar(labelMin);
        }

        var hovered = false;
        if (field.LaterMods.Count > 0)
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextColored(EditorTheme.Warning, EditorIcons.ExclamationTriangle.ToString());
            hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        }

        if (reason != null)
        {
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            ImGui.TextDisabled(EditorIcons.Lock.ToString());
            hovered |= ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip);
        }

        return hovered;
    }

    /// <summary>The tooltip lines of a bound row: the layer that sets the value, the later mods and the lock.</summary>
    private static void AppendMarks(StringBuilder tooltip, FieldBinding field, string? reason)
    {
        tooltip.Append("\n\n").Append(field.OriginText);
        if (field.LaterMods.Count > 0)
        {
            tooltip.Append("\n").Append(EditorIcons.ExclamationTriangle).Append(' ')
                .Append(field.LaterMods.Count == 1 ? $"Mod '{field.LaterMods[0]}' loads later and changes it."
                    : $"Mods {string.Join(", ", field.LaterMods.Select(m => $"'{m}'"))} load later and change it.")
                .Append(" The game uses the value of the last one.");
        }

        if (reason != null)
        {
            tooltip.Append("\n").Append(EditorIcons.Lock).Append(' ').Append(reason);
        }
    }

    /// <summary>The menu of the label of a bound row: Revert and Copy INI line.</summary>
    private void RowMenu(FieldBinding field)
    {
        if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup("##row-menu");
        }

        if (!ImGui.BeginPopup("##row-menu"))
        {
            return;
        }

        EditorTheme.PushMenuColors();
        var canEdit = window.Session?.NoEditReason == null;
        if (Theme.IconMenuItem(EditorIcons.Undo, "Revert to the lower layer", field.CanRevert && canEdit))
        {
            window.Commands.Run(session => FieldCommands.RevertKey(session, field.Section, field.Name));
        }

        ImGui.SetItemTooltip("Puts back the value of the base data and the mods that load before the active mod.");
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy INI line", field.Lines.Count > 0))
        {
            ImGui.SetClipboardText(FieldEdit.IniText(field.Name, field.Lines));
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
    }

    /// <summary>The buffer size of an edit field: room for the text and for more text.</summary>
    private static uint Capacity(string text) => (uint)(Encoding.UTF8.GetByteCount(text) + 1024);

    /// <summary>One section of the edit view with the bindings of its keys, for one edit version.</summary>
    /// <param name="layout">The section that the page lays out its rows from (see <see cref="LayoutView"/>).</param>
    /// <param name="editing">The key of the open field edit of the section, or null. It stays bound when the edit
    /// removes its last line, so that its field stays until the edit ends.</param>
    internal sealed class BoundSection(EditSession session, SectionRef section, SectionView view, SectionView layout,
        string? editing)
    {
        private readonly Dictionary<string, FieldBinding?> fields = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The section that the page lays out its rows from.</summary>
        public SectionView Layout => layout;

        /// <summary>The binding of a key, or null when neither the schema nor the section has the key, and no field
        /// edit of the key is open.</summary>
        public FieldBinding? Field(string name)
        {
            if (!fields.TryGetValue(name, out var field))
            {
                field = session.Schema(section, name) != null || view.Lines(name).Count > 0 ||
                        name.Equals(editing, StringComparison.OrdinalIgnoreCase)
                    ? FieldBinding.Create(session, section, view, name)
                    : null;
                fields[name] = field;
            }

            return field;
        }
    }

    /// <summary>The widget of an editable bound row (see <see cref="WidgetOf"/>).</summary>
    internal enum BoundWidget
    {
        Text,
        Bool,
        Enum,
        Components,
        Color,

        /// <summary>The picker of a key with a data list.</summary>
        List,
    }

    /// <summary>Formats a number for the text of a row that is not set.</summary>
    private static string Unset(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
