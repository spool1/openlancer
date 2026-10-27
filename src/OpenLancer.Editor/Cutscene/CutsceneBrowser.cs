using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.ImUI;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Cutscene;

/// <summary>
/// The Cutscenes tab (section 7.2 of the M8 design): each cutscene script of the edit view, with its folder, its
/// duration, its entity and event counts, the INI lines that use it and its state for the active mod. The counts come
/// from a scan that reads each script once. The commands are New cutscene, Open, Revert, Copy path and Copy INI value.
/// A new cutscene shows at once, with its counts from W (section 5.4 of the M8b design). The group menu shows one
/// category or group of the cutscene list of the Cutscene toolbar (section 6 of the cutscene categories design).
/// </summary>
internal sealed class CutsceneBrowser(EditorWindow window, CutsceneWorkspace workspace)
{
    /// <summary>The id of the panel.</summary>
    public const string PanelId = "cutscenes";

    /// <summary>The note of a new cutscene about the retail engine (risk 7 of the M8b design).</summary>
    public const string OpenLancerOnly =
        "OpenLancer only until the M11 check that the retail engine reads text cutscenes.";

    /// <summary>The text for a cutscene that no INI line names. The table, the cutscene list of the Cutscene toolbar
    /// and the Inspector show it.</summary>
    public const string NoReferenceText = "No INI line of the game data names this cutscene.";

    private const string NewPopupId = "##new-cutscene";

    /// <summary>The default name of a new cutscene (decision D134).</summary>
    private const string DefaultName = "new_cutscene";

    /// <summary>The time that the scan takes in each frame.</summary>
    private static readonly TimeSpan ScanBudget = TimeSpan.FromMilliseconds(8);

    // Sizes in unscaled points.
    private const float IconWidth = 20;
    private const float GroupMenuWidth = 180;
    private const float GroupMenuMinWidth = 260;
    private const float DotRadius = 3;

    /// <summary>The number of entries that the open group menu shows before it scrolls.</summary>
    private const int GroupMenuRows = 24;

    private const string AllCutscenes = "All cutscenes";

    private const ImGuiTableFlags Flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable |
                                          ImGuiTableFlags.Hideable | ImGuiTableFlags.Sortable |
                                          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                                          ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY |
                                          ImGuiTableFlags.SizingFixedFit;

    /// <summary>The columns: the header, the start width in points, and true for a number.</summary>
    private static readonly (string Name, float Width, bool Numeric)[] Columns =
    [
        ("Name", 300, false), ("Folder", 80, false), ("Duration", 80, true), ("Entities", 70, true),
        ("Events", 70, true), ("Used by", 330, true), ("State", 160, false),
    ];

    private CutsceneCatalog? catalog;
    private GameDataManager? catalogData;
    private readonly Dictionary<string, ThnFileSource> sources = new(VfsPath.Comparer);
    private readonly List<CutsceneEntry> shown = [];
    private string filter = "";

    // The category or group of the group menu with its label, or null for all cutscenes.
    private (CutsceneChoice Choice, string Label)? choice;

    // The filter, the choice of the group menu, the scan count and the edit version of the shown rows. Another value
    // filters and sorts again.
    private (string Filter, CutsceneChoice? Choice, int Scanned, int Version)? shownView;
    private (int Column, bool Descending)[] sortKeys = [(0, false)];
    private string? selected;

    // The version of the session whose new cutscenes the catalog has.
    private int? unsavedVersion;

    // The name in the popup of New cutscene.
    private string newName = DefaultName;

    /// <summary>Forgets the list of the game data. Call it when the profile closes.</summary>
    public void Reset()
    {
        catalog = null;
        catalogData = null;
        sources.Clear();
        shown.Clear();
        shownView = null;
        unsavedVersion = null;
        selected = null;
        choice = null;
    }

    /// <summary>The filter, the group menu, the count and the commands of the selected row.</summary>
    public void Toolbar()
    {
        var scale = ImGuiHelper.Scale;
        if (ToolbarItems.Button("new", EditorIcons.Plus, "New cutscene: a new script in the active mod.",
                window.Session != null))
        {
            newName = FreeName();
            ImGui.OpenPopup(NewPopupId);
        }

        NewPopup();
        ImGui.SameLine();
        ToolbarItems.SearchField("filter", "Filter by name or path", ref filter, 220 * scale);
        ImGui.SameLine();
        GroupMenu(GroupMenuWidth * scale);
        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        var row = selected;
        if (ToolbarItems.Button("open", EditorIcons.Open, "Open the selected cutscene (double-click).", row != null))
        {
            workspace.OpenAndShow(row!);
        }

        ImGui.SameLine();
        var revert = row == null ? "Select a cutscene." : RevertProblem(row);
        if (ToolbarItems.Button("revert", EditorIcons.Undo, revert ?? RevertTip, row != null && revert == null))
        {
            Revert(row!);
        }

        ImGui.SameLine();
        if (ToolbarItems.Button("copy", EditorIcons.Copy, "Copy the install path of the selected cutscene.", row != null))
        {
            ImGui.SetClipboardText(row!);
        }

        ImGui.SameLine();
        var value = row == null ? null : ThnReferences.IniValueOf(row);
        if (ToolbarItems.Button("copy-ini", EditorIcons.Link, IniValueTip(value), value != null))
        {
            ImGui.SetClipboardText(value!);
        }

        ImGui.SameLine();
        ToolbarItems.Separator();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        if (catalog != null)
        {
            var total = catalog.Entries.Count + catalog.Unsaved.Count;
            var count = $"{shown.Count.ToString("N0", CultureInfo.InvariantCulture)} of " +
                        $"{total.ToString("N0", CultureInfo.InvariantCulture)} cutscenes";
            ImGui.TextDisabled(catalog.Done
                ? count
                : $"{count}, reading {catalog.Scanned.ToString("N0", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>Draws the table, and runs a part of the scan.</summary>
    public void Draw()
    {
        var data = window.Session?.GameData;
        if (data == null)
        {
            PanelStates.NoGameData(window.Session);
            return;
        }

        if (!ReferenceEquals(catalogData, data) || catalog == null)
        {
            catalog = new CutsceneCatalog(workspace.Scripts());
            catalogData = data;
            sources.Clear();
            shownView = null;
        }

        if (!catalog.Done)
        {
            catalog.Scan(data.Items.VFS.ReadAllBytes, ThornEvaluator.Instance, ScanBudget);
        }

        // The new cutscenes of W follow each edit, undo and redo.
        var version = window.Session!.Version;
        if (unsavedVersion != version)
        {
            unsavedVersion = version;
            var edits = window.Session.Edits;
            catalog.SetUnsaved(edits.NewCutscenes.Select(p => (p, edits.Cutscene(p)!)), data.Items.VFS.FileExists);
            sources.Clear();
        }

        // A reload, or the undo of the last new cutscene of a mod, can remove the chosen category or group. Then the
        // menu goes back to all cutscenes.
        if (choice is { } chosen)
        {
            choice = workspace.Categories().LabelOf(chosen.Choice) is { } label ? (chosen.Choice, label) : null;
        }

        var kept = choice?.Choice;
        var view = (filter, kept, catalog.Scanned, version);
        if (shownView != view)
        {
            shownView = view;
            shown.Clear();
            // A new unsaved cutscene has a place too (decision D615).
            var categories = kept != null ? workspace.Categories() : null;
            shown.AddRange(catalog.All.Where(e => (kept is not { } c || c.Keeps(categories!.PlaceOf(e.Path))) &&
                (filter.Length == 0 || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))));
            Sort();
        }

        Table();
    }

    /// <summary>The popup of New cutscene: the name, the install path, the note about the retail engine, and Create
    /// and Cancel. Create is disabled with the refusal as its tooltip.</summary>
    private void NewPopup()
    {
        if (!ImGui.BeginPopup(NewPopupId))
        {
            return;
        }

        var session = window.Session;
        var scale = ImGuiHelper.Scale;
        ImGui.Text("New cutscene");
        ImGui.Separator();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(260 * scale);
        var enter = ImGui.InputText("Name", ref newName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        var path = session?.Edits.NewCutscenePath(newName);
        ImGui.TextDisabled(path ?? "No active mod");
        ImGui.SetItemTooltip("The install path of the new cutscene. A save writes it as a whole Lua text file into the " +
                             "files folder of the active mod.");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360 * scale);
        ImGui.TextDisabled(OpenLancerOnly);
        ImGui.PopTextWrapPos();
        var refusal = session == null ? "No profile is open."
            : path == null ? ThnEdits.NoActiveMod
            : session.NoEditReason ?? ThnEdits.NewCutsceneRefusal(session.Edits, path);
        ImGui.BeginDisabled(refusal != null);
        var create = ImGui.Button("Create") || enter && refusal == null;
        ImGui.EndDisabled();
        ImGui.SetItemTooltip(refusal ?? "Makes the cutscene with a scene, a monitor and a camera, and opens it.");
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            ImGui.CloseCurrentPopup();
        }
        else if (create && refusal == null && window.Commands.Execute(ThnEdits.NewCutscene(path!, ThnTemplates.NewScript())))
        {
            ImGui.CloseCurrentPopup();
            selected = path;
            window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource,
                $"New cutscene {path}. {OpenLancerOnly} The next save writes it.");
            workspace.OpenAndShow(path!);
        }

        ImGui.EndPopup();
    }

    /// <summary>The default name of a new cutscene: <c>new_cutscene</c>, or <c>new_cutscene_2</c> and up when the name
    /// is in use.</summary>
    private string FreeName()
    {
        if (window.Session?.Edits is not { ActiveMod: not null } edits)
        {
            return DefaultName;
        }

        for (var i = 1; ; i++)
        {
            var name = i == 1 ? DefaultName : $"{DefaultName}_{i}";
            if (ThnEdits.NewCutsceneRefusal(edits, edits.NewCutscenePath(name)!) != ThnEdits.PathInUse)
            {
                return name;
            }
        }
    }

    /// <summary>The tooltip of Copy INI value.</summary>
    private static string IniValueTip(string? value) => value == null
        ? "Copy INI value: select a cutscene below the DATA folder."
        : $"Copy the INI value of the selected cutscene: {value}. An INI line, for example start_script, names a " +
          "cutscene with its path below the DATA folder.";

    private const string RevertTip =
        "Revert: put back the cutscene as the lower mods leave it. The next save deletes the patch of the active mod.";

    /// <summary>Why Revert cannot run for a cutscene, or null.</summary>
    private string? RevertProblem(string path)
    {
        if (window.Session is not { } session)
        {
            return "No profile is open.";
        }

        if (session.NoEditReason is { } reason)
        {
            return reason;
        }

        return Source(path).Kind switch
        {
            ThnFileKind.WholeFileHere or ThnFileKind.NewHere =>
                "The active mod has a whole file for this cutscene, so there are no lower values to put back.",
            ThnFileKind.Lower when !session.Edits.HasUnsavedCutsceneEdits(path) =>
                "The active mod does not change this cutscene.",
            _ => null,
        };
    }

    private void Revert(string path)
    {
        if (window.Commands.Execute(ThnEdits.Revert(path)))
        {
            window.Log.Add(ConsoleSeverity.Info, EditorWindow.EditorSource,
                $"Reverted cutscene {path}. The next save deletes the patch of the active mod.");
        }
    }

    /// <summary>The source of a cutscene for the active mod. It changes only with a save, which loads the data
    /// again.</summary>
    private ThnFileSource Source(string path)
    {
        if (!sources.TryGetValue(path, out var source))
        {
            source = window.Session!.Edits.CutsceneSource(path);
            sources[path] = source;
        }

        return source;
    }

    /// <summary>
    /// The group menu: "All cutscenes", then each category with its groups indented below it, each with its count. The
    /// categories are the categories of the cutscene list of the Cutscene toolbar (decision D614).
    /// </summary>
    private void GroupMenu(float width)
    {
        var scale = ImGuiHelper.Scale;
        var style = ImGui.GetStyle();
        ImGui.SetNextItemWidth(width);
        EditorTheme.PushMenuColors();
        // The menu is wide enough for a label and its count, and it shows up to 24 rows before it scrolls.
        ImGui.SetNextWindowSizeConstraints(new Vector2(MathF.Round(GroupMenuMinWidth * scale), 0),
            new Vector2(float.MaxValue, GroupMenuRows * ImGui.GetTextLineHeightWithSpacing() + 2 * style.WindowPadding.Y));
        if (ImGui.BeginCombo("##group", choice?.Label ?? AllCutscenes))
        {
            var categories = window.Session?.GameData != null ? workspace.Categories() : null;
            if (GroupEntry("all", AllCutscenes, categories?.Count, choice == null))
            {
                choice = null;
            }

            foreach (var category in categories?.Categories ?? [])
            {
                var label = CutsceneCategories.Label(category.Category);
                var entry = new CutsceneChoice(category.Category, null);
                if (GroupEntry(CutscenePicker.CategoryId(category.Category),
                        $"{CutscenePicker.KindOf(category.Category).Icon}  {label}", category.Count,
                        choice?.Choice == entry))
                {
                    choice = (entry, label);
                }

                if (category.Groups is [{ Label.Length: 0 }])
                {
                    continue;
                }

                ImGui.Indent();
                foreach (var group in category.Groups)
                {
                    var groupEntry = new CutsceneChoice(category.Category, group.Id);
                    if (GroupEntry(CutscenePicker.GroupId(category.Category, group.Id), group.Label, group.Rows.Count,
                            choice?.Choice == groupEntry))
                    {
                        choice = (groupEntry, group.Label);
                    }
                }

                ImGui.Unindent();
            }

            ImGui.EndCombo();
        }
        else
        {
            ImGui.SetItemTooltip("Show one category or group. The categories are the same as in the cutscene list of " +
                                 "the Cutscene toolbar.");
        }

        EditorTheme.PopMenuColors();
    }

    /// <summary>One entry of the group menu: the label, cut with "..." when it is too long, and the dim count at the
    /// right edge. Returns true when the user chose it.</summary>
    /// <param name="id">The ImGui id of the entry, unique in the menu. Two groups can have the same label.</param>
    /// <param name="count">The number of scripts, or null when the game data is not loaded.</param>
    private static bool GroupEntry(string id, string label, int? count, bool isSelected)
    {
        // The text goes where ImGui puts the label of a selectable: from the cursor to the right edge of the content.
        // The rectangle of the item is larger by half the item spacing on each side.
        var start = ImGui.GetCursorScreenPos();
        var textEnd = start.X + ImGui.GetContentRegionAvail().X;
        ImGui.PushID(id);
        var chosen = ImGui.Selectable("##entry", isSelected);
        ImGui.PopID();
        var drawList = ImGui.GetWindowDrawList();
        if (count is { } value)
        {
            var text = value.ToString("N0", CultureInfo.InvariantCulture);
            textEnd -= ImGui.CalcTextSize(text).X;
            drawList.AddText(new Vector2(textEnd, start.Y), ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
            textEnd -= ImGui.GetStyle().ItemSpacing.X;
        }

        TextFit.AddText(drawList, ImGui.GetFont(), ImGui.GetFontSize(), start, ImGui.GetColorU32(ImGuiCol.Text), label,
            textEnd - start.X);
        return chosen;
    }

    private unsafe void Table()
    {
        var scale = ImGuiHelper.Scale;
        if (!ImGui.BeginTable("##cutscenes", Columns.Length + 1, Flags, ImGui.GetContentRegionAvail()))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize |
                                         ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.NoSort |
                                         ImGuiTableColumnFlags.NoReorder | ImGuiTableColumnFlags.NoHeaderLabel,
            MathF.Truncate(IconWidth * scale));
        for (var c = 0; c < Columns.Length; c++)
        {
            var (name, width, numeric) = Columns[c];
            var flags = ImGuiTableColumnFlags.WidthFixed;
            if (c == 0)
            {
                flags |= ImGuiTableColumnFlags.NoHide | ImGuiTableColumnFlags.DefaultSort;
            }

            if (numeric)
            {
                flags |= ImGuiTableColumnFlags.PreferSortDescending;
            }

            ImGui.TableSetupColumn(name, flags, MathF.Truncate(width * scale), (uint)(c + 1));
        }

        EditorTheme.PushMenuColors();
        var specs = ImGui.TableGetSortSpecs();
        EditorTheme.PopMenuColors();
        ImGui.TableHeadersRow();
        if (specs != null && specs.SpecsDirty)
        {
            var spec = (ImGuiTableColumnSortSpecs*)specs.Specs.Handle;
            sortKeys = Enumerable.Range(0, specs.SpecsCount)
                .Select(i => ((int)spec[i].ColumnUserID - 1, spec[i].SortDirection == ImGuiSortDirection.Descending))
                .ToArray();
            specs.SpecsDirty = false;
            Sort();
        }

        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        var clipper = new ImGuiListClipper();
        clipper.Begin(shown.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                Row(shown[i], i, focused);
            }
        }

        clipper.End();
        ImGui.EndTable();
    }

    private void Row(CutsceneEntry entry, int index, bool focused)
    {
        var scale = ImGuiHelper.Scale;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(index);
        var isSelected = VfsPath.Comparer.Equals(selected, entry.Path);
        var isOpen = VfsPath.Comparer.Equals(workspace.Path, entry.Path);
        var cell = ImGui.GetCursorScreenPos();
        var cellWidth = ImGui.GetContentRegionAvail().X;
        var selection = ListRow.SelectionColor(focused);
        ImGui.PushStyleColor(ImGuiCol.Header, selection);
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, selection);
        }

        if (ImGui.Selectable("##row", isSelected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            selected = entry.Path;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                workspace.OpenAndShow(entry.Path);
            }
        }

        ImGui.PopStyleColor(isSelected ? 2 : 1);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            selected = entry.Path;
        }

        var uses = workspace.References().UsedBy(entry.Path);
        if (ImGui.BeginPopupContextItem("##row-menu"))
        {
            EditorTheme.PushMenuColors();
            RowMenu(entry, uses);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // The glyph of the row. The open cutscene has the accent color.
        var icon = EditorIcons.Film.ToString();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(new Vector2(cell.X + MathF.Truncate((cellWidth - ImGui.CalcTextSize(icon).X) / 2), cell.Y),
            ImGui.GetColorU32(isOpen ? EditorTheme.AccentText : EditorTheme.TextDim), icon);

        var counts = entry.Counts;
        Cell(0, entry.Name, false, entry.Path);
        Cell(1, entry.Folder.ToString(), false, null);
        Cell(2, counts == null ? "..." : counts.Problem != null ? "" : Number(counts.Duration, "0.###"), true,
            counts?.Problem);
        Cell(3, counts is { Problem: null } ? Number(counts.Entities, "N0") : "", true, null);
        Cell(4, counts is { Problem: null } ? Number(counts.Events, "N0") : "", true, null);
        if (ImGui.TableSetColumnIndex(6))
        {
            UsedBy(uses);
        }

        if (ImGui.TableSetColumnIndex(7))
        {
            var state = CutsceneCatalog.StateText(Source(entry.Path));
            ImGui.Text(state);
            if (window.Session?.Edits.HasUnsavedCutsceneEdits(entry.Path) == true)
            {
                // A dot after the state marks unsaved edits.
                var radius = DotRadius * scale;
                var max = ImGui.GetItemRectMax();
                drawList.AddCircleFilled(new Vector2(max.X + 4 * scale + radius, (ImGui.GetItemRectMin().Y + max.Y) / 2),
                    radius, ImGui.GetColorU32(EditorTheme.AccentText));
                ImGui.SetItemTooltip("The cutscene has unsaved edits.");
            }
        }

        ImGui.PopID();
    }

    /// <summary>The text of a column, right-aligned for a number, with a tooltip or null.</summary>
    private static void Cell(int column, string text, bool numeric, string? tooltip)
    {
        if (!ImGui.TableSetColumnIndex(column + 1))
        {
            return;
        }

        if (numeric)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() +
                                Math.Max(0, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X));
        }

        ImGui.Text(text);
        if (tooltip != null)
        {
            ImGui.SetItemTooltip(tooltip);
        }
    }

    /// <summary>
    /// The INI lines that use a cutscene: the count and the first line. A click on the line opens its section in the
    /// Inspector. The tooltip lists all lines.
    /// </summary>
    private void UsedBy(IReadOnlyList<ThnReference> uses)
    {
        if (uses.Count == 0)
        {
            ImGui.TextDisabled("none");
            ImGui.SetItemTooltip(NoReferenceText);
            return;
        }

        var first = Describe(uses[0]);
        if (ImGui.TextLink(uses.Count == 1 ? first : $"{first} and {uses.Count - 1} more"))
        {
            ShowSection(uses[0]);
        }

        ImGui.SetItemTooltip(string.Join("\n", uses.Select(Describe)) + "\n\nClick to show the first line in the Inspector. " +
                             "The menu of the row shows each line.");
    }

    private void RowMenu(CutsceneEntry entry, IReadOnlyList<ThnReference> uses)
    {
        if (Theme.IconMenuItem(EditorIcons.Open, "Open", true))
        {
            workspace.OpenAndShow(entry.Path);
        }

        var revert = RevertProblem(entry.Path);
        if (Theme.IconMenuItem(EditorIcons.Undo, "Revert", revert == null))
        {
            Revert(entry.Path);
        }

        ImGui.SetItemTooltip(revert ?? RevertTip);
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy path", true))
        {
            ImGui.SetClipboardText(entry.Path);
        }

        var value = ThnReferences.IniValueOf(entry.Path);
        if (Theme.IconMenuItem(EditorIcons.Link, "Copy INI value", value != null))
        {
            ImGui.SetClipboardText(value!);
        }

        ImGui.SetItemTooltip(IniValueTip(value));

        if (uses.Count == 0)
        {
            return;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Used by");
        foreach (var use in uses)
        {
            if (ImGui.MenuItem(Describe(use)))
            {
                ShowSection(use);
            }
        }
    }

    /// <summary>Selects the section of an INI line, so that the Inspector shows it.</summary>
    private void ShowSection(ThnReference use)
    {
        if (workspace.SectionOf(use) is { } section)
        {
            window.Selection = section;
        }
        else
        {
            window.Log.Add(ConsoleSeverity.Warning, EditorWindow.EditorSource,
                $"The edit view has no single section [{use.Section}] in {use.File} with {use.Key} = {use.Value}.");
        }
    }

    /// <summary>A line that names a cutscene, for example "M000_s002xe_Li01_01_nrml.ini [CharacterEncounter] action".</summary>
    internal static string Describe(ThnReference use) =>
        $"{System.IO.Path.GetFileName(use.File)} [{use.Section}] {use.Key}";

    /// <summary>Sorts the shown rows by the sort keys of the table. Rows with the same keys keep the order of the
    /// paths. A row that the scan did not read yet sorts after the others.</summary>
    private void Sort()
    {
        var order = catalog!.All.Select((e, i) => (e, i)).ToDictionary(x => x.e, x => x.i);
        var references = workspace.References();
        shown.Sort((a, b) =>
        {
            foreach (var (column, descending) in sortKeys)
            {
                var result = Compare(a, b, column, references);
                if (result != 0)
                {
                    return descending ? -result : result;
                }
            }

            return order[a].CompareTo(order[b]);
        });
    }

    private int Compare(CutsceneEntry a, CutsceneEntry b, int column, ThnReferences references) => column switch
    {
        0 => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        1 => a.Folder.CompareTo(b.Folder),
        2 => Value(a, c => c.Duration).CompareTo(Value(b, c => c.Duration)),
        3 => Value(a, c => c.Entities).CompareTo(Value(b, c => c.Entities)),
        4 => Value(a, c => c.Events).CompareTo(Value(b, c => c.Events)),
        5 => references.UsedBy(a.Path).Count.CompareTo(references.UsedBy(b.Path).Count),
        _ => string.Compare(CutsceneCatalog.StateText(Source(a.Path)), CutsceneCatalog.StateText(Source(b.Path)),
            StringComparison.Ordinal),
    };

    /// <summary>A count of a row, or -1 when the scan did not read the row or could not read the script.</summary>
    private static double Value(CutsceneEntry entry, Func<CutsceneCounts, double> value) =>
        entry.Counts is { Problem: null } counts ? value(counts) : -1;

    private static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
