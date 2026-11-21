using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Export;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Mods;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor;

/// <summary>The edits of the active mod: the Edit and Mod menus, File > Save, their keys and their dialogs, and the save
/// prompt when the window closes.</summary>
internal sealed partial class EditorWindow : IClosePromptHost, ISaveCommandHost, ICommandHost
{
    private const string ModsTooltip =
        "Show the mods of the profile and the workspace, with their publish settings, checks and exports.";

    private readonly NewModDialog newModDialog = new();

    // The display names of the mods of the Mod menu, read from their files when the menu opens.
    private List<string> menuModNames = [];
    private readonly NewFromTemplateDialog templateDialog = new();
    private ExportDialog? exportDialog;

    // The close prompt has its own dialog. When another dialog replaces it, its cancel action still runs, so the next
    // close request asks again.
    private readonly ChoiceDialog closeDialog = new();
    private readonly ClosePrompt closePrompt;

    /// <summary>Runs the commands, the batches and the gestures of the command layer for the edits of the user (section
    /// 5.2, item 5 of the MCP1 design).</summary>
    public CommandRunner Commands { get; }

    EditSession? ICommandHost.Session => Session?.Edits;

    string? ICommandHost.NoEditReason => Session?.NoEditReason;

    void IClosePromptHost.EndEdits() => EndOpenEdits();

    string? IClosePromptHost.UnsavedMod =>
        Session is { Edits: { IsDirty: true, ActiveMod: { } mod } } ? mod.Manifest.Name : null;

    string? IClosePromptHost.UnappliedDraft => Mods.UnappliedDraft;

    void IClosePromptHost.ApplyDraft(Action applied) => Mods.ApplyDraft(applied);

    void IClosePromptHost.RevertDraft() => Mods.RevertDraft();

    void IClosePromptHost.Save(Action saved) => Edits.Save(saved);

    void IClosePromptHost.DiscardRecovery() => Edits.DiscardRecovery();

    DataLoads? IClosePromptHost.Loads => Session?.Loads;

    void IClosePromptHost.Exit() => Exit();

    void IClosePromptHost.Ask(string title, string message, IReadOnlyList<Choice> choices, Action cancel) =>
        closeDialog.Show(title, message, choices, cancel);

    void IClosePromptHost.Wait(string title, string message, IReadOnlyList<Choice> choices) =>
        closeDialog.ShowWithoutCancel(title, message, choices);

    bool ISaveCommandHost.DialogOpen => DialogOpen;

    bool ISaveCommandHost.EditOpen => IsEditing;

    /// <summary>Ends the open field edit or drag as the close prompt does. Then no window has the focus for a moment,
    /// so ImGui ends the active text field as after a click outside it. The panel gets the focus again at the next
    /// frame.</summary>
    void ISaveCommandHost.EndEdits()
    {
        EndOpenEdits();
        layout.KeepFocus();
        ImGui.SetWindowFocus(null);
    }

    void ISaveCommandHost.Save() => AfterGateStatus(() => Edits.Save());

    /// <summary>
    /// Runs a save or a Play of the user. When the save gate must wait for the index of the cutscene uses, the status bar
    /// first shows the wait for one frame (see <see cref="GateWait"/>). A session that takes no edits does not save, so
    /// it does not wait.
    /// </summary>
    internal void AfterGateStatus(Action action) =>
        gateWait.Run(Session is { NoEditReason: null } session && session.Edits.CutsceneGateWaits,
            status => Log.AddStatus(ConsoleSeverity.Info, SaveReport.Source, status), action);

    /// <summary>
    /// The section of the edit view that an item shows: the <c>[Ship]</c> of a ship, the section of equipment, the
    /// <c>[system]</c> of a system in <c>universe.ini</c>, the section of an object, a zone or a light of the open
    /// system, or the section itself. Null for other items, and when no section or more than one section has the
    /// nickname.
    /// </summary>
    public SectionRef? SectionOf(object? item) => item switch
    {
        null => null,
        SectionRef section => section,
        Ship ship => Session?.Edits.Find(ItemFileKind.Ships, ship.Nickname),
        Equipment equipment => Session?.Edits.Find(ItemFileKind.Equipment, equipment.Nickname),
        StarSystem system => Session?.Edits.SystemSection(system.Nickname),
        _ => Scene.SectionOf(item),
    };

    /// <summary>The tooltip of New from template while it can run.</summary>
    internal const string CopyTip = "Copies the selected ship or equipment with the sections that belong to it.";

    /// <summary>The tooltip of Revert while it can run.</summary>
    internal const string RevertTip =
        "Puts back the section as the base data and the mods before the active mod have it.";

    /// <summary>Why "Allow edits of this repo mod" is off after the user allowed the edits.</summary>
    internal const string RepoEditsAllowedReason = "You allowed the edits of this repo mod until the editor closes.";

    /// <summary>Why New from template cannot copy the section, or null when it can: it copies a ship or equipment,
    /// with a session that takes edits.</summary>
    public string? CopyReason(SectionRef? section) =>
        Session is not { } session ? MenuItems.NoProfile
        : session.NoEditReason is { } reason ? reason
        : section?.Id.Key is { } key &&
          (section.Id.Type.Equals("Ship", StringComparison.OrdinalIgnoreCase) &&
           section.Equals(session.Edits.Find(ItemFileKind.Ships, key)) ||
           section.Equals(session.Edits.Find(ItemFileKind.Equipment, key)))
            ? null
            : "Select a ship or equipment.";

    /// <summary>Why Revert cannot put back the section as the lower layer has it, or null when it can. A section of a
    /// file whose schema the editor does not know is read-only (decision D123), so it has no Revert.</summary>
    public string? RevertReason(SectionRef? section) =>
        Session is not { } session ? MenuItems.NoProfile
        : section == null ? "Select an item in the Hierarchy, the Scene or the Database."
        : session.NoEditReason is { } reason ? reason
        : FieldBinding.RevertLock(session.Edits, section) is { } locked ? locked
        : !session.Edits.KnowsSchema(section.Path)
            ? "The editor shows this file read-only, so it cannot revert its items."
        : session.Edits.Get(section) switch
        {
            null => "The edit view does not have this item.",
            { ReadOnlyReason: { } readOnly } => readOnly,
            { IsNew: true } => "The active mod adds this item, so no lower layer has it. Use Undo or Delete.",
            { IsChanged: false } => "The active mod does not change this item.",
            _ => null,
        };

    /// <summary>Why Undo or Redo cannot run now, or null when it can.</summary>
    private string? UndoReason(bool redo) =>
        Session is not { } session ? MenuItems.NoProfile
        : IsEditing ? MenuItems.EditOpen
        : redo ? (session.Edits.CanRedo ? null : "There is nothing to redo.")
        : session.Edits.CanUndo ? null : "There is nothing to undo.";

    /// <summary>Asks for the nickname of a copy of a ship or of equipment, and adds the copy. The copy is then
    /// selected.</summary>
    public void NewFromTemplate(SectionRef template)
    {
        if (Session is not { } session)
        {
            return;
        }

        templateDialog.Show(session.Edits, template, plan =>
        {
            if (Commands.Run(DatabaseCommands.CreateFromTemplate(plan)).Status == EditStatus.Applied)
            {
                Selection = plan.Copies.First(c => c.Source.Equals(plan.Template)).Copy;
                Log.Add(ConsoleSeverity.Info, EditController.Source,
                    $"Added {plan.Nickname} from {plan.Template.Id.Key}: " +
                    string.Join(", ", plan.Copies.Select(c => c.Copy.Id.ToString())) + ". Save to use it in the game data.");
            }
        });
    }

    public void Revert(SectionRef section) => Commands.Run(session => FieldCommands.RevertSection(session, section));

    /// <summary>
    /// Shows the game data that loaded again after a save. A selection of the game data becomes the item with the same
    /// nickname in the new data. A scene item stays, because the open system stays, and a section stays as it is. The
    /// open cutscene stays too.
    /// </summary>
    public void GameDataReplaced(GameDataManager data)
    {
        Selection = Selection switch
        {
            Ship ship => data.Items.Ships.Get(ship.Nickname),
            Equipment equipment => data.Items.Equipment.Get(equipment.Nickname),
            StarSystem system => data.Items.Systems.Get(system.Nickname) ?? (object)system,
            var other => other,
        };
        // The preview builds again with the new data (section 8.4 of the M8 design).
        Cutscenes.DataReplaced();
        // The checks of the mission scripts run again after each reload (section 12.3 of the M6a design).
        Missions.Run();
        // The scene keeps the game data of the open. A section that waits for the reload shows only in a new open of the
        // system (decision D282 of the M23 design).
        if (Scene.WaitsForReload)
        {
            reopenAfterReload = true;
        }
    }

    /// <summary>The Console warnings of the active mod at the open: the later mods (decision D2) and a mod in the
    /// OpenLancer repo (decision D5).</summary>
    private void ModWarnings(EditorSession session)
    {
        if (session.Edits.ActiveMod is not { } mod)
        {
            return;
        }

        if (LaterModsWarning(session) is { } later)
        {
            Log.Add(ConsoleSeverity.Warning, EditController.Source, later);
        }

        if (session.RepoRoot != null)
        {
            Log.Add(ConsoleSeverity.Warning, EditController.Source,
                $"The active mod '{mod.Id}' is part of the OpenLancer repo at {session.RepoRoot}. Its files are tracked " +
                "files, so the Inspector asks one time before the first edit.");
        }

        if (session.Edits.ReadOnlyReason is { } reason && !session.Edits.HasRecovery)
        {
            Log.Add(ConsoleSeverity.Warning, EditController.Source, $"The mod '{mod.Id}' takes no edits: {reason}");
        }
    }

    /// <summary>The warning about the mods that load after the active mod, or null when there are none.</summary>
    private static string? LaterModsWarning(EditorSession session) =>
        session.Edits.LaterMods.Count == 0
            ? null
            : $"The editor shows the data up to the active mod. These mods load later and can change the same values in " +
              $"the game: {string.Join(", ", session.Edits.LaterMods.Select(m => m.Id))}. A warning glyph marks such a value.";

    private void EditMenu()
    {
        var session = Session;
        var edits = session?.Edits;
        if (MenuItems.Item(EditorIcons.Undo, edits?.UndoLabel is { } undo ? $"Undo {undo}" : "Undo", UndoReason(false),
                shortcut: EditorKeys.Undo.Chord.Text))
        {
            Edits.Undo();
        }

        if (MenuItems.Item(EditorIcons.Redo, edits?.RedoLabel is { } redo ? $"Redo {redo}" : "Redo", UndoReason(true),
                shortcut: EditorKeys.Redo.Chord.Text))
        {
            Edits.Redo();
        }

        ImGui.Separator();
        var section = SectionOf(Selection);
        if (MenuItems.Item(EditorIcons.Clone, "New from template...", CopyReason(section), CopyTip))
        {
            NewFromTemplate(section!);
        }

        if (MenuItems.Item(EditorIcons.Undo, "Revert selected item", RevertReason(section), RevertTip))
        {
            Revert(section!);
        }

        // The actions on the items of the open system (milestone M3). Their keys belong to the Hierarchy and the Scene,
        // as in Unity.
        ImGui.Separator();
        if (MenuItems.BeginMenu($"{EditorIcons.Plus}   Create", session == null ? MenuItems.NoProfile
                : CenterMode == CenterMode.Cutscene ? "Create makes the items of a system. Open the Scene tab first."
                : null))
        {
            EditorTheme.PushMenuColors();
            SceneEdits.CreateMenu(null);
            EditorTheme.PopMenuColors();
            ImGui.EndMenu();
        }

        SceneEdits.ItemMenu(Selection);
    }

    /// <summary>File > Save, as in Unity. It runs the save of Ctrl+S, and the status bar shows the result, also when the
    /// session is read-only.</summary>
    private void SaveMenuItem()
    {
        var session = Session;
        if (MenuItems.Item(EditorIcons.Save, "Save", session == null ? MenuItems.NoProfile : null,
                session?.NoEditReason ?? $"Writes the edits as patches of the mod '{session?.Edits.ActiveMod?.Id}'.",
                EditorKeys.Save.Chord.Text))
        {
            SaveCommand.Run(this);
        }
    }

    private void ModMenu()
    {
        if (Theme.IconMenuItem(EditorIcons.LayerGroup, "Mods...", true))
        {
            ShowMods(null, null);
        }

        ImGui.SetItemTooltip(ModsTooltip);
        ImGui.Separator();
        if (Session is not { } session)
        {
            MenuItems.Note("No profile is open");
            return;
        }

        var edits = session.Edits;
        var active = edits.ActiveMod?.Id;
        // The display names come from the files, so a name that the Mods window applied shows at once.
        if (ImGui.IsWindowAppearing())
        {
            menuModNames = edits.Profile.Mods.Select(DisplayName).ToList();
        }

        ImGui.TextDisabled("Active mod");
        var loading = CanChangeProfile ? null : ProfileLoading;
        if (MenuItems.Item("None (read-only)", loading, selected: active == null))
        {
            SetActiveMod(null);
        }

        for (var i = 0; i < edits.Profile.Mods.Count; i++)
        {
            var mod = edits.Profile.Mods[i];
            var name = i < menuModNames.Count ? menuModNames[i] : mod.Manifest.Name;
            if (MenuItems.Item(EditorIcons.LayerGroup, name, loading, mod.Folder, mod.Id, mod.Id == active))
            {
                SetActiveMod(mod.Id);
            }
        }

        ImGui.Separator();
        if (MenuItems.Item(EditorIcons.Plus, "New mod...", loading))
        {
            NewMod(session, null);
        }

        if (edits.HasRecovery && Theme.IconMenuItem(EditorIcons.Undo, "Unsaved edits of an earlier session...", true))
        {
            Edits.OfferRecovery();
        }

        ImGui.Separator();
        if (MenuItems.Item(EditorIcons.BoxOpen, "Export mod...",
                edits.ActiveMod == null ? "Pick an active mod. The export makes the packages of the active mod."
                : IsEditing ? MenuItems.EditOpen
                : null,
                "Make the packages of the active mod for HD Edition, Freelancer 1.1 and OpenLancer."))
        {
            // The Mods window asks first when its publish draft of the mod has changes (section 12.6 of the M11c
            // design).
            Mods.Export(edits.ActiveMod!.Folder);
        }

        if (session.RepoRoot != null)
        {
            var allowed = session.RepoEditsAllowed;
            if (MenuItems.Toggle("Allow edits of this repo mod", ref allowed,
                    allowed ? RepoEditsAllowedReason : null))
            {
                session.RepoEditsAllowed = allowed;
            }
        }

        var warnings = new[]
        {
            LaterModsWarning(session),
            session.RepoRoot == null
                ? null
                : $"The mod '{active}' is part of the OpenLancer repo at {session.RepoRoot}. Its files are tracked files.",
        }.OfType<string>().ToList();
        if (warnings.Count > 0)
        {
            ImGui.Separator();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360 * ImGuiHelper.Scale);
            foreach (var warning in warnings)
            {
                ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {warning}");
            }

            ImGui.PopTextWrapPos();
        }
    }

    /// <summary>Makes another mod the active mod. The profile opens again, because the edit view changes.</summary>
    internal void SetActiveMod(string? modId)
    {
        if (Session is not { } session || session.Edits.ActiveMod?.Id == modId)
        {
            return;
        }

        var path = session.Profile.FilePath;
        Leave("changing the active mod", () =>
        {
            Settings.SetActiveMod(path, modId);
            Settings.Save();
            OpenNow(path);
        });
    }

    /// <summary>
    /// Runs an action that ends the edits of the session, for example a close of the profile. A publish draft of the
    /// Mods window with unapplied changes asks first (section 10.7 of the M11c design). Then unsaved edits ask: Save,
    /// Discard or Cancel.
    /// </summary>
    /// <param name="what">The action in the question about the unsaved edits, for example "closing the profile".</param>
    internal void Leave(string what, Action action) => Mods.AskDraft(() => Edits.Leave(what, action));

    /// <summary>Opens the open profile again, after the leave question, for example after Apply changed the targets of
    /// a mod of the profile. <paramref name="then"/> runs after the open.</summary>
    internal void ReopenProfile(Action? then)
    {
        if (Session is not { } session)
        {
            then?.Invoke();
            return;
        }

        var path = session.Profile.FilePath;
        Leave("opening the profile again", () =>
        {
            OpenNow(path);
            then?.Invoke();
        });
    }

    /// <summary>Opens a profile without the leave question. The caller asked it.</summary>
    internal void OpenProfileNow(string path) => OpenNow(path);

    /// <summary>Closes the open profile without the leave question. The caller asked it.</summary>
    internal void CloseProfileNow() => CloseNow();

    /// <summary>
    /// Makes a new mod in the mods folder next to the profiles folder, adds it to the end of the profile, and makes it
    /// the active mod. The new mod loads after the active mod, so the unsaved edits of the session can still be saved.
    /// </summary>
    /// <param name="created">Gets the folder of the new mod after the profile opened again, or null.</param>
    private void NewMod(EditorSession session, Action<string>? created)
    {
        var path = session.Profile.FilePath;
        var modsFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "mods"));
        newModDialog.Show(modsFolder, path, session.Profile.BaseFolder, [], id =>
        {
            Log.Add(ConsoleSeverity.Info, EditController.Source, $"Made mod '{id}' in {modsFolder} and added it to the profile.");
            Leave("making the new mod the active mod", () =>
            {
                Settings.SetActiveMod(path, id);
                Settings.Save();
                OpenNow(path);
                created?.Invoke(Path.Combine(modsFolder, id));
            });
        });
    }

    /// <summary>
    /// New mod... of the Mods window (section 12.1 of the M11c design). With an open profile, it works as Mod &gt; New
    /// mod. Without a profile, the mod goes into the mods folder of the workspace only.
    /// </summary>
    /// <param name="created">Gets the folder of the new mod.</param>
    internal void NewModFromModsWindow(Action<string> created)
    {
        if (Session is { } session)
        {
            NewMod(session, created);
            return;
        }

        var modsFolder = Path.Combine(Path.GetFullPath(Settings.WorkspaceOrDefault), ModWorkspace.ModsFolder);
        newModDialog.Show(modsFolder, null, null, Mods.GameRoots(), id =>
        {
            Log.Add(ConsoleSeverity.Info, ModsWindow.Source, $"Made mod '{id}' in {modsFolder}.");
            created(Path.Combine(modsFolder, id));
        });
    }

    /// <summary>
    /// Opens the Export dialog for a mod (milestones M11 and M11c). The export reads the saved files, so for the active
    /// mod with unsaved edits, a dialog asks to save first.
    /// </summary>
    internal void ShowExport(string modFolder)
    {
        if (ExportBlock is { } block)
        {
            Log.Add(ConsoleSeverity.Warning, ExportDialog.Source, block);
            return;
        }

        var workspace = Mods.Workspace;
        void Show() => (exportDialog ??= new ExportDialog(this)).Show(modFolder, workspace, LinkedGameReports());
        if (Session is not { Edits: { IsDirty: true, ActiveMod: { } active } } session ||
            !Data.Games.GameFolders.Same(active.Folder, modFolder))
        {
            Show();
            return;
        }

        Choice.Show("Export mod", "The export uses the saved files. Save the changes first?",
        [
            new Choice("Save", () =>
            {
                if (Edits.Save().Saved && Session == session && !session.Edits.IsDirty)
                {
                    Show();
                }
            }),
            new Choice("Cancel", () => { }),
        ]);
    }

    /// <summary>True while an export of the Export dialog or of the agent prepares or writes.</summary>
    internal bool ExportRuns => exportDialog?.Running == true || AgentExportRuns;

    /// <summary>Why the Export dialog cannot open now, or null.</summary>
    internal string? ExportBlock => ExportRuns ? "An export runs now. Wait until it ends." : null;

    /// <summary>Shows the Mods window. A folder selects its mod, and a field goes to that field of the Settings
    /// tab.</summary>
    internal void ShowMods(string? modFolder, PublishField? field) => Mods.Open(modFolder, field);

    /// <summary>The display name of a mod from its mod.json, or the name of the open when the file does not load
    /// now.</summary>
    private static string DisplayName(Mod mod)
    {
        try
        {
            return ModManifest.Load(mod.Folder).Name;
        }
        catch (ModLoadException)
        {
            return mod.Manifest.Name;
        }
    }

    private void DrawDialogs()
    {
        Choice.Draw();
        // The export takes the results of its tasks also while its dialog is closed.
        exportDialog?.Update();
        exportDialog?.Draw();
        newModDialog.Draw();
        newProfileDialog.Draw();
        Missions.DrawDialogs();
        templateDialog.Draw();
        SceneEdits.Draw();
        closeDialog.Draw();
        agentMenu.Draw();
        helpMenu.Draw();
    }

    /// <summary>True while a dialog of the editor is open. The close of the window that waits for the load of the game
    /// data counts as a dialog, also when another popup hid its dialog.</summary>
    internal bool DialogOpen => Choice.IsOpen || newModDialog.IsOpen || newProfileDialog.IsOpen || Missions.DialogOpen ||
                                templateDialog.IsOpen || SceneEdits.IsOpen || closeDialog.IsOpen || closePrompt.Waits ||
                                exportDialog?.IsOpen == true || Mods.DialogOpen || agentMenu.DialogOpen ||
                                helpMenu.DialogOpen;

    /// <summary>
    /// The keys of the editor. Ctrl+S (File > Save) also works while a text field has the keyboard or a field edit is
    /// open, as in Unity (see <see cref="SaveCommand"/>). The keys of the Edit menu, Ctrl+Z and Ctrl+Y or Ctrl+Shift+Z,
    /// and the keys of Play, F5 (a mission play in the Mission tab), Ctrl+F5 (Play in the game) and Shift+F5, wait while
    /// a text field has the keyboard. So
    /// Ctrl+Z and Ctrl+Y stay the undo and redo of the text field. They also wait while a field edit or a dialog is open.
    /// The chords come from <see cref="EditorKeys"/>, which Help &gt; Keys shows.
    /// </summary>
    private void Shortcuts()
    {
        const ImGuiInputFlags global = ImGuiInputFlags.RouteGlobal;
        // A global route of a key with Ctrl also fires while a text field is active. The text field has no route for
        // Ctrl+S, and ImGui types no character while Ctrl is down.
        if (ImGui.Shortcut(EditorKeys.Save.Chord.Shortcut, global))
        {
            SaveCommand.Run(this);
            return;
        }

        if (Session == null || ImGui.GetIO().WantTextInput || IsEditing || DialogOpen)
        {
            return;
        }

        if (ImGui.Shortcut(EditorKeys.Undo.Chord.Shortcut, global))
        {
            Edits.Undo();
        }
        else if (ImGui.Shortcut(EditorKeys.Redo.Chord.Shortcut, global) ||
                 ImGui.Shortcut(EditorKeys.RedoShift.Chord.Shortcut, global))
        {
            Edits.Redo();
        }
        else if (ImGui.Shortcut(EditorKeys.PlayInGame.Chord.Shortcut, global))
        {
            AfterGateStatus(GamePlay.PlayLast);
        }
        else if (ImGui.Shortcut(EditorKeys.Play.Chord.Shortcut, global))
        {
            // In the Mission tab, F5 plays the open mission from its play start (decision D744 of the M6c design).
            playMenu.PlayF5();
        }
        else if (ImGui.Shortcut(EditorKeys.Stop.Chord.Shortcut, global))
        {
            playMenu.Stop();
        }
    }
}
