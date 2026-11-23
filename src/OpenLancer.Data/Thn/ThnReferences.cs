using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Thn;

/// <summary>A value of an INI file that names a cutscene script.</summary>
/// <param name="File">The install path of the INI file.</param>
/// <param name="Section">The name of the section, as the file has it.</param>
/// <param name="Key">The key, as the file has it.</param>
/// <param name="Value">The value, as the file has it, for example <c>missions\\m01a\\m01a_fp7_01.thn</c>.</param>
/// <param name="SectionIndex">The place of the section in the file, from 0.</param>
public sealed record ThnReference(string File, string Section, string Key, string Value, int SectionIndex);

/// <summary>How a room plays a cutscene script.</summary>
public enum ThnRoomRole
{
    /// <summary><c>[Room_Info] set_script</c>: the room set. The game makes its entities and never runs its events
    /// (Cutscene.cs:279-283).</summary>
    Set,

    /// <summary><c>[Room_Info] scene</c>: an ambient script of the room.</summary>
    Scene,

    /// <summary><c>[Room_Info] goodscart_script</c>. The game does not play it (ResourceLoadExtensions.cs:63 has no
    /// caller).</summary>
    Cart,

    /// <summary><c>[CharacterPlacement] start_script</c>: the script when the player enters the room.</summary>
    Start,

    /// <summary><c>[PlayerShipPlacement] landing_script</c>.</summary>
    Landing,

    /// <summary><c>[PlayerShipPlacement] launching_script</c>.</summary>
    Launching,

    /// <summary>A story cutscene of <c>[CharacterEncounter]</c>, at its <c>location</c>.</summary>
    Encounter,

    /// <summary>A mission ambient of <c>[Trigger] Act_AddAmbient</c>. The game plays it after the ambient scripts of
    /// the room (RoomGameplay.cs:1053).</summary>
    Ambient,

    /// <summary>The fidget of a room NPC of <c>[MRoom] fixture</c>, at its stand marker.</summary>
    Fixture,
}

/// <summary>A room that plays a cutscene script.</summary>
/// <param name="Base">The nickname of the base, as the data names it.</param>
/// <param name="Room">The nickname of the room.</param>
/// <param name="Role">How the room plays the script.</param>
/// <param name="File">The INI file that names the script: the room file, <c>mbases.ini</c>, an RTC file or a mission
/// file.</param>
/// <param name="Spot">The stand marker of a <see cref="ThnRoomRole.Fixture"/>, else null.</param>
public sealed record ThnRoomUse(string Base, string Room, ThnRoomRole Role, string File, string? Spot = null);

/// <summary>
/// The INI values of a view of the game data that name cutscene scripts, and the rooms that play each script (section
/// 7.2 of the M8 design).
/// </summary>
/// <remarks>
/// <para>A value names a script below the data folder, with either separator and in any case. Some values double the
/// backslash, for example 67 of the 70 retail <c>Act_CallThorn</c> values. The engine reads such a value through its
/// file system, which drops the empty part. So <see cref="PathOf"/> gives each value the normal form of
/// <see cref="VfsPath.Normalize"/>, and the spellings of one script give one path.</para>
/// <para>The rooms come from the INI files too: <c>[Base] file</c> names the file of a base, <c>[Room] file</c> in that
/// file names the file of a room, and the room file names its scripts. <c>[MRoom] fixture</c> of <c>mbases.ini</c>,
/// <c>[CharacterEncounter] location</c> and <c>[Trigger] Act_AddAmbient</c> name the base and the room
/// directly.</para>
/// </remarks>
public sealed class ThnReferences
{
    /// <summary>The folder that the values are relative to, as retail <c>freelancer.ini</c> gives it.</summary>
    private const string DataFolder = "DATA";

    private const string ScriptExtension = ".thn";

    /// <summary>The room keys of a room file, by the section and the key.</summary>
    private static readonly Dictionary<string, ThnRoomRole> RoomKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        [Key("Room_Info", "set_script")] = ThnRoomRole.Set,
        [Key("Room_Info", "scene")] = ThnRoomRole.Scene,
        [Key("Room_Info", "goodscart_script")] = ThnRoomRole.Cart,
        [Key("CharacterPlacement", "start_script")] = ThnRoomRole.Start,
        [Key("PlayerShipPlacement", "landing_script")] = ThnRoomRole.Landing,
        [Key("PlayerShipPlacement", "launching_script")] = ThnRoomRole.Launching,
    };

    /// <summary>The keys of <c>[CharacterEncounter]</c> that name a story cutscene. Retail uses both <c>decline</c> and
    /// <c>reject</c> (StoryCutsceneIni.cs:46).</summary>
    private static readonly HashSet<string> EncounterKeys =
        new(["action", "offer", "decision", "accept", "decline", "reject"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The sections of a room file whose <c>name</c> names an entity of the scripts of the room. The game
    /// finds these entities by name (RoomGameplay.cs:1406-1427, :1467-1487).</summary>
    private static readonly HashSet<string> NameSections =
        new(["Camera", "CharacterPlacement", "PlayerShipPlacement", "ForSaleShipPlacement"], StringComparer.OrdinalIgnoreCase);

    private const string NameKey = "name";

    /// <summary>The key of a mission trigger that plays a script in space (CGameSession.Space.cs:565-594).</summary>
    private const string SpaceKey = "Act_CallThorn";

    /// <summary>The key of a mission trigger that adds an RTC file. Its first value is the INI path of the RTC
    /// file.</summary>
    private const string RtcKey = "Act_AddRTC";

    private static readonly IReadOnlyList<ThnReference> NoReferences = [];
    private static readonly IReadOnlyList<ThnRoomUse> NoRooms = [];

    private readonly Dictionary<string, List<ThnReference>> byPath;
    private readonly Dictionary<string, List<ThnRoomUse>> rooms;
    private readonly HashSet<string> fidgets;
    private readonly Dictionary<string, List<ThnReference>> roomNames;
    private readonly List<(string Base, string Room, ThnReference Spot)> fixtureSpots;

    private ThnReferences(IReadOnlyList<ThnReference> all, IReadOnlyList<ThnReference> rtcLines,
        Dictionary<string, List<ThnReference>> byPath, Dictionary<string, List<ThnRoomUse>> rooms, HashSet<string> fidgets,
        Dictionary<string, List<ThnReference>> roomNames, List<(string Base, string Room, ThnReference Spot)> fixtureSpots,
        IReadOnlyList<Diagnostic> problems)
    {
        All = all;
        RtcLines = rtcLines;
        this.byPath = byPath;
        this.rooms = rooms;
        this.fidgets = fidgets;
        this.roomNames = roomNames;
        this.fixtureSpots = fixtureSpots;
        Problems = problems;
    }

    /// <summary>Each value that names a script, in the order of the files and of their lines.</summary>
    public IReadOnlyList<ThnReference> All { get; }

    /// <summary>
    /// Each <c>[Trigger] Act_AddRTC</c> line, in scan order. The value is the first value of the line: the INI path of
    /// the RTC file, as the file has it. <see cref="PathOf"/> gives its install path. The cutscene list orders the
    /// scripts of a story mission by these lines (decision D604 of the cutscene categories design).
    /// </summary>
    public IReadOnlyList<ThnReference> RtcLines { get; }

    /// <summary>The scripts that the values name, as install paths in normal form, each once.</summary>
    public IReadOnlyCollection<string> Paths => byPath.Keys;

    /// <summary>The INI files that the engine parser cannot read. The scan skips them.</summary>
    public IReadOnlyList<Diagnostic> Problems { get; }

    /// <summary>
    /// The install path of the script that an INI value names, in normal form: the value below the data folder, with
    /// <c>/</c> as <c>\</c> and without empty parts. Null when the value leaves the install root. The engine resolves a
    /// room script through the data folder (GameItemDb.cs:122-138). <see cref="Editing.ContentPaths.DataValue"/> gives
    /// the value of an install path.
    /// </summary>
    public static string? PathOf(string value) => VfsPath.Normalize(DataFolder + "\\" + value);

    /// <summary>
    /// The scripts that the room keys of a room file name and that the game plays in the room: the set script, the
    /// scene scripts, the start script and the landing and launching scripts. The cart script is not in the result,
    /// because the game does not play it. The result has install paths in normal form, each once, in file order.
    /// </summary>
    public static IReadOnlyList<string> RoomScripts(IEnumerable<Section> roomFile)
    {
        var result = new List<string>();
        foreach (var section in roomFile)
        {
            foreach (var entry in section)
            {
                if (!RoomKeys.TryGetValue(Key(section.Name, entry.Name), out var role) || role == ThnRoomRole.Cart)
                    continue;
                foreach (var value in entry)
                {
                    if (value.ToString() is { } text && IsScript(text) && PathOf(text) is { } path &&
                        !result.Contains(path, VfsPath.Comparer))
                        result.Add(path);
                }
            }
        }
        return result;
    }

    /// <summary>Reads each INI file of a view of the game data, in the order of the paths.</summary>
    public static ThnReferences Scan(FileSystem view)
    {
        var scanner = new Scanner();
        var problems = new List<Diagnostic>();
        foreach (var file in VfsFiles.Find(view, ".ini"))
        {
            IReadOnlyList<Section> sections;
            try
            {
                sections = IniDocument.Parse(file, view.ReadAllBytes(file)).Sections;
            }
            catch (Exception e)
            {
                // The game cannot read the file either, and the load or the merge of the data reports it.
                problems.Add(Diagnostic.Warning(file, "The engine cannot parse this file: " + e.Message));
                continue;
            }
            scanner.Read(file, sections);
        }
        return scanner.Result(problems);
    }

    /// <summary>Reads the sections of INI files, in the order of the files. The edit session uses it for the INI files
    /// of W that differ from the files on disk.</summary>
    public static ThnReferences Read(IEnumerable<(string File, IReadOnlyList<Section> Sections)> files)
    {
        var scanner = new Scanner();
        foreach (var (file, sections) in files)
            scanner.Read(file, sections);
        return scanner.Result([]);
    }

    /// <summary>The values that name a script, in scan order. Empty when no value names it.</summary>
    public IReadOnlyList<ThnReference> UsedBy(string thnPath) =>
        VfsPath.Normalize(thnPath) is { } path && byPath.TryGetValue(path, out var list) ? list : NoReferences;

    /// <summary>The rooms that play a script, and how, in scan order.</summary>
    public IReadOnlyList<ThnRoomUse> Rooms(string thnPath) =>
        VfsPath.Normalize(thnPath) is { } path && rooms.TryGetValue(path, out var list) ? list : NoRooms;

    /// <summary>True when the game plays the script only in space: each value that names it is an
    /// <c>Act_CallThorn</c>, and at least one value names it. The space scene makes no COMPOUND of the script, and uses
    /// the objects of the mission in their place (Cutscene.cs:101-113).</summary>
    public bool OnlyInSpace(string thnPath) => OnlyInSpace(UsedBy(thnPath));

    /// <summary>True when the values that name a script make the game play it only in space: each value is an
    /// <c>Act_CallThorn</c>, and there is at least one value (see <see cref="OnlyInSpace(string)"/>).</summary>
    public static bool OnlyInSpace(IEnumerable<ThnReference> uses)
    {
        var any = false;
        foreach (var use in uses)
        {
            if (!IsSpace(use))
                return false;
            any = true;
        }
        return any;
    }

    private static bool IsSpace(ThnReference reference) => Same(reference.Key, SpaceKey);

    /// <summary>
    /// True when the game plays the script as a fidget: <c>[MRoom] fixture</c>, <c>[Char] fidget</c>, or a script of
    /// the <c>fidget</c> segment of <c>[GenericScripts]</c> (BaseNpcPopulation.cs:181). The game runs only its
    /// START_MOTION events, on a character (Cutscene.cs:229-241).
    /// </summary>
    public bool IsFidget(string thnPath) => VfsPath.Normalize(thnPath) is { } path && fidgets.Contains(path);

    /// <summary>
    /// The INI lines of other files that name an entity of a script, for a rename (section 7.5 of the M8 design): the
    /// <c>name</c> of <c>[Camera]</c>, <c>[CharacterPlacement]</c>, <c>[PlayerShipPlacement]</c> and
    /// <c>[ForSaleShipPlacement]</c> of each room whose set script or scene script is the script, and the stand markers
    /// of <c>[MRoom] fixture</c> in those rooms. The game finds these entities by name, without letter case
    /// (RoomGameplay.cs:1406-1427, :1467-1487, ThnRoomHandler.cs:112). For a fixture, the value is the stand marker.
    /// </summary>
    public IReadOnlyList<ThnReference> EntityNameUses(string thnPath, string entityName)
    {
        var result = new List<ThnReference>();
        foreach (var room in PlayingRooms(thnPath))
        {
            result.AddRange(roomNames.GetValueOrDefault(room.File)?.Where(r =>
                r.Value.Equals(entityName, StringComparison.OrdinalIgnoreCase)) ?? []);
            result.AddRange(fixtureSpots.Where(f => Same(f.Base, room.Base) && Same(f.Room, room.Room) &&
                                                    f.Spot.Value.Equals(entityName, StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Spot));
        }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// The other scripts that play in the rooms whose set script or scene script is the script, in scan order: their
    /// set, scene and cart scripts, and the scripts that the rooms play as a cutscene or a fidget. A REFERENCE entity of
    /// such a script uses the object of the same name in the scene (ThnScriptInstance.cs:70-71).
    /// </summary>
    public IReadOnlyList<string> OtherRoomScripts(string thnPath)
    {
        var path = VfsPath.Normalize(thnPath);
        var playing = PlayingRooms(thnPath).Select(r => (r.Base, r.Room)).ToList();
        return rooms.Where(p => !VfsPath.Comparer.Equals(p.Key, path) &&
                                p.Value.Any(u => playing.Any(r => Same(r.Base, u.Base) && Same(r.Room, u.Room))))
            .Select(p => p.Key).ToList();
    }

    /// <summary>The rooms whose set script or scene script is the script.</summary>
    private IEnumerable<ThnRoomUse> PlayingRooms(string thnPath) =>
        Rooms(thnPath).Where(r => r.Role is ThnRoomRole.Set or ThnRoomRole.Scene);

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string Key(string section, string key) => section + "\n" + key;

    private static bool IsScript(string value) => value.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase);


    /// <summary>The text of a value, or null for a value that is not there.</summary>
    private static string? Text(Entry? entry, int index) =>
        entry != null && index < entry.Count ? entry[index].ToString() : null;

    /// <summary>Collects the references and the room data of the files, then joins the rooms of the room
    /// files.</summary>
    private sealed class Scanner
    {
        private readonly List<ThnReference> all = [];
        private readonly List<ThnReference> rtcLines = [];
        private readonly Dictionary<string, List<ThnReference>> byPath = new(VfsPath.Comparer);
        private readonly HashSet<string> fidgets = new(VfsPath.Comparer);

        // The nickname of the base of each base file.
        private readonly Dictionary<string, string> baseFiles = new(VfsPath.Comparer);

        // The rooms of each base file: the nickname and the room file.
        private readonly List<(string BaseFile, string Room, string RoomFile)> roomFiles = [];

        // The room uses in scan order. A use of a room file has no base and no room until the scan ends.
        private readonly List<(string Path, string? RoomFile, ThnRoomRole Role, ThnRoomUse? Use)> uses = [];

        // The entity names of each room file, and the stand markers of the fixtures.
        private readonly Dictionary<string, List<ThnReference>> roomNames = new(VfsPath.Comparer);
        private readonly List<(string Base, string Room, ThnReference Spot)> fixtureSpots = [];

        public void Read(string file, IReadOnlyList<Section> sections)
        {
            // [MRoom] follows its [MBase] in mbases.ini.
            string? mbase = null;
            for (var index = 0; index < sections.Count; index++)
            {
                var section = sections[index];
                string? segment = null;
                foreach (var entry in section)
                {
                    if (section.Name.Equals("GenericScripts", StringComparison.OrdinalIgnoreCase) &&
                        entry.Name.Equals("set_segment", StringComparison.OrdinalIgnoreCase))
                        segment = Text(entry, 0);
                    // One record for each line, also when the value is not a script.
                    if (section.Name.Equals("Trigger", StringComparison.OrdinalIgnoreCase) && Same(entry.Name, RtcKey) &&
                        Text(entry, 0) is { } rtc)
                        rtcLines.Add(new ThnReference(file, section.Name, entry.Name, rtc, index));
                    foreach (var value in entry)
                    {
                        var text = value.ToString();
                        if (text == null || !IsScript(text))
                            continue;
                        Add(new ThnReference(file, section.Name, entry.Name, text, index), section, entry, segment, mbase);
                    }
                }

                var name = section.Name;
                if (NameSections.Contains(name) && First(section, NameKey) is { } nameLine && Text(nameLine, 0) is { } entity)
                {
                    if (!roomNames.TryGetValue(file, out var names))
                        roomNames[file] = names = [];
                    names.Add(new ThnReference(file, name, nameLine.Name, entity, index));
                }

                if (name.Equals("Base", StringComparison.OrdinalIgnoreCase) &&
                    Value(section, "nickname") is { } baseName && Value(section, "file") is { } baseFile &&
                    PathOf(baseFile) is { } basePath)
                    baseFiles.TryAdd(basePath, baseName);
                else if (name.Equals("Room", StringComparison.OrdinalIgnoreCase) &&
                         Value(section, "nickname") is { } room && Value(section, "file") is { } roomFile &&
                         PathOf(roomFile) is { } roomPath)
                    roomFiles.Add((file, room, roomPath));
                else if (name.Equals("MBase", StringComparison.OrdinalIgnoreCase))
                    mbase = Value(section, "nickname");
            }
        }

        public ThnReferences Result(IReadOnlyList<Diagnostic> problems)
        {
            var roomsOfFile = roomFiles
                .Where(r => baseFiles.ContainsKey(r.BaseFile))
                .ToLookup(r => r.RoomFile, r => (Base: baseFiles[r.BaseFile], r.Room), VfsPath.Comparer);
            var rooms = new Dictionary<string, List<ThnRoomUse>>(VfsPath.Comparer);
            foreach (var (path, roomFile, role, use) in uses)
            {
                var found = use != null
                    ? [use]
                    : roomsOfFile[roomFile!].Select(r => new ThnRoomUse(r.Base, r.Room, role, roomFile!));
                foreach (var room in found)
                {
                    if (!rooms.TryGetValue(path, out var list))
                        rooms[path] = list = [];
                    list.Add(room);
                }
            }
            return new ThnReferences(all, rtcLines, byPath, rooms, fidgets, roomNames, fixtureSpots, problems);
        }

        private void Add(ThnReference reference, Section section, Entry entry, string? segment, string? mbase)
        {
            all.Add(reference);
            if (PathOf(reference.Value) is not { } path)
                return;
            if (!byPath.TryGetValue(path, out var list))
                byPath[path] = list = [];
            list.Add(reference);

            var (name, key, file) = (section.Name, entry.Name, reference.File);
            if (RoomKeys.TryGetValue(Key(name, key), out var role))
                uses.Add((path, file, role, null));
            else if (name.Equals("CharacterEncounter", StringComparison.OrdinalIgnoreCase) &&
                     EncounterKeys.Contains(key) && First(section, "location") is { Count: >= 2 } location)
                uses.Add((path, null, ThnRoomRole.Encounter,
                    new ThnRoomUse(Text(location, 0)!, Text(location, 1)!, ThnRoomRole.Encounter, file)));
            else if (name.Equals("Trigger", StringComparison.OrdinalIgnoreCase) &&
                     key.Equals("Act_AddAmbient", StringComparison.OrdinalIgnoreCase) && entry.Count >= 3)
                // Act_AddAmbient = script, room, base
                uses.Add((path, null, ThnRoomRole.Ambient,
                    new ThnRoomUse(Text(entry, 2)!, Text(entry, 1)!, ThnRoomRole.Ambient, file)));
            else if (name.Equals("MRoom", StringComparison.OrdinalIgnoreCase) &&
                     key.Equals("fixture", StringComparison.OrdinalIgnoreCase))
            {
                fidgets.Add(path);
                // fixture = npc, stand marker, fidget script, role
                if (mbase != null && Value(section, "nickname") is { } room)
                {
                    uses.Add((path, null, ThnRoomRole.Fixture,
                        new ThnRoomUse(mbase, room, ThnRoomRole.Fixture, file, Text(entry, 1))));
                    if (Text(entry, 1) is { } spot)
                        fixtureSpots.Add((mbase, room, new ThnReference(file, name, key, spot, reference.SectionIndex)));
                }
            }
            else if (name.Equals("Char", StringComparison.OrdinalIgnoreCase) &&
                     key.Equals("fidget", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("GenericScripts", StringComparison.OrdinalIgnoreCase) &&
                     key.Equals("script", StringComparison.OrdinalIgnoreCase) &&
                     segment?.Equals("fidget", StringComparison.OrdinalIgnoreCase) == true)
                fidgets.Add(path);
        }

        /// <summary>The first value of the first line of a key in a section, or null.</summary>
        private static string? Value(Section section, string key) => Text(First(section, key), 0);

        /// <summary>The first line of a key in a section, or null. The engine reads the keys without case, and the
        /// retail RTC files write <c>Location</c>.</summary>
        private static Entry? First(Section section, string key) =>
            section.FirstOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
