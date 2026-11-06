using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>A use of the name of an entity in other data, which a rename does not change.</summary>
/// <param name="File">The install path of the file.</param>
/// <param name="Text">The use as a line for the rename dialog.</param>
internal sealed record ThnNameUse(string File, string Text);

/// <summary>
/// The other data that uses the name of an entity, for the rename dialog (section 7.5 of the M8 design): the room INI
/// lines and the fixture spots that name the entity (see <see cref="ThnReferences.EntityNameUses"/>), and the REFERENCE
/// entities with the name in the other scripts that play in those rooms. Those scripts skip such an entity and use the
/// object of the set by name (ThnScriptInstance.cs:70-71). A rename changes only the open file.
/// </summary>
/// <remarks>The caller scans the data for the uses and gives them to <see cref="CutsceneCommands.RenameEntity"/>. The
/// editor scans the game data. An agent scans the edit view, because the headless host has no game data.</remarks>
internal static class CutsceneRename
{
    /// <summary>The scan of the INI files of each edit view. The edit view changes only with a save, so the scan runs
    /// once for each one.</summary>
    private static readonly ConditionalWeakTable<ModOverlay, ThnReferences> Scans = new();

    /// <summary>
    /// The uses of a name of an entity in the edit view of a session, for a rename of an agent. The room INI lines come
    /// from the files of the edit view. Another script is W when the session has it open, else the script of the edit
    /// view. A script that the editor cannot read has no use.
    /// </summary>
    /// <remarks>The first call for an edit view reads each INI file of the view.</remarks>
    public static IReadOnlyList<ThnNameUse> Uses(EditSession session, string path, string name)
    {
        var view = session.EditView;
        var references = Scans.GetValue(view, v => ThnReferences.Scan(v.CreateFileSystem()));
        var files = new Lazy<LibreLancer.Data.IO.FileSystem>(view.CreateFileSystem);
        return Uses(references, path, name, other => Script(session, files, other), session.ThnSymbols);
    }

    /// <summary>W of a script that the session has open, else the script of the edit view, or null.</summary>
    private static ThnScriptModel? Script(EditSession session, Lazy<LibreLancer.Data.IO.FileSystem> files, string path)
    {
        if (session.OpenCutscenes.Contains(path, VfsPath.Comparer))
            return session.Cutscene(path);
        try
        {
            var bytes = files.Value.ReadAllBytes(path);
            return ThnScriptModel.From(session.ThnEvaluator.Evaluate(bytes, path), ThnElementId.Counter());
        }
        catch (Exception e) when (e is ThnEvaluationException or FormatException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The uses of a name of an entity of a script, in the order of the scan.</summary>
    /// <param name="script">Gives W of an install path, or null when it cannot read the script.</param>
    public static IReadOnlyList<ThnNameUse> Uses(ThnReferences references, string path, string name,
        Func<string, ThnScriptModel?> script, IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var uses = references.EntityNameUses(path, name)
            .Select(r => new ThnNameUse(r.File, $"{Path.GetFileName(r.File)} [{r.Section}] {r.Key} = {r.Value}"))
            .ToList();
        foreach (var other in references.OtherRoomScripts(path))
        {
            if (script(other)?.EntityNamed(name) is { } entity && ThnFlags.IsReference(entity.Table, symbols))
                uses.Add(new ThnNameUse(other, $"{Path.GetFileName(other)}: the REFERENCE entity {ThnScriptModel.NameOf(entity.Table)}"));
        }

        return uses;
    }

    /// <summary>The message of the rename dialog: the uses, and what the rename does.</summary>
    public static string Message(string oldName, string newName, IReadOnlyList<ThnNameUse> uses) =>
        $"Other data names the entity {oldName}. The rename changes only this file, so these uses keep the old name " +
        $"and no longer find the entity:\n\n{string.Join("\n", uses.Select(u => "  " + u.Text))}\n\n" +
        $"Rename {oldName} to {newName} in this file?";
}
