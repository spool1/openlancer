using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema.MBases;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>One room of a base as the engine finds it: a <c>[Room]</c> of the base file with a nickname and a room
/// file.</summary>
/// <param name="Nickname">The nickname of the room, as the base file spells it.</param>
/// <param name="File">The install path of the room file.</param>
public sealed record BaseRoomFile(string Nickname, string File);

/// <summary>
/// The base files and the room files of the edit view, as the engine finds them (decision D135 of the M8b design): each
/// <c>[Base]</c> of <c>universe.ini</c> names the file of a base with its last <c>file</c> line (Base.cs:45-52), and
/// each <c>[Room]</c> of a base file names the file of a room with its last <c>file</c> line (Room.cs:44-50). Both
/// values are relative to the data folder of <c>freelancer.ini</c>. A system file is relative to the folder of
/// <c>universe.ini</c> (see <see cref="SystemFiles"/>), so the two rules differ.
/// </summary>
/// <remarks>
/// <para>The map also gives the base file of each base, the rooms of each base and the bases that use each room file.
/// Nicknames and paths compare without letter case. Retail has 197 <c>[Base]</c> sections and 461 <c>[Room]</c>
/// sections, which name 446 room files. One room file has 16 bases.</para>
/// <para>The map reads a file from W when the session has its document open, else from the edit view. So the first
/// use opens no document. It keeps its result until the revision of W changes. Then it compares the state of each
/// file that it read: W of an open document, or the edit view, which changes only with a save. It reads the files
/// again only when one of them changed.</para>
/// </remarks>
internal sealed class BaseFiles(EditSession session, Func<IReadOnlyList<Section>> freelancerIni)
{
    private const string FileKey = "file";
    private const string NicknameKey = "nickname";

    /// <summary>The nickname prefix of an intro base (GameItemDb.cs:261).</summary>
    private const string IntroPrefix = "intro";

    /// <summary>The file of the bar and mission data of the bases, relative to the data folder. The retail programs
    /// read it at this fixed path, and <c>freelancer.ini</c> has no line for it.</summary>
    internal const string MBasesValue = @"MISSIONS\mBases.ini";

    /// <summary>The file of the news of the bases, relative to the data folder. The retail programs read it at this
    /// fixed path.</summary>
    internal const string NewsValue = @"MISSIONS\news.ini";

    /// <summary>The section types of a base file and the classes of their keys. The fork reads <c>[BaseInfo]</c>
    /// without <c>ship_repair_cost</c>, but retail reads it (see <see cref="RetailBaseInfo"/>).</summary>
    private static readonly Dictionary<string, Type> BaseFileClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BaseInfo"] = typeof(RetailBaseInfo),
        ["Room"] = typeof(Room),
    };

    private int revision = int.MinValue;

    // The files that the last read used, with their states: W of an open document, or the edit view.
    private (string Path, object? State)[] read = [];
    private Map map = Map.Empty;

    /// <summary>The base files, in the order of the bases, without repeats.</summary>
    public IReadOnlyList<string> Files => Current.Files;

    /// <summary>The room files, in the order of the bases and their rooms, without repeats.</summary>
    public IReadOnlyList<string> RoomFiles => Current.RoomFiles;

    /// <summary>The nicknames of the bases, in the order of <c>universe.ini</c>, each one time.</summary>
    public IReadOnlyList<string> Nicknames => Current.Nicknames;

    /// <summary>True when a <c>[Base]</c> of <c>universe.ini</c> has the nickname, in any letter case.</summary>
    public bool Has(string baseNickname) => Current.Bases.ContainsKey(baseNickname.Trim());

    /// <summary>True when a <c>[Base]</c> of <c>universe.ini</c> names the file.</summary>
    public bool IsBaseFile(string path) => VfsPath.Normalize(path) is { } normal && Current.FileSet.Contains(normal);

    /// <summary>True when a <c>[Room]</c> of a base file names the file.</summary>
    public bool IsRoomFile(string path) => VfsPath.Normalize(path) is { } normal && Current.RoomSet.Contains(normal);

    /// <summary>True when only the <c>[Room]</c> sections of intro bases name the file. The fork marks an intro base by
    /// the nickname prefix "intro" (GameItemDb.cs:261), and only the main menu shows its rooms.</summary>
    public bool IsIntroRoom(string path) => VfsPath.Normalize(path) is { } normal && Current.Intro.Contains(normal);

    /// <summary>The install path of the base file of a base, or null when no <c>[Base]</c> has the nickname or its
    /// <c>file</c> line names no path. When two <c>[Base]</c> sections have the nickname, the first one counts.</summary>
    public string? FileOf(string baseNickname) =>
        Current.Bases.TryGetValue(baseNickname.Trim(), out var entry) ? entry.File : null;

    /// <summary>The rooms of a base, in the order of its base file. Empty for an unknown base.</summary>
    public IReadOnlyList<BaseRoomFile> RoomsOf(string baseNickname) =>
        Current.Bases.TryGetValue(baseNickname.Trim(), out var entry) ? entry.Rooms : [];

    /// <summary>The nicknames of the bases whose <c>file</c> line names a base file, in the order of
    /// <c>universe.ini</c>.</summary>
    public IReadOnlyList<string> BasesOfBaseFile(string path)
    {
        if (VfsPath.Normalize(path) is not { } normal)
            return [];
        var current = Current;
        return current.Nicknames
            .Where(n => current.Bases[n].File is { } file && VfsPath.Comparer.Equals(file, normal)).ToList();
    }

    /// <summary>The nicknames of the bases whose rooms use a room file, in the order of <c>universe.ini</c>.</summary>
    public IReadOnlyList<string> BasesOfRoomFile(string path) =>
        VfsPath.Normalize(path) is { } normal && Current.Users.TryGetValue(normal, out var users) ? users : [];

    /// <summary>The install path of <c>mBases.ini</c>, or null when <c>freelancer.ini</c> names no data folder.</summary>
    public string? MBasesPath => DataPaths.Resolve(freelancerIni(), MBasesValue);

    /// <summary>The install path of <c>news.ini</c>, or null when <c>freelancer.ini</c> names no data folder.</summary>
    public string? NewsPath => DataPaths.Resolve(freelancerIni(), NewsValue);

    /// <summary>True for <c>mBases.ini</c>, in any letter case.</summary>
    public bool IsMBasesFile(string path) => IsFixedFile(path, MBasesValue);

    /// <summary>True for <c>news.ini</c>, in any letter case.</summary>
    public bool IsNewsFile(string path) => IsFixedFile(path, NewsValue);

    /// <summary>True when an install path names a file of a fixed path below the data folder, in any letter case.</summary>
    private bool IsFixedFile(string path, string dataValue) =>
        DataPaths.Resolve(freelancerIni(), dataValue) is { } fixedPath && VfsPath.Normalize(path) is { } normal &&
        VfsPath.Comparer.Equals(normal, fixedPath);

    /// <summary>
    /// The schema of a section type of a base file, of a room file or of <c>mBases.ini</c>, or null for another file.
    /// A base file has <c>[BaseInfo]</c> and <c>[Room]</c>. <c>mBases.ini</c> has <c>[MBase]</c> and its children
    /// <c>[MVendor]</c>, <c>[BaseFaction]</c>, <c>[GF_NPC]</c> and <c>[MRoom]</c>.
    /// </summary>
    public SectionSchema? SchemaOf(string path, string type)
    {
        if (IsBaseFile(path))
            return BaseFileClasses.TryGetValue(type, out var section) ? SchemaCatalog.SectionClass(section, type) : null;
        if (IsRoomFile(path))
            return SchemaCatalog.Section(typeof(Room), type);
        return IsMBasesFile(path) ? SchemaCatalog.Section(typeof(MBasesIni), type) : null;
    }

    private Map Current
    {
        get
        {
            Refresh();
            return map;
        }
    }

    private void Refresh()
    {
        if (revision == session.CacheRevision)
            return;
        revision = session.CacheRevision;
        if (read.Length > 0 && read.All(r => ReferenceEquals(StateOf(r.Path), r.State)))
            return;
        var used = new List<(string, object?)>();
        var cache = new Dictionary<string, IReadOnlyList<Section>>(VfsPath.Comparer);
        FileSystem? view = null;
        IReadOnlyList<Section> Read(string path)
        {
            if (cache.TryGetValue(path, out var known))
                return known;
            var state = StateOf(path);
            used.Add((path, state));
            IReadOnlyList<Section> sections;
            if (state is IReadOnlyList<Section> working)
                sections = working;
            else
            {
                view ??= session.EditView.CreateFileSystem();
                sections = view.FileExists(path) ? IniDocument.Parse(path, view.ReadAllBytes(path)).Sections : [];
            }
            cache[path] = sections;
            return sections;
        }

        var next = new Map();
        var other = new HashSet<string>(VfsPath.Comparer);
        var introRooms = new HashSet<string>(VfsPath.Comparer);
        if (session.UniversePath is { } universe)
        {
            var ini = freelancerIni();
            foreach (var section in Read(universe).Where(s => Is(s.Name, "Base")))
            {
                var nickname = LastValue(section, NicknameKey);
                var basePath = LastValue(section, FileKey) is { } file ? DataPaths.Resolve(ini, file) : null;
                var rooms = new List<BaseRoomFile>();
                if (basePath != null)
                {
                    if (next.FileSet.Add(basePath))
                        next.Files.Add(basePath);
                    foreach (var room in Read(basePath).Where(s => Is(s.Name, "Room")))
                    {
                        if (LastValue(room, FileKey) is not { } value || DataPaths.Resolve(ini, value) is not { } roomPath)
                            continue;
                        if (next.RoomSet.Add(roomPath))
                            next.RoomFiles.Add(roomPath);
                        var isIntro = nickname?.StartsWith(IntroPrefix, StringComparison.OrdinalIgnoreCase) == true;
                        (isIntro ? introRooms : other).Add(roomPath);
                        if (nickname != null)
                            next.AddUser(roomPath, nickname);
                        if (LastValue(room, NicknameKey) is { } roomName)
                            rooms.Add(new BaseRoomFile(roomName, roomPath));
                    }
                }
                if (nickname != null && next.Bases.TryAdd(nickname, new BaseEntry(basePath, rooms)))
                    next.Nicknames.Add(nickname);
            }
        }
        introRooms.ExceptWith(other);
        next.Intro.UnionWith(introRooms);
        (read, map) = (used.ToArray(), next);
    }

    /// <summary>The state of a file for the map: W of its document when the session has it open, else the edit
    /// view.</summary>
    private object? StateOf(string path) => session.OpenedDocument(path) is { } document ? document.Working : session.EditView;

    /// <summary>The first value of the last line of a key with a value, or null.</summary>
    private static string? LastValue(Section section, string key) =>
        section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0)?[0].ToString().Trim() is { Length: > 0 } value
            ? value
            : null;

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The base file and the rooms of one base.</summary>
    private sealed record BaseEntry(string? File, IReadOnlyList<BaseRoomFile> Rooms);

    /// <summary>The result of one read of the files.</summary>
    private sealed class Map
    {
        public static readonly Map Empty = new();

        public List<string> Files { get; } = [];
        public HashSet<string> FileSet { get; } = new(VfsPath.Comparer);
        public List<string> RoomFiles { get; } = [];
        public HashSet<string> RoomSet { get; } = new(VfsPath.Comparer);
        public HashSet<string> Intro { get; } = new(VfsPath.Comparer);
        public List<string> Nicknames { get; } = [];
        public Dictionary<string, BaseEntry> Bases { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Users { get; } = new(VfsPath.Comparer);

        public void AddUser(string roomPath, string nickname)
        {
            if (!Users.TryGetValue(roomPath, out var users))
                Users[roomPath] = users = [];
            if (!users.Contains(nickname, StringComparer.OrdinalIgnoreCase))
                users.Add(nickname);
        }
    }
}

/// <summary>
/// The keys of a <c>[BaseInfo]</c> section as retail reads them: the keys of the fork class, and
/// <c>ship_repair_cost</c>. Retail reads the cost of a ship repair at the base as a factor, with the default 0.33.
/// The fork does not read it. The editor reads the class only for its keys, and never parses a section into it.
/// </summary>
internal sealed class RetailBaseInfo : BaseInfo
{
    /// <summary>The factor of the cost of a ship repair at the base.</summary>
    [Entry("ship_repair_cost")]
    public float ShipRepairCost;
}
