using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a star system: identity, its place in the universe, the environment and music, the counts of
/// its contents, and the advanced values. A count shows its items in the Hierarchy.
/// </summary>
/// <remarks>
/// The rows edit the sections of W that set the fields of the system: the <c>[system]</c> of the system in
/// <c>universe.ini</c>, and the <c>[SystemInfo]</c>, <c>[Ambient]</c>, <c>[Background]</c>, <c>[Dust]</c>,
/// <c>[Music]</c> and <c>[TexturePanels]</c> sections of its file. Each row binds to its own section. The rows of the
/// <c>[system]</c> are the rows of <see cref="SystemEntryRows"/>, which the page of a new system shows too. The contents
/// and the preloads are read-only. A section that the file does not have shows the values of the game data.
/// </remarks>
internal sealed class StarSystemPage(EditorWindow window, PropertyGrid grid)
{
    // The "t:" words of the Hierarchy search that the Contents section counts, in the order of the section.
    private static readonly (string Label, string Word)[] ContentTypes =
    [
        ("Suns", "sun"), ("Planets", "planet"), ("Stations", "station"), ("Docking rings", "ring"),
        ("Gates", "gate"), ("Jump holes", "hole"), ("Trade lane rings", "lane"), ("Satellites", "satellite"),
        ("Weapons platforms", "platform"), ("Zones", "zone"), ("Asteroid fields", "field"), ("Nebulae", "nebula"),
        ("Lights", "light"),
    ];

    private readonly SectionExtras extras = new(window, grid);
    private readonly SystemEntryRows entryRows = new(window, grid);

    // The counts of the last system. They are counted again only for a different system.
    private StarSystem? countedSystem;
    private Dictionary<string, int> counts = [];

    // The sections of the last system, for one version of the edit view.
    private (EditorSession? Session, int Version, string? System) sectionsVersion;
    private SystemSections sections = SystemSections.None;

    /// <summary>Forgets the counted system and the sections. Call it when the profile closes.</summary>
    public void Reset()
    {
        countedSystem = null;
        counts = [];
        sectionsVersion = default;
        sections = SystemSections.None;
    }

    public void Draw(StarSystem selected)
    {
        var loaded = window.Session?.GameData?.Items.Systems.Get(selected.Nickname) ?? selected;
        // The open system holds the edits of W. Another system shows the loaded data.
        var system = window.Scene.System is { } open &&
                     open.Nickname.Equals(selected.Nickname, StringComparison.OrdinalIgnoreCase)
            ? open
            : loaded;
        var parts = Sections(selected.Nickname);
        Identity(system, parts);
        Universe(system, parts);
        Environment(system, parts);
        Contents(loaded);
        Advanced(system, parts);
        if (parts.All.Count > 0)
        {
            extras.Draw(parts.All, "StarSystem", moreKeys: true);
        }
    }

    /// <summary>The sections of a system in W, for the raw block of the page.</summary>
    public IReadOnlyList<SectionRef> RawSections(StarSystem selected) => Sections(selected.Nickname).All;

    private void Identity(StarSystem system, SystemSections parts)
    {
        if (!grid.BeginSection("StarSystem.Identity", EditorIcons.Tag, "Identity"))
        {
            return;
        }

        entryRows.Identity(system.Nickname, parts.Universe, system);
        grid.EndSection();
    }

    private void Universe(StarSystem system, SystemSections parts)
    {
        if (!grid.BeginSection("StarSystem.Universe", EditorIcons.Globe, "Universe"))
        {
            return;
        }

        entryRows.NavMap(system.Nickname, parts.Universe, system);
        using (grid.Bind(parts.Of(SceneSection.SystemInfo)))
        {
            var faction = system.LocalFaction;
            grid.Text("Local faction", faction == null ? null
                : window.Session?.Name(faction.IdsName) is { Length: > 0 } name ? $"{faction.Nickname} ({name})"
                : faction.Nickname, "local_faction");
        }

        grid.EndSection();
    }

    private void Environment(StarSystem system, SystemSections parts)
    {
        if (!grid.BeginSection("StarSystem.Environment", EditorIcons.Cloud, "Environment and music"))
        {
            return;
        }

        using (grid.Bind(parts.Of(SceneSection.SystemInfo)))
        {
            grid.Color("Background color", system.BackgroundColor, "space_color");
            grid.Number("Far clip", system.FarClip, iniKey: "space_farclip");
        }

        using (grid.Bind(parts.Of(SceneSection.Ambient)))
        {
            grid.Color("Ambient color", system.AmbientColor.ToColor4(), "color");
        }

        using (grid.Bind(parts.Of(SceneSection.Dust)))
        {
            // The row edits the alias that the section uses (Schema/Universe/SystemDust.cs:8-9).
            grid.Text("Spacedust", system.Spacedust, grid.KeyOrAlias("spacedust", "dust"));
            grid.Number("Spacedust particles", system.SpacedustMaxParticles, iniKey: "spacedust_maxparticles");
        }

        using (grid.Bind(parts.Of(SceneSection.Music)))
        {
            grid.Text("Space music", system.MusicSpace, "space");
            grid.Text("Danger music", system.MusicDanger, "danger");
            grid.Text("Battle music", system.MusicBattle, "battle");
        }

        using (grid.Bind(parts.Of(SceneSection.Background)))
        {
            grid.Text("Basic stars", system.StarsBasic?.SourcePath, "basic_stars");
            grid.Text("Complex stars", system.StarsComplex?.SourcePath, "complex_stars");
            grid.Text("Nebulae", system.StarsNebula?.SourcePath, "nebulae");
        }

        grid.EndSection();
    }

    /// <summary>
    /// The number of items of each type. A click on a count shows the items in the Hierarchy. It opens the system
    /// first when the system is not open.
    /// </summary>
    /// <param name="system">The system of the loaded data.</param>
    private void Contents(StarSystem system)
    {
        if (!grid.BeginSection("StarSystem.Contents", EditorIcons.List, "Contents"))
        {
            return;
        }

        if (!ReferenceEquals(system, countedSystem))
        {
            counts = HierarchyModel.TypeCounts(system);
            countedSystem = system;
        }

        var isOpen = ReferenceEquals(window.Scene.Index?.Source, system);
        foreach (var (label, word) in ContentTypes)
        {
            var count = counts.GetValueOrDefault(word);
            if (count == 0)
            {
                continue;
            }

            var tooltip = isOpen
                ? $"Show them in the Hierarchy (t:{word})."
                : $"Open the system and show them in the Hierarchy (t:{word}).";
            if (grid.Link(label, count.ToString("N0", CultureInfo.InvariantCulture), tooltip))
            {
                if (!isOpen)
                {
                    window.OpenSystem(system);
                    window.Selection = system;
                }

                window.ShowInHierarchy($"t:{word}");
            }
        }

        grid.EndSection();
    }

    private void Advanced(StarSystem system, SystemSections parts)
    {
        if (!grid.BeginSection("StarSystem.Advanced", EditorIcons.Cog, "Advanced", false))
        {
            return;
        }

        entryRows.Advanced(system.Nickname, parts.Universe, system);
        using (grid.Bind(parts.Of(SceneSection.TexturePanels)))
        {
            grid.Text("Texture panels", Join(system.TexturePanelsFiles), "file");
        }

        grid.Text("Encounter parameters", Join(system.EncounterParameters.Select(e => e.Nickname)),
            "EncounterParameters");
        grid.Number("Resource files", system.ResourceFiles.Count);
        grid.Number("Preloads", system.Preloads?.Length ?? 0);
        grid.EndSection();
    }

    /// <summary>The sections of W that set the fields of a system, for the version of the edit view.</summary>
    private SystemSections Sections(string nickname)
    {
        if (window.Session is not { } session)
        {
            return SystemSections.None;
        }

        var version = (session, session.Version, nickname);
        if (sectionsVersion == version)
        {
            return sections;
        }

        var edits = session.Edits;
        var singletons = new Dictionary<SceneSection, SectionRef>();
        if (edits.SystemPath(nickname) is { } path)
        {
            foreach (var section in edits.Sections(path))
            {
                // The engine reads the last section of each of these types.
                if (SceneFields.SectionOf(section.Id.Type, universe: false) is { } kind && SystemSections.Singleton(kind))
                {
                    singletons[kind] = section;
                }
            }
        }

        sections = new SystemSections(edits.SystemSection(nickname), singletons);
        sectionsVersion = version;
        return sections;
    }

    private static string? Join(IEnumerable<string> values) =>
        values.ToArray() is { Length: > 0 } array ? string.Join(", ", array) : null;

    /// <summary>The sections of W that set the fields of a system.</summary>
    /// <param name="Universe">The <c>[system]</c> of the system in <c>universe.ini</c>, or null.</param>
    /// <param name="Singletons">The section of each type that the system file has, for the types that a file has at
    /// most one of.</param>
    private sealed record SystemSections(SectionRef? Universe, IReadOnlyDictionary<SceneSection, SectionRef> Singletons)
    {
        public static readonly SystemSections None = new(null, new Dictionary<SceneSection, SectionRef>());

        /// <summary>All the sections: the <c>[system]</c> first, then the sections of the file.</summary>
        public IReadOnlyList<SectionRef> All { get; } =
            (Universe == null ? [] : new[] { Universe }).Concat(Singletons.Values).ToList();

        /// <summary>True for a section type of a system file that sets the fields of the system, and that a file has
        /// at most one of.</summary>
        public static bool Singleton(SceneSection kind) => kind is SceneSection.SystemInfo or SceneSection.Ambient or
            SceneSection.Background or SceneSection.Dust or SceneSection.Music or SceneSection.TexturePanels;

        /// <summary>The section of a type, or null when the file has none.</summary>
        public SectionRef? Of(SceneSection kind) => Singletons.GetValueOrDefault(kind);
    }
}
