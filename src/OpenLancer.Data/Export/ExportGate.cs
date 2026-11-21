using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Export;

/// <summary>Where an export of a mod gets the save gate of the mod (see <see cref="ExportGate.SourceOf"/>).</summary>
public enum ExportGateSource
{
    /// <summary>The open session holds the saved mod as its active mod. The gate of the session runs.</summary>
    Session,

    /// <summary>The profile holds the mod, but the open session does not hold its saved files. A session that only
    /// reads the profile with the mod active runs the gate.</summary>
    Profile,

    /// <summary>No profile holds the mod, so the export runs no gate.</summary>
    None,
}

/// <summary>
/// The save gate before an export (#87). An export refuses a mod that the save gate of the mod refuses, with the same
/// errors (see <see cref="EditSession.SaveProblems"/>). So a mod that the editor cannot save does not reach a package.
/// An example is a patch with <c>ol.delete</c> of a costume that only an unchanged retail cutscene loads. The agent tool
/// <c>export_mod</c>, the Export dialog and <c>oltool export</c> run this gate. <c>oltool validate</c> does not run it.
/// </summary>
public static class ExportGate
{
    /// <summary>The reason of the sessions that only read for the gate. Such a session takes no edits.</summary>
    private const string ReadOnlyReason = "The export reads the saved files of the mod. It takes no edits.";

    /// <summary>The errors of the save gate of the active mod of a session, in the order of the gate.</summary>
    public static IReadOnlyList<Diagnostic> Errors(EditSession session) =>
        [.. session.SaveProblems().Where(p => p.Severity == DiagnosticSeverity.Error)];

    /// <summary>
    /// The errors of the save gate of a mod of a profile, from a session that only reads the profile with the mod
    /// active (see <see cref="EditSession.OpenReadOnly"/>). The session takes no lock, completes no save and reads no
    /// recovery file, so it writes no file. It gets the saved files of the mod.
    /// </summary>
    /// <param name="reports">The checks of the linked games. The gate checks the mission patches on the game of each
    /// retail target that they give (see <see cref="TargetGames"/>).</param>
    /// <exception cref="ArgumentException">The profile has no mod with the id.</exception>
    /// <exception cref="ModLoadException">A mod or its text is not valid.</exception>
    public static IReadOnlyList<Diagnostic> Errors(ModProfile profile, string modId, IReadOnlyList<GameReport> reports,
        IStringResourceWriter strings, IThnEvaluator thn)
    {
        using var session = EditSession.OpenReadOnly(profile, modId, strings, thn, ReadOnlyReason);
        session.TargetGame = TargetGames(reports);
        return Errors(session);
    }

    /// <summary>
    /// Where an export of the mod in a folder gets the save gate. The open session gives it when its active mod is the
    /// mod and it has no unsaved edits, because the export reads the saved files. Else a session that only reads the
    /// profile of the open session gives it, when the profile holds the mod. Else the export runs no gate.
    /// </summary>
    /// <param name="session">The open session, or null.</param>
    public static ExportGateSource SourceOf(EditSession? session, string modFolder) =>
        session == null ? ExportGateSource.None
        : session is { IsDirty: false, ActiveMod: { } active } && GameFolders.Same(active.Folder, modFolder)
            ? ExportGateSource.Session
        : session.Profile.Mods.Any(m => GameFolders.Same(m.Folder, modFolder)) ? ExportGateSource.Profile
        : ExportGateSource.None;

    /// <summary>The linked game of each retail target, from the checks of the linked games, or null for a target
    /// without a linked game. The save gate checks the mission patches on these games.</summary>
    public static Func<string, GameBase?> TargetGames(IReadOnlyList<GameReport> reports) =>
        target => ExportTarget.Find(target)?.BaseIn(reports) is { Root: { } root } ? GameBase.Standard(root) : null;

    /// <summary>The refusal of an export for the errors of the save gate. Each host lists the errors after it.</summary>
    public static string Refusal(string modId, int errors) =>
        $"The export of mod '{modId}' stopped. The save gate refuses the mod with {Plural.Count(errors, "error")}, so " +
        $"a save of the mod stops too. Correct {(errors == 1 ? "the error" : "the errors")}, then export again.";

    /// <summary>The note of an export of a mod that no profile of the editor holds. The export then runs no save
    /// gate.</summary>
    public static string NoGate(string modId) =>
        $"The open profile does not hold mod '{modId}', so the export does not run the save gate of the mod.";
}
