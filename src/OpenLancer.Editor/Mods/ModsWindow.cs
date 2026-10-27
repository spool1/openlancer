using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Export;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Mods;

/// <summary>
/// Mod &gt; Mods (milestone M11c, decision D570): a floating window with the mods of the open profile and of the
/// workspace at the left, and the detail page of the selected mod at the right, as the Package Manager of Unity shows
/// packages. The detail page has the header with the chips and the actions, the publish checklist, and the tabs Settings,
/// Preview and History.
/// </summary>
/// <remarks>
/// <para>The list comes from <see cref="ModCatalog.Scan"/> on a worker thread. The scan runs when the window opens or
/// gets the focus, after each action of the window, after a save, after an export, and on Refresh (finding R21).</para>
/// <para>The Settings tab edits a <see cref="PublishDraft"/> of the selected mod (decision D571). A change of the
/// selection, a close of the window, of the profile or of the editor asks first: Apply, Revert or Cancel.</para>
/// <para>The actions are in <c>ModsWindow.Actions.cs</c>.</para>
/// </remarks>
internal sealed partial class ModsWindow : IDisposable
{
    /// <summary>The Console source of the Mods window.</summary>
    public const string Source = "Mods";

    private const string WindowId = "###mods";
    private static readonly Vector2 DefaultSize = new(1100, 720);

    // Sizes in unscaled points.
    private const float MinListWidth = 200;
    private const float MaxListWidth = 600;
    private const float SplitterWidth = 4;
    private const float RowGlyphGap = 4;

    // The part of the height of the detail page below the header that the steps of the checklist can take, and the
    // lowest height of the steps in lines.
    private const float ChecklistShare = 0.35f;
    private const float MinChecklistLines = 3;

    private readonly EditorWindow window;
    private readonly ModFingerprint fingerprints = new();
    private readonly ModHeader header;
    private readonly ChecklistPanel checklist;
    private readonly PublishPage page;
    private readonly PreviewTab preview;
    private readonly HistoryTab history;
    private readonly RenameModDialog renameDialog;
    private readonly DuplicateModDialog duplicateDialog;
    private readonly MarkPublishedDialog markDialog;

    // The repo mods whose edits the user allowed in this run of the editor (decision D584), by folder.
    private readonly HashSet<string> repoAllowed = new(StringComparer.OrdinalIgnoreCase);

    // The content of each mod that an automatic check checked in this run, so that a check that does not complete
    // does not run again in each frame.
    private readonly Dictionary<string, string> autoChecked = new(StringComparer.OrdinalIgnoreCase);

    // The reason of the write guard for each mod folder (decision D587), or null. The guard reads the disk, so the
    // window keeps each result until the next scan.
    private readonly Dictionary<string, string?> guards = new(StringComparer.OrdinalIgnoreCase);

    // True for each mod folder in the OpenLancer repo (decision D584). The check walks the folders above the mod, so
    // the window keeps each result until the next scan.
    private readonly Dictionary<string, bool> repoMods = new(StringComparer.OrdinalIgnoreCase);

    // The steps of an Apply of the Export dialog that need a question: the unused images and a new open of the
    // profile. They run when no dialog is open and no export runs.
    private (PublishDraft Draft, ApplyResult Result)? afterApply;

    private bool open;
    private bool focus;

    // The tab that the tab bar selects in the next frame: the saved tab at the open, or the Settings tab of a Go to.
    private ModsTab? pendingTab;
    private bool wasFocused;
    private bool scanWanted;
    private Task<IReadOnlyList<ModRow>>? scan;
    private IReadOnlyList<ModRow> rows = [];
    private string search = "";
    private string? draftError;

    // The session and its saves at the last scan. A new session or a save reads the mods again.
    private (EditorSession? Session, int Saves) seen;

    public ModsWindow(EditorWindow window)
    {
        this.window = window;
        Checks = new ModChecks(ModChecks.Run);
        Checks.Checked += CheckDone;
        Thumbnails = new ScreenshotThumbnails(window);
        header = new ModHeader(window, this);
        checklist = new ChecklistPanel(this);
        page = new PublishPage(window, this);
        preview = new PreviewTab(window, this);
        history = new HistoryTab(window, this);
        renameDialog = new RenameModDialog(this);
        duplicateDialog = new DuplicateModDialog(window, this);
        markDialog = new MarkPublishedDialog(window, this);
    }

    /// <summary>True while a dialog of the window is open.</summary>
    public bool DialogOpen => renameDialog.IsOpen || duplicateDialog.IsOpen || markDialog.IsOpen;

    /// <summary>The checks of the mods (decision D572).</summary>
    public ModChecks Checks { get; }

    /// <summary>The thumbnails of the screenshots (decision D592).</summary>
    public ScreenshotThumbnails Thumbnails { get; }

    /// <summary>The fingerprints of the mods, with the hash of each file that a scan read. One instance serves several
    /// threads.</summary>
    public ModFingerprint Fingerprints => fingerprints;

    /// <summary>The publish draft of the selected mod, or null when no mod is selected or its mod.json does not
    /// load.</summary>
    public PublishDraft? Draft { get; private set; }

    /// <summary>The row of the selected mod, or null.</summary>
    public ModRow? Selected => rows.FirstOrDefault(r => SelectedFolder is { } folder && GameFolders.Same(r.Folder, folder));

    /// <summary>The display name of the mod whose draft has unapplied changes, or null. The close of the editor asks
    /// about it first (section 10.7 of the M11c design).</summary>
    public string? UnappliedDraft => Draft is { HasChanges: true } draft ? draft.Applied.Name : null;

    /// <summary>The checks of the linked games, in the order of the links.</summary>
    public IReadOnlyList<GameReport> LinkedReports => window.LinkedGameReports();

    /// <summary>Shows the Game link window, for a step that needs a linked game.</summary>
    public void ShowGameLink() => window.ShowGameLink();

    /// <summary>The workspace of the window (finding R22): the folder above the folder of the open profile, or the
    /// workspace setting of the editor without an open profile.</summary>
    public string Workspace => window.Session?.Profile.FilePath is { } profile
        ? ModCatalog.WorkspaceOf(profile)
        : Path.GetFullPath(window.Settings.WorkspaceOrDefault);

    private ModsWindowSettings State => window.Settings.ModsWindow;

    private string? SelectedFolder => State.SelectedFolder.Length > 0 ? State.SelectedFolder : null;

    /// <summary>Shows the window and gives it the focus. A folder selects its mod, and a field goes to that field of the
    /// Settings tab, for example the display name from the Export dialog.</summary>
    public void Open(string? folder = null, PublishField? field = null)
    {
        if (!open)
        {
            Refresh();
            pendingTab = State.Tab;
        }

        open = true;
        focus = true;
        if (folder != null)
        {
            AskDraft(() =>
            {
                Select(folder);
                if (field is { } goTo)
                {
                    GoTo(goTo);
                }
            });
        }
    }

    /// <summary>Reads the mods again from the disk on a worker thread.</summary>
    public void Refresh()
    {
        scanWanted = true;
        guards.Clear();
        repoMods.Clear();
    }

    /// <summary>
    /// Runs an action after the question about the publish draft (decision D571): Apply, Revert or Cancel. Without a
    /// draft with changes, the action runs at once.
    /// </summary>
    public void AskDraft(Action proceed)
    {
        if (Draft is not { HasChanges: true } draft)
        {
            proceed();
            return;
        }

        window.Choice.Show("Unapplied publish settings", PublishMessages.DraftQuestion(draft.Applied.Name),
        [
            new Choice("Apply", () => ApplyDraft(proceed)),
            new Choice("Revert", () =>
            {
                RevertDraft();
                proceed();
            }),
            new Choice("Cancel", () => { }),
        ]);
    }

    /// <summary>Drops the changes of the publish draft.</summary>
    public void RevertDraft()
    {
        Draft?.Revert();
        Refresh();
    }

    /// <summary>The game folders that the window must not write into (decision D587): the base of the open profile
    /// and the root of each linked game.</summary>
    public IReadOnlyList<string> GameRoots() =>
        [.. window.Settings.Games.Select(g => g.Path).Append(window.Session?.Profile.BaseFolder).OfType<string>()];

    /// <summary>What the write guard of the window knows (decisions D584 and D587).</summary>
    public WriteContext WriteContext() => new(GameRoots(), folder =>
        repoAllowed.Contains(Path.GetFullPath(folder)) ||
        SessionHolds(folder) && window.Session is { RepoEditsAllowed: true });

    /// <summary>True when the edit session holds the lock of the mod, because it is the active mod.</summary>
    public bool SessionHolds(string folder) =>
        window.Session?.Edits is { HoldsLock: true, ActiveMod: { } mod } && GameFolders.Same(mod.Folder, folder);

    /// <summary>True when the mod is in the OpenLancer repo (decision D584).</summary>
    public bool IsRepoMod(string folder)
    {
        var key = Path.GetFullPath(folder);
        if (!repoMods.TryGetValue(key, out var repo))
        {
            repo = RepoCheck.RepoRoot(key) != null;
            repoMods[key] = repo;
        }

        return repo;
    }

    /// <summary>True when the user allowed the edits of a repo mod in this run (decision D584).</summary>
    public bool RepoAllowed(string folder) => WriteContext().RepoAllowed(folder);

    /// <summary>Allows the edits of a repo mod until the editor closes (decision D584).</summary>
    public void AllowRepo(string folder)
    {
        repoAllowed.Add(Path.GetFullPath(folder));
        guards.Clear();
        window.Log.Add(ConsoleSeverity.Warning, Source, $"Allowed the edits of the repo mod {folder} until the editor closes.");
    }

    /// <summary>Opens the dialog "Mark as published" for a version of a mod.</summary>
    public void ShowMarkDialog(ModRow row, string version) => markDialog.Show(row, version);

    /// <summary>
    /// What the publish checklist of a mod reads (section 11 of the M11c design): the files of the mod, the history,
    /// the base of each retail target, and the targets that "Play this mod alone" can start. The Mods window and the
    /// Export dialog use it, so both count the same steps.
    /// </summary>
    public ChecklistInput ChecklistInputOf(ModManifest manifest, PublishSettings settings, PublishHistory history,
        string content, string page)
    {
        var reports = LinkedReports;
        return new ChecklistInput(manifest, settings, history, content, page,
            target => ExportDialog.BaseOf(target, reports) is { } report ? TargetCheck.BaseKey(report) : null,
            [
                .. PlayGames(manifest).Select(g => ExportTarget.Find(g.Game.Target ?? "")).OfType<ExportTarget>()
                    .Distinct(),
            ]);
    }

    /// <summary>Opens the Settings tab and the field (a Go to of the checklist).</summary>
    public void GoTo(PublishField field)
    {
        pendingTab = ModsTab.Settings;
        page.Focus(field);
    }

    public void Draw()
    {
        Checks.Update();
        Thumbnails.Frame();
        PlayPrepared();
        AfterExportApply();
        Scan();
        renameDialog.Draw();
        duplicateDialog.Draw();
        markDialog.Draw();
        if (!open)
        {
            return;
        }

        if ((window.Session, window.Session?.Saves ?? 0) != seen)
        {
            seen = (window.Session, window.Session?.Saves ?? 0);
            Refresh();
        }

        var scale = ImGuiHelper.Scale;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f));
        ImGui.SetNextWindowSize(DefaultSize * scale, ImGuiCond.FirstUseEver);
        if (focus)
        {
            ImGui.SetNextWindowFocus();
            focus = false;
        }

        var stillOpen = true;
        // The title has a star while the draft has changes, as the main window does for unsaved edits.
        var title = (Draft is { HasChanges: true } ? "Mods*" : "Mods") + WindowId;
        if (ImGui.Begin(title, ref stillOpen, ImGuiWindowFlags.NoCollapse))
        {
            var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            if (focused && !wasFocused)
            {
                Refresh();
            }

            wasFocused = focused;
            Toolbar();
            Body();
        }

        ImGui.End();
        if (!stillOpen)
        {
            AskDraft(Close);
        }
    }

    public void Dispose() => Thumbnails.Dispose();

    /// <summary>Closes the window. The thumbnails go, and the selection stays for the next open.</summary>
    private void Close()
    {
        open = false;
        Thumbnails.Clear();
    }

    /// <summary>Starts a wanted scan when none runs, and takes the result of a scan that ended.</summary>
    private void Scan()
    {
        if (scan is { IsCompleted: true } done)
        {
            scan = null;
            if (done.IsCompletedSuccessfully)
            {
                rows = done.Result;
                Scanned();
            }
            else
            {
                window.Log.Add(ConsoleSeverity.Error, Source,
                    $"OpenLancer could not read the mods: {ErrorText.Of(done.Exception!.GetBaseException())}");
            }
        }

        if (!scanWanted || scan != null || !open)
        {
            return;
        }

        scanWanted = false;
        var session = window.Session;
        var input = new CatalogInput(Workspace, session?.Profile.FilePath)
        {
            ActiveModFolder = session?.Edits.ActiveMod?.Folder,
            UnsavedEdits = session?.Edits.IsDirty == true,
            DraftFolders = Draft is { HasChanges: true } draft ? [draft.ModFolder] : [],
            GameRoots = GameRoots(),
            Fingerprints = fingerprints,
        };
        Thumbnails.ForgetChanged();
        scan = Task.Run(() => ModCatalog.Scan(input));
    }

    /// <summary>After a scan: the selection stays on its folder, the draft reads the files again when it has no
    /// changes, and the selected mod gets a check when its last check is old. Without a selection, the active mod or
    /// the first mod gets the selection.</summary>
    private void Scanned()
    {
        if (Selected == null && rows.Count > 0 && Draft is not { HasChanges: true })
        {
            var active = window.Session?.Edits.ActiveMod?.Folder;
            Select(rows.FirstOrDefault(r => active != null && GameFolders.Same(r.Folder, active))?.Folder ?? rows[0].Folder);
            return;
        }

        if (Selected is { } row && Draft is not { HasChanges: true })
        {
            OpenDraft(row.Folder);
        }

        AutoCheck();
    }

    /// <summary>Checks the selected mod when its last check is old (decision D572), once for each content in this
    /// run. It waits while other checks run, so that a click through the list does not queue a check of each mod. The
    /// scan after the end of a check tries again.</summary>
    private void AutoCheck()
    {
        if (Selected is not { Manifest: not null, Content.Length: > 0 } row || !row.Has(ChipKind.NotChecked) ||
            row.Has(ChipKind.SaveNotComplete) || Checks.Running != null || Checks.Waiting > 0 ||
            autoChecked.TryGetValue(row.Folder, out var content) && content == row.Content)
        {
            return;
        }

        autoChecked[row.Folder] = row.Content;
        CheckNow(row);
    }

    /// <summary>Makes a mod the selected mod and opens its draft. Ask about the draft first (see
    /// <see cref="AskDraft"/>).</summary>
    public void Select(string folder)
    {
        State.SelectedFolder = Path.GetFullPath(folder);
        window.Settings.Save();
        OpenDraft(folder);
        AutoCheck();
    }

    private void OpenDraft(string folder)
    {
        try
        {
            Draft = PublishDraft.Open(folder);
            draftError = null;
        }
        catch (ModLoadException e)
        {
            Draft = null;
            draftError = e.Message;
        }
    }

    private void Toolbar()
    {
        if (ImGui.Button($"{EditorIcons.Plus}  New mod..."))
        {
            NewMod();
        }

        ImGui.SetItemTooltip("Make a new mod in the mods folder of the workspace.");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Show");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Truncate(200 * ImGuiHelper.Scale));
        if (ImGui.BeginCombo("##filter", FilterName(State.Filter)))
        {
            foreach (var filter in Enum.GetValues<ModsFilter>())
            {
                if (ImGui.Selectable(FilterName(filter), filter == State.Filter))
                {
                    State.Filter = filter;
                    window.Settings.Save();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Truncate(220 * ImGuiHelper.Scale));
        ImGui.InputTextWithHint("##search", $"{EditorIcons.MagnifyingGlass} Search", ref search, 128);
        ImGui.SetItemTooltip("Shows the mods whose display name, id or tags have this text.");
        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.Sync}  Refresh"))
        {
            Refresh();
        }

        ImGui.SetItemTooltip("Read the mods again from the disk.");
        ImGui.SameLine();
        ImGui.BeginDisabled(rows.All(r => r.Manifest == null));
        if (ImGui.Button($"{EditorIcons.CheckCircle}  Check all"))
        {
            foreach (var row in rows.Where(r => r.Manifest != null))
            {
                CheckNow(row);
            }
        }

        ImGui.EndDisabled();
        ImGui.SetItemTooltip("Check each mod of the list for each of its target games. The export runs the same checks.");
        if (Checks.Running is { } running)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{EditorIcons.CircleNotch}  Checking {Path.GetFileName(running)}" +
                               (Checks.Waiting > 0 ? $", {Checks.Waiting} more" : "") + "...");
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel"))
            {
                Checks.CancelAll();
            }
        }
    }

    private void Body()
    {
        var scale = ImGuiHelper.Scale;
        var listWidth = State.ListWidth;
        var width = MathF.Truncate(Math.Clamp(listWidth, MinListWidth, MaxListWidth) * scale);
        var height = ImGui.GetContentRegionAvail().Y;
        var start = ImGui.GetCursorScreenPos();
        if (ImGui.BeginChild("##list", new Vector2(width, height), ImGuiChildFlags.Borders))
        {
            List();
        }

        ImGui.EndChild();
        var gap = MathF.Truncate(SplitterWidth * scale);
        if (Splitter.Draw("##split", new Vector2(start.X + width, start.Y), new Vector2(gap, height), true,
                ref listWidth, 1, MinListWidth, MaxListWidth, ModsWindowSettings.DefaultListWidth))
        {
            window.Settings.Save();
        }

        State.ListWidth = listWidth;
        ImGui.SetCursorScreenPos(new Vector2(start.X + width + gap, start.Y));
        if (ImGui.BeginChild("##details", new Vector2(0, height)))
        {
            Details();
        }

        ImGui.EndChild();
    }

    /// <summary>The list: the mods of the profile in load order, then the other mods of the workspace.</summary>
    private void List()
    {
        if (rows.Count == 0)
        {
            ImGui.TextWrapped(scan != null ? "Reading the mods..." : "The workspace has no mods. Click New mod to make a mod.");
            return;
        }

        var visible = Visible(rows, State.Filter, search);
        var profileName = window.Session?.Profile.Name;
        ListRow.PushStyle(ImGui.IsWindowFocused());
        foreach (var group in new[] { ModGroup.Profile, ModGroup.Workspace })
        {
            var members = visible.Where(r => r.Group == group).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Label);
            ImGui.SeparatorText(group == ModGroup.Profile
                ? $"This profile: {profileName} (load order)"
                : "Other mods in the workspace");
            ImGui.PopStyleColor();
            foreach (var row in members)
            {
                Row(Live(row));
            }
        }

        ListRow.PopStyle();
        if (visible.Count == 0)
        {
            ImGui.TextDisabled("No mod matches the search.");
        }
    }

    /// <summary>A row: the display name, the version and the state glyphs, and the id below the name in the dim
    /// color. A mod of the profile has its place in the load order.</summary>
    private void Row(ModRow row)
    {
        ImGui.PushID(row.Folder);
        var lineHeight = ImGui.GetTextLineHeight();
        var padding = ImGui.GetStyle().FramePadding.Y;
        var height = 2 * lineHeight + 2 * padding;
        var selected = SelectedFolder != null && GameFolders.Same(row.Folder, SelectedFolder);
        var min = ImGui.GetCursorScreenPos();
        if (ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None, new Vector2(0, height)) && !selected)
        {
            AskDraft(() => Select(row.Folder));
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
        {
            ImGui.SetTooltip(row.LoadError ?? row.Folder);
        }

        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var x = min.X + style.FramePadding.X;
        if (row.Place is { } place)
        {
            var number = place.ToString(System.Globalization.CultureInfo.InvariantCulture);
            drawList.AddText(new Vector2(x, min.Y + padding), ImGui.GetColorU32(ImGuiCol.TextDisabled), number);
            x += ImGui.CalcTextSize("00").X + style.ItemSpacing.X;
        }

        var right = max.X - style.FramePadding.X;
        foreach (var (glyph, color, _) in Glyphs(row).Reverse())
        {
            var glyphWidth = ImGui.CalcTextSize(glyph.ToString()).X;
            right -= glyphWidth;
            drawList.AddText(new Vector2(right, min.Y + padding), ImGui.GetColorU32(color), glyph.ToString());
            right -= MathF.Truncate(RowGlyphGap * ImGuiHelper.Scale);
        }

        if (row.Manifest != null)
        {
            var version = row.Manifest.Version;
            right -= ImGui.CalcTextSize(version).X;
            drawList.AddText(new Vector2(right, min.Y + padding), ImGui.GetColorU32(ImGuiCol.TextDisabled), version);
            right -= style.ItemSpacing.X;
        }

        var font = ImGui.GetFont();
        TextFit.AddText(drawList, font, ImGui.GetFontSize(), new Vector2(x, min.Y + padding),
            ImGui.GetColorU32(ImGuiCol.Text), row.Name, right - x);
        TextFit.AddText(drawList, font, ImGui.GetFontSize(), new Vector2(x, min.Y + padding + lineHeight),
            ImGui.GetColorU32(ImGuiCol.TextDisabled), row.Id, max.X - style.FramePadding.X - x);
        ImGui.PopID();
    }

    /// <summary>
    /// The state glyphs of a row (section 10.2 of the M11c design): a blue dot for unsaved or unapplied changes, a
    /// yellow triangle for a change after the last export, and the glyph of the check state.
    /// </summary>
    internal static IEnumerable<(char Glyph, Vector4 Color, string Name)> Glyphs(ModRow row)
    {
        if (row.Has(ChipKind.DoesNotLoad))
        {
            yield return (EditorIcons.TimesCircle, EditorTheme.Error, "Does not load");
            yield break;
        }

        if (row.Has(ChipKind.UnsavedEdits) || row.Has(ChipKind.UnappliedChanges) || row.Has(ChipKind.EarlierSession))
        {
            yield return (EditorIcons.Circle, EditorTheme.Accent, "Unsaved or unapplied changes");
        }

        if (row.Has(ChipKind.ContentChanged) || row.Has(ChipKind.PageChanged))
        {
            yield return (EditorIcons.ExclamationTriangle, EditorTheme.Warning, "Changed since the last export");
        }

        if (row.Chips.FirstOrDefault(c => c.Kind == ChipKind.Checks) is { } checks)
        {
            yield return checks.Color switch
            {
                ChipColor.Red => (EditorIcons.TimesCircle, EditorTheme.Error, checks.Text),
                ChipColor.Yellow => (EditorIcons.CheckCircle, EditorTheme.Warning, checks.Text),
                _ => (EditorIcons.CheckCircle, EditorTheme.Success, checks.Text),
            };
        }
    }

    /// <summary>The rows that the filter and the search show. The search matches the display name, the id and the
    /// tags, without letter case.</summary>
    internal static IReadOnlyList<ModRow> Visible(IReadOnlyList<ModRow> rows, ModsFilter filter, string search)
    {
        var text = search.Trim();
        return
        [
            .. rows.Where(r => filter switch
                {
                    ModsFilter.Profile => r.Group == ModGroup.Profile,
                    ModsFilter.Workspace => r.Group == ModGroup.Workspace,
                    _ => true,
                })
                .Where(r => text.Length == 0 || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                            r.Id.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                            r.Settings.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>The row with the chip of the draft as it is now.</summary>
    private ModRow Live(ModRow row) =>
        ModCatalog.WithDraft(row, Draft is { HasChanges: true } draft && GameFolders.Same(draft.ModFolder, row.Folder));

    private static string FilterName(ModsFilter filter) => filter switch
    {
        ModsFilter.Profile => "This profile",
        ModsFilter.Workspace => "Workspace",
        _ => "This profile and workspace",
    };

    /// <summary>The detail page of the selected mod: the header, the checklist and the tabs.</summary>
    private void Details()
    {
        if (Selected is not { } selected)
        {
            ImGui.TextDisabled(rows.Count == 0 ? "" : "Select a mod in the list.");
            return;
        }

        var row = Live(selected);
        header.Draw(row);
        if (row.Manifest == null || Draft == null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(row.LoadError ?? draftError ?? "");
            return;
        }

        CardLayout.Divider();
        // The steps of the checklist get a part of the page at most, so that the tabs keep their room (section 10.1 of
        // the M11c design). More steps scroll.
        checklist.Draw(row, MathF.Max(MinChecklistLines * ImGui.GetFrameHeightWithSpacing(),
            ChecklistShare * ImGui.GetContentRegionAvail().Y));
        CardLayout.Divider();
        if (row.HistoryProblem is { } problem)
        {
            CardLayout.Note(EditorIcons.ExclamationTriangle, EditorTheme.Warning, problem);
        }

        if (ImGui.BeginTabBar("##tabs"))
        {
            Tab(ModsTab.Settings, "Settings", () => page.Draw(row, Draft));
            Tab(ModsTab.Preview, "Preview", () => preview.Draw(row));
            Tab(ModsTab.History, "History", () => history.Draw(row));
            ImGui.EndTabBar();
        }
    }

    private void Tab(ModsTab tab, string label, Action draw)
    {
        var flags = pendingTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        var shown = true;
        if (!ImGui.BeginTabItem(label, ref shown, flags | EditorLayout.NoCloseButton))
        {
            return;
        }

        if (pendingTab == tab)
        {
            pendingTab = null;
        }

        if (pendingTab == null && State.Tab != tab)
        {
            State.Tab = tab;
            window.Settings.Save();
        }

        draw();
        ImGui.EndTabItem();
    }
}
