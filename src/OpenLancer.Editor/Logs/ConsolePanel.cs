using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Logs;

/// <summary>
/// The Console: the editor messages, the mod diagnostics and the engine warnings and errors in a list. The toolbar
/// strip filters the list by origin, by severity and by text. The selected message shows in full in a pane below the
/// list, with its origin and its kind.
/// </summary>
/// <remarks>The base-game toggle shows or hides the messages that the base game without mods gives too (decision D519 of
/// the console filter design). It is off by default. While it hides messages, a banner at the top of the list counts
/// them. While engine lines wait for the base run, a row at the top counts them.</remarks>
internal sealed class ConsolePanel(EditorWindow window)
{
    /// <summary>The id of the panel. The layout reports its focus by this id.</summary>
    public const string PanelId = "console";

    // The empty states of the Console (#56).
    internal static readonly EmptyState NoMessages =
        new("No messages.", "Open a profile, save or play. The warnings and errors then show here.");
    internal static readonly EmptyState NoMessageMatch =
        new("No message matches the filters.", "Turn on the severity toggles or clear the search.");

    // Sizes in unscaled points.
    private const float IconWidth = 20;
    private const float TimeWidth = 64;
    private const float SourceWidth = 120;
    private const float SearchMinWidth = 120;
    private const float DetailMin = 40;
    private const float DetailMaxFraction = 0.7f;
    private const float SplitterGap = 4;
    private static readonly Vector2 DetailPadding = new(8, 6);

    // A count above this number shows as "999+".
    private const int MaxShownCount = 999;

    private static readonly Severity[] Severities =
    [
        new(ConsoleSeverity.Info, EditorIcons.InfoCircle, EditorTheme.Label, "info messages"),
        new(ConsoleSeverity.Warning, EditorIcons.ExclamationTriangle, EditorTheme.Warning, "warnings"),
        new(ConsoleSeverity.Error, EditorIcons.TimesCircle, EditorTheme.Error, "errors"),
    ];

    private const string BaseGameToggleId = "show-base-game";

    private readonly bool[] shown = [true, true, true];
    private readonly List<ConsoleEntry> visible = [];
    private readonly List<PanelBadge> badges = [];
    private string search = "";

    // The texts of the severity toggles and of the base-game toggle. They are made again only when the log or the
    // toggles change.
    private readonly string[] countTexts = ["", "", ""];
    private readonly string[] toggleTips = ["", "", ""];
    private string baseGameCountText = "";
    private string baseGameTip = "";
    private int toggleTextsVersion = -1;
    private int toggleTextsShown = -1;

    // The unseen counts of the badges in the list.
    private (int Warnings, int Errors) badgeCounts = (-1, -1);

    // The filter of the visible list. The list is made again when the log or the filter changes.
    private int visibleVersion = -1;
    private string visibleSearch = "";
    private int visibleShown = -1;

    private ConsoleEntry? selected;
    private bool scrollToSelected;

    /// <summary>The look of one severity: its glyph, its color and its name in the tooltips.</summary>
    private sealed record Severity(ConsoleSeverity Value, char Icon, Vector4 Color, string Name)
    {
        /// <summary>The glyph as a string, for the draw calls.</summary>
        public string Glyph { get; } = Icon.ToString();

        /// <summary>The glyph and the severity, for the details pane.</summary>
        public string Title { get; } = $"{Icon}  {Value}";

        public string ToggleId { get; } = $"show-{Value}";
    }

    private ConsoleLog Log => window.Log;

    /// <summary>True while the list shows the base-game messages. The setting keeps it between runs.</summary>
    private bool ShowBaseGame => window.Settings.ShowBaseGameMessages;

    /// <summary>The glyph and the color of a severity.</summary>
    public static (string Glyph, Vector4 Color) Look(ConsoleSeverity severity) =>
        (Severities[(int)severity].Glyph, Severities[(int)severity].Color);

    /// <summary>
    /// The badges of the tab: the warnings and errors that came after the user last looked at the Console.
    /// </summary>
    public IReadOnlyList<PanelBadge> Badges()
    {
        var counts = (Log.UnseenWarnings, Log.UnseenErrors);
        if (counts == badgeCounts)
        {
            return badges;
        }

        badgeCounts = counts;
        badges.Clear();
        if (counts.UnseenWarnings > 0)
        {
            var warning = Severities[(int)ConsoleSeverity.Warning];
            badges.Add(new PanelBadge(warning.Glyph, CountText(counts.UnseenWarnings), warning.Color));
        }

        if (counts.UnseenErrors > 0)
        {
            var error = Severities[(int)ConsoleSeverity.Error];
            badges.Add(new PanelBadge(error.Glyph, CountText(counts.UnseenErrors), error.Color));
        }

        return badges;
    }

    /// <summary>Sets the search of the list, for example "Agent" to show the messages of the agent server.</summary>
    public void Search(string text) => search = text;

    /// <summary>Selects an entry and scrolls to it. The filters change when they hide it.</summary>
    public void Select(ConsoleEntry entry)
    {
        shown[(int)entry.Severity] = true;
        if (entry.Origin.IsBaseGame && !ShowBaseGame)
        {
            SetShowBaseGame(true);
        }

        if (!Matches(entry, search))
        {
            search = "";
        }

        selected = entry;
        scrollToSelected = true;
    }

    /// <summary>Draws the items of the toolbar strip: Clear, the search field, the base-game toggle and the severity
    /// toggles.</summary>
    public void Toolbar()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        if (ToolbarItems.Button("clear", EditorIcons.TrashAlt, "Clear the Console", Log.Entries.Count > 0))
        {
            Clear();
        }

        ImGui.SameLine();
        UpdateToggleTexts();
        var togglesWidth = Severities.Length * spacing + ToolbarItems.CountToggleWidth(EditorIcons.Lock, baseGameCountText);
        for (var i = 0; i < Severities.Length; i++)
        {
            togglesWidth += ToolbarItems.CountToggleWidth(Severities[i].Icon, countTexts[i]);
        }

        var searchWidth = Math.Max(SearchMinWidth * ImGuiHelper.Scale,
            ImGui.GetContentRegionAvail().X - togglesWidth - spacing);
        ToolbarItems.SearchField("console-search", "Search messages", ref search, searchWidth);

        // The toggles are at the right end of the strip.
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - togglesWidth));
        var showBaseGame = ToolbarItems.CountToggle(BaseGameToggleId, EditorIcons.Lock, EditorTheme.TextDim,
            baseGameCountText, baseGameTip, ShowBaseGame);
        if (showBaseGame != ShowBaseGame)
        {
            SetShowBaseGame(showBaseGame);
        }

        for (var i = 0; i < Severities.Length; i++)
        {
            ImGui.SameLine();
            var severity = Severities[i];
            shown[i] = ToolbarItems.CountToggle(severity.ToggleId, severity.Icon, severity.Color, countTexts[i],
                toggleTips[i], shown[i]);
        }
    }

    /// <summary>
    /// Makes the count texts and the tooltips of the severity toggles again when the log or a toggle changed.
    /// </summary>
    private void UpdateToggleTexts()
    {
        var shownBits = ShownBits();
        if (Log.Version == toggleTextsVersion && shownBits == toggleTextsShown)
        {
            return;
        }

        (toggleTextsVersion, toggleTextsShown) = (Log.Version, shownBits);
        for (var i = 0; i < Severities.Length; i++)
        {
            var severity = Severities[i];
            var count = Log.Count(severity.Value, ShowBaseGame);
            countTexts[i] = CountText(count);
            toggleTips[i] = string.Create(CultureInfo.InvariantCulture,
                $"{(shown[i] ? "Hide" : "Show")} the {severity.Name} ({count:N0})");
        }

        var baseGame = Log.BaseGameCount;
        baseGameCountText = CountText(baseGame);
        baseGameTip = BaseGameToggleTip(baseGame, ShowBaseGame);
    }

    /// <summary>The tooltip of the base-game toggle (section 4.4 of the console filter design).</summary>
    internal static string BaseGameToggleTip(int count, bool showing) => showing
        ? string.Create(CultureInfo.InvariantCulture, $"Hide the base-game messages ({count:N0}).")
        : string.Create(CultureInfo.InvariantCulture, $"Show the base-game messages ({count:N0}). {BaseGameReason}");

    private const string BaseGameReason =
        "The base game without mods gives these messages too. The mods do not cause them.";

    /// <summary>The banner text while the list hides base-game messages, and the text of its link.</summary>
    internal static (string Text, string Link) BaseGameBanner(int count) => count == 1
        ? ("The Console hides 1 base-game message.", "Show it")
        : (string.Create(CultureInfo.InvariantCulture, $"The Console hides {count:N0} base-game messages."), "Show them");

    /// <summary>The text of the row while engine lines wait for the base run.</summary>
    internal static string PendingText(int count) => count == 1
        ? "The Console compares 1 engine message with the base game. Then it shows the message if a mod causes it."
        : string.Create(CultureInfo.InvariantCulture,
            $"The Console compares {count:N0} engine messages with the base game. {PendingThen}");

    private const string PendingThen = "Then it shows the messages that the mods cause.";

    /// <summary>The toggles as bits: 1 for info, 2 for warnings, 4 for errors and 8 for the base-game messages.</summary>
    private int ShownBits() => (shown[0] ? 1 : 0) | (shown[1] ? 2 : 0) | (shown[2] ? 4 : 0) | (ShowBaseGame ? 8 : 0);

    /// <summary>Shows or hides the base-game messages, and keeps the choice in the settings.</summary>
    private void SetShowBaseGame(bool value)
    {
        window.Settings.ShowBaseGameMessages = value;
        window.Settings.Save();
    }

    public void Draw()
    {
        // The Console shows, so the user sees the new warnings and errors.
        Log.MarkSeen();
        UpdateVisible();
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        if (focused && !ImGui.GetIO().WantTextInput && selected != null &&
            ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.C)))
        {
            ImGui.SetClipboardText(selected.ToString());
        }

        var scale = ImGuiHelper.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var gap = MathF.Truncate(SplitterGap * scale);
        var settings = window.Settings;
        var detailMax = Math.Max(DetailMin, DetailMaxFraction * avail.Y / scale);
        var detailHeight = selected == null
            ? 0
            : MathF.Truncate(Math.Clamp(settings.ConsoleDetailHeight, DetailMin, detailMax) * scale);
        var listSize = new Vector2(avail.X, Math.Max(1, avail.Y - (selected == null ? 0 : detailHeight + gap)));

        // The rows of the pending lines and of the hidden base-game messages come first. The list takes the rest.
        var notes = Notes();
        if (notes > 0)
        {
            NoteRows();
        }

        var listTop = ImGui.GetCursorScreenPos().Y;
        listSize.Y = Math.Max(1, listSize.Y - (listTop - origin.Y));

        if (visible.Count == 0)
        {
            ImGui.BeginChild("##empty", listSize);
            if (notes == 0)
            {
                PanelStates.Message(Log.Entries.Count == 0 ? NoMessages : NoMessageMatch);
            }

            ImGui.EndChild();
        }
        else
        {
            List(listSize, focused);
        }

        if (selected == null)
        {
            return;
        }

        // A line in the middle of the gap separates the list from the details. A drag on the gap moves it.
        var gapTop = listTop + listSize.Y;
        PixelLine.Horizontal(ImGui.GetWindowDrawList(), gapTop + gap / 2, origin.X, origin.X + avail.X,
            ImGui.GetColorU32(EditorTheme.Border));
        var shownHeight = detailHeight / scale;
        var value = shownHeight;
        if (Splitter.Draw("##split-details", new Vector2(origin.X, gapTop), new Vector2(avail.X, gap), false,
                ref value, -1, DetailMin, detailMax, EditorSettings.DefaultConsoleDetailHeight))
        {
            settings.Save();
        }

        if (value != shownHeight)
        {
            settings.ConsoleDetailHeight = value;
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, gapTop + gap));
        Details(selected, new Vector2(avail.X, detailHeight));
    }

    /// <summary>The number of note rows above the list: one while engine lines wait for the base run, and one while the
    /// list hides base-game messages.</summary>
    private int Notes() => (Log.PendingCount > 0 ? 1 : 0) + (!ShowBaseGame && Log.BaseGameCount > 0 ? 1 : 0);

    /// <summary>Draws the muted note rows above the list: the pending row, and the banner of the hidden base-game
    /// messages with its link that shows them.</summary>
    private void NoteRows()
    {
        if (Log.PendingCount is > 0 and var pending)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"{EditorIcons.InfoCircle}  {PendingText(pending)}");
        }

        if (!ShowBaseGame && Log.BaseGameCount is > 0 and var hidden)
        {
            var (text, link) = BaseGameBanner(hidden);
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled($"{EditorIcons.Lock}  {text}");
            ImGui.SameLine();
            if (ImGui.TextLink(link))
            {
                SetShowBaseGame(true);
            }
        }
    }

    /// <summary>Makes the visible list again when the log, the search or the severity toggles changed.</summary>
    private void UpdateVisible()
    {
        var shownBits = ShownBits();
        if (Log.Version == visibleVersion && search == visibleSearch && shownBits == visibleShown)
        {
            return;
        }

        (visibleVersion, visibleSearch, visibleShown) = (Log.Version, search, shownBits);
        visible.Clear();
        var baseGame = ShowBaseGame;
        visible.AddRange(Log.Entries.Where(e => IsShown(e.Origin, baseGame) && shown[(int)e.Severity] &&
                                                Matches(e, search)));
        if (selected != null && !visible.Contains(selected))
        {
            selected = null;
        }
    }

    /// <summary>True when an entry of the origin shows: a pending entry never shows, and a base-game entry shows only
    /// while the base-game toggle is on.</summary>
    internal static bool IsShown(MessageOrigin origin, bool baseGame) =>
        origin.Kind != OriginKind.Pending && (baseGame || !origin.IsBaseGame);

    /// <summary>True when the message or the source contains the text. Case does not matter.</summary>
    private static bool Matches(ConsoleEntry entry, string text) =>
        text.Length == 0 || entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        entry.Source.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Draws the list: one row for each visible entry with its glyph, time, source and the first line of its message.
    /// It follows new entries only while it is scrolled to the bottom.
    /// </summary>
    private void List(Vector2 size, bool focused)
    {
        var scale = ImGuiHelper.Scale;
        if (!ImGui.BeginTable("##console", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                                              ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit, size))
        {
            return;
        }

        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize,
            MathF.Truncate(IconWidth * scale));
        ImGui.TableSetupColumn("##time", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize,
            MathF.Truncate(TimeWidth * scale));
        ImGui.TableSetupColumn("##source", ImGuiTableColumnFlags.WidthFixed, MathF.Truncate(SourceWidth * scale));
        ImGui.TableSetupColumn("##message", ImGuiTableColumnFlags.WidthStretch);

        var scrollIndex = scrollToSelected && selected != null ? visible.IndexOf(selected) : -1;
        scrollToSelected = false;
        var clipper = new ImGuiListClipper();
        clipper.Begin(visible.Count);
        if (scrollIndex >= 0)
        {
            clipper.IncludeItemByIndex(scrollIndex);
        }

        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                Row(i, visible[i], focused);
                if (i == scrollIndex)
                {
                    ImGui.SetScrollHereY(0.5f);
                }
            }
        }

        clipper.End();

        // New entries move the list only when it is at the bottom, so the user can read older entries.
        if (scrollIndex < 0 && ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
        {
            ImGui.SetScrollHereY(1);
        }

        ImGui.EndTable();
    }

    private void Row(int index, ConsoleEntry entry, bool focused)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(index);
        var isSelected = ReferenceEquals(entry, selected);
        var cell = ImGui.GetCursorScreenPos();
        var cellWidth = ImGui.GetContentRegionAvail().X;
        var lineHeight = ImGui.GetTextLineHeight();

        // The selection keeps its color under the mouse, so the hover does not hide it.
        var selection = ListRow.SelectionColor(focused);
        ImGui.PushStyleColor(ImGuiCol.Header, selection);
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, selection);
        }

        if (ImGui.Selectable("##row", isSelected, ImGuiSelectableFlags.SpanAllColumns))
        {
            selected = entry;
            // An entry about a mission script opens the mission in the Mission tab at the line (section 12.4 of the
            // M6a design). An entry about another section selects the section, so the Inspector shows it. An entry about
            // a cutscene opens the cutscene and selects the element.
            if (entry.Target is { } target)
            {
                if (!window.Missions.GoTo(target, entry.EntryIndex))
                {
                    window.Selection = target;
                }
            }
            else if (entry.Cutscene != null)
            {
                window.Cutscenes.Show(entry.Cutscene);
            }
        }
        else if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            selected = entry;
        }

        ImGui.PopStyleColor(isSelected ? 2 : 1);
        if (ImGui.BeginPopupContextItem("##row-menu"))
        {
            EditorTheme.PushMenuColors();
            ContextMenu(entry);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // Only the glyph has the severity color. A base-game row is muted.
        var muted = entry.Origin.IsBaseGame;
        var (glyph, color) = Look(entry.Severity);
        ImGui.GetWindowDrawList().AddText(
            new Vector2(cell.X + MathF.Truncate((cellWidth - ImGui.CalcTextSize(glyph).X) / 2), cell.Y),
            ImGui.GetColorU32(muted ? EditorTheme.TextDim : color), glyph);

        if (ImGui.TableSetColumnIndex(1))
        {
            // The time is smaller than the other text, so it moves down to the same center line.
            ImGui.PushFont(null, EditorTheme.CaptionSize);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Truncate((lineHeight - ImGui.GetTextLineHeight()) / 2));
            ImGui.TextDisabled(entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            ImGui.PopFont();
        }

        if (ImGui.TableSetColumnIndex(2))
        {
            ImGui.TextDisabled(SourceText(entry));
        }

        if (ImGui.TableSetColumnIndex(3))
        {
            if (muted)
            {
                ImGui.TextDisabled(entry.FirstLine);
            }
            else
            {
                ImGui.Text(entry.FirstLine);
            }
        }

        ImGui.PopID();
    }

    /// <summary>The source of a row, after the lock glyph of a base-game entry and the wrench glyph of an engine
    /// gap.</summary>
    internal static string SourceText(ConsoleEntry entry) =>
        (entry.Origin.IsBaseGame ? $"{EditorIcons.Lock} " : "") +
        (entry.Kind?.Class == EngineLineClass.EngineGap ? $"{EditorIcons.Wrench} " : "") + entry.Source;

    private void ContextMenu(ConsoleEntry entry)
    {
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy", true))
        {
            ImGui.SetClipboardText(entry.ToString());
        }

        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy all visible", true))
        {
            ImGui.SetClipboardText(string.Join(Environment.NewLine, visible));
        }

        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.TrashAlt, "Clear", true))
        {
            Clear();
        }
    }

    /// <summary>
    /// Draws the details pane: the severity, the source and the time with a copy button, the origin line, the kind line
    /// of an engine line, and the full message.
    /// </summary>
    private static void Details(ConsoleEntry entry, Vector2 size)
    {
        var scale = ImGuiHelper.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(MathF.Truncate(DetailPadding.X * scale),
            MathF.Truncate(DetailPadding.Y * scale)));
        var visibleChild = ImGui.BeginChild("##details", size, ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (visibleChild)
        {
            var severity = Severities[(int)entry.Severity];
            var copyWidth = ToolbarItems.IconWidth;
            var top = ImGui.GetCursorPos();
            var width = ImGui.GetContentRegionAvail().X;
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(severity.Color, severity.Title);
            ImGui.SameLine();
            ImGui.TextDisabled($"{entry.Source}   {entry.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}");

            // The copy button is at the right end of the first line.
            ImGui.SetCursorPos(new Vector2(top.X + Math.Max(0, width - copyWidth), top.Y));
            EditorTheme.PushFlatButtonColors();
            if (ToolbarItems.Button("copy", EditorIcons.Copy, "Copy the message"))
            {
                ImGui.SetClipboardText(entry.ToString());
            }

            EditorTheme.PopFlatButtonColors();
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
            ImGui.TextWrapped(entry.Origin.Text);
            if (entry.Kind != null)
            {
                ImGui.TextWrapped(entry.Kind.DetailText);
            }

            ImGui.PopStyleColor();
            ImGui.TextWrapped(entry.Message);
        }

        ImGui.EndChild();
    }

    private void Clear()
    {
        Log.Clear();
        selected = null;
    }

    private static string CountText(int count) =>
        count > MaxShownCount ? $"{MaxShownCount}+" : count.ToString(CultureInfo.InvariantCulture);
}
