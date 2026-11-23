using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The result of <c>base_create</c>: the result of the edit, with the new base, its base file and its dock
/// objects. A step of <c>edit_batch</c> gives the result of the edit only.</summary>
internal sealed record BaseEditResult : EditResult
{
    public BaseEditResult(EditResult result) : base(result)
    {
    }

    /// <summary>The <c>base:</c> reference of the new base.</summary>
    public string? Base { get; init; }

    /// <summary>The install path of the new base file.</summary>
    public string? File { get; init; }

    /// <summary>The <c>obj:</c> reference of the object that the games link to the base: the new station, or the planet.
    /// Null without a dock object.</summary>
    public string? DockObject { get; init; }

    /// <summary>The <c>obj:</c> reference of the new docking ring of a planet base, or null.</summary>
    public string? Ring { get; init; }
}

/// <summary>A base of the edit view (tool <c>list_bases</c>).</summary>
/// <param name="Ref">The <c>base:</c> reference.</param>
/// <param name="System">The <c>system:</c> reference of the system of the base, or null.</param>
/// <param name="Rooms">The nicknames of the rooms of the base file.</param>
/// <param name="Characters">The number of characters of the bar data of the base.</param>
/// <param name="Goods">The number of goods that the market of the base lists: the lines that the games keep.</param>
/// <param name="DockObjects">The number of objects whose <c>dock_with</c> line names the base.</param>
internal sealed record BaseRow(string Ref, string Nickname, string Name, string? System, BaseKind Kind,
    IReadOnlyList<string> Rooms, int Characters, int Goods, int DockObjects, ItemOrigin Origin);

/// <summary>A door of a room (tool <c>get_base</c>).</summary>
/// <param name="Name">The name of the hotspot, for example <c>IDS_HOTSPOT_BAR</c>.</param>
/// <param name="To">The room that the door opens, or null for a door without a <c>room_switch</c> line.</param>
/// <param name="SetVirtualRoom">The virtual room that the door sets, or null.</param>
internal sealed record BaseDoorRow(string Name, string? To, string? SetVirtualRoom);

/// <summary>A room of a base (tool <c>get_base</c>).</summary>
/// <param name="Ref">The reference of the <c>[Room]</c> of the base file.</param>
/// <param name="File">The install path of the room file, or null.</param>
/// <param name="Shared">True when another base uses the room file too.</param>
/// <param name="Own">True when the active mod adds the room file in its own folder.</param>
/// <param name="Scripts">The install paths of the cutscene scripts that the game plays in the room.</param>
internal sealed record BaseRoomRow(string Ref, string Nickname, string? File, bool Shared, bool Own,
    IReadOnlyList<BaseDealer> Dealers, IReadOnlyList<string> VirtualRooms, int ShipPlaces, IReadOnlyList<BaseDoorRow> Doors,
    IReadOnlyList<string> Scripts);

/// <summary>A character of the bar data of a base (tool <c>get_base</c>).</summary>
/// <param name="Ref">The reference of the <c>[GF_NPC]</c>.</param>
/// <param name="Role">The role of the fixture of the character, for example <c>bartender</c>, or null for the
/// crowd.</param>
/// <param name="Name">The text of the name of the character, or an empty text.</param>
internal sealed record BaseCharacterRow(string Ref, string Nickname, string? Role, string? Room, string? Faction,
    string? Affiliation, string? Voice, string Name);

/// <summary>A market line of a base (tool <c>get_base</c>).</summary>
/// <param name="Kept">True for the line that the games keep: the last line of the good that does not hold only default
/// values.</param>
/// <param name="CanChange">True when an edit can change the line. A line of a lower layer changes through a later line of
/// the active mod.</param>
/// <param name="File">The install path of the market file of the line.</param>
internal sealed record BaseGoodRow(string Good, string? Category, MarketTransaction? Transaction, float? PriceMultiplier,
    int? MinStock, int? MaxStock, bool Kept, bool CanChange, string File, ItemOrigin Origin);

/// <summary>An object that names a base (tool <c>get_base</c>).</summary>
/// <param name="Ref">The <c>obj:</c> reference.</param>
/// <param name="DocksWith">True when its <c>dock_with</c> line names the base.</param>
/// <param name="HasBaseLine">True when its <c>base</c> line names the base.</param>
internal sealed record BaseDockRow(string Ref, bool DocksWith, bool HasBaseLine);

/// <summary>A <c>terrain_*</c> line of a base (tool <c>get_base</c>).</summary>
/// <param name="Archetype">The solar archetype that the room scenes show for the template of the key.</param>
internal sealed record BaseTerrainRow(string Key, string Archetype);

/// <summary>A step of the completeness of a base (tool <c>get_base</c>).</summary>
/// <param name="Detail">What is missing: the message of the first problem of the step, or null.</param>
/// <param name="GoTo">The reference of the item of the step, or null.</param>
/// <param name="Fix">What the fix of a missing step adds, or null.</param>
internal sealed record BaseStepRow(string Name, CompletenessState State, string Reason, string? Detail, string? GoTo,
    string? Fix, bool IsError);

/// <summary>The completeness of a base (tool <c>get_base</c>).</summary>
internal sealed record BaseCompletenessView(int Done, int Total, IReadOnlyList<BaseStepRow> Steps);

/// <summary>A base with its parts, its completeness and its problems (tool <c>get_base</c>).</summary>
internal sealed record BaseDetailView(string Ref, string Nickname, string Name)
{
    public string? System { get; init; }

    public string? File { get; init; }

    /// <summary>The reference of the <c>[BaseInfo]</c> of the base file, or null.</summary>
    public string? Info { get; init; }

    public string? StartRoom { get; init; }

    public BaseKind Kind { get; init; }

    public IReadOnlyList<BaseRoomRow> Rooms { get; init; } = [];

    /// <summary>The reference of the <c>[MBase]</c>, the bar data of the base, or null.</summary>
    public string? BarData { get; init; }

    public string? LocalFaction { get; init; }

    public float? Difficulty { get; init; }

    public IReadOnlyList<BaseCharacterRow> Characters { get; init; } = [];

    public IReadOnlyList<BaseGoodRow> Market { get; init; } = [];

    public IReadOnlyList<BaseDockRow> DockObjects { get; init; } = [];

    public string? NamedObject { get; init; }

    public IReadOnlyList<BaseTerrainRow> Terrain { get; init; } = [];

    public required ItemOrigin Origin { get; init; }

    public required BaseCompletenessView Completeness { get; init; }

    public IReadOnlyList<Problem> Problems { get; init; } = [];
}

/// <summary>A room of a template base (resource <c>openlancer://catalog/base-templates</c>).</summary>
internal sealed record TemplateRoomRow(string Nickname, IReadOnlyList<BaseDealer> Dealers, IReadOnlyList<string> VirtualRooms,
    int ShipPlaces);

/// <summary>A base that <c>base_create</c> takes as a template (resource
/// <c>openlancer://catalog/base-templates</c>).</summary>
/// <param name="Terrain">The keys of the <c>terrain_*</c> lines of the base.</param>
/// <param name="Goods">The number of goods that the market lists, by category.</param>
/// <param name="DockArchetype">The archetype of the object that the base links to, or of the first object that docks
/// with it, or null.</param>
internal sealed record BaseTemplateRow(string Base, string Name, string? System, BaseKind Kind, string? StartRoom,
    IReadOnlyList<TemplateRoomRow> Rooms, IReadOnlyList<string> Terrain, int Characters,
    IReadOnlyDictionary<string, int> Goods, string? DockArchetype);

/// <summary>The resource <c>openlancer://catalog/base-templates</c>.</summary>
internal sealed record BaseTemplatesView(IReadOnlyList<BaseTemplateRow> Templates);

/// <summary>
/// The tools of the bases (section E of the bases design): a new base from a template base, the read of one base and
/// the list of the bases. <c>base_create</c> calls the plan of the bases (see <see cref="BasePlans.Create"/>) in the
/// shared edit call of <see cref="EditTools"/>, so each call is one Undo step. The read tools read the base view of W
/// (see <see cref="BaseView"/>), so a new base shows before the save.
/// </summary>
/// <remarks>A refusal of the plan keeps the text of the plan, so the editor and the agent read one text. The tool gives
/// it the error kind of its detail code and the argument that the plan names (see
/// <see cref="BasePlan.RefusedPart"/>).</remarks>
internal static class BaseTools
{
    /// <summary>The detail code of a copy of a base with <c>duplicate_items</c>.</summary>
    public const string BaseCopy = "BaseCopy";

    /// <summary>The words of <c>characters</c> of <c>base_create</c>.</summary>
    public static IReadOnlyList<string> CharacterCopies { get; } = AgentTools.Names<CharacterCopy>();

    /// <summary>The words of <c>kind</c> of <c>list_bases</c>.</summary>
    public static IReadOnlyList<string> Kinds { get; } = AgentTools.Names<BaseKind>();

    /// <summary>The value of <c>market</c> that copies the market of the template.</summary>
    public const string MarketTemplate = "template";

    /// <summary>The value of <c>market</c> that gives the base no market lines.</summary>
    public const string MarketNone = "none";

    /// <summary>The most characters of the name of a base.</summary>
    public const int MaxName = 64;

    /// <summary>The most characters of the infocard of the dock object of a base.</summary>
    public const int MaxInfo = 2000;

    /// <summary>The nickname prefix of an intro base. The OpenLancer engine shows its rooms in the main menu, so it is
    /// not in the list of the bases and it is no template.</summary>
    private const string IntroPrefix = "intro";

    /// <summary>The error kind of each refusal of <see cref="BasePlans.Create"/>, by the detail code of the refusal.
    /// The plan names the argument (see <see cref="BasePlan.RefusedPart"/>).</summary>
    private static readonly Dictionary<string, EditErrorKind> Refusals = new()
    {
        [MissionErrors.NameInvalid] = EditErrorKind.InvalidValue,
        [MissionErrors.NameTaken] = EditErrorKind.NameTaken,
        [MissionErrors.ItemNotFound] = EditErrorKind.NotFound,
        [MissionErrors.FieldValue] = EditErrorKind.InvalidValue,
        [BasePlans.IntroTemplate] = EditErrorKind.Refused,
        [BasePlans.NoBaseFile] = EditErrorKind.Refused,
        [BasePlans.NoSystemFile] = EditErrorKind.Refused,
        [BasePlans.RoomUnknown] = EditErrorKind.InvalidValue,
        [BasePlans.StartRoomNotKept] = EditErrorKind.InvalidValue,
        [BasePlans.DockArchetype] = EditErrorKind.InvalidValue,
        [BasePlans.NotAPlanet] = EditErrorKind.InvalidValue,
        [BasePlans.DockPlace] = EditErrorKind.InvalidArgument,
    };

    /// <summary>
    /// Makes a new base of the active mod from a template base (see <see cref="BasePlans.Create"/>): its universe entry,
    /// its base file, its bar data, its news, its market, its name and the object that docks with it. The result gives
    /// the new base, its base file and its dock objects.
    /// </summary>
    public static ToolStep Create(ToolCall call)
    {
        Func<EditResult, EditResult> finish = result => result;
        return EditTools.Apply(call, scope =>
        {
            var session = scope.Session;
            var args = call.Args;
            var system = EditTools.At("system", () => SceneQueries.System(session, args.Text("system")!)).Nickname;
            var template = EditTools.At("template", () => Nickname(session, args.Text("template")!, "template"));
            var market = args.Text("market")!.Trim();
            var marketFrom = market.Equals(MarketTemplate, StringComparison.OrdinalIgnoreCase) ||
                             market.Equals(MarketNone, StringComparison.OrdinalIgnoreCase)
                ? null
                : EditTools.At("market", () => Nickname(session, market, "market"));
            var values = new RowValues(session);
            var faction = UniverseTools.Listed(values, args.Text("local_faction"), "local_faction", "faction");
            values.ThrowIfAny();
            var spec = new BaseSpec(args.Text("nickname")!, system, template, args.Text("name")!)
            {
                Info = args.Text("info"),
                Rooms = args.Texts("rooms"),
                StartRoom = args.Text("start_room"),
                Characters = Enum.Parse<CharacterCopy>(args.Text("characters")!, ignoreCase: true),
                LocalFaction = faction,
                Dock = Dock(args.Group("dock_object")),
                MarketFrom = marketFrom,
                NoMarket = market.Equals(MarketNone, StringComparison.OrdinalIgnoreCase),
            };
            var plan = BaseCommands.Create(session, spec, call.Texts);
            UniverseTools.Run(scope, plan,
                error => (Refusals.GetValueOrDefault(error.Detail ?? "", error.Kind), plan.RefusedPart));
            // The references take the form that they have while the new items exist, also in a preview.
            var made = ItemRefs.Canonical(session, plan.Base!);
            var dock = plan.DockObject is { } docked ? ItemRefs.Canonical(session, docked) : null;
            var ring = plan.Ring is { } added ? ItemRefs.Canonical(session, added) : null;
            finish = result => new BaseEditResult(result) { Base = made, File = plan.File, DockObject = dock, Ring = ring };
        }, result => finish(result));
    }

    /// <summary>
    /// Gives one base of W with its parts: the entry, the base file, the rooms, the bar data, the market lines of all
    /// market files, the dock objects and the terrain lines. It also gives the completeness and the problems of the base
    /// checks (see <see cref="BaseCompleteness"/> and <see cref="BaseChecks"/>).
    /// </summary>
    public static ToolStep GetBase(ToolCall call)
    {
        var session = call.Session;
        var nickname = EditTools.At("base", () => Nickname(session, call.Args.Text("base")!, "base"));
        var view = session.Base(nickname)!;
        string Ref(SectionRef section) => ItemRefs.Canonical(session, section);
        var mbase = view.MBase is { } found ? session.Get(found) : null;
        var kept = BaseMarkets.KeptLines(view.Market, r => r.Good, r => r.Line.Values).ToHashSet();
        var report = BaseCompleteness.Of(session, nickname)!;
        return ToolResult.Of(new BaseDetailView(Ref(view.Entry), view.Nickname, call.Texts.TextOf(view.Entry, "strid_name"))
        {
            System = view.System is { } system ? SceneQueries.SystemRef(system) : null,
            File = view.File,
            Info = view.Info is { } info ? Ref(info) : null,
            StartRoom = view.StartRoom,
            Kind = view.Kind,
            Rooms = view.Rooms.Select(r => new BaseRoomRow(Ref(r.Section), r.Nickname, r.File, r.Shared, r.Own, r.Dealers,
                r.VirtualRooms, r.ShipPlaces, r.Doors.Select(d => new BaseDoorRow(d.Name, d.Target, d.SetVirtualRoom)).ToList(),
                r.File == null ? [] : ThnReferences.RoomScripts(session.WorkingSections(r.File) ?? []))).ToList(),
            BarData = view.MBase is { } section ? Ref(section) : null,
            LocalFaction = mbase == null ? null : SceneSections.Last(mbase, "local_faction"),
            Difficulty = mbase != null && mbase.Lines("diff") is [.., { Values: [var diff, ..] }] &&
                         diff.TryToSingle(out var value)
                ? value
                : null,
            Characters = view.Characters.Select(c => new BaseCharacterRow(Ref(c.Section), c.Nickname, c.Role, c.Room, c.Faction,
                c.Affiliation, c.Voice, call.Texts.TextOf(c.Section, "individual_name"))).ToList(),
            Market = view.Market.Select(r => new BaseGoodRow(r.Good, MarketPlans.Category(session, r.Good),
                r.Line.Transaction, Number(r.Line, MarketLine.PriceIndex), Whole(r.Line, MarketLine.MinimumIndex),
                Whole(r.Line, MarketLine.MaximumIndex), kept.Contains(r), r.CanChange, r.Line.Section.Path,
                new ItemOrigin(r.Origin, r.ModId, session.IsUnsaved(r.Line.Section)))).ToList(),
            DockObjects = view.DockObjects.Select(d => new BaseDockRow(Ref(d.Section), d.DocksWith, d.HasBaseLine)).ToList(),
            NamedObject = view.NamedObject is { } named ? Ref(named) : null,
            Terrain = view.Terrain.Select(t => new BaseTerrainRow(t.Key, t.Value)).ToList(),
            Origin = AgentRows.OriginOf(session, view.Entry, session.Get(view.Entry)!, "nickname"),
            Completeness = new BaseCompletenessView(report.Done, report.Total,
                report.Steps.Select(s => Step(session, view.Nickname, s)).ToList()),
            Problems = BaseChecks.Problems(session, view.Nickname).Select(p => AgentRows.ProblemOf(session, p)).ToList(),
        });
    }

    /// <summary>
    /// Gives one page of the bases of W in the order of <c>universe.ini</c>, without the intro bases. The filters are
    /// the system, a text of the nickname or the name, rooms that each base must have, the kind and the bases that the
    /// active mod adds.
    /// </summary>
    public static ToolStep ListBases(ToolCall call)
    {
        var session = call.Session;
        var args = call.Args;
        var system = args.Text("system") is { } text ? SceneQueries.System(session, text).Nickname : null;
        var filter = args.Text("text");
        var rooms = args.Texts("has_rooms");
        var kind = args.Text("kind");
        var activeOnly = args.Flag("active_mod_only");
        var rows = new List<(SectionRef Entry, string Name)>();
        foreach (var entry in Entries(session))
        {
            var nickname = entry.Id.Key!;
            var view = session.Get(entry)!;
            if (system != null && !SceneSections.Is(SceneSections.Last(view, "system") ?? "", system))
                continue;
            var name = call.Texts.TextOf(entry, "strid_name");
            if (!ToolCall.Matches(filter, nickname, name))
                continue;
            if (activeOnly && session.Origin(entry, "nickname").Origin != ValueOrigin.ActiveMod)
                continue;
            if (rooms != null && !rooms.All(r => session.Bases.RoomsOf(nickname).Any(h => SceneSections.Is(h.Nickname, r.Trim()))))
                continue;
            if (kind != null && AgentTools.Snake(BaseView.KindOf(session, nickname).ToString()) != kind)
                continue;
            rows.Add((entry, name));
        }
        return ToolResult.Of(call.Paged(rows, r => Row(session, r.Entry, r.Name)));
    }

    /// <summary>
    /// The bases of a system for <c>get_system</c>, as <c>base:</c> references: first each <c>[Base]</c> of W whose
    /// <c>system</c> line names the system, in the order of <c>universe.ini</c>, then each other base that a
    /// <c>base</c> line of an object of the system names. So a new base shows at once, also before its dock object. An
    /// intro base of the main menu is not a base of its system, as in <c>list_bases</c>.
    /// </summary>
    /// <param name="objectBases">The values of the <c>base</c> lines of the objects of the system, in file order.</param>
    internal static IReadOnlyList<string> OfSystem(EditSession session, string system, IEnumerable<string> objectBases)
    {
        var names = Entries(session)
            .Where(e => session.Get(e) is { } view && SceneSections.Is(SceneSections.Last(view, "system") ?? "", system))
            .Select(e => e.Id.Key!)
            .Concat(objectBases)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return names.Select(b => ItemRefs.Format(new ItemReference(ItemRefKind.Base, b))).ToList();
    }

    /// <summary>
    /// The template bases for the resource <c>openlancer://catalog/base-templates</c>: each base of W with a base file
    /// with rooms, except the intro bases, with its kind, its rooms and their dealers, its terrain lines, its character
    /// count, its goods by category and the archetype of its dock object. Without a profile, the list is empty. It reads
    /// the session, so it runs on the thread that owns the session.
    /// </summary>
    internal static BaseTemplatesView Templates(AgentHost host)
    {
        if (host.Session is not { } session)
            return new BaseTemplatesView([]);
        var texts = new ItemTexts(session, host.NameText, host.InfoText);
        var rows = new List<BaseTemplateRow>();
        foreach (var entry in Entries(session))
        {
            if (session.Base(entry.Id.Key!) is not { File: not null, Info: not null } view ||
                view.Rooms.All(r => r.Nickname.Length == 0))
                continue;
            var goods = BaseMarkets.KeptLines(view.Market, r => r.Good, r => r.Line.Values)
                .GroupBy(r => MarketPlans.Category(session, r.Good) ?? "other", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count());
            var dock = view.NamedObject ?? view.DockObjects.FirstOrDefault(d => d.DocksWith)?.Section;
            rows.Add(new BaseTemplateRow(view.Nickname, texts.TextOf(view.Entry, "strid_name"), view.System, view.Kind,
                view.StartRoom,
                view.Rooms.Where(r => r.Nickname.Length > 0)
                    .Select(r => new TemplateRoomRow(r.Nickname, r.Dealers, r.VirtualRooms, r.ShipPlaces)).ToList(),
                view.Terrain.Select(t => t.Key).ToList(), view.Characters.Count, goods,
                dock is { } found && session.Get(found) is { } object_ ? SceneSections.Last(object_, "archetype") : null));
        }
        return new BaseTemplatesView(rows);
    }

    /// <summary>
    /// The nickname of a base that an argument names: a nickname or a <c>base:</c> reference. The nickname takes the
    /// spelling of the <c>[Base]</c> of W.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a reference of another kind, or
    /// <see cref="EditErrorKind.NotFound"/> with the closest names.</exception>
    internal static string Nickname(EditSession session, string text, string field)
    {
        // No nickname holds a ':' (section 5.6 of the MCP1 design), so a text with one is a reference.
        var reference = text.Contains(':') ? ItemRefs.Parse(text) : new ItemReference(ItemRefKind.Base, text.Trim());
        if (reference.Kind != ItemRefKind.Base)
            throw ToolInput.Bad(field, AgentTexts.Error("base_ref", ("name", field), ("ref", text)));
        return ItemRefs.ResolveSection(session, reference).Id.Key ?? reference.Name;
    }

    /// <summary>True for a <c>[Base]</c> of <c>universe.ini</c>. <c>duplicate_items</c> refuses it.</summary>
    internal static bool IsEntry(EditSession session, SectionRef section) =>
        SceneSections.Is(section.Id.Type, "Base") && session.UniversePath is { } universe &&
        VfsPath.Comparer.Equals(section.Path, universe);

    /// <summary>The first <c>[Base]</c> of W of <c>universe.ini</c> for each nickname, in file order, without the intro
    /// bases.</summary>
    private static IEnumerable<SectionRef> Entries(EditSession session)
    {
        if (session.UniversePath is not { } universe)
            yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in session.Sections(universe))
        {
            if (!SceneSections.Is(section.Id.Type, "Base") || section.Id.Key is not { } key || !seen.Add(key))
                continue;
            if (!key.StartsWith(IntroPrefix, StringComparison.OrdinalIgnoreCase))
                yield return section;
        }
    }

    /// <summary>The row of a base of <c>list_bases</c>. It reads only the counts of the base, not a view of the whole
    /// base, so a page of many bases stays fast.</summary>
    private static BaseRow Row(EditSession session, SectionRef entry, string name)
    {
        var nickname = entry.Id.Key!;
        var view = session.Get(entry)!;
        var (characters, goods, docks) = BaseView.CountsOf(session, nickname);
        return new BaseRow(ItemRefs.Canonical(session, entry), nickname, name,
            SceneSections.Last(view, "system") is { } system ? SceneQueries.SystemRef(system) : null,
            BaseView.KindOf(session, nickname), session.Bases.RoomsOf(nickname).Select(r => r.Nickname).ToList(),
            characters, goods, docks, AgentRows.OriginOf(session, entry, view, "nickname"));
    }

    /// <summary>A step of the completeness report as a row. The fix of the market step tells which buy-only lines of
    /// ship packages it adds (see <see cref="MarketPlans.ShipLines"/>).</summary>
    private static BaseStepRow Step(EditSession session, string baseNickname, CompletenessStep step)
    {
        var fix = step.Fix == BaseCompleteness.ShipLinesFix
            ? string.Join(" ", MarketPlans.ShipLines(session, baseNickname).Fixes.Select(f => f.Text)) is { Length: > 0 } text
                ? text
                : null
            : step.Fix;
        return new BaseStepRow(step.Name, step.State, step.Reason, step.Detail,
            step.GoTo is SectionRef section ? ItemRefs.Canonical(session, section) : null, fix, step.IsError);
    }

    /// <summary>The dock object of the request, or null when the call gives none.</summary>
    private static DockSpec? Dock(ToolArgs? group)
    {
        if (group == null)
            return null;
        var ring = group.Group("ring");
        return new DockSpec
        {
            Archetype = group.Text("archetype"),
            Position = group.Point("position"),
            Rotation = group.Point("rotation"),
            Nickname = group.Text("nickname"),
            Planet = group.Text("planet"),
            Ring = ring == null ? null : new RingSpec(ring.Text("archetype"), ring.Point("position"), ring.Point("rotation")),
        };
    }

    /// <summary>A number of a market line, or null when the line does not have it.</summary>
    private static float? Number(MarketLine line, int index) =>
        index < line.Values.Count && line.Values[index].TryToSingle(out var value) ? value : null;

    /// <summary>A whole number of a market line, or null when the line does not have it.</summary>
    private static int? Whole(MarketLine line, int index) =>
        index < line.Values.Count && line.Values[index].TryToInt32(out var value) ? value : null;
}
