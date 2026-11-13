using System;
using System.Globalization;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.Market;
using LibreLancer.Data.Schema.NewCharDB;
using LibreLancer.Data.Schema.Save;
using LibreLancer.Server;
using OpenLancer.Data.Missions.Run;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The save that starts a play (section 10.3 of the M4 design). It is the multiplayer new-character save of the server,
/// with the start point of the editor. When the user selected a ship package, the save has the ship of that package
/// (decision D15). Without a start save, the save has no <c>[StoryInfo]</c>, so the game starts in free roam. A mission
/// play copies the story, mission and trigger parts of its start save into the save (section 5.6 of the M6c design).
/// When the start save has a ship, the save takes the ship and its lines from the start save, and the ship package has
/// no effect (see <see cref="PlayStartSave"/>).
/// </summary>
public static class PlaySave
{
    /// <summary>The name of the character of a play.</summary>
    private const string CharacterName = "OpenLancer";

    /// <summary>The faction of the new-character data that the save uses. The first faction gives the package of a new
    /// multiplayer character.</summary>
    private const int Faction = 0;

    /// <summary>The template of the new-character save. <c>GameServer.NewCharacter</c> reads it.</summary>
    private const string TemplateFile = @"EXE\mpnewcharacter.fl";

    /// <summary>Tells if the data can start the play. <see cref="Create"/> runs this check first.</summary>
    /// <param name="package">The nickname of the ship package, or null for the package of a new multiplayer
    /// character. A start save with a ship does not use it, so the check does not read it then.</param>
    /// <returns>Null when the data can start the play. Else the reason: an unknown system, base or package,
    /// new-character data that cannot make a character, or a start save that the game cannot read or start.</returns>
    public static string? Check(GameItemDb items, PlayStart start, string? package)
    {
        if (start is PlayStart.FromSave fromSave)
        {
            if (PlayStartSave.TryRead(fromSave.Save, out var problem) is not { } saved)
                return problem;
            return StartProblem(items, saved.Place) ?? MissionProblem(items, saved.Save) ??
                   (saved.HasShip
                       ? StartShipProblem(items.Ships, items.Equipment, saved.Save.Player!)
                       : PackageProblem(items, package)) ??
                   NewCharacterProblem(items, !saved.HasShip && package == null);
        }

        return StartProblem(items, start) ?? PackageProblem(items, package) ?? NewCharacterProblem(items, package == null);
    }

    /// <summary>
    /// The nickname of the ship of a play without a package: the ship of the package of the new-character faction. Null
    /// when the new-character data has no such faction or package. The Play menu of the editor names this ship.
    /// </summary>
    public static string? DefaultShip(GameItemDb items) => DefaultPackage(items)?.Ship;

    /// <summary>The package of the new-character faction, or null when the new-character data has no such faction or
    /// package. The player ship of the cutscene preview uses it too.</summary>
    internal static NewCharPackage? DefaultPackage(GameItemDb items)
    {
        var data = items.Ini.NewCharDB;
        return data.Factions.Count > Faction ? NewCharacterPackage(data, data.Factions[Faction]) : null;
    }

    /// <summary>
    /// The multiplayer new-character save of the server, with the start point. With a package, the save gets the ship,
    /// the equipment and the cargo of that package. The money stays the money of the new character. See
    /// <see cref="Equip"/> for the rules of the equipment and the cargo.
    /// </summary>
    /// <remarks>
    /// The save of a mission play (section 5.6 of the M6c design) is the new character at the place of the start save,
    /// with the <c>[StoryInfo]</c>, <c>[MissionState]</c> and <c>[TriggerSave]</c> parts of the start save. The server
    /// then loads the mission of the story. Without a saved trigger, it starts the start triggers. When the start save
    /// has a ship, the save gets the ship lines of the start save, not the package (see <see cref="GiveShip"/>).
    /// </remarks>
    /// <exception cref="ArgumentException">The data cannot start the play. The message is the reason of
    /// <see cref="Check"/>.</exception>
    public static SaveGame Create(GameServer server, PlayStart start, string? package)
    {
        var items = server.GameData.Items;
        if (Check(items, start, package) is { } problem)
            throw new ArgumentException(problem);
        var saved = start is PlayStart.FromSave fromSave ? PlayStartSave.Read(fromSave.Save) : null;
        var save = server.NewCharacter(CharacterName, Faction);
        var player = save.Player!;
        switch (saved?.Place ?? start)
        {
            case PlayStart.InSpace space:
                // The new character starts at its home base. Without a base, the server spawns the ship in space.
                player.Base = null;
                player.System = items.Systems.Get(space.System)!.Nickname;
                player.Position = space.Position;
                break;
            case PlayStart.AtBase atBase:
                var dock = items.Bases.Get(atBase.Base)!;
                player.Base = dock.Nickname;
                player.System = dock.System;
                break;
        }
        GiveShip(player, saved, package == null ? null : items.GetShipPackage(FLHash.CreateID(package)), items.Equipment);
        if (saved != null)
        {
            save.StoryInfo = saved.Save.StoryInfo;
            save.MissionState = saved.Save.MissionState;
            save.TriggerSave = [.. saved.Save.TriggerSave];
        }
        return save;
    }

    /// <summary>The reason when the storyline of the game data does not have the mission of a start save, or null. The
    /// server would load no mission. The reason also tells about an ambient script of <c>[MissionState]</c> whose base or
    /// room the game data does not have, because the server stops at such a line when it loads the save.</summary>
    internal static string? MissionProblem(GameItemDb items, SaveGame save)
    {
        var mission = save.StoryInfo?.Mission;
        if (!items.Ini.Storyline.Missions.Any(m => m.Nickname.Equals(mission, StringComparison.OrdinalIgnoreCase)))
            return $"The start save names the mission '{mission}', which the storyline of the game data does not have.";
        foreach (var ambient in save.MissionState?.Ambients ?? [])
        {
            if (items.Bases.Get(ambient.Base.Hash) is not { } dock)
                return $"The start save names the ambient script '{ambient.Script}' in a base that the game data does " +
                       "not have. The game cannot load the save.";
            if (dock.Rooms.Get(ambient.Room.Hash) == null)
                return $"The start save names the ambient script '{ambient.Script}' in a room of base '{dock.Nickname}' " +
                       "that the game data does not have. The game cannot load the save.";
        }

        return null;
    }

    /// <summary>
    /// Gives the save the equipment and the cargo that the server can load (decisions D47 and D56). The server mounts
    /// each <c>equip</c> line at its hardpoint, at <c>internal</c> when the line has none, and puts each <c>cargo</c>
    /// line into the hold.
    /// </summary>
    /// <remarks>
    /// With a package, the save gets the ship of the package, and the addons of the package replace the equipment and
    /// the cargo of the save. <see cref="StartSaveShip.FromAddons"/> tells the line of each addon. Then each
    /// <c>equip</c> line of a combinable good becomes a <c>cargo</c> line with the amount 1, with or without a package.
    /// The line can come from an addon, from the loadout of the new-character package, from a start save, or from the
    /// autosave of a reload when the new data makes a mounted good combinable. The server cannot mount such a good: it
    /// throws on its thread, and the game ends.
    /// </remarks>
    /// <param name="package">The ship package, or null to keep the ship and the lines of the save.</param>
    /// <param name="equipment">The equipment of the game data, which tells the good of each line.</param>
    internal static void Equip(SavePlayer player, ShipPackage? package, GameItemCollection<Equipment> equipment)
    {
        if (package != null)
        {
            var ship = StartSaveShip.FromAddons(package.Ship.Nickname,
                package.Addons.Select(a => (a.Equipment.Nickname, a.Hardpoint, a.Amount)));
            player.ShipArchetype = new HashValue(ship.Ship);
            player.Equip.Clear();
            player.Equip.AddRange(ship.Equip.Select(e =>
                new PlayerEquipment { Item = new HashValue(e.Item), Hardpoint = e.Hardpoint }));
            player.Cargo.Clear();
            player.Cargo.AddRange(ship.Cargo.Select(c => new PlayerCargo { Item = new HashValue(c.Item), Count = c.Count }));
        }

        // The rule of the server for a good that it adds to a stack in the hold.
        bool Combinable(PlayerEquipment line) => equipment.Get(line.Item)?.Good?.Ini.Combinable ?? false;
        player.Cargo.AddRange(player.Equip.Where(Combinable).Select(l => new PlayerCargo { Item = l.Item, Count = 1 }));
        player.Equip.RemoveAll(Combinable);
    }

    /// <summary>
    /// Gives the save the ship and the lines of a play. A start save with a ship (see <see cref="PlayStartSave.HasShip"/>)
    /// gives its ship, its <c>equip</c> lines and its <c>cargo</c> lines, and the package has no effect. Else the package
    /// gives them (see <see cref="Equip"/>), or the save keeps the ship of the new character. Then the combinable rule of
    /// <see cref="Equip"/> runs.
    /// </summary>
    /// <param name="saved">The start save of a mission play, or null.</param>
    /// <param name="package">The ship package, or null.</param>
    /// <param name="equipment">The equipment of the game data, which tells the good of each line.</param>
    internal static void GiveShip(SavePlayer player, PlayStartSave? saved, ShipPackage? package,
        GameItemCollection<Equipment> equipment)
    {
        if (saved is { HasShip: true })
        {
            var start = saved.Save.Player!;
            player.ShipArchetype = start.ShipArchetype;
            player.Equip.Clear();
            player.Equip.AddRange(start.Equip);
            player.Cargo.Clear();
            player.Cargo.AddRange(start.Cargo);
            package = null;
        }

        Equip(player, package, equipment);
    }

    /// <summary>The reason when the game data does not have the ship of a start save, or the equipment of one of its
    /// <c>equip</c> or <c>cargo</c> lines, or null.</summary>
    /// <param name="ships">The ships of the game data.</param>
    /// <param name="equipment">The equipment of the game data.</param>
    /// <param name="start">The <c>[Player]</c> section of a start save with a ship.</param>
    internal static string? StartShipProblem(GameItemCollection<Ship> ships, GameItemCollection<Equipment> equipment,
        SavePlayer start)
    {
        if (ships.Get(start.ShipArchetype) == null)
            return $"The start save names the ship '{Name(start.ShipArchetype)}', which is not in the game data. The game " +
                   "cannot give the player a ship.";
        foreach (var item in start.Equip.Select(e => e.Item).Concat(start.Cargo.Select(c => c.Item)))
        {
            if (equipment.Get(item) == null)
                return $"The start save names the equipment '{Name(item)}', which is not in the game data. The game " +
                       "cannot give it to the player.";
        }

        return null;
    }

    /// <summary>The nickname of a value of a save, or its game id when the save gives only the id.</summary>
    private static string Name(HashValue value) => value.String ?? value.Hash.ToString(CultureInfo.InvariantCulture);

    /// <summary>The reason when the items do not have the system. The check of a reload uses it too.</summary>
    internal static string? SystemProblem(GameItemDb items, string system) =>
        items.Systems.Get(system) == null ? $"The system '{system}' is not in the game data." : null;

    /// <summary>The reason when the items do not have the base. The check of a reload uses it too.</summary>
    internal static string? BaseProblem(GameItemDb items, string dock) =>
        items.Bases.Get(dock) == null ? $"The base '{dock}' is not in the game data." : null;

    internal static string? StartProblem(GameItemDb items, PlayStart start) => start switch
    {
        PlayStart.InSpace space => SystemProblem(items, space.System),
        PlayStart.AtBase atBase => BaseProblem(items, atBase.Base),
        _ => null,
    };

    private static string? PackageProblem(GameItemDb items, string? package) =>
        package != null && items.GetShipPackage(FLHash.CreateID(package)) == null
            ? $"The ship package '{package}' is not in the game data."
            : null;

    /// <summary>
    /// Finds what <c>GameServer.NewCharacter</c> reads and does not find. The server would throw, or make a character
    /// without a ship.
    /// </summary>
    /// <param name="usesItsShip">True when the save keeps the ship of the new-character package: no ship package and no
    /// start save with a ship.</param>
    private static string? NewCharacterProblem(GameItemDb items, bool usesItsShip)
    {
        var data = items.Ini.NewCharDB;
        if (data.Factions.Count <= Faction)
            return "The new-character data has no faction.";
        var faction = data.Factions[Faction];
        if (!data.Pilots.Any(p => Same(p.Nickname, faction.Pilot)))
            return $"The new-character faction '{faction.Nickname}' names the pilot '{faction.Pilot}', which the " +
                   "new-character data does not have.";
        var package = NewCharacterPackage(data, faction);
        if (package == null)
            return $"The new-character faction '{faction.Nickname}' names the package '{faction.Package}', which the " +
                   "new-character data does not have.";
        if (items.Bases.Get(faction.Base) == null)
            return $"The new-character faction '{faction.Nickname}' names the base '{faction.Base}', which is not in " +
                   "the game data.";
        if (!items.Ini.Loadouts.Loadouts.Any(l => Same(l.Nickname, package.Loadout)))
            return $"The new-character package '{package.Nickname}' names the loadout '{package.Loadout}', which is " +
                   "not in the game data.";
        if (usesItsShip && items.Ships.Get(package.Ship) == null)
            return $"The new-character package '{package.Nickname}' names the ship '{package.Ship}', which is not in " +
                   "the game data.";
        return items.VFS.FileExists(TemplateFile) ? null : $"The game data has no {TemplateFile}.";
    }

    /// <summary>The package that a new-character faction names, or null.</summary>
    private static NewCharPackage? NewCharacterPackage(NewCharDBIni data, NewCharFaction faction) =>
        data.Packages.FirstOrDefault(p => Same(p.Nickname, faction.Package));

    private static bool Same(string a, string? b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}
