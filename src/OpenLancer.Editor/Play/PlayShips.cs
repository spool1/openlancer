using System;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Editor.Play;

/// <summary>
/// The ship packages that Play can give the player (decision D15): the <c>[Good]</c> sections of W with
/// <c>category = ship</c>. The Goods table of the Database and the Play menu read them here, so they show the same
/// packages (section 11.2 of the M4 design). It also makes the ship part of a start save from a package.
/// </summary>
internal static class PlayShips
{
    /// <summary>True when a section of W is a ship package: a <c>[Good]</c> with <c>category = ship</c>.</summary>
    public static bool IsPackage(SectionRef section, SectionView view) =>
        section.Id.Type.Equals("Good", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(First(view, "category"), "ship", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ship package of W with a nickname, or null when W has no such package.</summary>
    public static SectionRef? Find(EditSession edits, string package) =>
        edits.Find(ItemFileKind.Goods, package) is { } section && edits.Get(section) is { } view &&
        IsPackage(section, view)
            ? section
            : null;

    /// <summary>
    /// The ship part of a start save for a ship package of W: the <c>[Ship]</c> of the package, and the lines that its
    /// <c>addon</c> lines give by the addon rule (<see cref="StartSaveShip.FromAddons"/>). The builder reads the addon
    /// lines as the engine reads them when it loads the package. A line needs the equipment and the hardpoint, and the
    /// amount is 1 without a third value. An addon whose equipment W does not have gives no line, because the engine
    /// drops it too. So the ship part gives the same ship as the package. When sections share the nickname of the hull
    /// good or of the ship, the builder uses the last one, as the engine does (see <see cref="ItemRelation.Ship"/>).
    /// </summary>
    /// <param name="package">The nickname of the package.</param>
    /// <param name="problem">Null, or the reason when W has no such package, more than one good with the nickname, or
    /// no ship of the package.</param>
    /// <returns>The ship part, or null when <paramref name="problem"/> gives a reason.</returns>
    public static StartSaveShip? ShipPart(EditSession edits, string package, out string? problem)
    {
        if (Find(edits, package) is not { } section)
        {
            // Find gives null also when more than one good has the nickname. The engine stops when it loads two ship
            // packages of one nickname, so there is no package to choose.
            problem = edits.Find(ItemFileKind.Goods, package) == null && edits.Has(ItemFileKind.Goods, package)
                ? $"More than one section of the goods files has the nickname '{package}'. Play cannot give the player " +
                  "this ship."
                : $"The edit view has no ship package '{package}'. Play cannot give the player this ship.";
            return null;
        }

        if (edits.Related(section, ItemRelation.Ship) is not [{ Id.Key: { } ship }])
        {
            problem = $"The edit view has no ship of the ship package '{package}'. Play cannot give the player this ship.";
            return null;
        }

        problem = null;
        var addons = edits.Get(section)!.Lines("addon")
            .Where(l => l.Values.Count >= 2 && edits.Has(ItemFileKind.Equipment, l.Values[0].ToString()))
            .Select(l => (l.Values[0].ToString(), (string?)l.Values[1].ToString(),
                l.Values.Count > 2 ? l.Values[2].ToInt32() : 1));
        return StartSaveShip.FromAddons(ship, addons);
    }

    /// <summary>
    /// The name of the ship that a play gives, for the Play menu and the Console: the name of the <c>[Ship]</c> section,
    /// or its nickname when it has no name. Without a package, it is the ship of a new multiplayer character (see
    /// <see cref="PlaySave.DefaultShip"/>). Null when W has no such package, or when the game data has no ship of a new
    /// character.
    /// </summary>
    /// <param name="package">The nickname of the package, or null for the default package.</param>
    public static string? ShipName(EditorSession session, string? package)
    {
        var edits = session.Edits;
        string? nickname;
        SectionRef? ship;
        if (package == null)
        {
            nickname = session.GameData is { } data ? PlaySave.DefaultShip(data.Items) : null;
            ship = nickname == null ? null : edits.Find(ItemFileKind.Ships, nickname);
        }
        else
        {
            if (Find(edits, package) is not { } section)
            {
                return null;
            }

            ship = edits.Related(section, ItemRelation.Ship) is [var one] ? one : null;
            nickname = ship?.Id.Key ?? package;
        }

        return ship != null && session.SectionName(ship) is { Length: > 0 } name ? name : nickname;
    }

    /// <summary>
    /// The ship of Play in words, with its tooltip, for the Play menu and the Play start page: "{ship} (default)" without
    /// a package, the ship of the package, or "{package} (not in the data)" with the reason when W has no such
    /// package.
    /// </summary>
    /// <param name="package">The nickname of the package, or null for the default package.</param>
    public static (string Text, string Tooltip) ShipText(EditorSession? session, string? package)
    {
        var name = session == null ? null : ShipName(session, package);
        if (package == null)
        {
            return ($"{name ?? "the ship of a new character"} (default)",
                "The ship of a new multiplayer character. To fly another ship, right-click a ship package in " +
                "Database > Goods, then click Use for Play.");
        }

        return name != null
            ? (name, $"The ship package {package}. Select another one in Database > Goods.")
            : ($"{package} (not in the data)",
                $"The edit view has no ship package {package}, so Play stops with an error. Click Use the default ship " +
                "in the Play menu, or select a ship package in Database > Goods.");
    }

    /// <summary>The first value of the first line of a key, as text, or null. The item index of W reads the category of
    /// a good in the same way.</summary>
    private static string? First(SectionView view, string key) =>
        view.Lines(key) is [{ Values: [var value, ..] }, ..] ? value.ToString() : null;
}
