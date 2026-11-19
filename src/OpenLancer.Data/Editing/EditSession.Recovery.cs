using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>
    /// True when the active mod has a recovery file from an earlier session that <see cref="RestoreRecovery"/> or
    /// <see cref="DiscardRecovery"/> did not handle yet. Until then, the session takes no edits. So the session never
    /// has edits that the recovery file cannot keep. A session without the lock of the mod does not read the recovery
    /// file, because it can belong to the editor that holds the lock.
    /// </summary>
    public bool HasRecovery { get; private set; }

    /// <summary>
    /// Writes the unsaved edits to the recovery file of the active mod, or deletes the file when there are none.
    /// Call it after edits, so that a window close or a crash does not lose them (decision D8). Does nothing while
    /// <see cref="HasRecovery"/> is true, because the file then holds the edits of the earlier session. Does nothing
    /// without the lock of the mod.
    /// </summary>
    /// <returns>The problems: a file whose patch the writer cannot write is not in the recovery file.</returns>
    public IReadOnlyList<Diagnostic> WriteRecovery()
    {
        if (ActiveMod == null || HasRecovery || modLock == null)
            return [];
        var problems = new List<Diagnostic>();
        var files = new List<RecoveredFile>();
        foreach (var key in touchedModFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            var modFile = modFiles[key];
            if (modFile.HasChanges)
                files.Add(new RecoveredFile(modFile.RelativePath, RecoveredKind.ModFile, modFile.SourceHash, modFile.Text));
        }
        foreach (var path in touched.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (cutscenes.TryGetValue(path, out var cutscene))
            {
                if (cutscene.ChangedFile() is not { } changed)
                    continue;
                if (changed.Problem == null)
                    files.Add(new RecoveredFile(path, cutscene.IsWholeFile ? RecoveredKind.WholeFile : RecoveredKind.Patch,
                        cutscene.SourceHash, changed.Text));
                else
                    problems.Add(Diagnostic.Error(path, changed.Problem));
                continue;
            }
            var document = documents[path];
            if (document.IsWholeFile)
            {
                // A whole file without text is the delete of the file (DeleteScript).
                if (document.ChangedWholeFile() is { } whole)
                    files.Add(new RecoveredFile(path, RecoveredKind.WholeFile, document.SourceHash,
                        whole.Bytes == null ? null : IniDocument.TextEncoding.GetString(whole.Bytes)));
                continue;
            }
            var patch = document.ChangedPatch();
            if (patch == null)
                continue;
            if (patch.Succeeded)
                files.Add(new RecoveredFile(path, RecoveredKind.Patch, document.SourceHash,
                    patch.Sections.Count == 0 ? null : patch.Text));
            else
                problems.AddRange(patch.Problems);
        }
        var recoveredText = text.SameAs(savedText) ? null : new RecoveredText(textHash, text);
        var file = RecoveryFile.PathOf(ActiveMod.Folder);
        try
        {
            if (files.Count == 0 && recoveredText == null)
                DeleteRecoveryFile();
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.WriteAllBytes(file, new RecoveryFile(files, recoveredText).ToBytes());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add(Diagnostic.Error(file, e.Message));
        }
        return problems;
    }

    /// <summary>
    /// Restores the edits of the recovery file as one entry of the edit history, then deletes the file. When a file
    /// of the mod changed after the editor wrote the recovery file, this restores nothing and returns the problems.
    /// The edits then do not apply, so <see cref="DiscardRecovery"/> is the next step.
    /// </summary>
    /// <exception cref="InvalidOperationException">A transaction or a batch is open.</exception>
    public IReadOnlyList<Diagnostic> RestoreRecovery()
    {
        ThrowIfBusy();
        if (ActiveMod == null || !HasRecovery)
            return [];
        var file = RecoveryFile.PathOf(ActiveMod.Folder);
        RecoveryFile recovery;
        try
        {
            recovery = RecoveryFile.Read(file);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return [Diagnostic.Error(file, e.Message)];
        }

        var problems = new List<Diagnostic>();
        var change = new ChangeSet("Restore unsaved edits", EditOrigin.Recovery);
        var scope = new CommandScope(this, change);
        const string changed = "The mod changed after the editor wrote the recovery file, so the edits do not apply to it.";
        try
        {
            foreach (var recovered in recovery.Files)
            {
                if (recovered.Kind == RecoveredKind.ModFile)
                {
                    var modFile = scope.ModFile(recovered.Path);
                    if (modFile.SourceHash != recovered.KnownHash)
                        problems.Add(Diagnostic.Error(Path.Combine(ActiveMod.Folder, recovered.Path), changed));
                    else
                        modFile.SetText(recovered.Text);
                    continue;
                }
                if (recovered.Path.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
                {
                    // A new cutscene that the earlier session did not save has no file, so the session cannot open
                    // it. The restore makes its absent document first, when the path and name rules of a new cutscene
                    // allow the path. It does not use the read-only rule of New cutscene: the session is read-only
                    // while it has the recovery file.
                    if (!TryCutscene(recovered.Path, out _) && recovered.Kind == RecoveredKind.WholeFile &&
                        ThnEdits.NewCutscenePathProblem(ActiveMod, recovered.Path) == null)
                        NewCutsceneDocument(VfsPath.Normalize(recovered.Path)!);
                    var cutscene = scope.Cutscene(recovered.Path, absent: true);
                    // Only a new cutscene has a whole file without text: the delete after an undo of New cutscene.
                    if (cutscene.SourceHash != recovered.KnownHash ||
                        cutscene.IsWholeFile != (recovered.Kind == RecoveredKind.WholeFile) ||
                        recovered is { Kind: RecoveredKind.WholeFile, Text: null } && cutscene.Kind != ThnFileKind.NewHere)
                    {
                        problems.Add(Diagnostic.Error(recovered.Path, changed));
                        continue;
                    }
                    var reopened = cutscene.Reopen(recovered.Text == null ? null : Encoding.ASCII.GetBytes(recovered.Text), file);
                    if (reopened.ReadOnlyReason != null)
                        problems.Add(Diagnostic.Error(recovered.Path, reopened.ReadOnlyReason));
                    else
                        cutscene.Restore(reopened.State);
                    continue;
                }
                // A new script or a new file of the mod folder that the earlier session did not save has no file, so the
                // session makes its document first (decision D680).
                if (recovered is { Kind: RecoveredKind.WholeFile, KnownHash: null } && OpenDocument(recovered.Path) == null &&
                    EditDocument.EditsWholeFile(recovered.Path, ActiveMod.Id))
                    NewWholeDocument(recovered.Path);
                var document = scope.Document(recovered.Path);
                if (document.SourceHash != recovered.KnownHash ||
                    recovered.Kind != (document.IsWholeFile ? RecoveredKind.WholeFile : RecoveredKind.Patch))
                {
                    problems.Add(Diagnostic.Error(recovered.Path, changed));
                    continue;
                }
                if (document.IsWholeFile)
                {
                    try
                    {
                        document.Restore(recovered.Text == null
                            ? document.Initial with { Working = [], Exists = false }
                            : document.WholeState(IniDocument.TextEncoding.GetBytes(recovered.Text)));
                    }
                    catch (Exception e) when (e is not InvalidOperationException)
                    {
                        problems.Add(Diagnostic.Error(recovered.Path, e.Message));
                    }
                    continue;
                }
                var diagnostics = new List<Diagnostic>();
                var bytes = recovered.Text == null ? null : Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(recovered.Text)).ToArray();
                var restored = document.Reopen(ActiveMod.Id, file, bytes, diagnostics);
                if (restored.ReadOnlyReason != null)
                    problems.AddRange(diagnostics.Where(IsError));
                else
                    document.Restore(restored.State);
            }
            if (recovery.Text != null)
            {
                if (recovery.Text.KnownHash != textHash)
                    problems.Add(Diagnostic.Error(Path.Combine(ActiveMod.Folder, Mod.StringsFile), changed));
                else
                    scope.Text = recovery.Text.Text;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or
                                       UnauthorizedAccessException)
        {
            problems.Add(Diagnostic.Error(file, e.Message));
        }
        if (problems.Count > 0)
        {
            text = change.Undo(FileAt) ?? text;
            CacheRevision++;
            return problems;
        }
        change.NoteStructural();
        Push(change);
        DiscardRecovery();
        return [];
    }

    /// <summary>Deletes the recovery file of an earlier session. Does nothing without the lock of the mod.</summary>
    /// <exception cref="IOException">The file could not be deleted.</exception>
    public void DiscardRecovery()
    {
        if (ActiveMod == null || modLock == null)
            return;
        DeleteRecoveryFile();
        HasRecovery = false;
    }

    private void DeleteRecoveryFile()
    {
        var file = RecoveryFile.PathOf(ActiveMod!.Folder);
        if (File.Exists(file))
            File.Delete(file);
        var folder = Path.GetDirectoryName(file)!;
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }
}
