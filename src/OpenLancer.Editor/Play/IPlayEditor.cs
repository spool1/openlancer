using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>
/// What each kind of Play needs from the editor: the Console, the open profile, the save of the active mod and its
/// validation. Play in OpenLancer (<see cref="IPlayEditor"/>) and Play in the game (<see cref="IGamePlayHost"/>) use it.
/// <see cref="EditorWindow"/> implements it. The tests give a fake, because an editor window needs a GPU and reads and
/// writes the real settings file.
/// </summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IPlayHost : IUIThread
{
    ConsoleLog Log { get; }

    /// <summary>The open profile, or null without an open profile with loaded game data.</summary>
    PlaySession? Session { get; }

    /// <summary>Saves the edits of the active mod for Play, with all checks of a save (section 9.2).</summary>
    SaveOutcome Save();

    /// <summary>
    /// True while a Play request must wait one more frame before it saves. The save gate then waits for the index of
    /// the cutscene uses, and the status bar shows that first (see <see cref="Editing.GateWait.Hold"/>). Play asks it in
    /// each frame for the request that it starts next.
    /// </summary>
    /// <param name="request">The request that starts next.</param>
    bool HoldForGate(object request);

    /// <summary>The state of the last validation of the session, or null.</summary>
    ValidationState? Validation { get; }

    /// <summary>Runs on the UI thread when a validation of the session gives its result.</summary>
    event Action<ValidationState>? Validated;

    /// <summary>Validates the saved files as they are now, with the number of <see cref="PlaySession.Saves"/>. Play does
    /// not call it for a save that is not complete (decision D59).</summary>
    void ValidateNow();
}

/// <summary>
/// What Play in OpenLancer needs from the editor (section 9.12 of the M4 design, decision D50). It adds the names and
/// the shapes of the open data to the host of each Play.
/// </summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IPlayEditor : IPlayHost
{
    /// <summary>The name of a system, or an empty text when the game data has no name for it.</summary>
    string SystemName(string nickname);

    /// <summary>The name of a base, or an empty text when the game data has no name for it.</summary>
    string BaseName(string nickname);

    /// <summary>
    /// True while the Scene does not show each change of the edits. The Scene applies them at its next flush, at the
    /// start of its next frame (section 3 of the M2 design). For example, an Inspector field that a click on Play ends
    /// commits its edit after the flush of that frame. The Scene does not flush while it does not draw.
    /// </summary>
    bool SceneBehind { get; }

    /// <summary>
    /// Makes the Scene apply each change of the edits now, also while it does not draw, for example while the Mission tab
    /// hides its Scene part. A new model waits while an edit is open or the game data loads, so the Scene can stay
    /// behind. Call it only before the panels draw: then no worker task of the renderer reads the scene.
    /// </summary>
    void CatchUpScene();

    /// <summary>The shapes of a system for the start of Play from camera (see <see cref="SpawnPoint.Shapes"/>), as the
    /// Scene shows them. Null when the Scene does not show that system.</summary>
    /// <param name="system">The nickname of the system.</param>
    IReadOnlyList<SpawnShape>? SpawnShapes(string system);

    /// <summary>Takes the trigger changes of the mission of the game for the live view (section 6.3 of the M6c
    /// design).</summary>
    void MissionChanged(PlayEvent.MissionChanges changes);

    /// <summary>A new game started, or the game starts its mission play again (section 7.2 of the M6c design). The live
    /// view starts a new play: the play number grows, and the trace of the earlier play goes.</summary>
    void PlayStarted();

    /// <summary>The game exited. The live view stops its clock, so the times of the trace stay as at the exit.</summary>
    void PlayEnded();
}

/// <summary>
/// The start of a mission play (sections 5.6 and 5.8 of the M6c design): the editor makes the start plan from the model
/// of the saved script and writes the start save and the test layer. Play prepares the start after its save, and again
/// before each restart, so the plan reads the files that the game reads.
/// </summary>
/// <remarks>Each member runs on the UI thread.</remarks>
internal interface IMissionStart
{
    /// <summary>The install path of the mission script.</summary>
    string Script { get; }

    /// <summary>The story slot of the mission in the Console texts, for example "M02".</summary>
    string Slot { get; }

    /// <summary>The name of the start in the Console texts, for example "Checkpoint t_restore" (section 8.1 of the M6c
    /// design).</summary>
    string StartName { get; }

    /// <summary>True when the last start that <see cref="Prepare"/> made is approximate.</summary>
    bool Approximate { get; }

    /// <summary>True when <see cref="Prepare"/> can read the Scene: the start place is the Scene camera, or the suggested
    /// place, which can be a point in the system that the Scene shows and gets the start check. A base that the user
    /// picks does not read the Scene.</summary>
    bool ReadsScene { get; }

    /// <summary>The nicknames of the setup triggers of the last start that <see cref="Prepare"/> made. They are in the
    /// test layer, not in the mod.</summary>
    IReadOnlySet<string> SetupTriggers { get; }

    /// <summary>The plan of the last start that <see cref="Prepare"/> made, or null.</summary>
    StartPlan? Plan { get; }

    /// <summary>Why the last <see cref="Prepare"/> could not start, or null.</summary>
    string? Refusal { get; }

    /// <summary>Makes the plan of the start from the model of the saved script, and writes the start save and the test
    /// layer. Null when the start cannot start: <see cref="Refusal"/> then has the reason, and Play gives it to the
    /// Console.</summary>
    PlayStart.FromSave? Prepare();
}

/// <summary>The texts of a mission play in the Console (section 8.4 of the M6c design).</summary>
internal static class MissionPlayTexts
{
    /// <summary>The start note: "OpenLancer plays {slot} from {start}."</summary>
    public static string Start(IMissionStart start) => $"OpenLancer plays {start.Slot} from {start.StartName}.";

    /// <summary>The note of an approximate start.</summary>
    public const string Approximate = "This start is approximate. Setup triggers rebuild the state of the path. Some " +
                                      "state can differ, for example damage. See the Play start page.";

    /// <summary>The restart note after a save.</summary>
    public static string Restart(int save, IMissionStart start) =>
        $"Save {save} changed the files of the mission play. The game starts {start.Slot} again from {start.StartName}.";

    /// <summary>The refusal of a start: "Play from here is not available: {reason}"</summary>
    public static string Refused(string reason) => StartPlan.RefusalPrefix + reason;

    /// <summary>The restart failure: "The game cannot start {slot} again. {reason}"</summary>
    public static string RestartFailed(IMissionStart start, string reason) =>
        $"The game cannot start {start.Slot} again. {reason.TrimEnd()}";

    /// <summary>The restart failure of a start that cannot start before the restart. The editor sends no restart, so
    /// the game keeps its mission and its data.</summary>
    /// <param name="shown">The save of the game in words, for example "save 2".</param>
    public static string RestartRefused(IMissionStart start, string reason, string shown) =>
        $"{RestartFailed(start, reason)} The running game keeps {shown} and its mission.";

    /// <summary>The restart failure of the game. The game goes on from its autosave, which keeps only the last checkpoint
    /// of the mission while the player is in space.</summary>
    public static string RestartFailedInGame(IMissionStart start, string reason) =>
        $"{RestartFailed(start, reason.TrimEnd('.'))}. The game goes on with its old data. In space, the mission goes " +
        "back to its last checkpoint, or to the mission start when no checkpoint ran.";
}

/// <summary>What Play reads of the open profile when it starts a game.</summary>
/// <param name="ProfilePath">The full path of the profile file.</param>
/// <param name="ActiveMod">The id of the active mod, or null when no mod is active.</param>
/// <param name="Saves">The number of the last save of the session that changed files, or 0.</param>
/// <param name="IncompleteSave">The number of a save of the session that is not complete, or 0. The save journal
/// holds it, so the files of the mod are a mix of two saves (decision D59).</param>
/// <param name="NoEditReason">Why the editor takes no edits for the active mod now, or null.</param>
/// <param name="LaterMods">The ids of the mods that load after the active mod. Play does not include them (decision
/// D38).</param>
/// <param name="BaseFolder">The base folder of the profile, or null when it is not known. Play does not start when the
/// profile file is in it (decision D214).</param>
/// <param name="Unsaved">True when the active mod has unsaved edits. The save of an agent request that nobody allowed to
/// save refuses them (user decision of 2026-10-20 of the M6c design).</param>
internal sealed record PlaySession(string ProfilePath, string? ActiveMod, int Saves, int IncompleteSave,
    string? NoEditReason, IReadOnlyList<string> LaterMods, string? BaseFolder = null, bool Unsaved = false)
{
    /// <summary>The snapshot of an open profile. The edit session gives the profile, the mods and the unsaved edits. The
    /// editor gives the other values, because the edit session does not keep them.</summary>
    public static PlaySession Of(EditSession edits, int saves, int incompleteSave, string? noEditReason) =>
        new(edits.Profile.FilePath, edits.ActiveMod?.Id, saves, incompleteSave, noEditReason,
            edits.LaterMods.Select(m => m.Id).ToList(), edits.Profile.BaseFolder, edits.ActiveMod != null && edits.IsDirty);
}
