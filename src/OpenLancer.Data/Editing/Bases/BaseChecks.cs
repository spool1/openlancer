using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>The step of the completeness of a base that a problem of the base checks belongs to (see
/// <see cref="BaseCompleteness"/>).</summary>
internal enum BaseStep
{
    /// <summary>The <c>[Base]</c> of <c>universe.ini</c> and its nickname.</summary>
    Entry,

    /// <summary>The base file.</summary>
    File,

    /// <summary>The start room of <c>[BaseInfo]</c>.</summary>
    StartRoom,

    /// <summary>The rooms, their room files, their doors and the terrain of their scenes.</summary>
    Rooms,

    /// <summary>The <c>[MBase]</c> and its characters.</summary>
    Characters,

    /// <summary>The market lines.</summary>
    Market,

    /// <summary>The objects that dock with the base and that link to it.</summary>
    DockObject,
}

/// <summary>One problem of the base checks, with the step of the completeness of the base.</summary>
internal sealed record BaseProblem(Diagnostic Problem, BaseStep Step);

/// <summary>
/// The save checks of the bases (section D of the bases design). They read W, so an unsaved edit counts. Four kinds of
/// problems are errors, because the game can crash:
/// <list type="bullet">
/// <item>a base without a base file, or with a base file that does not exist,</item>
/// <item>a base file without <c>[BaseInfo]</c>,</item>
/// <item>a <c>[Room]</c> without a nickname or a room file,</item>
/// <item>a room file that does not exist.</item>
/// </list>
/// The list rule of <c>start_room</c> gives the error of a start room that the base does not have. Each other check
/// gives a warning.
/// </summary>
/// <remarks>
/// <para>An error comes only from the active mod: a line that it sets or removes, a section that it adds, or a file that
/// it adds, changes or deletes. The same problem of the base game or of a lower mod is a warning, so it does not stop
/// the save.</para>
/// <para>The gate reads only the errors (see <see cref="SaveErrors"/>). The warnings show in the checks of each section
/// (see <see cref="Check"/>) and in the problems of a base (see <see cref="Problems"/>). The checks of the retail
/// targets give the problems that only the retail games have as errors (see
/// <see cref="Validation.TargetChecks"/>).</para>
/// <para>Retail facts: the game makes one room record for each <c>[Room]</c>. A door to a room that the base does not
/// have makes a room id with no room. The game reads the first line of an <c>[MBase]</c> as its nickname. It copies the
/// nickname of a base into a buffer of 64 bytes to find its <c>[MBase]</c>. It drops a fixture whose character comes
/// later in the block, and a character without a body, a voice or a name. A room scene fills in each
/// <c>$terrain_*</c> template with the <c>terrain_*</c> line of the base, and shows no model without the line. The trade
/// lists of the base screens show only a base that an object with its <c>base</c> line and its name text links
/// to.</para>
/// </remarks>
internal static class BaseChecks
{
    /// <summary>The most characters of a base nickname that a mission condition holds.</summary>
    public const int MissionNameLength = 31;

    /// <summary>The most characters of a base nickname for which the retail game finds the <c>[MBase]</c>.</summary>
    public const int BarDataNameLength = 63;

    /// <summary>The nickname prefix of an intro base. The OpenLancer engine shows its rooms in the main menu.</summary>
    private const string IntroPrefix = "intro";

    /// <summary>The keys of a character that the game needs to show it.</summary>
    private static readonly string[] CharacterKeys = ["body", "base_appr", "voice", "individual_name"];

    /// <summary>The most ship packages that the trade-in warning names.</summary>
    private const int ShownShips = 5;

    /// <summary>The <c>$terrain_*</c> templates that a room scene can name, each with the key of its line in the
    /// <c>[Base]</c>.</summary>
    private static readonly string[] TerrainKeys =
        ["terrain_tiny", "terrain_sml", "terrain_mdm", "terrain_lrg", "terrain_dyna_01", "terrain_dyna_02"];

    /// <summary>All problems of a base, also of its dock objects, in a fixed order. Empty when no <c>[Base]</c> of W has
    /// the nickname. The session keeps the result until W changes.</summary>
    public static IReadOnlyList<Diagnostic> Problems(EditSession session, string baseNickname) =>
        Steps(session, baseNickname).Select(p => p.Problem).ToList();

    /// <summary>The problems of <see cref="Problems"/>, each with the step of the completeness of the base.</summary>
    public static IReadOnlyList<BaseProblem> Steps(EditSession session, string baseNickname) =>
        session.BaseCheckState.Problems(baseNickname.Trim());

    /// <summary>
    /// The problems of a section, also of a lower layer. The section is a <c>[Base]</c> of <c>universe.ini</c>, a section
    /// of a base file or of <c>mBases.ini</c>, or an object of a system file that docks with a base of another system.
    /// Each problem of a base has one target section, so one fault gives one problem. Empty for another section.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Check(EditSession session, SectionRef section)
    {
        if (session.Get(section) is not { } view)
            return [];
        IEnumerable<string> bases;
        if (session.UniversePath is { } universe && VfsPath.Comparer.Equals(section.Path, universe))
            bases = SceneSections.Is(section.Id.Type, "Base") && section.Id.Key is { } key ? [key] : [];
        else if (session.IsBaseFile(section.Path))
            bases = session.Bases.BasesOfBaseFile(section.Path);
        else if (session.IsMBasesFile(section.Path))
            bases = BaseView.BaseOf(section) is { } owner ? [owner] : [];
        else if (SceneSections.Is(section.Id.Type, SceneSections.ObjectType) && session.IsSystemFile(section.Path))
            return DockProblem(session, section, view) is { } problem ? [problem] : [];
        else
            return [];
        return bases.SelectMany(b => Problems(session, b)).Where(p => section.Equals(p.Target)).ToList();
    }

    /// <summary>True for a section that <see cref="Check"/> reads as a part of a base: a section of a base file or of
    /// <c>mBases.ini</c>.</summary>
    public static bool IsBasePart(EditSession session, SectionRef section) =>
        session.IsBaseFile(section.Path) || session.IsMBasesFile(section.Path);

    /// <summary>
    /// The errors of the bases that the active mod adds or changes, for the save gate. These are the bases whose
    /// <c>[Base]</c> the active mod adds or changes, and the bases whose base file or room file it changes. The gate
    /// reads only errors, so the warnings do not reach it.
    /// </summary>
    public static IEnumerable<Diagnostic> SaveErrors(EditSession session)
    {
        var changed = session.ActiveChangedPaths();
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (session.UniversePath is { } universe && changed.Contains(universe, VfsPath.Comparer))
        {
            foreach (var section in session.Sections(universe).Where(s => SceneSections.Is(s.Id.Type, "Base")))
            {
                if (section.Id.Key is { } name && session.Get(section) is { IsChanged: true })
                    bases.Add(name);
            }
        }
        foreach (var path in changed)
        {
            bases.UnionWith(session.Bases.BasesOfBaseFile(path));
            bases.UnionWith(session.Bases.BasesOfRoomFile(path));
        }
        return bases.SelectMany(b => Problems(session, b)).Where(p => p.Severity == DiagnosticSeverity.Error).ToList();
    }

    /// <summary>
    /// The warning of an object that docks with a base of another system. After a launch, the game puts the player in
    /// the system of the base. Null for another object.
    /// </summary>
    private static Diagnostic? DockProblem(EditSession session, SectionRef section, SectionView view)
    {
        if (SceneSections.Last(view, "dock_with") is not { } dock ||
            ComponentChecks.SystemOfBase(session, dock) is not { } system || session.SystemPath(system) is not { } path ||
            VfsPath.Comparer.Equals(path, section.Path))
            return null;
        var here = session.SystemNicknameOf(section.Path) ?? SceneSections.FileName(section.Path);
        return Warning(section, "dock_with",
            $"The object {section.Id.Key} in {here} docks with {dock}, but {dock} is in {system}. After a launch, the " +
            $"game puts the player in {system}.");
    }

    /// <summary>The checks of one base of a session at one state of W.</summary>
    private sealed class Run(EditSession session, State state, BaseView view)
    {
        private readonly List<BaseProblem> problems = [];
        private BaseStep step;
        private string Name => view.Nickname;

        public IReadOnlyList<BaseProblem> Problems()
        {
            step = BaseStep.Entry;
            Nickname();
            step = BaseStep.File;
            Files();
            step = BaseStep.Characters;
            Characters();
            step = BaseStep.Market;
            Market();
            step = BaseStep.DockObject;
            Docks();
            return problems;
        }

        /// <summary>The length and the prefix of the nickname.</summary>
        private void Nickname()
        {
            if (Name.Length > BarDataNameLength)
                Add(false, view.Entry, "nickname",
                    $"The base nickname {Name} has more than {BarDataNameLength} characters. The retail game then ignores " +
                    "its bar data.");
            if (Name.Length > MissionNameLength)
                Add(false, view.Entry, "nickname",
                    $"The base nickname {Name} has more than {MissionNameLength} characters. A mission condition cannot " +
                    "name this base.");
            if (view.Origin == ValueOrigin.ActiveMod && Name.StartsWith(IntroPrefix, StringComparison.OrdinalIgnoreCase))
                Add(false, view.Entry, "nickname",
                    $"The base nickname {Name} starts with 'intro'. The OpenLancer engine also shows the rooms of the " +
                    "base in the main menu.");
        }

        /// <summary>The base file, its rooms, the doors of the rooms and the terrain templates of the room
        /// scenes.</summary>
        private void Files()
        {
            var entry = session.Get(view.Entry);
            var value = entry == null ? null : SceneSections.Last(entry, "file");
            if (value == null)
            {
                Add(Mine(view.Entry, "file"), view.Entry, "file",
                    $"The base {Name} names no base file. The game can crash when the player docks here.");
                return;
            }
            if (view.File == null || !session.HasFile(view.File))
            {
                Add(Mine(view.Entry, "file", view.File), view.Entry, "file",
                    $"The base file {value} of {Name} does not exist. The game can crash when the player docks here.");
                return;
            }
            if (view.Info == null)
                Add(Mine(view.Entry, "file", view.File), view.Entry, "file",
                    $"The base file {value} of {Name} has no [BaseInfo] with a start room. The game can crash when the " +
                    "player docks here.");

            step = BaseStep.Rooms;
            var names = view.Rooms.Select(r => r.Nickname).Where(n => n.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var room in view.Rooms)
                Room(room, names);
            Terrain();
        }

        private void Room(BaseRoomView room, IReadOnlySet<string> names)
        {
            var section = session.Get(room.Section);
            var value = section == null ? null : SceneSections.Last(section, "file");
            if (room.Nickname.Length == 0)
            {
                Add(Mine(room.Section, "nickname"), room.Section, "nickname",
                    $"A [Room] of the base file of {Name} has no nickname. The game keeps a broken room, and it can crash.");
                return;
            }
            if (value == null)
            {
                Add(Mine(room.Section, "file"), room.Section, "file",
                    $"The room {room.Nickname} of {Name} names no room file. The game keeps a broken room, and it can " +
                    "crash.");
                return;
            }
            if (room.File == null || !session.HasFile(room.File))
            {
                Add(Mine(room.Section, "file", room.File), room.Section, "file",
                    $"The room file {value} of the room {room.Nickname} of {Name} does not exist. The game can crash when " +
                    "the player enters this room.");
                return;
            }
            var seen = new HashSet<(string, string)>();
            foreach (var door in room.Doors)
            {
                if (door.Target is not { } target || names.Contains(target) || !seen.Add((door.Name, target.ToLowerInvariant())))
                    continue;
                Add(false, room.Section, "file",
                    $"The door {door.Name} in the room {room.Nickname} of {Name} leads to {target}, but {Name} has no " +
                    $"room {target}. The retail game can crash when the player uses the door.");
            }
        }

        /// <summary>The <c>$terrain_*</c> templates of the room scenes that the base has no line for.</summary>
        private void Terrain()
        {
            var lines = view.Terrain.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var room in view.Rooms.Where(r => r.File != null))
            {
                foreach (var key in state.TerrainKeys(room.File!).Where(k => !lines.Contains(k) && reported.Add(k)))
                    Add(false, view.Entry, key,
                        $"The room {room.Nickname} of {Name} shows the template ${key}, but {Name} has no {key} line. " +
                        "The room shows no model there.");
            }
        }

        /// <summary>The <c>[MBase]</c> and its children.</summary>
        private void Characters()
        {
            if (view.MBase is not { } mbase)
            {
                if (view.Origin == ValueOrigin.ActiveMod)
                    Add(false, view.Entry, "nickname",
                        $"{Name} has no [MBase]. Its bar has no characters, and it offers no missions.");
                return;
            }
            if (session.WorkingSection(mbase) is { Count: > 0 } block &&
                !block[0].Name.Equals("nickname", StringComparison.OrdinalIgnoreCase))
                Add(false, mbase, "nickname",
                    $"The first line of [MBase] {Name} must be its nickname line. The retail game reads the first line " +
                    "as the nickname.");

            var rooms = view.Rooms.Select(r => r.Nickname).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var characters = view.Characters.Select(c => c.Nickname).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in view.MBaseParts)
            {
                if (session.WorkingSection(part) is not { } section)
                    continue;
                if (SceneSections.Is(section.Name, "GF_NPC"))
                {
                    var nickname = part.Id.Key ?? Value(section, "nickname");
                    if (nickname != null)
                        before.Add(nickname);
                    if (Value(section, "room") is { } room && !rooms.Contains(room))
                        Add(false, part, "room", NoRoom(room));
                    Complete(part, nickname ?? "", section);
                }
                else if (SceneSections.Is(section.Name, "MRoom"))
                {
                    var room = Value(section, "nickname");
                    if (room != null && !rooms.Contains(room))
                        Add(false, part, "nickname", NoRoom(room));
                    foreach (var fixture in Values(section, "fixture").Where(f => !before.Contains(f)))
                        Add(false, part, "fixture",
                            $"The fixture {fixture} in the room {room} of {Name} names no [GF_NPC] before it in the same " +
                            "[MBase]. The retail game shows no character at this place.");
                }
                else if (SceneSections.Is(section.Name, "BaseFaction"))
                {
                    var faction = Value(section, "faction");
                    foreach (var npc in Values(section, "npc").Where(n => !characters.Contains(n)))
                        Add(false, part, "npc",
                            $"The faction {faction} of {Name} names the character {npc}, but the [MBase] has no such " +
                            "character. The game ignores this line.");
                }
            }
        }

        private string NoRoom(string room) =>
            $"The room {room} in the [MBase] of {Name} is not a room of the base. The game ignores this line.";

        /// <summary>The parts that the game needs to show a character of the active mod: a body or a costume, a voice
        /// and a name. A character is of the active mod when the active mod adds it, or sets or removes one of these
        /// lines. Each character of a whole file of the active mod is of the active mod.</summary>
        private void Complete(SectionRef part, string nickname, Section section)
        {
            if (!CharacterKeys.Any(k => session.Origin(part, k).Origin == ValueOrigin.ActiveMod))
                return;
            var missing = new List<string>();
            if (Value(section, "body") == null && Value(section, "base_appr") == null)
                missing.Add("body");
            if (Value(section, "voice") == null)
                missing.Add("voice");
            if (Value(section, "individual_name") == null)
                missing.Add("name");
            if (missing.Count > 0)
                Add(false, part, missing[0] == "name" ? "individual_name" : missing[0],
                    $"The character {nickname} of {Name} has no {Words.Join(missing, "and no")}. The game does not show it.");
        }

        /// <summary>The goods that the dealers of the base sell, the places for ships for sale and the lines of the
        /// trade-in.</summary>
        private void Market()
        {
            // The game keeps the last line of a good (load order of the market files) that it does not skip.
            var lines = BaseMarkets.KeptLines(view.Market, r => r.Good, r => r.Line.Values);
            var sold = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ships = 0;
            foreach (var line in lines)
            {
                var category = state.Category(line.Good);
                var ship = BaseMarkets.Ship.Equals(category, StringComparison.OrdinalIgnoreCase);
                if (category == null || !BaseMarkets.Sells(line.Line.Values, ship))
                    continue;
                sold.Add(category);
                if (ship)
                    ships++;
            }
            var dealers = view.Rooms.SelectMany(r => r.Dealers).ToHashSet();
            foreach (var category in sold.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (BaseMarkets.DealerOf(category) is { } dealer && !dealers.Contains(dealer))
                    Add(false, view.Entry, "nickname",
                        $"{Name} sells {GoodsWords(dealer)}, but no room of the base has " +
                        $"{SceneSections.WithArticle(BaseMarkets.NameOf(dealer))}. The player cannot buy them.");
            }
            foreach (var dealer in dealers.Order())
            {
                if (!sold.Contains(BaseMarkets.CategoryOf(dealer)))
                    Add(false, view.Entry, "nickname",
                        $"{Name} has {SceneSections.WithArticle(BaseMarkets.NameOf(dealer))}, but its market sells no " +
                        $"{GoodsWords(dealer)}. The {BaseMarkets.NameOf(dealer)} shows an empty list.");
            }
            if (ships > 0 && dealers.Contains(BaseDealer.ShipDealer))
            {
                var places = view.Rooms.Where(r => r.Dealers.Contains(BaseDealer.ShipDealer)).Max(r => r.ShipPlaces);
                if (ships > places)
                    Add(false, view.Entry, "nickname", places == 0
                        ? $"{Name} sells {Words.Count(ships, "ship")}, but the room of its ship dealer has no place for a " +
                          "ship for sale. The game shows no ship."
                        : $"{Name} sells {Words.Count(ships, "ship")}, but its rooms show at most {places}. The game shows " +
                          $"only the first {places}.");
            }
            // Only a market that the active mod adds or changes. A new ship package of the mod does not warn about each
            // base of the game that does not list it. The market plans add the same lines (MarketPlans.ShipLines).
            if (ships > 0 && MarketPlans.MissingShipLines(session, view) is { Count: > 0 } missing)
                Add(false, view.Entry, "nickname",
                    $"{Name} sells ships, but its market has no line for {ShipList(missing)}. The ship markets of the " +
                    "game list each ship package at each base that sells ships.");
        }

        /// <summary>The goods of the category of a dealer in plain words, for example "commodities".</summary>
        private static string GoodsWords(BaseDealer dealer) => dealer switch
        {
            BaseDealer.CommodityTrader => "commodities",
            BaseDealer.EquipmentDealer => "equipment",
            _ => "ships",
        };

        private static string ShipList(IReadOnlyList<string> ships) =>
            ships.Count == 1
                ? $"the ship package {ships[0]}"
                : $"{ships.Count} ship packages: {string.Join(", ", ships.Take(ShownShips))}" +
                  (ships.Count > ShownShips ? $" and {ships.Count - ShownShips} more" : "");

        /// <summary>The objects that dock with the base and that link to it.</summary>
        private void Docks()
        {
            if (!view.DockObjects.Any(d => d.DocksWith))
                Add(false, view.Entry, "system", $"No object docks with {Name}. The player cannot dock there.");
            if (view.System is { } system && session.SystemPath(system) is { } path)
            {
                if (!view.DockObjects.Any(d => d.HasBaseLine && VfsPath.Comparer.Equals(d.Section.Path, path)))
                    Add(false, view.Entry, "system",
                        $"No object in {system} has 'base = {Name}'. The navigation map does not list the base, its rooms " +
                        "show no planet, and the trade lists of the game skip it.");
                else if (view.Origin == ValueOrigin.ActiveMod && view.NamedObject == null)
                    Add(false, view.Entry, "strid_name",
                        $"No object with 'base = {Name}' shows the name of the base. The trade lists of the game then " +
                        $"skip {Name}.");
            }
            foreach (var dock in view.DockObjects.Where(d => d.DocksWith))
            {
                if (session.Get(dock.Section) is { } dockView && DockProblem(session, dock.Section, dockView) is { } problem)
                    problems.Add(new BaseProblem(problem, step));
            }
        }

        /// <summary>True when the active mod causes a problem of a key of a section. The active mod sets or removes the
        /// lines of the key or adds the section. Or it adds, changes or deletes the file that the key names.</summary>
        private bool Mine(SectionRef section, string key, string? file = null) =>
            session.Origin(section, key).Origin == ValueOrigin.ActiveMod || file != null && state.Changed.Contains(file);

        private void Add(bool error, SectionRef target, string key, string message) =>
            problems.Add(new BaseProblem(error ? Error(target, key, message) : Warning(target, key, message), step));
    }

    /// <summary>The checks of the bases at one state of W, and the data that each check of a base reads.</summary>
    internal sealed class State(EditSession session, int revision)
    {
        private readonly Dictionary<string, IReadOnlyList<BaseProblem>> problems = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> categories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<string>> terrain = new(VfsPath.Comparer);
        private HashSet<string>? changed;
        private IReadOnlyList<string>? shipPackages;
        private bool? buyOnly;

        /// <summary>The state of W that the results are for (<see cref="EditSession.CacheRevision"/>).</summary>
        public int Revision => revision;

        public IReadOnlyList<BaseProblem> Problems(string nickname)
        {
            if (!problems.TryGetValue(nickname, out var result))
                problems[nickname] = result = session.Base(nickname) is { } view ? new Run(session, this, view).Problems() : [];
            return result;
        }

        /// <summary>The INI files that the active mod adds, changes or deletes.</summary>
        public HashSet<string> Changed => changed ??= session.ActiveChangedPaths().ToHashSet(VfsPath.Comparer);

        /// <summary>The goods category of a good, or null.</summary>
        public string? Category(string good)
        {
            if (!categories.TryGetValue(good, out var category))
                categories[good] = category = BaseMarkets.CategoryOf(session, good);
            return category;
        }

        /// <summary>The ship packages of the goods files: the goods of the category <c>ship</c>, in file
        /// order.</summary>
        public IReadOnlyList<string> ShipPackages => shipPackages ??= session.Items.List(ItemFileKind.Goods)
            .Where(g => g.Id.Key != null && BaseMarkets.Ship.Equals(Category(g.Id.Key), StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Id.Key!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>True when the ship markets of the game and the lower mods use the buy-only pattern (see
        /// <see cref="BaseMarkets.UsesBuyOnlyShipLines"/>). The lines of the active mod do not count, so a buy-only
        /// line that the mod writes does not make the pattern.</summary>
        public bool UsesBuyOnlyShipLines => buyOnly ??= BaseMarkets.UsesBuyOnlyShipLines(LowerShipLines());

        private IEnumerable<IReadOnlyList<ValueBase>> LowerShipLines()
        {
            var packages = ShipPackages.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var market in session.Items.List(ItemFileKind.Markets))
            {
                if (session.Origin(market, "marketgood").Origin == ValueOrigin.ActiveMod ||
                    session.Items.Section(market) is not { } section)
                    continue;
                foreach (var line in section.Where(e => SceneSections.Is(e.Name, "marketgood") && e.Count > 0))
                {
                    if (packages.Contains(line[0].ToString()!.Trim()))
                        yield return line.ToList();
                }
            }
        }

        /// <summary>The keys of the <c>terrain_*</c> lines that the scripts of a room file need: one for each
        /// <c>$terrain_*</c> template of an entity. The scripts come from the cutscene documents of the session, so a
        /// script edit counts.</summary>
        public IReadOnlyList<string> TerrainKeys(string roomFile)
        {
            if (terrain.TryGetValue(roomFile, out var known))
                return known;
            var keys = new List<string>();
            foreach (var script in ThnReferences.RoomScripts(session.WorkingSections(roomFile) ?? []))
            {
                if (session.CutsceneDocument(script) is not { } document)
                    continue;
                foreach (var entity in document.Working.Entities)
                {
                    if (entity.Table["template_name"] is ThnText { Value: { } template } && template.StartsWith('$') &&
                        BaseChecks.TerrainKeys.FirstOrDefault(k => k.Equals(template[1..], StringComparison.OrdinalIgnoreCase))
                            is { } key && !keys.Contains(key))
                        keys.Add(key);
                }
            }
            return terrain[roomFile] = keys;
        }
    }

    /// <summary>The first value of the last line of a key with a value, trimmed, or null.</summary>
    private static string? Value(Section section, string key) =>
        section.LastOrDefault(e => SceneSections.Is(e.Name, key) && e.Count > 0)?[0].ToString()?.Trim() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>The first value of each line of a key, trimmed, without empty values.</summary>
    private static IEnumerable<string> Values(Section section, string key) =>
        section.Where(e => SceneSections.Is(e.Name, key) && e.Count > 0)
            .Select(e => e[0].ToString()?.Trim()).OfType<string>().Where(v => v.Length > 0);

    private static Diagnostic Error(SectionRef section, string key, string message) =>
        new(DiagnosticSeverity.Error, section.Path, message) { Target = section, Key = key };

    private static Diagnostic Warning(SectionRef section, string key, string message) =>
        new(DiagnosticSeverity.Warning, section.Path, message) { Target = section, Key = key };

    /// <summary>Small helpers for the words of the texts.</summary>
    private static class Words
    {
        /// <summary>A count and a noun, for example "1 ship" or "3 ships".</summary>
        public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

        /// <summary>The words joined with ", no " and a last joiner, for example "voice and no name".</summary>
        public static string Join(IReadOnlyList<string> words, string last) =>
            words.Count == 1 ? words[0] : string.Join(", no ", words.Take(words.Count - 1)) + $" {last} " + words[^1];
    }
}
