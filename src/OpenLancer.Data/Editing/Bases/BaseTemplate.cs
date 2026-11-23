using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>One room of a template base.</summary>
/// <param name="View">The room as the view of the base gives it, with its doors.</param>
/// <param name="Section">The <c>[Room]</c> of the base file of the template, as W has it. Its <c>file</c> line names
/// the room file.</param>
internal sealed record TemplateRoom(BaseRoomView View, Section Section)
{
    /// <summary>The nickname of the room.</summary>
    public string Nickname => View.Nickname;
}

/// <summary>One fixture of a room of a template base: a character at a fixed place, for example the bartender.</summary>
/// <param name="Room">The nickname of the <c>[MRoom]</c> of the fixture.</param>
/// <param name="Character">The nickname of the <c>[GF_NPC]</c> that the fixture names.</param>
/// <param name="Role">The role of the fixture, for example <c>bartender</c>, or null.</param>
internal sealed record TemplateFixture(string Room, string Character, string? Role);

/// <summary>One character of a template base: a <c>[GF_NPC]</c> of its <c>[MBase]</c>.</summary>
/// <param name="Ref">The section in W of <c>mBases.ini</c>.</param>
/// <param name="Section">The section as W has it.</param>
/// <param name="Nickname">The nickname of the character.</param>
/// <param name="Room">The room of its <c>room</c> line, or null. A character of the bar crowd has one.</param>
internal sealed record TemplateCharacter(SectionRef Ref, Section Section, string Nickname, string? Room);

/// <summary>
/// A base of the edit view that a new base copies (section C.5 of the bases design). The template gives the rooms with
/// their files, the start room and the kind, and the fixtures and the bar crowd. It also gives the <c>[MBase]</c>
/// values, the run-by and terrain lines of the <c>[Base]</c>, the market lines, the dock objects and the news items.
/// Each part comes from W, so an unsaved edit counts.
/// </summary>
/// <remarks>
/// <para>A template needs a base file with at least one room. An intro base is not a template, because the OpenLancer
/// engine shows its rooms in the main menu. A template without <c>[MBase]</c> gives a new base no characters, and the
/// template warns about it.</para>
/// <para>The template is a snapshot of W. Read it again after an edit.</para>
/// </remarks>
internal sealed class BaseTemplate
{
    /// <summary>The nickname prefix of an intro base, in any letter case (GameItemDb.cs:261).</summary>
    internal const string IntroPrefix = "intro";

    /// <summary>The lines of a dock object of a station that a new station copies (section C.5 of the bases design). The
    /// other lines of a retail station name its place, its archetype, its base and its texts.</summary>
    internal static readonly string[] StationLines =
        ["loadout", "reputation", "voice", "space_costume", "pilot", "behavior", "difficulty_level"];

    private BaseTemplate(BaseView view, Section entry)
    {
        View = view;
        Entry = entry;
    }

    /// <summary>The view of the template base.</summary>
    public BaseView View { get; }

    /// <summary>The nickname of the template, as its <c>[Base]</c> spells it.</summary>
    public string Nickname => View.Nickname;

    /// <summary>The <c>[Base]</c> of <c>universe.ini</c>, as W has it.</summary>
    public Section Entry { get; }

    /// <summary>The <c>[BaseInfo]</c> of the base file, as W has it, or null.</summary>
    public Section? Info { get; private init; }

    /// <summary>The rooms of the base file with a nickname, in file order.</summary>
    public IReadOnlyList<TemplateRoom> Rooms { get; private init; } = [];

    /// <summary>The <c>[MBase]</c>, as W has it, or null.</summary>
    public Section? MBase { get; private init; }

    /// <summary>The <c>[MVendor]</c> of the <c>[MBase]</c>, or null.</summary>
    public Section? Vendor { get; private init; }

    /// <summary>The <c>[BaseFaction]</c> sections of the <c>[MBase]</c>, in file order.</summary>
    public IReadOnlyList<Section> Factions { get; private init; } = [];

    /// <summary>The characters of the <c>[MBase]</c>, in file order.</summary>
    public IReadOnlyList<TemplateCharacter> Characters { get; private init; } = [];

    /// <summary>The <c>[MRoom]</c> sections of the <c>[MBase]</c>, in file order.</summary>
    public IReadOnlyList<Section> RoomParts { get; private init; } = [];

    /// <summary>The fixtures of the <c>[MRoom]</c> sections, in file order.</summary>
    public IReadOnlyList<TemplateFixture> Fixtures { get; private init; } = [];

    /// <summary>The <c>[NewsItem]</c> sections of <c>news.ini</c> whose <c>base</c> lines name the template, in file
    /// order.</summary>
    public IReadOnlyList<Section> News { get; private init; } = [];

    /// <summary>The object that the games link to the base, or null when no object names the base. It is the named
    /// object, else the first object with the <c>base</c> line, else the first object that docks with the base. For a
    /// station, it is the station. For a planet, it is the planet.</summary>
    public SectionRef? MainObject { get; private init; }

    /// <summary>The docking ring of a planet: the first object that docks with the base when the main object does not
    /// dock, or null. Retail planets have no <c>dock_with</c> line, and their rings have no <c>base</c> line.</summary>
    public SectionRef? Ring { get; private init; }

    /// <summary>The warnings of the template, for example a template without <c>[MBase]</c>.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; private init; } = [];

    /// <summary>The market lines of the template over all market files, in load order.</summary>
    public IReadOnlyList<BaseMarketRow> Market => View.Market;

    /// <summary>
    /// Reads a template base from W, or gives why the base is not a template.
    /// </summary>
    /// <param name="refusal">Gets the reason and the detail code when the result is null. The detail code is
    /// <see cref="Missions.Editing.MissionErrors.ItemNotFound"/> when the edit view has no such base, and
    /// <see cref="BasePlans.IntroTemplate"/> for an intro base. It is <see cref="BasePlans.NoBaseFile"/> for a base
    /// without a base file with a room.</param>
    public static BaseTemplate? Of(EditSession session, string nickname, out (string Message, string Detail)? refusal)
    {
        refusal = null;
        if (session.Base(nickname.Trim()) is not { } view || session.WorkingSection(view.Entry) is not { } entry)
        {
            refusal = ($"No [Base] of universe.ini has the nickname {nickname.Trim()}. Get the bases from list_values " +
                       "with the list base.", Missions.Editing.MissionErrors.ItemNotFound);
            return null;
        }
        if (view.Nickname.StartsWith(IntroPrefix, StringComparison.OrdinalIgnoreCase))
        {
            refusal = ($"The base {view.Nickname} is a scene of the main menu. Pick another template.",
                BasePlans.IntroTemplate);
            return null;
        }
        var rooms = view.Rooms
            .Where(r => r.Nickname.Length > 0)
            .Select(r => session.WorkingSection(r.Section) is { } room ? new TemplateRoom(r, room) : null)
            .OfType<TemplateRoom>()
            .ToList();
        if (view.File == null || view.Info == null || rooms.Count == 0)
        {
            refusal = ($"The base {view.Nickname} has no base file with rooms. Pick a template with rooms.",
                BasePlans.NoBaseFile);
            return null;
        }

        var refs = view.MBaseParts.Select(p => (Ref: p, Section: session.WorkingSection(p)))
            .Where(p => p.Section != null)
            .ToList();
        var parts = refs.Select(p => p.Section!).ToList();
        var roomParts = parts.Where(p => Is(p.Name, "MRoom")).ToList();
        var fixtures = roomParts
            .SelectMany(r => r.Where(e => Is(e.Name, "fixture") && e.Count > 0)
                .Select(e => new TemplateFixture(Last(r, "nickname") ?? "", e[0].ToString().Trim(),
                    e.Count > 3 ? e[3].ToString().Trim() : null)))
            .ToList();
        var characters = refs.Where(p => Is(p.Section!.Name, "GF_NPC") && Last(p.Section, "nickname") != null)
            .Select(p => new TemplateCharacter(p.Ref, p.Section!, Last(p.Section!, "nickname")!, Last(p.Section!, "room")))
            .ToList();
        var (main, ring) = DockObjectsOf(view);
        return new BaseTemplate(view, entry)
        {
            Info = session.WorkingSection(view.Info),
            Rooms = rooms,
            MBase = view.MBase is { } mbase ? session.WorkingSection(mbase) : null,
            Vendor = parts.FirstOrDefault(p => Is(p.Name, "MVendor")),
            Factions = parts.Where(p => Is(p.Name, "BaseFaction")).ToList(),
            Characters = characters,
            RoomParts = roomParts,
            Fixtures = fixtures,
            News = NewsOf(session, view.Nickname),
            MainObject = main,
            Ring = ring,
            Warnings = view.MBase == null
                ? [Diagnostic.Warning(view.Entry.Path, $"The template {view.Nickname} has no [MBase], so the new base has " +
                                                       "no bar characters and no dealers.") with { Target = view.Entry }]
                : [],
        };
    }

    /// <summary>The room of the template with a nickname, in any letter case, or null.</summary>
    public TemplateRoom? Room(string nickname) => Rooms.FirstOrDefault(r => Is(r.Nickname, nickname.Trim()));

    /// <summary>The main object and the docking ring of a template (see <see cref="MainObject"/> and
    /// <see cref="Ring"/>).</summary>
    private static (SectionRef? Main, SectionRef? Ring) DockObjectsOf(BaseView view)
    {
        var main = view.NamedObject ?? view.DockObjects.FirstOrDefault(d => d.HasBaseLine)?.Section ??
                   view.DockObjects.FirstOrDefault(d => d.DocksWith)?.Section;
        if (main == null || view.DockObjects.Any(d => d.Section.Equals(main) && d.DocksWith))
            return (main, null);
        return (main, view.DockObjects.FirstOrDefault(d => d.DocksWith && !d.Section.Equals(main))?.Section);
    }

    /// <summary>The <c>[NewsItem]</c> sections of W of <c>news.ini</c> with a <c>base</c> line of the base, in file
    /// order.</summary>
    private static IReadOnlyList<Section> NewsOf(EditSession session, string baseNickname)
    {
        if (session.Bases.NewsPath is not { } path)
            return [];
        return session.Sections(path)
            .Where(s => Is(s.Id.Type, NewsType))
            .Select(s => session.WorkingSection(s))
            .OfType<Section>()
            .Where(s => s.Any(e => Is(e.Name, "base") && e.Count > 0 && Is(e[0].ToString().Trim(), baseNickname)))
            .ToList();
    }

    /// <summary>The section type of a news item of <c>news.ini</c>.</summary>
    internal const string NewsType = "NewsItem";

    /// <summary>The first value of the last line of a key with a value, trimmed, or null.</summary>
    internal static string? Last(Section section, string key) =>
        section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0)?[0].ToString().Trim() is { Length: > 0 } value
            ? value
            : null;

    private static bool Is(string? a, string? b) => SceneSections.Is(a ?? "", b ?? "");
}
