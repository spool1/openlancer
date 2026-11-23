using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>
/// The plans of the bases (section C.5 of the bases design). Each plan gives one command. The command runs as one
/// entry of the edit history, also when it changes several files and the text of the mod. The agent tools and the
/// editor call the same plans.
/// </summary>
/// <remarks>
/// <para>Retail facts: the id of a base is the FLHash of its nickname, and the game drops a second base with the same
/// id. A mission condition holds at most 31 characters of a base nickname. The game links a base to the object whose
/// <c>base</c> line names the base and whose name id is the name id of the base. The OpenLancer engine shows the rooms of
/// a base whose nickname starts with "intro" in the main menu.</para>
/// <para>A new base file goes to <c>DATA\MODS\&lt;mod id&gt;\BASES\&lt;base&gt;.ini</c> (see
/// <see cref="ContentPaths.NewBaseFile"/>). Its <c>[Base]</c> names it relative to the data folder.</para>
/// </remarks>
public static partial class BasePlans
{
    /// <summary>The detail code of a template base without a base file with rooms.</summary>
    public const string NoBaseFile = "NoBaseFile";

    /// <summary>The detail code of a template base whose nickname starts with "intro": a scene of the main menu.</summary>
    public const string IntroTemplate = "IntroTemplate";

    /// <summary>The detail code of a system without a file in the edit view, or with a read-only file. Only a plan with
    /// a dock object needs the file.</summary>
    public const string NoSystemFile = "NoSystemFile";

    /// <summary>The detail code of a room that the template does not have.</summary>
    public const string RoomUnknown = "RoomUnknown";

    /// <summary>The detail code of a start room that the base does not keep.</summary>
    public const string StartRoomNotKept = "StartRoomNotKept";

    /// <summary>The rule of a base nickname: a letter, then letters, digits or <c>_</c>, 1 to 31 characters. A mission
    /// condition holds at most 31 characters of a base nickname.</summary>
    public const string NicknamePattern = "^[A-Za-z][A-Za-z0-9_]{0,30}$";

    private static readonly Regex NicknameRule = new(NicknamePattern, RegexOptions.CultureInvariant);

    /// <summary>The lines of the <c>[BaseInfo]</c> of the template that a new base file copies.</summary>
    private static readonly string[] InfoLines = ["price_variance", "ship_repair_cost"];

    /// <summary>The line of a <c>[Base]</c> that names the voice line of the owner of the base, which the bar characters
    /// speak.</summary>
    private const string RunBy = "BGCS_base_run_by";

    /// <summary>The prefix of the lines of a <c>[Base]</c> that give the models of the <c>$terrain_*</c> templates of
    /// its room scenes.</summary>
    private const string TerrainPrefix = "terrain_";

    /// <summary>Why a part of the request refuses the plan. It holds the file, the text, the detail code, the part of the
    /// request (see <see cref="BasePlan.RefusedPart"/>) and the section that the text names.</summary>
    private sealed record Refusal(string Path, string Message, string? Detail = null, string? Part = null,
        SectionRef? Target = null);

    /// <summary>
    /// Plans a new base of the active mod from a template base, as one entry of the edit history "Create base
    /// &lt;nickname&gt;". The new base uses the room files of the template.
    /// </summary>
    /// <remarks>
    /// <para>The entry adds, in this order:</para>
    /// <list type="number">
    /// <item>The <c>[Base]</c> of <c>universe.ini</c> with <c>nickname</c>, <c>system</c>, <c>strid_name</c> and
    /// <c>file</c>. Then come the <c>BGCS_base_run_by</c> and <c>terrain_*</c> lines of the template. The <c>[Base]</c>
    /// goes directly before the first <c>[system]</c>, as the merge puts it.</item>
    /// <item>The base file at the first free place of <see cref="ContentPaths.NewBaseFile"/>. Its <c>[BaseInfo]</c>
    /// has the start room and the <c>price_variance</c> and <c>ship_repair_cost</c> of the template. Then comes one
    /// <c>[Room]</c> for each room that the base keeps. A room names the room file of the template.</item>
    /// <item>A copy of a room file when a kept room has a door to a room that the base does not keep. The copy has no
    /// such door. The plan lists each copy in its fixes.</item>
    /// <item>The <c>[MBase]</c> at the end of <c>mBases.ini</c>, with <c>nickname</c> first, the faction and the
    /// <c>diff</c>. It has no <c>msg_id_prefix</c>, because the voice lines of a base name exist only for the bases of
    /// the game. Its children follow in the retail order. These are the <c>[MVendor]</c>, the <c>[BaseFaction]</c>
    /// sections without <c>mission_type</c> and <c>offers_missions</c>, the characters and the <c>[MRoom]</c> sections
    /// of the kept rooms (see <see cref="CharacterCopy"/>). Each copied fixture character gets its own name text.</item>
    /// <item>One <c>[NewsItem]</c> at the end of <c>news.ini</c> for each news item of the template, with the new base
    /// in place of its <c>base</c> lines.</item>
    /// <item>The dock objects of the request (see <see cref="DockSpec"/>).</item>
    /// </list>
    /// <para>The name goes into the text of the active mod with one key. The <c>strid_name</c> of the base and the
    /// <c>ids_name</c> of the station or the planet share the key. The games link a base to its object only when the two
    /// ids are equal. The infocard goes to the <c>ids_info</c> of that object.</para>
    /// <para>The market lines of the template stay out of this version of the plan.</para>
    /// </remarks>
    public static BasePlan Create(EditSession session, BaseSpec spec)
    {
        var nickname = spec.Nickname.Trim();
        var plan = new BasePlan($"Create base {nickname}");
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (session.ActiveMod is not { } active)
            return plan.Refuse("", "The profile has no active mod.");
        if (session.UniversePath is not { } universe ||
            session.OpenDocument(universe) is not { State.Exists: true } universeFile)
            return plan.Refuse("", "freelancer.ini names no universe.ini, so the game has no bases.");
        if (universeFile.ReadOnlyReason is { } universeReason)
            return plan.Refuse(universe, $"{SceneSections.FileName(universe)}: {universeReason}");
        if (NicknameProblem(nickname) is { } nicknameProblem)
            return plan.Refuse(universe, nicknameProblem, detail: MissionErrors.NameInvalid, part: "nickname");
        if (TakenBy(session, nickname) is var (takenText, taken))
            return plan.Refuse(universe, takenText, taken, MissionErrors.NameTaken, "nickname");
        if (session.SystemSection(spec.System.Trim()) is not { Id.Key: { } system })
            return plan.Refuse(universe, $"No [system] of universe.ini has the nickname {spec.System.Trim()}. Get the " +
                                         "systems from list_values with the list system.",
                detail: MissionErrors.ItemNotFound, part: "system");
        var name = spec.Name.Trim();
        if (name.Length == 0)
            return plan.Refuse(universe, "The base needs a name. The game shows it in the base menus, in the trade lists " +
                                         "and on the dock object.", detail: MissionErrors.FieldValue, part: "name");
        if (BaseTemplate.Of(session, spec.Template, out var templateRefusal) is not { } template)
            return plan.Refuse(universe, templateRefusal!.Value.Message, detail: templateRefusal.Value.Detail,
                part: "template");

        var kept = new List<TemplateRoom>();
        foreach (var room in spec.Rooms ?? template.Rooms.Select(r => r.Nickname))
        {
            if (template.Room(room) is not { } found)
                return plan.Refuse(universe, $"The template {template.Nickname} has no room {room.Trim()}. Its rooms are " +
                                             $"{SceneSections.Join(template.Rooms.Select(r => r.Nickname))}.",
                    detail: RoomUnknown, part: "rooms");
            if (!kept.Contains(found))
                kept.Add(found);
        }
        var startName = spec.StartRoom ?? template.View.StartRoom;
        if (startName == null || kept.FirstOrDefault(r => SceneSections.Is(r.Nickname, startName.Trim())) is not { } start)
            return plan.Refuse(universe, startName == null
                    ? $"The template {template.Nickname} has no start room. Give the start room of the base."
                    : $"The start room {startName.Trim()} is not one of the rooms that the base keeps.",
                detail: StartRoomNotKept, part: "start_room");

        var problems = new List<string>();
        var texts = new SectionCopies(session, problems.Add);
        var baseId = new SectionIdentity("Base", nickname);
        if (texts.New(StringKind.String, name, baseId, "strid_name") is not { } nameValue)
            return plan.Refuse(universe, problems[0]);
        var path = ContentPaths.FreePath(session, ContentPaths.NewBaseFile(active.Id, nickname));
        var binary = session.IsBinaryFile(universe);
        if (ScenePlans.Entries(
            [
                ("nickname", nickname), ("system", system), ("strid_name", nameValue.ToString()!),
                ("file", ContentPaths.DataValue(path)!),
            ], binary, out var lineError) is not { } baseEntries)
            return plan.Refuse(universe, lineError!);
        baseEntries.AddRange(Copied(template.Entry, e => SceneSections.Is(e.Name, RunBy) ||
                                                         e.Name.StartsWith(TerrainPrefix, StringComparison.OrdinalIgnoreCase),
            binary));
        var (files, fileCommands, fixes) = BaseFile(session, texts, active.Id, nickname, path, template, kept, start);
        List<EditCommand> commands = [EditCommand.AddSection(universe, "Base", baseEntries), .. fileCommands];

        var warnings = new List<Diagnostic>(template.Warnings);
        var adds = new List<SectionRef> { new(universe, baseId), new(path, new SectionIdentity("BaseInfo", nickname)) };
        adds.AddRange(kept.Select(r => new SectionRef(path, new SectionIdentity("Room", r.Nickname))));
        if (MBaseCommands(session, spec, template, kept, nickname, texts, warnings) is { } bar)
        {
            if (bar.Refusal is { } barRefusal)
                return plan.Refuse(barRefusal.Path, barRefusal.Message);
            commands.AddRange(bar.Commands);
            adds.AddRange(bar.Adds);
            plan.MBase = bar.Adds[0];
            plan.Characters = bar.Characters;
        }
        var news = NewsCommands(session, template, nickname);
        if (news.Refusal is { } newsRefusal)
            return plan.Refuse(newsRefusal.Path, newsRefusal.Message);
        commands.AddRange(news.Commands);
        adds.AddRange(news.Adds);
        var (dock, dockRefusal) = DockCommands(session, spec, template, system, nickname, nameValue, texts);
        if (dockRefusal != null)
            return plan.Refuse(dockRefusal.Path, dockRefusal.Message, dockRefusal.Target, dockRefusal.Detail, dockRefusal.Part);
        if (problems.Count > 0)
            return plan.Refuse(universe, problems[0]);

        plan.Base = new SectionRef(universe, baseId);
        if (dock != null)
        {
            commands.AddRange(dock.Commands);
            adds.AddRange(dock.Adds);
            plan.Changes = dock.Changes;
            plan.DockObject = dock.Named;
            plan.Ring = dock.Ring;
        }
        if (dock is not { Docks: true })
            warnings.Add(Diagnostic.Warning(universe, $"No object docks with {nickname} yet. The player cannot dock there " +
                                                      $"until an object of {system} docks with it.") with
            {
                Target = plan.Base,
            });
        plan.File = path;
        plan.Files = files;
        plan.News = news.Adds;
        plan.Adds = adds;
        plan.Fixes = fixes;
        plan.Warnings = warnings;
        plan.SelectAfter = plan.DockObject ?? plan.Base;
        plan.Command = EditCommand.Group(plan.Label, [.. commands, .. texts.Commands], session.Snapshot());
        return plan;
    }

    /// <summary>
    /// The base file of a new base and the copies of its room files (see <see cref="Create"/>). Each file is one copy of
    /// <see cref="SectionCopies.CopyTo"/>. The result holds the new files, their commands and a fix for each copy of a
    /// room file.
    /// </summary>
    private static (List<string> Files, List<EditCommand> Commands, List<PlannedFix> Fixes) BaseFile(EditSession session,
        SectionCopies texts, string modId, string nickname, string path, BaseTemplate template,
        IReadOnlyList<TemplateRoom> kept, TemplateRoom start)
    {
        var files = new List<string> { path };
        var fixes = new List<PlannedFix>();
        var roomCopies = new List<EditCommand>();
        var copies = new List<SectionCopy>
        {
            new(SectionEdits.Build("BaseInfo",
            [
                new EntryView("nickname", [new StringValue(nickname)]),
                new EntryView("start_room", [new StringValue(start.Nickname)]),
                .. template.Info!.Where(e => InfoLines.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).Select(Line),
            ])),
        };
        foreach (var room in kept)
        {
            var source = room.Section;
            if (RoomCopy(session, texts, modId, nickname, template, kept, room) is var (copy, copyCommand, gone))
            {
                files.Add(copy);
                roomCopies.Add(copyCommand);
                source = SectionEdits.WithLines(source, "file", [[new StringValue(ContentPaths.DataValue(copy)!)]]);
                var oneRoom = gone.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
                var doors = gone.Count == 1 ? "door" : "doors";
                fixes.Add(new PlannedFix(new SectionRef(path, new SectionIdentity("Room", room.Nickname)), "file",
                    $"The room {room.Nickname} uses a copy of its room file without the {doors} to " +
                    $"{SceneSections.Join(gone)}, because the base does not keep {(oneRoom ? "that room" : "those rooms")}. " +
                    $"The copy is {copy}."));
            }
            copies.Add(new SectionCopy(source));
        }
        return (files, [texts.CopyTo(path, copies, newFile: true), .. roomCopies], fixes);
    }

    /// <summary>Why a text cannot be the nickname of a new base, or null.</summary>
    internal static string? NicknameProblem(string nickname)
    {
        if (!NicknameRule.IsMatch(nickname))
            return $"The base nickname '{nickname}' is not valid. Use 1 to {BaseChecks.MissionNameLength} letters, digits " +
                   "or '_', and start with a letter. A mission condition holds at most " +
                   $"{BaseChecks.MissionNameLength} characters of a base nickname.";
        return nickname.StartsWith(BaseTemplate.IntroPrefix, StringComparison.OrdinalIgnoreCase)
            ? "A base nickname cannot start with 'intro'. The OpenLancer engine shows such a base in the main menu."
            : null;
    }

    /// <summary>
    /// Why a nickname is taken, with the section that takes it, or null. A <c>[Base]</c> of W takes the nickname
    /// and each nickname with its FLHash. An <c>[MBase]</c> of W takes its nickname too, because a new <c>[MBase]</c>
    /// cannot have the nickname of another one.
    /// </summary>
    internal static (string Message, SectionRef Section)? TakenBy(EditSession session, string nickname)
    {
        var hash = FLHash.CreateID(nickname);
        var bases = session.UniversePath is { } universe
            ? session.Sections(universe).Where(s => SceneSections.Is(s.Id.Type, "Base") && s.Id.Key != null).ToList()
            : [];
        if (bases.FirstOrDefault(s => SceneSections.Is(s.Id.Key!, nickname)) is { } same)
            return ($"The base nickname {nickname} is taken by the base {same.Id.Key}.", same);
        if (bases.FirstOrDefault(s => FLHash.CreateID(s.Id.Key!) == hash) is { } hashed)
            return ($"The base nickname {nickname} has the FLHash of the base {hashed.Id.Key}. The game keeps only one " +
                    "base with an id.", hashed);
        return session.Bases.MBasesPath is { } mbases &&
               session.Sections(mbases).FirstOrDefault(s => SceneSections.Is(s.Id.Type, "MBase") &&
                                                            SceneSections.Is(s.Id.Key ?? "", nickname)) is { } mbase
            ? ($"The base nickname {nickname} is taken by an [MBase] of mBases.ini.", mbase)
            : null;
    }

    /// <summary>
    /// The copy of the room file of a kept room, or null when the room needs no copy. A room needs a copy when its file
    /// has a door to a room of the template that the base does not keep. The result holds the first free place of
    /// <see cref="ContentPaths.NewRoomFile"/>, the command of the file without those doors, and the rooms of the
    /// doors.
    /// </summary>
    private static (string Path, EditCommand Command, List<string> Rooms)? RoomCopy(EditSession session,
        SectionCopies texts, string modId, string nickname, BaseTemplate template, IReadOnlyList<TemplateRoom> kept,
        TemplateRoom room)
    {
        bool Dropped(string? target) => target != null && template.Room(target) != null &&
                                        !kept.Any(k => SceneSections.Is(k.Nickname, target));
        if (room.View.File is not { } file || !room.View.Doors.Any(d => Dropped(d.Target)) ||
            session.OpenDocument(file) is not { } document)
            return null;
        var gone = new List<string>();
        var sections = new List<SectionCopy>();
        foreach (var section in document.Working)
        {
            if (SceneSections.Is(section.Name, "Hotspot") &&
                SceneSections.Is(BaseTemplate.Last(section, "behavior") ?? "", "ExitDoor") &&
                BaseTemplate.Last(section, "room_switch") is { } target && Dropped(target))
            {
                gone.Add(target);
                continue;
            }
            sections.Add(new SectionCopy(section));
        }
        var path = ContentPaths.FreePath(session, ContentPaths.NewRoomFile(modId, nickname, room.Nickname));
        return (path, texts.CopyTo(path, sections, newFile: true), gone);
    }

    /// <summary>
    /// The news items of a new base (owner decision of 2026-11-21). Each news item of the template gets a new
    /// <c>[NewsItem]</c> at the end of <c>news.ini</c>. The copy has each line of the item except its <c>base</c> lines,
    /// and one <c>base</c> line of the new base. Without news, the bar of the base shows no news button. A patch always
    /// adds a <c>[NewsItem]</c>, because it has no key.
    /// </summary>
    private static (List<EditCommand> Commands, List<SectionRef> Adds, Refusal? Refusal) NewsCommands(EditSession session,
        BaseTemplate template, string nickname)
    {
        if (template.News.Count == 0 || session.Bases.NewsPath is not { } path)
            return ([], [], null);
        if (session.OpenDocument(path) is { ReadOnlyReason: { } reason })
            return ([], [], new Refusal(path, $"{SceneSections.FileName(path)}: {reason}"));
        var binary = session.IsBinaryFile(path);
        var next = session.Sections(path).Count(s => SceneSections.Is(s.Id.Type, BaseTemplate.NewsType));
        var commands = new List<EditCommand>();
        var adds = new List<SectionRef>();
        foreach (var item in template.News)
        {
            var entries = new List<EntryView>();
            foreach (var line in item)
            {
                if (!SceneSections.Is(line.Name, "base"))
                    entries.Add(new EntryView(line.Name, line.Select(v => SectionCopies.Stored(v, binary)).ToList()));
                else if (!entries.Any(e => SceneSections.Is(e.Name, "base")))
                    entries.Add(new EntryView("base", [new StringValue(nickname)]));
            }
            commands.Add(EditCommand.AddSection(path, BaseTemplate.NewsType, entries));
            adds.Add(new SectionRef(path, new SectionIdentity(BaseTemplate.NewsType, null, null, next++)));
        }
        return (commands, adds, null);
    }

    /// <summary>The lines of a section that a test keeps, with their values, as a file stores a new value (see
    /// <see cref="SectionCopies.Stored"/>).</summary>
    /// <param name="binary">True for lines of a binary file.</param>
    private static IEnumerable<EntryView> Copied(Section section, Func<Entry, bool> keep, bool binary) =>
        section.Where(keep).Select(e => new EntryView(e.Name, e.Select(v => SectionCopies.Stored(v, binary)).ToList()));

    /// <summary>A line of a template in a copy: a text of a mod gets a copy for the new section (see
    /// <see cref="SectionCopies.Copy(Entry, int, SectionIdentity)"/>), and each value is as a file stores it.</summary>
    /// <param name="copy">The new section, for the key of a copied text.</param>
    /// <param name="binary">True for a line of a binary file.</param>
    private static EntryView CopiedLine(Entry line, SectionIdentity copy, SectionCopies texts, bool binary) =>
        new(line.Name, line.Select((_, i) => SectionCopies.Stored(texts.Copy(line, i, copy), binary)).ToList());

    /// <summary>A line of a section with its values.</summary>
    private static EntryView Line(Entry entry) => new(entry.Name, entry.ToArray());

    /// <summary>A copy of a section with only the lines that a test keeps, for <see cref="SectionCopies.CopyTo"/>.</summary>
    private static Section Only(Section section, Func<Entry, bool> keep) =>
        SectionEdits.Build(section.Name, section.Where(keep).Select(Line));
}
