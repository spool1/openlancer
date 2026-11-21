using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a system object: the components strip, the completeness panel of a jump gate, a jump hole or a
/// trade lane ring, identity, transform, archetype and loadout, docking, the jump connection and the trade lane ring,
/// faction, behaviors, visit flags and appearance, and the lines that name the object. With the section of the object in
/// W, the rows edit its keys, and the page ends with the keys that no row shows, and the panel adds the raw INI block
/// (see <see cref="SectionExtras"/>).
/// </summary>
/// <remarks>In a bound scope, a row of a key that not every object has shows when the section has a line of the key
/// (see <see cref="PropertyGrid.Shows(bool, string[])"/>). Rows of the values of other files, for example the model
/// of the archetype, do not bind.</remarks>
internal sealed class SystemObjectPage(EditorWindow window, PropertyGrid grid)
{
    private static readonly string[] LaneKeys = ["next_ring", "prev_ring", "tradelane_space_name"];
    private static readonly string[] AppearanceKeys = ["atmosphere_range", "burn_color", "ambient_color", "ambient", "ring"];

    private readonly SectionExtras extras = new(window, grid);
    private readonly ComponentStrip strip = new(window, grid);
    private readonly ConnectionSections connections = new(window, grid);
    private readonly UsedBySection usedBy = new(window, grid);
    private readonly MissionUsesRows missionRows = new(window, grid);

    // The story mentions of the last object, with the edit view that they came from (decision D261 of the M3 design).
    private (string Nickname, object View, IReadOnlyList<Data.Editing.Scene.StoryMention> Mentions)? mentions;

    /// <param name="section">The section of the object in W, or null when the edit view has none.</param>
    public void Draw(SystemObject obj, SectionRef? section)
    {
        if (section != null)
        {
            strip.Draw(section);
            connections.Completeness(section);
        }

        using (grid.Bind(section))
        {
            Identity(obj);
            Transform(obj);
            ArchetypeAndLoadout(obj);
            var rows = DockingRows(grid.Layout, obj.Dock);
            Docking(obj, rows.Base);
            connections.Jump(obj, section, rows.Jump);
            connections.Lane(obj, section, rows.Lane);
            FactionAndReputation(obj);
            Behaviors(obj, section);
            Visit(obj);
            Appearance(obj);
        }

        if (section != null)
        {
            usedBy.Draw(section, "SystemObject");
            extras.Draw([section], "SystemObject", moreKeys: true);
        }
    }

    private void Identity(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        grid.Text("Nickname", obj.Nickname, "nickname");
        grid.Ids("Name", obj.IdsName, "ids_name");
        grid.Info("Info", obj.IdsInfo, "ids_info");
        grid.Text("Base", Named(obj.Base), "base");
        var parent = Object(obj.Parent);
        grid.Reference("Parent", parent == null ? ObjectKinds.OtherObject : ObjectKinds.Of(parent), obj.Parent,
            Name(parent), parent, "parent");
        if (grid.Shows(!string.IsNullOrEmpty(obj.Comment), "comment"))
        {
            grid.Text("Comment", obj.Comment, "comment");
        }

        grid.EndSection();
    }

    private void Transform(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Transform", EditorIcons.MapMarkerAlt, "Transform"))
        {
            return;
        }

        grid.Vector("Position", obj.Position, "pos");
        grid.Euler("Rotation", obj.Rotation, "rotate");
        grid.Vector("Spin", obj.Spin, "spin");
        grid.SetNextNote("The cell of the navmap grid. The game calculates it from the position and the navmap " +
                         "scale of the system.");
        grid.Text("Sector", window.Scene.Index?.System.WaypointSector(obj.Position));
        grid.EndSection();
    }

    private void ArchetypeAndLoadout(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Archetype", EditorIcons.Boxes, "Archetype and loadout"))
        {
            return;
        }

        var archetype = obj.Archetype;
        grid.Text("Archetype", archetype?.Nickname, "archetype");
        if (archetype != null)
        {
            // The values of the archetype in the solar archetype file.
            using (grid.Bind(null))
            {
                grid.Enum("Type", archetype.Type, "type");
                grid.Text("Model", archetype.ModelFile?.ModelFile, "DA_archetype");
                grid.Number("Solar radius", archetype.SolarRadius, iniKey: "solar_radius");
                grid.Text("Navmap icon", archetype.NavmapIcon, "shape_name");
            }
        }

        if (grid.Shows(obj.Star != null, "star"))
        {
            grid.SubHeading("Star");
            grid.Text("Star", obj.Star?.Nickname, "star");
            if (obj.Star is { } star)
            {
                using (grid.Bind(null))
                {
                    grid.Number("Radius", star.Radius, iniKey: "radius");
                }
            }
        }

        grid.SubHeading("Loadout");
        grid.Text("Loadout", obj.Loadout?.Nickname, "loadout");
        foreach (var item in obj.Loadout?.Items ?? [])
        {
            var equipment = item.Equipment;
            grid.Reference(item.Hardpoint ?? "Internal", ObjectKinds.Of(equipment), equipment.Nickname,
                Name(equipment), equipment);
        }

        grid.EndSection();
    }

    /// <summary>The base that the object docks with. The jump connection and the trade lane ring have their own
    /// sections (see <see cref="ConnectionSections"/>).</summary>
    /// <param name="show">True when the page shows the section (see <see cref="DockingRows"/>).</param>
    private void Docking(SystemObject obj, bool show)
    {
        if (!show || !grid.BeginSection("SystemObject.Docking", EditorIcons.LifeRing, "Docking"))
        {
            return;
        }

        var dock = obj.Dock;
        var items = window.Session?.GameData?.Items;
        grid.Text("Docks with", dock is { Kind: DockKinds.Base, Target: { } target }
            ? Named(items?.Bases.Get(target)) ?? target
            : null, "dock_with");
        grid.EndSection();
    }

    /// <summary>
    /// The sections of the docks of the object: the base of the docking section, the Jump connection section and the
    /// Trade lane ring section. Without a bound section, the dock of the game data decides. In a bound scope, only the
    /// lines of the keys decide (see <see cref="PropertyGrid.Shows(bool, string[])"/>), because an edit of
    /// <c>dock_with</c> or <c>goto</c> changes the dock of the scene item at once. A row would then come while the field
    /// of its key is open in "More keys", and that field would end.
    /// </summary>
    /// <param name="layout">The section that a bound page lays out its rows from, or null without a bound
    /// section.</param>
    internal static (bool Base, bool Jump, bool Lane) DockingRows(SectionView? layout, DockAction? dock) =>
    (
        PropertyGrid.Shows(layout, dock?.Kind == DockKinds.Base, "dock_with"),
        PropertyGrid.Shows(layout, dock?.Kind == DockKinds.Jump, "goto"),
        PropertyGrid.Shows(layout, dock?.Kind == DockKinds.Tradelane, LaneKeys)
    );

    private void FactionAndReputation(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Faction", EditorIcons.Users, "Faction and reputation"))
        {
            return;
        }

        grid.Text("Reputation", Named(obj.Reputation), "reputation");
        grid.Text("Voice", obj.Voice, "voice");
        grid.SetNextNote("The engine does not use this field. The editor keeps it so that the INI text stays the " +
                         "same.");
        grid.Text("Faction", Named(obj.Faction), "faction");
        grid.EndSection();
    }

    /// <summary>
    /// The behaviors of the object (decision D247 of the M3 design). They are its pilot, behavior and difficulty, its
    /// reputation, and the encounters of the zones that hold its position. They are also the mission lines that name it
    /// (section 10.18 of the M6a design), and the other files that name it as text. The rows of keys stay bound, so a
    /// raw edit shows at once. The other rows are read-only, and each one selects its item. A mission row opens the
    /// mission at its line, so the text rows leave out the mission scripts. Milestone M29 adds the cutscene scripts.
    /// </summary>
    private void Behaviors(SystemObject obj, SectionRef? section)
    {
        if (!grid.BeginSection("SystemObject.Behaviors", EditorIcons.Bolt, "Behaviors"))
        {
            return;
        }

        grid.Text("Pilot", obj.Pilot?.Nickname, "pilot");
        grid.Text("Behavior", obj.Behavior, "behavior");
        grid.Number("Difficulty level", obj.DifficultyLevel, iniKey: "difficulty_level");
        using (grid.Bind(null))
        {
            grid.Reference("Reputation", ObjectKinds.OfList("faction"), obj.Reputation?.Nickname, Name(obj.Reputation), null);
            foreach (var zone in window.Scene.System?.Zones.Where(z => z.Encounters is { Length: > 0 } && z.ContainsPoint(obj.Position)) ?? [])
            {
                foreach (var encounter in zone.Encounters!)
                {
                    var factions = encounter.FactionSpawns is { Count: > 0 } spawns
                        ? string.Join(", ", spawns.Select(f => f.Faction))
                        : "";
                    grid.Reference("Encounter", window.Scene.KindOf(zone) ?? ObjectKinds.Of(ZoneRole.Population),
                        $"{encounter.Archetype} in {zone.Nickname}", factions, zone);
                }
            }

            missionRows.Rows(missionRows.UsesOf(obj.Nickname));
            foreach (var mention in Mentions(section).Where(m => !window.Missions.IsScript(m.Path)))
            {
                grid.Text("Named in", $"{mention.Path} ({mention.Count} {(mention.Count == 1 ? "time" : "times")})");
            }
        }

        grid.EndSection();
    }

    /// <summary>The story mentions of the object, when the text scan is ready. The page asks in each frame, so it keeps
    /// the result until the object or the edit view changes.</summary>
    private IReadOnlyList<Data.Editing.Scene.StoryMention> Mentions(SectionRef? section)
    {
        if (section?.Id.Key is not { } nickname || window.Session is not { } session || !session.Edits.StoryMentionsReady)
        {
            return [];
        }

        var view = session.Edits.EditView;
        if (mentions is not { } known || known.Nickname != nickname || !ReferenceEquals(known.View, view))
        {
            mentions = (nickname, view, session.Edits.StoryMentions(nickname));
        }

        return mentions.Value.Mentions;
    }

    private void Visit(SystemObject obj)
    {
        if (!grid.BeginSection("SystemObject.Visit", EditorIcons.CheckCircle, "Visit flags"))
        {
            return;
        }

        grid.FlagChecks(obj.Visit, "visit");
        grid.EndSection();
    }

    /// <summary>
    /// The atmosphere, colors and ring of a planet or a sun. The section shows only when one of them is set. In a
    /// bound scope, it then shows each of them, so that the other values can be added.
    /// </summary>
    private void Appearance(SystemObject obj)
    {
        var hasAppearance = obj.AtmosphereRange != 0 || obj.BurnColor != null || obj.AmbientColor != null ||
                            obj.RingZone != null || obj.RingFile != null;
        if (!grid.Shows(hasAppearance, AppearanceKeys) ||
            !grid.BeginSection("SystemObject.Appearance", EditorIcons.Star, "Appearance"))
        {
            return;
        }

        var all = grid.IsBound;
        if (all || obj.AtmosphereRange != 0)
        {
            grid.Number("Atmosphere range", obj.AtmosphereRange, iniKey: "atmosphere_range");
        }

        if (all || obj.BurnColor != null)
        {
            grid.Color("Burn color", obj.BurnColor, "burn_color");
        }

        if (all || obj.AmbientColor != null)
        {
            // The row edits the alias that the section uses (Schema/Universe/SystemObject.cs:18-19).
            grid.Color("Ambient color", obj.AmbientColor, grid.KeyOrAlias("ambient_color", "ambient"));
        }

        if (all || obj.RingZone != null)
        {
            var ring = obj.RingZone == null ? null : window.Scene.Index?.System.ZoneDict.GetValueOrDefault(obj.RingZone);
            if (all)
            {
                grid.SetNextNote("The zone of the ring, and the file of the ring model.");
            }

            grid.Reference(all ? "Ring" : "Ring zone", window.Scene.KindOf(ring) ?? ObjectKinds.Of(ZoneRole.Other),
                obj.RingZone, Name(ring), ring, "ring");
        }

        if (!all && obj.RingFile != null)
        {
            grid.Text("Ring file", obj.RingFile, "ring");
        }

        grid.EndSection();
    }

    /// <summary>An object of the open system by nickname, or null.</summary>
    private SystemObject? Object(string? nickname) =>
        nickname == null ? null : window.Scene.Index?.ObjectsByNickname.GetValueOrDefault(nickname);

    /// <summary>The name of an item: from W for an item of the open system or a system (see
    /// <see cref="EditorSession.ItemName"/>), or null for no item.</summary>
    private string? Name(object? item) => item == null ? null : window.Session?.ItemName(item);

    /// <summary>"nickname (Name)", the nickname when the item has no name, or null for no item.</summary>
    private string? Named(NamedItem? item)
    {
        if (item == null)
        {
            return null;
        }

        var name = Name(item);
        return string.IsNullOrEmpty(name) ? item.Nickname : $"{item.Nickname} ({name})";
    }
}
