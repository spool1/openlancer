using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>Why Play cannot start at a start (see <see cref="PlayChecks.StartBlock"/>).</summary>
/// <param name="Reason">The reason in plain words. The Play menu writes it to the Console, and the agent tool play gives
/// it as its error.</param>
/// <param name="SaveFirst">True for a start at a base that the edit view has and the loaded game data does not have
/// yet. A save and the reload of the game data let the start run.</param>
internal sealed record PlayBlock(string Reason, bool SaveFirst = false);

/// <summary>
/// The checks before a play request that the Play menu and the agent tool <c>play</c> share (review finding R21 of
/// MCP1-2). A profile is open, its game data loaded, and the start is a place of the game data. A start in space also
/// needs the Scene to show its system, because the start check of Play reads the shapes of the Scene (section 9.5 of the
/// M4 design). The checks have no side effect.
/// </summary>
internal static class PlayChecks
{
    public const string NoProfile = "No profile is open.";

    public const string NoGameData = "The game data of the profile did not load.";

    public const string NoOpenSystem = "No system is open. Play from camera starts the ship at the Scene camera.";

    /// <summary>The reason of a start in a system that the game data does not have.</summary>
    public static string NoSystem(string system) => $"The game data of the profile has no system '{system}'.";

    /// <summary>The reason of a start in space in a system that the Scene does not show.</summary>
    public static string NotInScene(string system) =>
        $"The Scene does not show the system '{system}', so Play cannot check the start point. Open the system in " +
        "the Scene first.";

    /// <summary>The reason of a start at a base that the game data does not have in one of its systems.</summary>
    public static string NoBase(string dock) =>
        $"The game data of the profile has no base '{dock}' in one of its systems.";

    /// <summary>The reason of a start at a new base that the edit view has and the loaded game data does not have yet.
    /// The game data gets the base only after the save, when the editor loads it again.</summary>
    public static string SaveFirst(string dock) =>
        $"The game data gets the new base '{dock}' after the save. Save the mod, wait until the editor loads the game " +
        "data again, and then start Play again.";

    /// <summary>The note of the Play at base list when the edit view has a base that the game data does not have yet
    /// (see <see cref="NewBases"/>).</summary>
    public const string NewBasesNote =
        "New bases of the mod show here after the save and the reload of the game data.";

    /// <summary>
    /// The note of the Play at base list, or null. The list shows the bases of the loaded game data. A new base of the
    /// edit view, for example a base that an agent made, comes into the game data only after the save and the reload.
    /// So the note tells why the list does not show it yet.
    /// </summary>
    /// <param name="editViewBases">The nicknames of the bases of the edit view.</param>
    /// <param name="gameDataHas">Tells if the game data has a base nickname.</param>
    public static string? NewBases(IEnumerable<string> editViewBases, Func<string, bool> gameDataHas) =>
        editViewBases.Any(b => !gameDataHas(b)) ? NewBasesNote : null;

    /// <summary>Why no play can start now, or null: no profile is open, or its game data did not load.</summary>
    /// <param name="items">The game data of the profile, or null when it did not load.</param>
    public static string? DataBlock(bool profileOpen, GameItemDb? items) =>
        !profileOpen ? NoProfile : items == null ? NoGameData : null;

    /// <summary>
    /// Why Play cannot start at a start now, or null. A start in space needs a system of the game data that the Scene
    /// shows. A start at a base needs a base of the game data whose system the game data has, as the Play at base list
    /// does (see <see cref="PlayMenu.BaseGroups"/>). A new base that the edit view has and the game data does not have
    /// yet needs a save first (<see cref="PlayBlock.SaveFirst"/>).
    /// </summary>
    /// <param name="items">The game data of the profile, or null when it did not load.</param>
    /// <param name="openSystem">The nickname of the system that the Scene shows, or null.</param>
    /// <param name="editViewHas">Tells if the edit view has a base nickname, also before the save. Null when no profile
    /// is open.</param>
    public static PlayBlock? StartBlock(bool profileOpen, GameItemDb? items, string? openSystem, PlayStart start,
        Func<string, bool>? editViewHas = null)
    {
        if (DataBlock(profileOpen, items) is { } data)
            return new PlayBlock(data);
        return start switch
        {
            PlayStart.InSpace space when items!.Systems.Get(space.System) == null => new PlayBlock(NoSystem(space.System)),
            PlayStart.InSpace space when !space.System.Equals(openSystem, StringComparison.OrdinalIgnoreCase) =>
                new PlayBlock(NotInScene(space.System)),
            PlayStart.AtBase dock when items!.Bases.Get(dock.Base) == null && editViewHas?.Invoke(dock.Base) == true =>
                new PlayBlock(SaveFirst(dock.Base), SaveFirst: true),
            PlayStart.AtBase dock when items!.Bases.Get(dock.Base) is not { System: { } system } ||
                                       items.Systems.Get(system) == null => new PlayBlock(NoBase(dock.Base)),
            _ => null,
        };
    }

    /// <summary>Why Play from camera cannot run now, or null: the checks of <see cref="StartBlock"/> for the start at
    /// the camera of the Scene. Without an open system, Play from camera has no start.</summary>
    /// <param name="items">The game data of the profile, or null when it did not load.</param>
    /// <param name="camera">The start at the camera of the Scene in the open system, or null when no system is
    /// open.</param>
    public static string? CameraBlock(bool profileOpen, GameItemDb? items, PlayStart.InSpace? camera) =>
        camera == null
            ? DataBlock(profileOpen, items) ?? NoOpenSystem
            : StartBlock(profileOpen, items, camera.System, camera)?.Reason;
}
