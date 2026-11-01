using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Mods;
using OpenLancer.Editor.Games;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// The start page that shows while no profile is open. A card in the middle of the workspace has the open button, the
/// linked games, the recent profiles and the other profiles of the workspace. At the first start, the first-run screen
/// replaces the card (see <see cref="FirstRunCard"/>). When an open failed, a banner at the top of the card shows the
/// error.
/// </summary>
/// <remarks>
/// The details of a recent profile (its mods and its base folder) load in the background, once each time the page
/// shows. A background check also finds the files that are not there any more, every few seconds.
/// </remarks>
internal sealed class WelcomeScreen(EditorWindow window)
{
    // Sizes in unscaled points.
    private const float CardMaxWidth = 760;
    private const float CardPadding = 24;
    private const float CardRounding = 4;
    private const float CardMargin = 16;
    private const float ButtonColumnWidth = 220;
    private const float ColumnGap = 24;
    private const float SectionGap = 16;
    private const float BannerPadding = 8;
    private const float IconColumnWidth = 20;
    private const float NameColumnWidth = 110;
    private const float ModsColumnWidth = 40;
    private const float AgeColumnWidth = 96;

    // The Welcome page shows at most this number of linked games. The Game link window shows all.
    private const int MaxGameLines = 3;

    // The interval of the background check for missing files and new workspace profiles, in seconds.
    private const double CheckInterval = 2;

    /// <summary>The first line below the title: what the editor does.</summary>
    internal const string Purpose =
        "The editor makes mods: changes to the star systems, ships, equipment, missions and cutscenes of the game.";

    /// <summary>The second line below the title: what a profile is.</summary>
    internal const string ProfileNote = "A profile names the base game data and the mods that go on top of it.";

    /// <summary>The label of the link that opens the Game link window.</summary>
    internal const string GameLinkLabel = "Game link...";

    /// <summary>The label of the link that makes a new profile for a linked game.</summary>
    internal const string NewProfileLabel = "New profile...";

    /// <summary>The label of the primary button that opens a profile file.</summary>
    internal const string OpenProfileLabel = "Open profile...";

    /// <summary>The first action when no game is linked.</summary>
    internal const string FirstActionWithoutGame = "To start, link your game with Game link, or click Open profile.";

    /// <summary>The first action when a game is linked.</summary>
    internal const string FirstActionWithGame = "To start, click Open profile, or make a profile with New profile.";

    /// <summary>The line that names the first action. Without a linked game, the first action is to link the game.</summary>
    internal static string FirstAction(bool hasLinkedGame) =>
        hasLinkedGame ? FirstActionWithGame : FirstActionWithoutGame;

    private readonly Dictionary<string, ProfileInfo> infos = [];
    private readonly HashSet<string> loading = [];
    private HashSet<string> missing = [];
    private List<string> workspaceProfiles = [];

    // The workspace profiles that are not in the recent list. They are found again when one of the two lists changes.
    private List<string> otherProfiles = [];
    private bool checking;
    private double nextCheck;
    private int lastFrame = -2;
    private float cardHeight;
    private string? selectedPath;

    /// <summary>The first-run screen (section 7 of the M11a design).</summary>
    public FirstRunCard FirstRun { get; } = new(window);

    /// <summary>The details of a recent profile file.</summary>
    /// <param name="Error">The reason why the profile did not load, or null when it loaded.</param>
    private sealed record ProfileInfo(int Mods, string BaseFolder, string? Error);

    /// <summary>Draws the page in the workspace area.</summary>
    public void Draw(Vector2 position, Vector2 size)
    {
        Refresh();
        var scale = ImGuiHelper.Scale;
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(Vector2.Max(size, Vector2.One), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // The window has no background, so the backdrop shows around the card.
        ImGui.Begin("##welcome", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings |
                                 ImGuiWindowFlags.NoBackground);
        ImGui.PopStyleVar();

        // The card is centered. Its height is the height of the last frame, because it fits its content.
        var margin = MathF.Truncate(CardMargin * scale);
        var width = MathF.Truncate(Math.Max(1, Math.Min(CardMaxWidth * scale, size.X - 2 * margin)));
        var top = Math.Max(margin, MathF.Truncate((size.Y - cardHeight) / 2));
        ImGui.SetCursorScreenPos(position + new Vector2(MathF.Truncate((size.X - width) / 2), top));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Panel);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, MathF.Truncate(CardRounding * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(MathF.Truncate(CardPadding * scale)));
        var visible = ImGui.BeginChild("##card", new Vector2(width, 0),
            ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
        if (visible)
        {
            Card();
            cardHeight = ImGui.GetWindowHeight();
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private void Card()
    {
        if (window.OpenError is { } error)
        {
            Banner(error);
        }

        if (window.ShowsFirstRun)
        {
            FirstRun.Draw();
            return;
        }

        var scale = ImGuiHelper.Scale;
        ImGui.PushFont(null, EditorTheme.HeroSize);
        ImGui.Text("OpenLancer Editor");
        ImGui.PopFont();
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
        ImGui.TextWrapped(Purpose);
        ImGui.TextWrapped(ProfileNote);
        ImGui.TextWrapped(FirstAction(window.Links.Games.Count > 0));
        ImGui.PopStyleColor();
        LinkedGames();
        ImGui.Dummy(new Vector2(0, MathF.Truncate(SectionGap * scale)));

        var columnWidth = MathF.Truncate(ButtonColumnWidth * scale);
        ImGui.BeginGroup();
        OpenButton(columnWidth);
        ImGui.EndGroup();

        ImGui.SameLine(0, MathF.Truncate(ColumnGap * scale));
        ImGui.BeginGroup();
        RecentProfiles();
        WorkspaceProfiles();
        ImGui.EndGroup();
    }

    /// <summary>A banner with the error of the last open, and a link that hides it.</summary>
    private void Banner(string error)
    {
        var scale = ImGuiHelper.Scale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var padding = MathF.Truncate(BannerPadding * scale);
        var dismissWidth = ImGui.CalcTextSize("Dismiss").X;
        var drawList = ImGui.GetWindowDrawList();

        // The fill goes below the text, but its height is known only after the text. So the text draws in the second
        // channel and the fill in the first.
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(padding));
        ImGui.BeginGroup();
        ImGui.TextColored(EditorTheme.Error, EditorIcons.TimesCircle.ToString());
        ImGui.SameLine();
        var wrap = ImGui.GetCursorPosX() + Math.Max(1, width - 3 * padding - dismissWidth -
                                                       (ImGui.GetCursorScreenPos().X - start.X));
        ImGui.PushTextWrapPos(wrap);
        ImGui.TextWrapped(error);
        ImGui.PopTextWrapPos();
        if (window.OpenErrorMissingBase != null && ImGui.TextLink("Open Game link"))
        {
            window.ShowGameLink();
        }

        ImGui.EndGroup();
        var bottom = ImGui.GetItemRectMax().Y + padding;
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - padding - dismissWidth, start.Y + padding));
        if (ImGui.TextLink("Dismiss"))
        {
            window.DismissOpenError();
        }

        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(start, new Vector2(start.X + width, bottom),
            ImGui.GetColorU32(EditorTheme.Error with { W = 0.15f }), MathF.Truncate(CardRounding * scale));
        drawList.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
        ImGui.Dummy(new Vector2(width, MathF.Truncate(SectionGap * scale)));
    }

    /// <summary>
    /// One line for each linked game, at most three: its glyph, its name and its folder. Then the links to the Game link
    /// window and to New profile.
    /// </summary>
    private void LinkedGames()
    {
        var links = window.Links;
        ImGui.Dummy(new Vector2(0, MathF.Truncate(BannerPadding * ImGuiHelper.Scale)));
        foreach (var game in links.Games.Take(MaxGameLines))
        {
            var report = links.ReportOf(game.Path);
            var (glyph, color) = GameLook.Of(report);
            ImGui.TextColored(color, glyph.ToString());
            ImGui.SameLine();
            ImGui.Text(GameLook.Name(report, game));
            ImGui.SameLine();
            ImGui.TextDisabled(TextFit.CutLeft(game.Path, ImGui.GetContentRegionAvail().X, t => ImGui.CalcTextSize(t).X));
            ImGui.SetItemTooltip($"{game.Path}\n{GameLook.State(report, links.IsChecking(game.Path))}");
        }

        if (links.Games.Count == 0)
        {
            ImGui.TextDisabled("There is no linked game.");
        }

        if (ImGui.TextLink(GameLinkLabel))
        {
            window.ShowGameLink();
        }

        ImGui.SetItemTooltip("Shows the linked game folders, their versions and their profiles.");
        ImGui.SameLine(0, MathF.Truncate(ColumnGap * ImGuiHelper.Scale));
        var games = window.LinkedGameReports().Where(g => g.CanBeBase).ToList();
        ImGui.BeginDisabled(games.Count == 0);
        if (ImGui.TextLink(NewProfileLabel))
        {
            window.ShowNewProfile(games);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(games.Count == 0
                ? "Link a game folder first."
                : "Makes a profile with an empty mod for a linked game, and opens it.");
        }
    }

    /// <summary>The primary button that opens a profile file, and a hint below it.</summary>
    private void OpenButton(float width)
    {
        EditorTheme.PushPrimaryButtonColors();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        if (ImGui.Button($"{EditorIcons.Open}  {OpenProfileLabel}",
                new Vector2(width, MathF.Truncate(CardLayout.ButtonHeight * ImGuiHelper.Scale))))
        {
            window.OpenProfileDialog();
        }

        ImGui.PopStyleVar();
        EditorTheme.PopPrimaryButtonColors();
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextDisabled("Select a profile .json file. Your profiles are in " +
                           $"{Path.Combine(window.Settings.WorkspaceOrDefault, ModWorkspace.ProfilesFolder)}.");
        ImGui.PopTextWrapPos();
        ImGui.PopFont();
    }

    /// <summary>
    /// The table of the recent profiles. A double-click or Enter opens a profile. The context menu opens it, copies
    /// its path or removes it from the list.
    /// </summary>
    private void RecentProfiles()
    {
        CardLayout.Heading("Recent profiles");
        var recent = window.Settings.RecentProfiles;
        if (recent.Count == 0)
        {
            ImGui.TextDisabled("No recent profiles.");
            return;
        }

        var scale = ImGuiHelper.Scale;
        if (!ImGui.BeginTable("##recent", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings |
                                             ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, MathF.Truncate(IconColumnWidth * scale));
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, MathF.Truncate(NameColumnWidth * scale));
        ImGui.TableSetupColumn("Mods", ImGuiTableColumnFlags.WidthFixed, MathF.Truncate(ModsColumnWidth * scale));
        ImGui.TableSetupColumn("Base folder", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Last opened", ImGuiTableColumnFlags.WidthFixed, MathF.Truncate(AgeColumnWidth * scale));
        ImGui.TableHeadersRow();

        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        string? open = null;
        string? remove = null;
        foreach (var profile in recent)
        {
            switch (RecentRow(profile, focused))
            {
                case RowAction.Open:
                    open = profile.Path;
                    break;
                case RowAction.Remove:
                    remove = profile.Path;
                    break;
            }
        }

        ImGui.EndTable();
        if (focused && selectedPath != null && !ImGui.GetIO().WantTextInput && ImGui.IsKeyPressed(ImGuiKey.Enter))
        {
            open = selectedPath;
        }

        // The list changes after the loop, so the loop does not see a changed list.
        if (remove != null)
        {
            window.Settings.RemoveRecentProfile(remove);
            window.Settings.Save();
            UpdateOtherProfiles();
        }

        if (open != null)
        {
            window.OpenProfile(open);
        }
    }

    private enum RowAction
    {
        None,
        Open,
        Remove,
    }

    private RowAction RecentRow(RecentProfile profile, bool focused)
    {
        var path = profile.Path;
        var isMissing = missing.Contains(path);
        var info = infos.GetValueOrDefault(path);
        var action = RowAction.None;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.PushID(path);
        var cell = ImGui.GetCursorScreenPos();
        var isSelected = path == selectedPath;
        var selection = ListRow.SelectionColor(focused);
        ImGui.PushStyleColor(ImGuiCol.Header, selection);
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, selection);
        }

        if (ImGui.Selectable("##row", isSelected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
        {
            selectedPath = path;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                action = RowAction.Open;
            }
        }

        ImGui.PopStyleColor(isSelected ? 2 : 1);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            selectedPath = path;
        }

        ImGui.SetItemTooltip(isMissing ? $"The file is not found.\n{path}"
            : info?.Error is { } error ? $"The profile is not valid.\n{error}"
            : path);
        if (ImGui.BeginPopupContextItem("##recent-menu"))
        {
            EditorTheme.PushMenuColors();
            if (Theme.IconMenuItem(EditorIcons.Open, "Open", !isMissing))
            {
                action = RowAction.Open;
            }

            if (Theme.IconMenuItem(EditorIcons.Copy, "Copy path", true))
            {
                ImGui.SetClipboardText(path);
            }

            ImGui.Separator();
            if (Theme.IconMenuItem(EditorIcons.TrashAlt, "Remove from list", true))
            {
                action = RowAction.Remove;
            }

            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // A missing file shows at half alpha with a warning glyph. A profile that is not valid also has the glyph.
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, isMissing ? 0.5f : 1);
        var warn = isMissing || info?.Error != null;
        ImGui.GetWindowDrawList().AddText(cell, ImGui.GetColorU32(warn ? EditorTheme.Warning : EditorTheme.Label),
            (warn ? EditorIcons.ExclamationTriangle : EditorIcons.LayerGroup).ToString());

        ImGui.TableSetColumnIndex(1);
        ImGui.Text(Path.GetFileNameWithoutExtension(path));
        ImGui.TableSetColumnIndex(2);
        ImGui.Text(info?.Error == null && info != null ? info.Mods.ToString(CultureInfo.InvariantCulture) : "");
        ImGui.TableSetColumnIndex(3);
        var folder = isMissing ? "Not found" : info == null ? "" : info.Error != null ? "Not valid" : info.BaseFolder;
        ImGui.TextDisabled(TextFit.CutLeft(folder, ImGui.GetContentRegionAvail().X, t => ImGui.CalcTextSize(t).X));
        ImGui.TableSetColumnIndex(4);
        ImGui.TextDisabled(Age(DateTime.UtcNow - profile.LastOpenedUtc));
        ImGui.PopStyleVar();
        ImGui.PopID();
        return action;
    }

    /// <summary>
    /// Links to the profiles of the workspace that are not in the recent list. A click opens the profile.
    /// </summary>
    private void WorkspaceProfiles()
    {
        var others = otherProfiles;
        if (others.Count == 0)
        {
            return;
        }

        ImGui.Dummy(new Vector2(0, MathF.Truncate(SectionGap * ImGuiHelper.Scale)));
        CardLayout.Heading("Profiles in this workspace");
        // The links flow from left to right and wrap at the edge of the column.
        var clicked = CardLayout.LinkFlow(others.Select(p => Path.GetFileNameWithoutExtension(p)).ToList(), others);
        if (clicked >= 0)
        {
            window.OpenProfile(others[clicked]);
        }
    }

    /// <summary>
    /// Starts the background reads. The details of the recent profiles load again each time the page shows. The check
    /// for missing files and the list of the workspace profiles run again at an interval.
    /// </summary>
    private void Refresh()
    {
        var frame = ImGui.GetFrameCount();
        if (frame != lastFrame + 1)
        {
            infos.Clear();
            nextCheck = 0;
            // The recent list can have changed while the page did not show, for example after a profile opened.
            UpdateOtherProfiles();
        }

        lastFrame = frame;
        foreach (var recent in window.Settings.RecentProfiles)
        {
            var path = recent.Path;
            if (!infos.ContainsKey(path) && loading.Add(path))
            {
                Task.Run(() =>
                {
                    var info = Read(path);
                    window.QueueUIThread(() =>
                    {
                        infos[path] = info;
                        loading.Remove(path);
                    });
                });
            }
        }

        if (checking || window.TotalTime < nextCheck)
        {
            return;
        }

        checking = true;
        var paths = window.Settings.RecentProfiles.Select(r => r.Path).ToArray();
        var workspace = window.Settings.WorkspaceOrDefault;
        Task.Run(() =>
        {
            HashSet<string>? gone = null;
            List<string>? profiles = null;
            try
            {
                gone = paths.Where(p => !File.Exists(p)).ToHashSet();
                profiles = EditorWindow.ProfileFiles(workspace);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The profiles folder can change or go away during the read. The lists stay as they are until the
                // next check.
            }
            finally
            {
                // The check always ends on the UI thread, also after an error. Otherwise no check starts again.
                window.QueueUIThread(() =>
                {
                    if (gone != null && profiles != null)
                    {
                        missing = gone;
                        workspaceProfiles = profiles;
                        UpdateOtherProfiles();
                    }

                    checking = false;
                    nextCheck = window.TotalTime + CheckInterval;
                });
            }
        });
    }

    /// <summary>Finds the workspace profiles that are not in the recent list.</summary>
    private void UpdateOtherProfiles()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var recent = window.Settings.RecentProfiles.Select(r => Path.GetFullPath(r.Path)).ToHashSet(comparer);
        otherProfiles = workspaceProfiles.Where(p => !recent.Contains(Path.GetFullPath(p))).ToList();
    }

    /// <summary>
    /// Reads the details of a profile file. It runs in the background. Any error makes the profile "not valid", with
    /// the error in the tooltip of its row. The row must always get its details, so this catches all exceptions.
    /// </summary>
    private static ProfileInfo Read(string path)
    {
        try
        {
            var profile = ModProfile.Load(path);
            return new ProfileInfo(profile.Mods.Count, profile.BaseFolder, null);
        }
        catch (Exception e)
        {
            return new ProfileInfo(0, "", e.Message);
        }
    }

    /// <summary>The age of a time as words, for example "just now", "5 minutes ago" or "2 days ago".</summary>
    internal static string Age(TimeSpan age)
    {
        static string Count(int value, string unit) => value == 1 ? $"1 {unit} ago" : $"{value} {unit}s ago";

        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? Count((int)age.TotalMinutes, "minute")
            : age.TotalDays < 1 ? Count((int)age.TotalHours, "hour")
            : age.TotalDays < 60 ? Count((int)age.TotalDays, "day")
            : Count((int)(age.TotalDays / 30), "month");
    }
}
