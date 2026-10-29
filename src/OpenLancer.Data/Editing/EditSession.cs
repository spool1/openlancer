using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The edits of one profile, with one active mod that gets them. Each edit is a command. A save writes the edits as
/// patches of the active mod.
/// </summary>
/// <remarks>
/// <para>The profile's mods in load order are the lower mods, the active mod and the later mods. The session shows
/// the edit view E: the base data with the lower mods and the active mod (decision D2). For each file it keeps B, the
/// file as the lower mods leave it, and W, B with the active mod's patch and the edits (see
/// <see cref="EditDocument"/>). A save writes the patch that changes B into W.</para>
/// <para>Use a session on one thread only. To change the active mod, open a new session. The history then starts
/// empty, because B changes.</para>
/// <para>A session holds the lock of its active mod (see <see cref="ModLock"/>) until it is disposed. A second session
/// on the same mod, also in another editor, is read-only. A disposed session is read-only too.</para>
/// </remarks>
public sealed partial class EditSession : IDisposable
{
    private readonly IStringResourceWriter stringWriter;

    /// <summary>Reads the cutscene scripts and their THN patches for the merged views.</summary>
    private readonly IThnEvaluator thn;

    private readonly IReadOnlyList<Mod> lower;
    private readonly ModOverlay lowerView;
    private readonly LayerInputs inputs;
    private readonly Dictionary<string, EditDocument> documents = new(VfsPath.Comparer);
    private readonly EditHistory history = new();
    private readonly Lazy<ModOverlay> fullView;

    /// <summary>Why the session cannot edit, or null. It changes only when the session closes (see
    /// <see cref="Close"/>).</summary>
    private string? readOnlyReason;

    /// <summary>The paths that a command changed, also when an undo took the change back (rule P1).</summary>
    private readonly HashSet<string> touched = new(VfsPath.Comparer);

    private ModText text;
    private ChangeSet? transaction;
    private EditBatch? batch;

    /// <summary>The lock of the active mod, or null when the session does not hold it. Without the lock, the session
    /// does not read, write or delete the recovery file.</summary>
    private ModLock? modLock;

    /// <param name="recoveryFailure">Why the open could not complete an earlier save of the active mod or of a lower
    /// mod, or null.</param>
    /// <param name="modLock">The lock of the active mod, or null.</param>
    /// <param name="lockReason">Why the open could not take the lock of the active mod, or null.</param>
    private EditSession(ModProfile profile, IReadOnlyList<Mod> mods, int active, IStringResourceWriter stringWriter,
        IThnEvaluator thn, IReadOnlyList<Diagnostic> openProblems, string? recoveryFailure, ModLock? modLock,
        string? lockReason)
    {
        this.modLock = modLock;
        Profile = profile;
        this.stringWriter = stringWriter;
        this.thn = thn;
        OpenProblems = openProblems;
        ActiveMod = active < 0 ? null : mods[active];
        lower = active < 0 ? mods : mods.Take(active).ToList();
        LaterMods = active < 0 ? [] : mods.Skip(active + 1).ToList();
        inputs = new LayerInputs(profile.BaseFolder, lower, ActiveMod);
        lowerView = ModOverlay.Build(profile.BaseFolder, lower, stringWriter, thn, keepDocuments: true);
        EditView = ActiveMod == null ? lowerView : BuildEditView(ActiveMod);
        // Only the marks of the later mods need the whole profile, so it is built at the first use.
        fullView = new Lazy<ModOverlay>(() => ModOverlay.Build(profile.BaseFolder, mods, stringWriter, thn));
        readOnlyReason = ActiveMod == null ? "There is no active mod. Select a mod to edit."
            : lockReason
              ?? recoveryFailure
              ?? (lowerView.HasErrors ? "The mods that load before the active mod have merge errors. Correct them, then " +
                                        "open the profile again." : null);
        (text, textHash) = ActiveMod == null ? (ModText.Empty, null) : ReadText(ActiveMod);
        savedText = text;
        HasRecovery = ActiveMod != null && modLock != null && File.Exists(RecoveryFile.PathOf(ActiveMod.Folder));
        // The mission editor counts the script versions from the open (section 4.2 of the M6b design).
        _ = Missions;
    }

    public ModProfile Profile { get; }

    /// <summary>The mod that gets the edits, or null when the session is read-only.</summary>
    public Mod? ActiveMod { get; private set; }

    /// <summary>The mods that load after the active mod. The edit view does not show their changes.</summary>
    public IReadOnlyList<Mod> LaterMods { get; }

    /// <summary>
    /// True when the session takes edits. It takes none while <see cref="HasRecovery"/> is true: restore or discard the
    /// unsaved edits of the earlier session first.
    /// </summary>
    public bool CanEdit => ReadOnlyReason == null;

    /// <summary>
    /// Why the session takes no edits, or null: it has no active mod, another editor has the active mod open, the open
    /// could not complete an earlier save, the lower mods have merge errors, the session is closed, or the recovery
    /// file of an earlier session waits (see <see cref="HasRecovery"/>).
    /// </summary>
    public string? ReadOnlyReason =>
        readOnlyReason ?? (HasRecovery ? "The mod has unsaved edits of an earlier session. Restore or discard them first." : null);

    /// <summary>
    /// The problems that the open found in the earlier saves of the mods of the profile. An error tells that the open
    /// could not complete a save. When that save is of the active mod or of a lower mod, the session is read-only. A
    /// warning tells about a file that changed after a save stopped, and that the open kept.
    /// </summary>
    public IReadOnlyList<Diagnostic> OpenProblems { get; }

    /// <summary>
    /// The edit view E as the files on disk give it: the base data with the lower mods and the active mod. A save
    /// builds it again. Load the game data from a new <see cref="ModOverlay.CreateFileSystem"/> of it.
    /// </summary>
    public ModOverlay EditView { get; private set; }

    public bool CanUndo => history.CanUndo;

    public bool CanRedo => history.CanRedo;

    public string? UndoLabel => history.UndoLabel;

    public string? RedoLabel => history.RedoLabel;

    /// <summary>Who made the entry that <see cref="Undo()"/> takes back, or null when there is nothing to
    /// undo.</summary>
    public EditOrigin? UndoOrigin => history.UndoOrigin;

    /// <summary>Who made the entry that <see cref="Redo"/> puts back, or null when there is nothing to redo.</summary>
    public EditOrigin? RedoOrigin => history.RedoOrigin;

    /// <summary>Who undid the entry that <see cref="Redo"/> puts back, or null when there is nothing to redo. An agent
    /// redoes only an entry that it undid itself (decision D709 of the MCP1 design).</summary>
    public EditOrigin? RedoUndoneBy => history.RedoUndoneBy;

    /// <summary>The agent entries since the last save, at the version of the history that it read.</summary>
    private (long Version, int Count, HashSet<SectionRef> Sections)? agentEdits;

    /// <summary>
    /// The sections that the entries of an agent changed since the last save, by their references now (section 8.5 of the
    /// MCP1 design). A later rename of such a section, also by the user, gives its new reference. An undo of the entry
    /// removes its sections, a redo puts them back, and a save clears them. The Hierarchy marks these rows.
    /// </summary>
    public IReadOnlySet<SectionRef> AgentSections => AgentEditsNow().Sections;

    /// <summary>The number of agent entries since the last save. The status bar shows it.</summary>
    public int AgentEdits => AgentEditsNow().Count;

    private (long Version, int Count, HashSet<SectionRef> Sections) AgentEditsNow()
    {
        if (agentEdits is { } known && known.Version == history.Version)
            return known;
        var sections = new HashSet<SectionRef>();
        var count = 0;
        foreach (var entry in history.SinceSave)
        {
            // The renames of each entry come first, so a section of an earlier agent entry keeps its mark under its new
            // key. The sections of an agent rename hold the old and the new reference.
            foreach (var (from, to) in entry.Renames)
            {
                if (sections.Remove(from))
                    sections.Add(to);
            }
            if (entry.Origin != EditOrigin.Agent)
                continue;
            count++;
            sections.UnionWith(entry.Change.Sections);
        }
        agentEdits = (history.Version, count, sections);
        return agentEdits.Value;
    }

    /// <summary>
    /// The revision of the session (decision D764 of the MCP1 design). It grows when the history changes the state: a
    /// commit that changes data, an undo, a redo and a restore of unsaved edits. A preview, a cancel, a refused
    /// command, a step of a transaction and a save do not change it. A caller that read data can compare it before
    /// an edit (<c>if_revision</c>).
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>The detail code of the refusal of <see cref="RevisionGuard"/>.</summary>
    public const string RevisionChanged = "RevisionChanged";

    /// <summary>
    /// The guard of a request that names the revision that its caller read (<c>if_revision</c>, section 4.0.5 of the
    /// M6b design). Each command calls it before it changes data.
    /// </summary>
    /// <param name="ifRevision">The revision that the caller read, or null for a request without the guard.</param>
    /// <returns>A refusal of the kind <see cref="EditErrorKind.StaleRevision"/> when the revision changed after the
    /// read, else null.</returns>
    public EditError? RevisionGuard(long? ifRevision) =>
        ifRevision is { } read && read != Revision
            ? new EditError(EditErrorKind.StaleRevision, RevisionChanged,
                $"The edit view changed after revision {read}. Read the items again, then edit.")
            : null;

    /// <summary>The open batch, or null (see <see cref="BeginBatch"/>). A command of the command layer joins
    /// it.</summary>
    public EditBatch? OpenBatch => batch;

    /// <summary>True while a transaction is open (see <see cref="Begin"/>).</summary>
    public bool InTransaction => transaction != null;

    /// <summary>True when the session has edits that it did not save.</summary>
    public bool IsDirty => history.IsDirty;

    /// <summary>True while the session holds the lock of its active mod (see <see cref="ModLock"/>). The Mods window
    /// then writes into the active mod without a lock of its own.</summary>
    public bool HoldsLock => modLock != null;

    /// <summary>
    /// The files that a command of the session changed, also when an undo took the change back: the install paths of
    /// the INI files and the cutscenes, and the keys of the editor files of the mod (<c>mod:</c> and the path in the mod
    /// folder, see <see cref="ModFileDocument"/>). A save writes these files. The session opened each of them, so a
    /// read of W of such a file opens no new file.
    /// </summary>
    public IReadOnlyList<string> EditedPaths =>
        touched.Concat(touchedModFiles).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Tells what an edit, an undo or a redo changed.</summary>
    public event Action<EditChange>? Changed;

    /// <summary>
    /// Opens a session. It takes the lock of the active mod first. When another editor has the mod open, the session
    /// is read-only. When an earlier save of a mod of the profile stopped after its commit point, this completes it
    /// (see <see cref="SaveTransaction"/>). <see cref="OpenProblems"/> tells what it could not complete.
    /// </summary>
    /// <param name="activeModId">The id of the mod that gets the edits, or null for a read-only session of the whole
    /// profile.</param>
    /// <param name="strings">Writes the text DLL of the merged views.</param>
    /// <param name="thn">Reads the cutscene scripts and their THN patches for the merged views.</param>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    /// <exception cref="ModLoadException">A mod or its text is not valid.</exception>
    public static EditSession Open(ModProfile profile, string? activeModId, IStringResourceWriter strings,
        IThnEvaluator thn) => Open(profile, activeModId, strings, thn, readOnly: null);

    /// <summary>
    /// Opens a session that shows the edit view of the active mod and takes no edits. It takes no lock, completes no
    /// save and reads no recovery file, so it writes no file. The headless agent server opens a profile in this way when
    /// the user starts it only to read (section 6.2 of the MCP1 design).
    /// </summary>
    /// <param name="reason">Why the session takes no edits. <see cref="ReadOnlyReason"/> gives it.</param>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    /// <exception cref="ModLoadException">A mod or its text is not valid.</exception>
    public static EditSession OpenReadOnly(ModProfile profile, string activeModId, IStringResourceWriter strings,
        IThnEvaluator thn, string reason) => Open(profile, activeModId, strings, thn, reason);

    /// <param name="readOnly">Why the session must not write, or null for a session that takes the lock.</param>
    private static EditSession Open(ModProfile profile, string? activeModId, IStringResourceWriter strings,
        IThnEvaluator thn, string? readOnly)
    {
        var active = -1;
        if (activeModId != null)
        {
            active = profile.IndexOf(activeModId);
            if (active < 0)
                throw new ArgumentException($"The profile {profile.Name} has no mod '{activeModId}'.", nameof(activeModId));
        }
        // OpenLancer never writes into the game folder (decisions D204 and D214). The lock and the recovery write into
        // the active mod, and a save, Play and New mod write next to the profile file. So when one of them is in the
        // base folder, the session takes no lock, completes no save, and is read-only. A session that only reads takes
        // the same path.
        var guardReason = readOnly ?? WriteGuard(profile, active);
        // The lock comes before the recovery, so that the session never completes or deletes the files of a save that
        // another editor runs now.
        string? lockReason = guardReason;
        var modLock = active < 0 || guardReason != null ? null : ModLock.TryTake(profile.Mods[active], out lockReason);
        try
        {
            var problems = new List<Diagnostic>();
            var mods = new List<Mod>();
            string? failure = null;
            for (var i = 0; i < profile.Mods.Count; i++)
            {
                var mod = profile.Mods[i];
                // Some files of a save that is not complete can be from the save and some from before it. The open
                // shows them as they are. The edit view does not show a later mod, so only a lower mod or the active
                // mod stops the edits.
                void NotComplete(string message)
                {
                    problems.Add(Diagnostic.Error(mod.Folder, message));
                    if (i <= active)
                        failure ??= message;
                }

                // The recovery writes into the mod folder, so it never runs for a mod in the base folder. A session
                // that must not write completes no save of any mod.
                var modInGame = GameFolders.Overlaps(mod.Folder, profile.BaseFolder);
                if (guardReason != null || modInGame)
                {
                    if (SaveTransaction.HasJournal(mod.Folder))
                        NotComplete(readOnly != null && !modInGame
                            ? $"A save of mod '{mod.Id}' is not complete. The session only reads, so OpenLancer did " +
                              "not complete the save."
                            : GameTexts.SaveNotRecovered(mod.Id, modInGame));
                    mods.Add(mod);
                    continue;
                }

                try
                {
                    var completed = Recover(mod, i == active ? modLock : null, problems, out var blocked);
                    if (blocked != null)
                        NotComplete($"A save of mod '{mod.Id}' is not complete. {blocked}");
                    // A completed save changes the files of the mod, so the mod loads again.
                    mods.Add(completed ? Mod.Load(mod.Folder) : mod);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    NotComplete($"An earlier save of mod '{mod.Id}' is not complete: {e.Message} Correct the problem, " +
                                "then open the profile again.");
                    mods.Add(Mod.Load(mod.Folder));
                }
            }
            return new EditSession(profile, mods, active, strings, thn, problems, failure, modLock, lockReason);
        }
        catch
        {
            modLock?.Dispose();
            throw;
        }
    }

    /// <summary>Why the session must not write, or null: the active mod or the profile file is in the base folder,
    /// also through a link.</summary>
    private static string? WriteGuard(ModProfile profile, int active) =>
        active >= 0 && GameFolders.Overlaps(profile.Mods[active].Folder, profile.BaseFolder) ? GameTexts.ActiveModInGame
        : PathRules.IsInside(profile.FilePath, profile.BaseFolder) ||
          PathRules.IsInside(PathRules.RealPath(profile.FilePath), PathRules.RealPath(profile.BaseFolder))
            ? GameTexts.ProfileInGame
            : null;

    /// <summary>
    /// Completes a save of a mod that stopped after its commit point, and deletes the staged files of a save that
    /// stopped before it (see <see cref="SaveTransaction.Recover"/>). It does this only with the lock of the mod,
    /// because another editor that has the mod open can be in the middle of a save. Without the lock, the files of a
    /// save stay as they are.
    /// </summary>
    /// <param name="held">The lock of the mod that the session holds, or null. Without it, the recovery takes the lock
    /// for its own time.</param>
    /// <param name="blocked">Why the open cannot complete the save that the journal of the mod holds, or null: the open
    /// could not take the lock of the mod.</param>
    /// <returns>True when it completed a save.</returns>
    /// <exception cref="IOException">A move or a delete of the recovery failed.</exception>
    /// <exception cref="UnauthorizedAccessException">A move or a delete of the recovery failed.</exception>
    /// <exception cref="InvalidDataException">The journal is not valid.</exception>
    private static bool Recover(Mod mod, ModLock? held, List<Diagnostic> problems, out string? blocked)
    {
        blocked = null;
        if (!Directory.Exists(Path.Combine(mod.Folder, SaveTransaction.FolderName)))
            return false;
        string? reason = null;
        using var recoveryLock = held == null ? ModLock.TryTake(mod, out reason) : null;
        if (held == null && recoveryLock == null)
        {
            if (SaveTransaction.HasJournal(mod.Folder))
                blocked = reason;
            return false;
        }
        return SaveTransaction.Recover(mod.Folder, problems);
    }

    /// <summary>Releases the lock of the active mod and closes the session (see <see cref="Close"/>).</summary>
    public void Dispose()
    {
        modLock?.Dispose();
        Close();
    }

    /// <summary>Closes the handle of the lock and deletes no file, as a crash does. Then it closes the session (see
    /// <see cref="Close"/>). Only the tests call it.</summary>
    internal void Abandon()
    {
        modLock?.Abandon();
        Close();
    }

    /// <summary>
    /// Makes the session read-only after its lock is gone. The session then takes no edits and does not save, and it
    /// does not read, write or delete the recovery file. So it changes no file of the mod, which another editor can
    /// have open now.
    /// </summary>
    private void Close()
    {
        modLock = null;
        readOnlyReason = "The edit session is closed. Open the profile again.";
        HasRecovery = false;
    }

    /// <summary>A view of a section of the edit view, or null when the edit view has no such section.</summary>
    public SectionView? Get(SectionRef target)
    {
        if (!TryFind(target, out var document, out var index))
            return null;
        var lower = document.BaseSection(document.IdentityAt(index));
        // W shares each section of B that the patch and the edits leave as it is. A patch or an edit puts a new section
        // into W, also when its lines equal B, for example a pinned line.
        var changed = lower == null || !ReferenceEquals(lower, document.Working[index]);
        return new SectionView(document.Working[index], ReadOnlyReason ?? document.ReadOnlyReasonAt(index), lower == null,
            changed);
    }

    /// <summary>
    /// Why value texts cannot be the lines of a name of a section, or null. It is the check of
    /// <see cref="EditCommand.SetLinesFromText"/> when it runs, so the plan of a command can refuse before. Also null
    /// when the edit view has no such section: the command then fails when it runs.
    /// </summary>
    internal string? LineTextError(SectionRef target, string name, IReadOnlyList<string> lines)
    {
        if (!TryFind(target, out var document, out var index))
            return null;
        EditCommand.ReadLines(document, index, name, lines, out var error);
        return error;
    }

    /// <summary>True when the session has an edit of a section that it did not save: W of the section is not W at the
    /// open of its file or at the last save. False when the edit view has no such section.</summary>
    internal bool IsUnsaved(SectionRef target) =>
        TryFind(target, out var document, out var index) &&
        !ReferenceEquals(document.InitialSection(document.IdentityAt(index)), document.Working[index]);

    /// <summary>The section as W had it at the open of its file or at the last save, or null when W did not have it
    /// then or the edit view has no such section. A caller compares it with W to find the lines of an edit that the
    /// session did not save.</summary>
    internal Section? SavedSection(SectionRef target) =>
        TryFind(target, out var document, out var index) ? document.InitialSection(document.IdentityAt(index)) : null;

    /// <summary>
    /// Where the lines of a name in a section come from: the active mod when it sets or removes the name (in its
    /// saved patch or with an edit), else the last lower mod that changes the name or the section, else the base
    /// data.
    /// </summary>
    /// <exception cref="ArgumentException">The edit view has no such section.</exception>
    public (ValueOrigin Origin, string? ModId) Origin(SectionRef target, string name)
    {
        var (document, index) = Find(target);
        var id = document.IdentityAt(index);
        // Each line of a whole file of the active mod comes from the active mod.
        if (ActiveMod != null && document.IsWholeFile)
            return (ValueOrigin.ActiveMod, ActiveMod.Id);
        if (ActiveMod != null && (!document.InBase(id) || document.Pins(id).Contains(name) ||
                                  !SameLines(document.BaseSection(id)!, document.Working[index], name, document.IsBinary)))
            return (ValueOrigin.ActiveMod, ActiveMod.Id);
        var owner = lowerView.Ledger.Owner(document.Path, document.BaseIdOf(id) ?? id, name);
        return owner == null ? (ValueOrigin.Base, null) : (ValueOrigin.LowerMod, owner);
    }

    /// <summary>
    /// The later mods that change the lines of a name in a section, that add or delete the section, or that replace
    /// its file, in load order. The game shows their values, not the values of the edit view.
    /// </summary>
    /// <exception cref="ArgumentException">The edit view has no such section.</exception>
    public IReadOnlyList<string> LaterModsThatChange(SectionRef target, string name)
    {
        if (LaterMods.Count == 0)
            return [];
        var (document, index) = Find(target);
        var later = LaterMods.Select(m => m.Id).ToHashSet();
        return fullView.Value.Ledger.ModsThatChange(document.Path, document.IdentityAt(index), name)
            .Where(later.Contains)
            .ToList();
    }

    /// <summary>The text of a placeholder of the active mod, with the edits, or null when it has none.</summary>
    public string? Text(StringPlaceholder placeholder) => text.Get(placeholder.Kind, placeholder.Key);

    /// <summary>Runs a command as one entry of the edit history.</summary>
    /// <exception cref="ArgumentException">The command refers to a section or a file that the edit view does not
    /// have.</exception>
    /// <exception cref="InvalidOperationException">The session or the section is read-only, a transaction or a batch
    /// is open, or the command does not apply. The edit view does not change.</exception>
    public void Execute(EditCommand command)
    {
        ThrowIfBusy();
        ThrowIfNoEdits();
        var change = new ChangeSet(command.Label);
        Run(change, command);
        Push(change);
    }

    /// <summary>Starts a transaction: one entry of the edit history for many steps.</summary>
    /// <exception cref="InvalidOperationException">A transaction or a batch is open, or the session takes no
    /// edits.</exception>
    public EditTransaction Begin(string label)
    {
        ThrowIfBusy();
        ThrowIfNoEdits();
        transaction = new ChangeSet(label);
        return new EditTransaction(this, transaction);
    }

    /// <summary>
    /// Opens a batch: commands that give one entry of the edit history and one validation (see
    /// <see cref="EditBatch"/>). Only one batch is open at a time.
    /// </summary>
    /// <param name="label">The label of the request, or null for the label of the first step.</param>
    /// <param name="origin">Who makes the batch: the user or an agent. A host sets it. A request cannot set it.</param>
    /// <exception cref="ArgumentException">The origin is <see cref="EditOrigin.Recovery"/>. Only the restore of the
    /// unsaved edits has that origin, because the guard of the mission files lets it through (decision D660).</exception>
    /// <exception cref="InvalidOperationException">A transaction or a batch is open, or the session takes no
    /// edits.</exception>
    public EditBatch BeginBatch(string? label = null, EditOrigin origin = EditOrigin.User)
    {
        if (origin == EditOrigin.Recovery)
            throw new ArgumentException("Only the restore of the unsaved edits has the edit origin Recovery.", nameof(origin));
        ThrowIfBusy();
        ThrowIfNoEdits();
        batch = new EditBatch(this, new ChangeSet(label ?? "", origin), label, origin);
        return batch;
    }

    /// <summary>Takes back the top entry of the edit history, as the user does.</summary>
    /// <exception cref="InvalidOperationException">There is nothing to undo, or a transaction or a batch is
    /// open.</exception>
    public void Undo() => Undo(EditOrigin.User);

    /// <summary>Takes back the top entry of the edit history.</summary>
    /// <param name="by">Who undoes the entry. <see cref="RedoUndoneBy"/> gives it while the entry is on the redo
    /// stack.</param>
    /// <exception cref="InvalidOperationException">There is nothing to undo, or a transaction or a batch is
    /// open.</exception>
    public void Undo(EditOrigin by)
    {
        ThrowIfBusy();
        var change = history.Undo(by);
        text = change.Undo(FileAt) ?? text;
        CacheRevision++;
        NoteWorld(change);
        Revision++;
        Changed?.Invoke(change.UndoChange);
    }

    /// <exception cref="InvalidOperationException">There is nothing to redo, or a transaction or a batch is
    /// open.</exception>
    public void Redo()
    {
        ThrowIfBusy();
        var change = history.Redo();
        text = change.Redo(FileAt) ?? text;
        CacheRevision++;
        NoteWorld(change);
        Revision++;
        Changed?.Invoke(change.Change);
    }

    /// <summary>Puts back the states before the transaction, then runs a command in it.</summary>
    internal void ApplyInTransaction(ChangeSet change, EditCommand command)
    {
        text = change.Undo(FileAt) ?? text;
        // The restore changes W, so the caches must not keep the state of the step before. The command of a step of the
        // command layer makes its plan when it runs, and the plan reads the caches (review finding R2 of MCP1-2).
        CacheRevision++;
        NoteWorld(change);
        try
        {
            Run(change, command);
        }
        finally
        {
            CacheRevision++;
            NoteWorld(change);
            // Also when the command throws: the edit view is then back at its state before the transaction, not at
            // the state of the step before.
            if (!change.IsEmpty)
            {
                change.Finish(FileAt, text);
                Changed?.Invoke(change.Change);
            }
        }
    }

    internal void EndTransaction(ChangeSet change, bool commit)
    {
        transaction = null;
        CacheRevision++;
        NoteWorld(change);
        if (commit)
        {
            Push(change);
            return;
        }
        change.Finish(FileAt, text);
        text = change.Undo(FileAt) ?? text;
        NoteWorld(change);
        if (!change.IsEmpty)
            Changed?.Invoke(change.UndoChange);
    }

    /// <summary>True when the change set of a batch changes a state now. It records the states after the
    /// steps.</summary>
    internal bool ChangesState(ChangeSet change)
    {
        change.Finish(FileAt, text);
        return change.ChangesState;
    }

    /// <summary>Puts back the states before a batch. Raises no <see cref="Changed"/>.</summary>
    internal void CancelBatch(ChangeSet change)
    {
        text = change.Undo(FileAt) ?? text;
        CacheRevision++;
        NoteWorld(change);
    }

    /// <summary>Forgets a batch that ended.</summary>
    internal void EndBatch(EditBatch ended)
    {
        if (ReferenceEquals(batch, ended))
            batch = null;
    }

    /// <summary>Runs a command, also a step of a batch on top of the steps before it. When it throws, puts back the
    /// states before the change set.</summary>
    internal void Run(ChangeSet change, EditCommand command)
    {
        try
        {
            command.Run(new CommandScope(this, change));
        }
        catch
        {
            text = change.Undo(FileAt) ?? text;
            throw;
        }
        finally
        {
            // Also a command that throws: it changed W and put back the states before.
            CacheRevision++;
            NoteWorld(change);
        }
    }

    /// <summary>
    /// A number that grows at each change of W of a file that can give names to the mission checks: each INI file
    /// except the mission scripts. The mission editor makes a new mission world when it changed (section 7 of the M6b
    /// design). A file that a command only reads through the scope does not change it.
    /// </summary>
    internal long WorldRevision { get; private set; }

    /// <summary>The state of each such file at the last growth of <see cref="WorldRevision"/>. A file that is not in it
    /// has its state of the open.</summary>
    private readonly Dictionary<string, DocumentState> worldStates = new(VfsPath.Comparer);

    /// <summary>Grows <see cref="WorldRevision"/> when W of an INI file of a change set that is not a mission script
    /// has another state than at the last growth.</summary>
    private void NoteWorld(ChangeSet change)
    {
        var grew = false;
        foreach (var path in change.Paths)
        {
            if (!documents.TryGetValue(path, out var document) || document.HoldsScript)
                continue;
            var seen = worldStates.GetValueOrDefault(path) ?? document.Initial;
            if (ReferenceEquals(seen, document.State))
                continue;
            worldStates[path] = document.State;
            grew = true;
        }
        if (grew)
            WorldRevision++;
    }

    /// <summary>Adds a change set to the history, also the change set of a committed batch, when it changed
    /// anything.</summary>
    internal void Push(ChangeSet change)
    {
        CacheRevision++;
        NoteWorld(change);
        change.Finish(FileAt, text);
        // A transaction whose last step threw ends in its state before, so it can change nothing.
        if (!change.ChangesState)
            return;
        Revision++;
        history.Push(change);
        foreach (var path in change.Paths)
            (ModFileDocument.IsKey(path) ? touchedModFiles : touched).Add(path);
        Changed?.Invoke(change.Change);
    }

    /// <summary>Throws while a transaction or a batch is open.</summary>
    private void ThrowIfBusy()
    {
        if (transaction != null)
            throw new InvalidOperationException("A transaction is open. Commit or cancel it first.");
        if (batch != null)
            throw new InvalidOperationException("A batch is open. Commit or cancel it first.");
    }

    /// <summary>The INI document, the cutscene or the editor file of a key that the session opened.</summary>
    private IEditFile FileAt(string path) =>
        documents.TryGetValue(path, out var document) ? document
        : cutscenes.TryGetValue(path, out var cutscene) ? cutscene
        : modFiles[path];

    /// <summary>The section of a reference.</summary>
    /// <exception cref="ArgumentException">The edit view has no such section.</exception>
    private (EditDocument Document, int Index) Find(SectionRef target) =>
        TryFind(target, out var document, out var index)
            ? (document, index)
            : throw new ArgumentException($"The edit view has no section {target}.", nameof(target));

    private bool TryFind(SectionRef target, out EditDocument document, out int index)
    {
        index = -1;
        if (!TryDocument(target.Path, out document!))
            return false;
        index = document.IndexOf(target.Id);
        return index >= 0;
    }

    /// <summary>The document of an INI file when the session has it open, or null. It opens no file.</summary>
    internal EditDocument? OpenedDocument(string path) =>
        VfsPath.Normalize(path) is { } normal && documents.TryGetValue(normal, out var document) ? document : null;

    /// <summary>The document of an INI file. Opens it at first use.</summary>
    private bool TryDocument(string path, out EditDocument? document)
    {
        document = null;
        // An install path is relative to the game folder, so the session refuses a drive, a ':' and a rooted path
        // (decision D763 of the MCP1 design).
        var normal = InstallPaths.TryParse(path);
        if (normal == null || !normal.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            return false;
        if (documents.TryGetValue(normal, out document))
            return true;
        // The session keeps the path in the letter case of the files (section 4.0.7 of the M6b design). So the first
        // request does not decide the letter case of the results and of the patch file.
        normal = LetterCaseOfFiles(normal);
        // The fingerprint first: a change after it shows at the save.
        inputs.Remember(normal, lower);
        var b = lowerView.Document(normal);
        // A whole file of the active mod that the editor edits opens also when the lower layer does not have it.
        if (b == null && !(ActiveMod != null && ActiveMod.Files.ContainsKey(normal) && EditDocument.EditsWholeFile(normal)))
            return false;
        document = EditDocument.Open(normal, b, ActiveMod, []);
        documents[normal] = document;
        return true;
    }

    /// <summary>The file system of the edit view for <see cref="LetterCaseOfFiles"/>, made once for each edit
    /// view.</summary>
    private (ModOverlay View, LibreLancer.Data.IO.FileSystem Files)? caseFiles;

    /// <summary>
    /// An install path with the letter case of the folders and the file of the edit view. A part that the edit view does
    /// not have keeps its letter case, for example the file of a new script.
    /// </summary>
    /// <param name="normal">An install path in normal form.</param>
    internal string LetterCaseOfFiles(string normal)
    {
        if (caseFiles is not var (view, files) || !ReferenceEquals(view, EditView))
        {
            files = EditView.CreateFileSystem();
            caseFiles = (EditView, files);
        }
        var parts = normal.Split(Separator);
        var folder = "";
        for (var i = 0; i < parts.Length; i++)
        {
            string[] names;
            try
            {
                names = i == parts.Length - 1 ? files.GetFiles(folder) : files.GetDirectories(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return normal;
            }
            var part = parts[i];
            if (names.Select(n => n.Split(Separator, '/')[^1])
                    .FirstOrDefault(n => n.Equals(part, StringComparison.OrdinalIgnoreCase)) is not { } found)
                return normal;
            parts[i] = found;
            folder = folder.Length == 0 ? found : folder + Separator + found;
        }
        return string.Join(Separator, parts);
    }

    /// <summary>The separator of the parts of an install path.</summary>
    private const char Separator = '\\';

    private ModOverlay BuildEditView(Mod active) => ModOverlay.Build(Profile.BaseFolder, [.. lower, active], stringWriter, thn);

    /// <summary>The text of a mod, and the hash of its strings.json, or null when it has none.</summary>
    private static (ModText Text, string? Hash) ReadText(Mod mod)
    {
        var path = Path.Combine(mod.Folder, Mod.StringsFile);
        if (!File.Exists(path))
            return (ModText.Empty, null);
        var bytes = File.ReadAllBytes(path);
        using var reader = new StreamReader(new MemoryStream(bytes, false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return (ModText.From(ModStrings.Parse(path, reader.ReadToEnd())), ContentHash.Of(bytes));
    }

    private static bool SameLines(Section original, Section edited, string name, bool binary)
    {
        var before = SectionComparer.ByName(original).GetValueOrDefault(name) ?? [];
        var after = SectionComparer.ByName(edited).GetValueOrDefault(name) ?? [];
        return SectionComparer.SameLines(before, after, binary);
    }

    /// <summary>
    /// What a command can change: the INI documents and the cutscenes of the session, and the text. The scope records
    /// the state of each file before the first change to it, so the session can undo the command.
    /// </summary>
    internal sealed class CommandScope(EditSession session, ChangeSet change)
    {
        /// <summary>
        /// The depth of the mission plan commands that run in the scope now (section 4.1 of the M6b design, decision
        /// D660). While it is 0, the scope refuses a section of a mission file, except in the restore of unsaved edits.
        /// </summary>
        internal int MissionDepth { get; set; }

        /// <summary>Who makes the change.</summary>
        internal EditOrigin Origin => change.Origin;

        /// <summary>The session of the scope.</summary>
        internal EditSession Session => session;

        /// <summary>The text of the active mod.</summary>
        /// <exception cref="InvalidOperationException">The session is read-only.</exception>
        public ModText Text
        {
            get => session.text;
            set
            {
                session.ThrowIfReadOnly();
                change.CaptureText(session.text);
                session.text = value;
            }
        }

        /// <summary>The document of a file, to change it.</summary>
        /// <exception cref="ArgumentException">The edit view has no such INI file.</exception>
        /// <exception cref="InvalidOperationException">The session or the file is read-only.</exception>
        public EditDocument Document(string path)
        {
            if (!session.TryDocument(path, out var document))
                throw new ArgumentException($"The edit view has no INI file {path}.", nameof(path));
            Guard(document!.Path);
            return Editable(document!);
        }

        /// <summary>The document and the index of a section, to change it.</summary>
        /// <exception cref="ArgumentException">The edit view has no such section.</exception>
        /// <exception cref="InvalidOperationException">The session or the section is read-only.</exception>
        public (EditDocument Document, int Index) Find(SectionRef target)
        {
            var (document, index) = session.Find(target);
            Guard(document.Path);
            Editable(document);
            if (document.ReadOnlyReasonAt(index) is { } reason)
                throw new InvalidOperationException($"{target}: {reason}");
            return (document, index);
        }

        /// <summary>A cutscene, to change it.</summary>
        /// <param name="absent">True to allow a new cutscene that does not exist now, for the restore of the recovery
        /// file.</param>
        /// <exception cref="ArgumentException">The edit view has no such cutscene, or the editor cannot read it.</exception>
        /// <exception cref="InvalidOperationException">The session or the cutscene is read-only.</exception>
        public ThnDocument Cutscene(string path, bool absent = false)
        {
            if (!session.TryCutscene(path, out var document) || !absent && !document.State.Exists)
                throw new ArgumentException(session.CutsceneProblem(path), nameof(path));
            session.ThrowIfReadOnly();
            if (document.ReadOnlyReason != null)
                throw new InvalidOperationException(document.ReadOnlyReason);
            return Captured(document);
        }

        /// <summary>
        /// The document of a new cutscene, to make its script (see <see cref="ThnEdits.NewCutscene"/>). The session
        /// makes the absent document when it has none for the path.
        /// </summary>
        /// <exception cref="ArgumentException">The path is not an install path of a cutscene.</exception>
        /// <exception cref="InvalidOperationException">The session is read-only, or a cutscene with the path
        /// exists.</exception>
        public ThnDocument NewCutscene(string path)
        {
            session.ThrowIfReadOnly();
            var normal = InstallPaths.TryParse(path);
            if (normal == null || !normal.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{path} is not an install path of a cutscene.", nameof(path));
            var document = session.NewCutsceneDocument(normal);
            if (document.State.Exists || document.Kind != ThnFileKind.NewHere)
                throw new InvalidOperationException(ThnEdits.PathInUse);
            return Captured(document);
        }

        private ThnDocument Captured(ThnDocument document)
        {
            change.Capture(document);
            change.NoteCutscene(document.Path);
            return document;
        }

        public void Changed(SectionRef section, bool structural) =>
            change.Note(Normal(section), structural);

        /// <summary>Records a change of sections that the scope does not list one by one, for example a new whole
        /// file.</summary>
        public void NoteStructural() => change.NoteStructural();

        /// <summary>Records that a section got a new key, for <see cref="EditChange.Renames"/>.</summary>
        public void Renamed(SectionRef from, SectionRef to) => change.NoteRename(Normal(from), Normal(to));

        private static SectionRef Normal(SectionRef section) =>
            section with { Path = VfsPath.Normalize(section.Path) ?? section.Path };

        /// <summary>An editor file of the active mod, to change it (section 6.8 of the M6b design). The session opens
        /// the file at the first use. A file that does not exist has no text.</summary>
        /// <param name="relativePath">The path relative to the mod folder.</param>
        /// <exception cref="ArgumentException">The path is not the path of an editor file.</exception>
        /// <exception cref="InvalidOperationException">The session is read-only, or it has no active mod.</exception>
        /// <exception cref="IOException">The file could not be read.</exception>
        public ModFileDocument ModFile(string relativePath)
        {
            session.ThrowIfReadOnly();
            if (ModFileDocument.Normalize(relativePath) == null)
                throw new ArgumentException(ModFileDocument.PathProblem(relativePath), nameof(relativePath));
            // The editor files are mission files: a graph file or a mission prefab (decision D660).
            if (MissionDepth == 0 && change.Origin != EditOrigin.Recovery)
                throw new InvalidOperationException(MissionGuardMessage(relativePath));
            var document = session.ModFile(relativePath) ??
                           throw new InvalidOperationException("There is no active mod. Select a mod to edit.");
            change.Capture(document);
            return document;
        }

        /// <summary>Refuses a mission file outside a mission plan command (decision D660). The restore of unsaved edits
        /// passes, because it writes the patches that the mission commands made (review finding RB1).</summary>
        private void Guard(string path)
        {
            if (MissionDepth == 0 && change.Origin != EditOrigin.Recovery && session.IsGuardedMissionFile(path))
                throw new InvalidOperationException(MissionGuardMessage(path));
        }

        private EditDocument Editable(EditDocument document)
        {
            session.ThrowIfReadOnly();
            if (document.ReadOnlyReason != null)
                throw new InvalidOperationException(document.ReadOnlyReason);
            change.Capture(document);
            return document;
        }
    }

    private void ThrowIfNoEdits()
    {
        if (ReadOnlyReason is { } reason)
            throw new InvalidOperationException(reason);
    }

    /// <summary>Throws when the session is read-only. Unlike <see cref="ThrowIfNoEdits"/>, it allows the restore of a
    /// recovery file.</summary>
    private void ThrowIfReadOnly()
    {
        if (readOnlyReason != null)
            throw new InvalidOperationException(readOnlyReason);
    }
}
