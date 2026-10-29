using System;
using LibreLancer.Data;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

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

    /// <summary>Why no play can start now, or null: no profile is open, or its game data did not load.</summary>
    /// <param name="items">The game data of the profile, or null when it did not load.</param>
    public static string? DataBlock(bool profileOpen, GameItemDb? items) =>
        !profileOpen ? NoProfile : items == null ? NoGameData : null;

    /// <summary>
    /// Why Play cannot start at a start now, or null. A start in space needs a system of the game data that the Scene
    /// shows. A start at a base needs a base of the game data whose system the game data has, as the Play at base list
    /// does (see <see cref="PlayMenu.BaseGroups"/>).
    /// </summary>
    /// <param name="items">The game data of the profile, or null when it did not load.</param>
    /// <param name="openSystem">The nickname of the system that the Scene shows, or null.</param>
    public static string? StartBlock(bool profileOpen, GameItemDb? items, string? openSystem, PlayStart start) =>
        DataBlock(profileOpen, items) ?? start switch
        {
            PlayStart.InSpace space when items!.Systems.Get(space.System) == null => NoSystem(space.System),
            PlayStart.InSpace space when !space.System.Equals(openSystem, StringComparison.OrdinalIgnoreCase) =>
                NotInScene(space.System),
            PlayStart.AtBase dock when items!.Bases.Get(dock.Base) is not { System: { } system } ||
                                       items.Systems.Get(system) == null => NoBase(dock.Base),
            _ => null,
        };
}
