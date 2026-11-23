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

    /// <summary>No open profile holds the mod, so the export cannot run the gate of the mod. The Export dialog refuses
    /// the export (see <see cref="ExportGate.NoProfileRefusal"/>).</summary>
    None,
}

/// <summary>The save gate of one export: where the export gets it, and the call that gives its errors or the refusal of
/// the export (see <see cref="ExportGate.For"/>). Exactly one of <paramref name="Errors"/> and
/// <paramref name="Refusal"/> is set.</summary>
/// <param name="Source">Where the export gets the gate.</param>
/// <param name="Errors">Gives the errors of the gate, or null for <see cref="ExportGateSource.None"/>. For
/// <see cref="ExportGateSource.Session"/>, the call reads the open session, so it runs on the thread of the session. For
/// <see cref="ExportGateSource.Profile"/>, the call opens a session of its own, so a worker can run it.</param>
/// <param name="Refusal">For <see cref="ExportGateSource.None"/>, the refusal of the export (see
/// <see cref="ExportGate.NoProfileRefusal"/>), else null. The export then writes no package.</param>
public sealed record ExportGateRun(ExportGateSource Source, Func<IReadOnlyList<Diagnostic>>? Errors, string? Refusal);

/// <summary>
/// The save gate before an export (#87). An export refuses a mod that the save gate of the mod refuses, with the same
/// errors (see <see cref="EditSession.SaveProblems"/>). So a mod that the editor cannot save does not reach a package.
/// An example is a patch with <c>ol.delete</c> of a costume that an unchanged retail cutscene loads for a DEFORMABLE
/// without <c>userprops.Actor</c>. The agent tool <c>export_mod</c>, the Export dialog and <c>oltool export</c> run this
/// gate. The Export dialog refuses a mod that no open profile holds, because it cannot run the gate of such a mod
/// (#89). <c>oltool export</c> and <c>export_mod</c> always run the gate. <c>oltool validate</c> does not run it.
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
    /// profile of the open session gives it, when the profile holds the mod. Else no open profile holds the mod.
    /// </summary>
    /// <param name="session">The open session, or null.</param>
    public static ExportGateSource SourceOf(EditSession? session, string modFolder) =>
        session == null ? ExportGateSource.None
        : session is { IsDirty: false, ActiveMod: { } active } && GameFolders.Same(active.Folder, modFolder)
            ? ExportGateSource.Session
        : session.Profile.Mods.Any(m => GameFolders.Same(m.Folder, modFolder)) ? ExportGateSource.Profile
        : ExportGateSource.None;

    /// <summary>
    /// The save gate of an export of the mod in a folder (see <see cref="SourceOf"/>). The open session gives the errors
    /// of its gate. Else a session that only reads the saved profile file of the open session gives them. Else the run
    /// has the refusal of <see cref="NoProfileRefusal"/> and no gate call, so the export writes no package. The Export
    /// dialog uses it.
    /// </summary>
    /// <param name="session">The open session, or null.</param>
    /// <param name="reports">The checks of the linked games (see <see cref="TargetGames"/>).</param>
    public static ExportGateRun For(EditSession? session, string modFolder, string modId,
        IReadOnlyList<GameReport> reports, IStringResourceWriter strings, IThnEvaluator thn)
    {
        switch (SourceOf(session, modFolder))
        {
            case ExportGateSource.Session:
                return new ExportGateRun(ExportGateSource.Session, () => Errors(session!), null);
            case ExportGateSource.Profile:
                var profile = session!.Profile.FilePath;
                return new ExportGateRun(ExportGateSource.Profile,
                    () => Errors(ModProfile.Load(profile), modId, reports, strings, thn), null);
            default:
                return new ExportGateRun(ExportGateSource.None, null, NoProfileRefusal(modId, session != null));
        }
    }

    /// <summary>The linked game of each retail target, from the checks of the linked games, or null for a target
    /// without a linked game. The save gate checks the mission patches on these games.</summary>
    public static Func<string, GameBase?> TargetGames(IReadOnlyList<GameReport> reports) =>
        target => ExportTarget.Find(target)?.BaseIn(reports) is { Root: { } root } ? GameBase.Standard(root) : null;

    /// <summary>The refusal of an export for the errors of the save gate. Each host lists the errors after it.</summary>
    public static string Refusal(string modId, int errors) =>
        $"The export of mod '{modId}' stopped. The save gate refuses the mod with {Plural.Count(errors, "error")}, so " +
        $"a save of the mod stops too. Correct {(errors == 1 ? "the error" : "the errors")}, then export again.";

    /// <summary>The refusal of an export when no open profile holds the mod. The export cannot run the save gate of
    /// the mod, so it writes no package.</summary>
    /// <param name="profileOpen">True when a profile is open.</param>
    public static string NoProfileRefusal(string modId, bool profileOpen) =>
        $"The export of mod '{modId}' stopped. " +
        $"{(profileOpen ? $"The open profile does not hold mod '{modId}'" : "No profile is open")}, so the export " +
        "cannot run the save gate of the mod. Open a profile that holds the mod, then export again.";
}
