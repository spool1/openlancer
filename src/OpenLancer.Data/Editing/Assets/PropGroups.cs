using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing.Assets;

/// <summary>
/// The draft of "Save as prop group" (section 9.3 of the M23 design): the members with their positions relative to the
/// anchor, the lines that the group leaves out, the warnings, the refusals and the texts.
/// </summary>
public sealed class PropGroupDraft
{
    internal PropGroupDraft(IReadOnlyList<PropMember> members, Vector3 anchor, IReadOnlyList<Diagnostic> leftOut,
        IReadOnlyList<Diagnostic> warnings, IReadOnlyList<Diagnostic> refusals,
        IReadOnlyDictionary<StringPlaceholder, string> texts)
    {
        Members = members;
        Anchor = anchor;
        LeftOut = leftOut;
        Warnings = warnings;
        Refusals = refusals;
        Texts = texts;
    }

    /// <summary>The members in file order, the parents before their children.</summary>
    public IReadOnlyList<PropMember> Members { get; }

    /// <summary>The center of the box that holds the member positions, in whole numbers (decision D287).</summary>
    public Vector3 Anchor { get; }

    /// <summary>The lines that the group leaves out, each with its reason (decision D286).</summary>
    public IReadOnlyList<Diagnostic> LeftOut { get; }

    /// <summary>The warnings, for example a member that names an entry that the mod did not save.</summary>
    public IReadOnlyList<Diagnostic> Warnings { get; }

    /// <summary>Why the items cannot be a group. A draft with a refusal cannot be written.</summary>
    public IReadOnlyList<Diagnostic> Refusals { get; }

    /// <summary>The nickname that the dialog starts with: the first member id with <c>_group</c>, made valid
    /// (decision D294).</summary>
    public string SuggestedNickname => PropGroups.ValidNickname(Members.Count > 0 ? Members[0].Member + "_group" : "group");

    /// <summary>The texts of the members, by the placeholders that their lines hold.</summary>
    internal IReadOnlyDictionary<StringPlaceholder, string> Texts { get; }
}

/// <summary>
/// The prop groups of the mods of a session (decisions D283 to D294 of the M23 design): read, draft, write, rename,
/// delete and place. A group is a set of separate objects, zones and lights in a plain INI file in the folder
/// <c>props</c> of a mod. The merge, the game and the export of the game package ignore the folder.
/// </summary>
/// <remarks>
/// <para>The editor writes a group file at once under the lock of the mod, as Unity writes a prefab asset at once.
/// Ctrl+S does not save it, and Undo does not take it back (decision D283). A placement is one entry of the edit
/// history, and the copies keep no link to the group, as a Unity prefab that you unpack (decision D288).</para>
/// <para>The groups of the active mod and of the mods that load before it show. The edit view does not show the later
/// mods, so their groups do not show (decision D289).</para>
/// </remarks>
public static class PropGroups
{
    /// <summary>A number that changes at each write, rename and delete, and when <see cref="Refresh"/> finds a
    /// change.</summary>
    public static int Version(EditSession session) => session.PropGroupStore.Version;

    /// <summary>The groups of the lower mods in load order, then of the active mod. A group of the active mod is
    /// editable.</summary>
    public static IReadOnlyList<PropGroup> Of(EditSession session) => session.PropGroupStore.Groups;

    /// <summary>Reads the folders again when a file changed on disk, for example when another program wrote a group. The
    /// editor calls it when its window gets the focus, as Unity refreshes its assets.</summary>
    public static void Refresh(EditSession session) => session.PropGroupStore.Refresh();

    /// <summary>
    /// Why a text cannot be the nickname of a new group of the active mod, or null (decision D294): a nickname has
    /// lowercase letters, digits, <c>-</c> and <c>_</c>, starts with a letter, and no other group of the active mod has
    /// it without letter case.
    /// </summary>
    /// <param name="except">A group that can keep the nickname, for a rename, or null.</param>
    public static string? NicknameProblem(EditSession session, string nickname, PropGroup? except = null)
    {
        if (nickname.Length == 0)
            return "Type a nickname.";
        if (!IsValidNickname(nickname))
            return "A nickname uses lowercase letters, digits, '-' and '_', and starts with a letter.";
        return Of(session).Any(g => g.ModId == null && g.File != except?.File &&
                                    g.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase))
            ? "A prop group with this nickname exists. Type another nickname."
            : null;
    }

    /// <summary>Why a text cannot be the name of a group, or null. The name is free ASCII text without <c>;</c>, which
    /// starts a comment, and without a line break.</summary>
    public static string? NameProblem(string name) =>
        !Ascii.IsValid(name) || name.Any(char.IsControl) || name.Contains(';')
            ? "A name uses ASCII text without ';' and without a line break."
            : null;

    /// <summary>A valid nickname from a text: lowercase, each other character as <c>_</c>, and <c>g</c> before a first
    /// character that is not a letter.</summary>
    internal static string ValidNickname(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' ? c : '_')
            .ToArray();
        var result = new string(chars);
        return result.Length > 0 && char.IsAsciiLetterLower(result[0]) ? result : "g" + result;
    }

    /// <summary>True when a text has the form of a nickname of a group (see <see cref="NicknameProblem"/>).</summary>
    internal static bool IsValidNickname(string nickname) =>
        nickname.Length > 0 && char.IsAsciiLetterLower(nickname[0]) &&
        nickname.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>
    /// The draft of a group of items (section 9.3 of the M23 design, decisions D285 to D287 and D291): each item and, for
    /// an object, each object of the file whose <c>parent</c> names an object of the set, again and again. The member id
    /// is the nickname without a leading <c>&lt;system&gt;_</c>. The lines follow the copy builder of the Scene: the
    /// <c>base</c> line goes, and a <c>parent</c> or a <c>ring</c> line names a member id or goes. A text of a mod goes
    /// into the texts of the group.
    /// </summary>
    /// <param name="items">The objects, zones and lights of one system file.</param>
    public static PropGroupDraft Draft(EditSession session, IReadOnlyList<SectionRef> items)
    {
        var refusals = new List<Diagnostic>();
        var set = new List<SectionRef>();
        foreach (var item in items)
            ScenePlans.Collect(session, item, set);
        if (set.Count == 0)
            refusals.Add(Diagnostic.Error("", "Select an object, a zone or a light first."));
        foreach (var section in set)
        {
            if (session.Get(section) is not { } view || SceneSections.KindOf(session, section) is not { } kind)
                refusals.Add(Diagnostic.Error(section.Path, $"{section.Id}: a prop group holds only objects, zones and " +
                                                            "lights of a system file.") with { Target = section });
            else if (SceneSections.ConnectionKind(session, view) is { } connection)
                refusals.Add(Diagnostic.Error(section.Path, SceneSections.ConnectionMessage("put into a prop group",
                    section, view, connection)) with { Target = section });
            else if (kind is SceneKind.Nebula or SceneKind.Asteroids ||
                     kind == SceneKind.Zone && ScenePlans.HoldsVolume(session, section))
                refusals.Add(Diagnostic.Error(section.Path, $"{SceneSections.Label(kind, section)}: nebulae, asteroid " +
                                                            "fields and their zones come with milestone M26.")
                    with { Target = section });
        }
        if (set.Select(s => s.Path).Distinct(VfsPath.Comparer).Count() > 1)
            refusals.Add(Diagnostic.Error(set[0].Path, "A prop group takes the items of one system file."));
        if (refusals.Count > 0)
            return new PropGroupDraft([], Vector3.Zero, [], [], refusals, new Dictionary<StringPlaceholder, string>());

        var path = set[0].Path;
        var system = session.SystemNicknameOf(path) ?? "";
        // The member ids: the nickname without the system (review finding R25), numbered as a copy when one repeats.
        var ids = new Dictionary<SectionRef, string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in set)
        {
            var nickname = section.Id.Key ?? "";
            var id = system.Length > 0 && nickname.Length > system.Length + 1 &&
                     nickname.StartsWith(system + "_", StringComparison.OrdinalIgnoreCase)
                ? nickname[(system.Length + 1)..]
                : nickname;
            if (taken.Contains(id))
                id = session.Nicknames.CopyName(id, taken, taken.Contains);
            taken.Add(id);
            ids[section] = id;
        }

        var (objects, zones) = ScenePlans.CopiedNames(set, ids);
        var leftOut = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        var texts = new Dictionary<StringPlaceholder, string>();
        var copiedTexts = new Dictionary<string, StringPlaceholder>();
        var positions = new Dictionary<SectionRef, Vector3>();
        var lines = new Dictionary<SectionRef, List<EntryView>>();
        foreach (var section in set)
        {
            var id = ids[section];
            var source = session.WorkingSection(section)!;
            var type = PropGroupFile.MemberType(source.Name)!;
            void LeaveOut(EntryView line, string reason) =>
                leftOut.Add(Diagnostic.Warning(path, $"{id}: the '{LineLabel(line)}' line goes. {reason}") with
                {
                    Target = section, Key = line.Name,
                });

            // The texts first: a line whose text gets no key goes.
            var withTexts = new List<EntryView>();
            foreach (var line in ScenePlans.Lines(source))
            {
                if (TextLine(session, type, id, line, texts, copiedTexts, out var reason) is { } copied)
                    withTexts.Add(copied);
                else
                    LeaveOut(line, reason!);
            }
            var copy = ScenePlans.CopyLines(type, withTexts, "nickname", PropGroupFile.MemberKey, id, objects, zones,
                keepOutside: false, (_, _, value) => value, (line, why) => LeaveOut(line, why == ScenePlans.LeftOutReason.Base
                    ? "A base has one object in space, and the group copies are new objects."
                    : $"{line.Values[0].ToString().Trim()} is not a member of the group."));
            var memberLines = new List<EntryView>();
            foreach (var line in copy)
            {
                if (SceneSections.Is(line.Name, PropGroupFile.MemberKey))
                    continue;
                if (PropGroupFile.LineText(type, line) == null)
                {
                    LeaveOut(line, $"A text file cannot hold it: {PropGroupFile.LineError(type, line)}");
                    continue;
                }
                memberLines.Add(line);
            }
            warnings.AddRange(UnsavedWarnings(session, section, id, memberLines));
            positions[section] = session.Get(section) is { } view && view.Lines("pos").LastOrDefault() is { } pos &&
                                 PropGroupFile.TryPosition(pos.Values, out var position)
                ? position
                : Vector3.Zero;
            lines[section] = memberLines;
        }

        // The anchor is the center of the box that holds the member positions (decision D287).
        var min = positions.Values.Aggregate(Vector3.Min);
        var max = positions.Values.Aggregate(Vector3.Max);
        var anchor = Round((min + max) / 2);
        var members = new List<PropMember>();
        foreach (var section in set)
        {
            var relative = positions[section] - anchor;
            var memberLines = lines[section];
            var posLine = new EntryView("pos", [Number(relative.X), Number(relative.Y), Number(relative.Z)]);
            var at = memberLines.FindIndex(l => SceneSections.Is(l.Name, "pos"));
            if (at >= 0)
                memberLines[at] = posLine;
            else
                memberLines.Insert(0, posLine);
            members.Add(new PropMember(ids[section], PropGroupFile.MemberType(section.Id.Type)!, relative, memberLines));
        }
        return new PropGroupDraft(members, anchor, leftOut, warnings, [], texts);
    }

    /// <summary>
    /// Writes a draft as a new group of the active mod, at once under the lock of the mod (decision D283): the file of
    /// the texts first, then the group file. So a group file never names a text that is not on disk.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session takes no edits, or the draft has a refusal.</exception>
    /// <exception cref="ArgumentException">The nickname or the name is not valid.</exception>
    /// <exception cref="IOException">A write failed.</exception>
    public static PropGroup Write(EditSession session, PropGroupDraft draft, string nickname, string name)
    {
        var mod = Writable(session);
        if (draft.Refusals.Count > 0)
            throw new InvalidOperationException(draft.Refusals[0].Message);
        if (NicknameProblem(session, nickname) is { } problem)
            throw new ArgumentException(problem, nameof(nickname));
        if (NameProblem(name) is { } nameProblem)
            throw new ArgumentException(nameProblem, nameof(name));
        var file = Path.Combine(mod.Folder, Mod.PropsFolder, nickname + PropGroupFile.Extension);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var textsPath = PropGroupFile.TextsPath(file);
        var texts = PropGroupFile.WriteTexts(textsPath, draft.Texts);
        var bytes = PropGroupFile.Write(nickname, name.Trim().Length > 0 ? name.Trim() : nickname, draft.Members);
        if (texts != null)
            AtomicFile.WriteAllBytes(textsPath, texts);
        else if (File.Exists(textsPath))
            File.Delete(textsPath);
        AtomicFile.WriteAllBytes(file, bytes);
        session.PropGroupStore.Changed();
        return Of(session).First(g => g.ModId == null && g.File.Equals(file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gives a group of the active mod a new nickname (decision D294): the group file and the file of its texts get the
    /// new name, and the <c>nickname</c> line of the group file follows. The rest of the file stays as it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session takes no edits, or the group is not of the active
    /// mod.</exception>
    /// <exception cref="ArgumentException">The nickname is not valid.</exception>
    /// <exception cref="IOException">A move failed.</exception>
    public static PropGroup Rename(EditSession session, PropGroup group, string nickname)
    {
        var mod = Writable(session);
        if (!group.Editable)
            throw new InvalidOperationException($"The prop group {group.Name} is of mod '{group.ModId}', so the editor " +
                                                "cannot rename it.");
        if (nickname.Equals(group.Nickname, StringComparison.Ordinal))
            return group;
        if (NicknameProblem(session, nickname, group) is { } problem)
            throw new ArgumentException(problem, nameof(nickname));
        var file = Path.Combine(mod.Folder, Mod.PropsFolder, nickname + PropGroupFile.Extension);
        var text = IniDocument.TextEncoding.GetString(SharedFile.ReadAllBytes(group.File));
        AtomicFile.WriteAllBytes(file, IniDocument.TextEncoding.GetBytes(WithNickname(text, nickname)));
        if (File.Exists(group.TextsFile))
            File.Move(group.TextsFile, PropGroupFile.TextsPath(file), overwrite: true);
        // A rename that changes only the letter case keeps the file that the write replaced.
        if (!file.Equals(group.File, StringComparison.OrdinalIgnoreCase))
            File.Delete(group.File);
        session.PropGroupStore.Changed();
        return Of(session).First(g => g.ModId == null && g.File.Equals(file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes a group of the active mod: its file and the file of its texts (decision D294). The copies in the
    /// systems stay. Undo does not take a delete back, as in the Unity Project window.</summary>
    /// <exception cref="InvalidOperationException">The session takes no edits, or the group is not of the active
    /// mod.</exception>
    /// <exception cref="IOException">A delete failed.</exception>
    public static void Delete(EditSession session, PropGroup group)
    {
        Writable(session);
        if (!group.Editable)
            throw new InvalidOperationException($"The prop group {group.Name} is of mod '{group.ModId}', so the editor " +
                                                "cannot delete it.");
        if (File.Exists(group.TextsFile))
            File.Delete(group.TextsFile);
        File.Delete(group.File);
        session.PropGroupStore.Changed();
    }

    /// <summary>
    /// Plans a placement of a group in a system (section 9.4 of the M23 design, decisions D288 and D290). Each member
    /// becomes a separate item with the nickname <c>&lt;system&gt;_&lt;member&gt;</c> in lowercase, numbered as a copy
    /// when another item has it. Its position is the anchor plus its relative position, in whole numbers. The members go
    /// to the end of the system file, the parents first. The plan runs the list checks of each planned line first: a
    /// line with an error never reaches W.
    /// </summary>
    /// <param name="anchor">The point of the drop. The anchor of the group goes there.</param>
    /// <param name="parentRow">The object of a Hierarchy row that gets each root object as a child, or null.</param>
    /// <param name="leaveOutErrorLines">True to leave out each line with an error and place the rest ("Place without
    /// these lines"). Without it, a plan with such lines has no command and lists them in
    /// <see cref="ScenePlan.PlacementErrors"/>.</param>
    public static ScenePlan Place(EditSession session, string system, PropGroup group, Vector3 anchor,
        SectionRef? parentRow, bool leaveOutErrorLines = false)
    {
        var plan = new ScenePlan(SceneAction.Place, $"Place {group.Name}");
        if (group.Problems.Count > 0)
            return ScenePlans.Refused(plan, Diagnostic.Error(group.File, $"The prop group {group.Name} has a problem, so " +
                                                                         $"it does not place: {group.Problems[0].Message}"));
        if (group.Members.Count == 0)
            return ScenePlans.Refused(plan, Diagnostic.Error(group.File, $"The prop group {group.Name} has no members."));
        if (ScenePlans.SystemFileProblem(session, system, out var path) is { } fileProblem)
            return ScenePlans.Refused(plan, fileProblem);
        var binary = session.IsBinaryFile(path);

        // The names (decision D288): <system>_<member>, then the numbering of decision D262 when it is taken.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<PropMember, string>();
        foreach (var member in group.Members)
        {
            var stem = $"{system}_{member.Member}".ToLowerInvariant();
            Func<string, bool> taken = SceneSections.Is(member.Type, SceneSections.LightType)
                ? n => reserved.Contains(n) || session.Nicknames.LightTaken(path, n) != null
                : n => reserved.Contains(n) || session.Nicknames.Taken(n) != null;
            var name = taken(stem) ? session.Nicknames.CopyName(stem, reserved, taken) : stem;
            reserved.Add(name);
            names[member] = name;
        }
        var objects = group.Members.Where(m => SceneSections.Is(m.Type, SceneSections.ObjectType))
            .ToDictionary(m => m.Member, m => names[m], StringComparer.OrdinalIgnoreCase);
        var zones = group.Members.Where(m => SceneSections.Is(m.Type, SceneSections.ZoneType))
            .ToDictionary(m => m.Member, m => names[m], StringComparer.OrdinalIgnoreCase);
        var planned = objects.Values.Concat(zones.Values).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var refusals = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        var errors = new List<PlacementError>();
        var problems = new List<string>();
        var texts = new SectionCopies(session, problems.Add);
        var built = new List<(PropMember Member, List<EntryView> Lines)>();
        foreach (var member in ParentsFirst(group.Members))
        {
            var name = names[member];
            var copyId = new SectionIdentity(member.Type, name);
            var source = new List<EntryView> { new(PropGroupFile.MemberKey, [new StringValue(member.Member)]) };
            source.AddRange(member.Lines);
            var lines = ScenePlans.CopyLines(member.Type, source, PropGroupFile.MemberKey, "nickname", name, objects, zones,
                keepOutside: false, (key, i, value) => GroupText(group, texts, member.Type, key, i, value, copyId),
                (line, why) => warnings.Add(Diagnostic.Warning(path, $"{member.Member}: the '{LineLabel(line)}' line " +
                                                                     (why == ScenePlans.LeftOutReason.Base
                                                                         ? "goes. A base has one object in space."
                                                                         : "goes. It names no member of the group."))));
            // The position (decision D288): the anchor plus the relative position, in whole numbers.
            var position = new EntryView("pos", ReadLine("pos", ScenePlans.PositionText(anchor + member.Position), binary));
            var at = lines.FindIndex(l => SceneSections.Is(l.Name, "pos"));
            if (at >= 0)
                lines[at] = position;
            else
                lines.Insert(1, position);
            if (SceneSections.Is(member.Type, SceneSections.ObjectType))
            {
                if (parentRow?.Id.Key is { } parent && !lines.Any(l => SceneSections.Is(l.Name, "parent")))
                    lines.Add(new EntryView("parent", ReadLine("parent", parent, binary)));
                DockWithOtherSystem(session, system, member, lines, warnings);
            }

            // Each line takes the value types of the target file, as a patch line does.
            var typed = new List<EntryView>();
            foreach (var line in lines)
            {
                if (PropGroupFile.ValuesFor(member.Type, line, binary, out var error) is { } values)
                    typed.Add(new EntryView(line.Name, values));
                else
                    refusals.Add(Diagnostic.Error(path, $"{member.Member}: the '{LineLabel(line)}' line cannot go into " +
                                                        $"{SceneSections.FileName(path)}: {error}"));
            }
            if (PatchFormatter.KeyError(name, binary) is { } keyError)
                refusals.Add(Diagnostic.Error(path, $"{member.Member}: {keyError}"));
            built.Add((member, typed));
        }
        refusals.AddRange(problems.Select(p => Diagnostic.Error(path, p)));
        if (refusals.Count > 0)
            return ScenePlans.Refused(plan, [.. refusals]);

        // The list checks of each planned line (decision D290): a line with an error never reaches W.
        foreach (var (member, lines) in built)
        {
            var name = names[member];
            var remove = new List<(string Key, int Line)>();
            foreach (var problem in ListChecks.CheckPlanned(session, path, member.Type, name, lines, planned))
            {
                if (!problem.IsError)
                {
                    warnings.Add(problem.Diagnostic);
                    continue;
                }
                var required = problem.Line < 0 || problem.Rule is { Required: true };
                if (required)
                {
                    refusals.Add(Diagnostic.Error(path, RequiredMessage(member, problem)));
                    continue;
                }
                errors.Add(new PlacementError(member.Member, problem.Diagnostic.Key ?? "", problem.Value,
                    $"{problem.Reason} {problem.Consequence}", false));
                remove.Add((problem.Diagnostic.Key ?? "", problem.Line));
            }
            if (remove.Count == 0 || !leaveOutErrorLines)
                continue;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            lines.RemoveAll(l =>
            {
                var index = counts.GetValueOrDefault(l.Name);
                counts[l.Name] = index + 1;
                return remove.Any(r => SceneSections.Is(r.Key, l.Name) && r.Line == index);
            });
        }
        if (refusals.Count > 0)
            return ScenePlans.Refused(plan, [.. refusals]);
        if (errors.Count > 0 && !leaveOutErrorLines)
        {
            plan.PlacementErrors = errors;
            plan.Command = null;
            return plan;
        }
        warnings.AddRange(errors.Select(e => Diagnostic.Warning(path, $"{e.Member}: the '{e.Key}' line goes. {e.Message}")));

        var commands = built.Select(b => EditCommand.AddSection(path, b.Member.Type, b.Lines)).ToList();
        commands.AddRange(texts.Commands);
        var adds = built.Select(b => new SectionRef(path, new SectionIdentity(b.Member.Type, names[b.Member]))).ToList();
        foreach (var add in adds.Where(a => !SceneSections.Is(a.Id.Type, SceneSections.LightType)))
            warnings.AddRange(ScenePlans.DanglingWarnings(session, add.Id.Key!, path));
        plan.PlacementErrors = errors;
        plan.Adds = adds;
        plan.SelectAfter = adds[0];
        plan.Warnings = warnings;
        plan.Command = EditCommand.Group(plan.Label, commands, session.Snapshot());
        return plan;
    }

    /// <summary>The active mod of a session that takes edits.</summary>
    private static Mod Writable(EditSession session)
    {
        if (session.ReadOnlyReason is { } reason)
            throw new InvalidOperationException(reason);
        return session.ActiveMod!;
    }

    /// <summary>The members in file order, each parent before its children.</summary>
    private static List<PropMember> ParentsFirst(IReadOnlyList<PropMember> members)
    {
        var result = new List<PropMember>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var left = members.ToList();
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(m => ParentOf(m) is not { } parent || placed.Contains(parent) ||
                                                !members.Any(o => o.Member.Equals(parent, StringComparison.OrdinalIgnoreCase)))
                       ?? left[0];
            result.Add(next);
            placed.Add(next.Member);
            left.Remove(next);
        }
        return result;
    }

    private static string? ParentOf(PropMember member) =>
        SceneSections.Is(member.Type, SceneSections.ObjectType) &&
        member.Lines.LastOrDefault(l => SceneSections.Is(l.Name, "parent") && l.Values.Count > 0) is { } line
            ? line.Values[0].ToString().Trim()
            : null;

    /// <summary>
    /// Leaves out a <c>dock_with</c> line whose base is in another system (decision D286), with a warning. A base that no
    /// list has stays, so the list check finds it.
    /// </summary>
    private static void DockWithOtherSystem(EditSession session, string system, PropMember member, List<EntryView> lines,
        List<Diagnostic> warnings)
    {
        var at = lines.FindLastIndex(l => SceneSections.Is(l.Name, "dock_with") && l.Values.Count > 0);
        if (at < 0)
            return;
        var baseName = lines[at].Values[0].ToString().Trim();
        if (session.Lists.List("base")?.Find(baseName)?.Section is not { } baseSection ||
            session.Get(baseSection) is not { } view || SceneSections.Last(view, "system") is not { } baseSystem ||
            baseSystem.Equals(system, StringComparison.OrdinalIgnoreCase))
            return;
        lines.RemoveAll(l => SceneSections.Is(l.Name, "dock_with"));
        warnings.Add(Diagnostic.Warning(session.SystemPath(system) ?? system,
            $"{member.Member}: the base {baseName} is in {baseSystem}. The copy docks with no base. Add the Dockable " +
            "station component to pick a base."));
    }

    /// <summary>The message of an error of a required line, which refuses the whole placement (decision D290).</summary>
    private static string RequiredMessage(PropMember member, ListProblem problem) =>
        problem.Value != null && problem.Diagnostic.Key is { } key && SceneSections.Is(key, "archetype")
            ? $"{member.Member}: the archetype {problem.Value} is not in this profile. {problem.Consequence} Add the mod " +
              $"that has {problem.Value} to the profile."
            : $"{member.Member}: {problem.Reason} {problem.Consequence}";

    /// <summary>A value of a line of a member at its placement: a placeholder of the texts of the group becomes a new
    /// text of the active mod, as a copy of a section gets its text (section 9.4).</summary>
    private static ValueBase GroupText(PropGroup group, SectionCopies texts, string type, string key, int i,
        ValueBase value, SectionIdentity copy)
    {
        if (i != 0 || SchemaCatalog.TextKind(type, key) == null || !StringPlaceholder.TryParse(value, out var placeholder) ||
            !group.Texts.TryGetValue(placeholder, out var text))
            return value;
        return texts.Add(new CopiedText(placeholder.Kind, text, $"{group.File}|{placeholder}"), copy, key) ?? value;
    }

    /// <summary>
    /// A line of a member of a draft with its text (section 9.3): a text placeholder of the active mod and an id of a text
    /// of a mod become a placeholder of a text of the group. Each other line stays. Null when the text gets no key, with
    /// the reason.
    /// </summary>
    private static EntryView? TextLine(EditSession session, string type, string id, EntryView line,
        Dictionary<StringPlaceholder, string> texts, Dictionary<string, StringPlaceholder> copied, out string? reason)
    {
        reason = null;
        if (line.Values.Count == 0)
            return line;
        string? problem = null;
        if (SectionCopies.TextOf(session, type, line.Name, 0, line.Values[0], p => problem = p) is not { } text)
        {
            // A placeholder without text would name no text of the group. The save of the mod reports it too.
            if (problem == null && SchemaCatalog.TextKind(type, line.Name) != null &&
                StringPlaceholder.TryParse(line.Values[0], out var empty))
                problem = $"the mod has no text for {empty}.";
            reason = problem == null ? null : $"Its text is missing: {problem}";
            return problem == null ? line : null;
        }
        if (!copied.TryGetValue(text.Source, out var placeholder))
        {
            try
            {
                var key = ItemEdits.NewTextKeys(new SectionIdentity(type, id), line.Name)
                    .First(k => !texts.ContainsKey(new StringPlaceholder(text.Kind, k)));
                placeholder = new StringPlaceholder(text.Kind, key);
            }
            catch (ArgumentException e)
            {
                reason = $"Its text gets no key in the group: {e.Message}";
                return null;
            }
            texts[placeholder] = text.Text;
            copied[text.Source] = placeholder;
        }
        return new EntryView(line.Name, [new StringValue(placeholder.ToString()), .. line.Values.Skip(1)]);
    }

    /// <summary>
    /// The warnings of the lines of a member that name an entry of the active mod that the mod did not save (section 9.3):
    /// a placement in another session finds no such entry.
    /// </summary>
    private static IEnumerable<Diagnostic> UnsavedWarnings(EditSession session, SectionRef section, string id,
        IReadOnlyList<EntryView> lines)
    {
        foreach (var line in lines)
        {
            foreach (var rule in session.Lists.Rules(section, line.Name))
            {
                if (rule.IndexIn(line.Values) is not (>= 0 and var at) || line.Values[at].ToString().Trim() is not { Length: > 0 } value ||
                    session.Lists.List(rule, section, line.Values)?.Find(value) is not { Unsaved: true })
                    continue;
                yield return Diagnostic.Warning(section.Path, $"{id} names {value}, which the mod did not save. Save the " +
                                                              "mod, or the group names an entry that the mod does not have.")
                    with { Target = section, Key = line.Name };
            }
        }
    }

    /// <summary>The values of a new line from its text, as the merge reads a patch line of the file.</summary>
    private static IReadOnlyList<ValueBase> ReadLine(string name, string text, bool binary) =>
        PatchFormatter.TryReadLine(name, text, null, binary, out var values, out var error)
            ? values
            : throw new ArgumentException(error);

    /// <summary>A whole number as an Int32 value, so the file shows <c>286</c>, not <c>286.0</c>. Another number
    /// stays a float.</summary>
    private static ValueBase Number(float value) =>
        MathF.Round(value) == value && Math.Abs(value) < int.MaxValue ? new Int32Value((int)value) : new SingleValue(value, null);

    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X), MathF.Round(v.Y), MathF.Round(v.Z));

    /// <summary>A line as text for a message, for example <c>parent = Li01_14</c>.</summary>
    private static string LineLabel(EntryView line) =>
        line.Values.Count == 0 ? $"{line.Name} =" : $"{line.Name} = {string.Join(", ", line.Values.Select(v => v.ToString()))}";

    /// <summary>The text of a group file with a new value of the <c>nickname</c> line of the <c>[PropGroup]</c>
    /// section.</summary>
    private static string WithNickname(string text, string nickname)
    {
        var lines = text.Split('\n');
        var inHeader = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                inHeader = trimmed.StartsWith($"[{PropGroupFile.HeaderType}]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var at = trimmed.IndexOf('=');
            if (inHeader && at > 0 && trimmed[..at].Trim().Equals("nickname", StringComparison.OrdinalIgnoreCase))
            {
                var end = lines[i].EndsWith('\r') ? "\r" : "";
                lines[i] = $"nickname = {nickname}{end}";
                return string.Join('\n', lines);
            }
        }
        return text;
    }
}

/// <summary>
/// The prop groups of the mods of a session, read at the first use and again after a write or a change on disk
/// (section 9.1 of the M23 design).
/// </summary>
internal sealed class PropGroupStore(EditSession session)
{
    private IReadOnlyList<PropGroup>? groups;
    private List<(string Path, long Length, DateTime Written)> stamps = [];

    /// <summary>A number that changes when the groups change.</summary>
    public int Version { get; private set; }

    /// <summary>The groups of the lower mods in load order, then of the active mod.</summary>
    public IReadOnlyList<PropGroup> Groups
    {
        get
        {
            if (groups == null)
            {
                stamps = Stamps();
                groups = Read();
            }
            return groups;
        }
    }

    /// <summary>Notes a write, a rename or a delete of the editor.</summary>
    public void Changed()
    {
        groups = null;
        Version++;
    }

    /// <summary>Reads the folders again when a file of them changed.</summary>
    public void Refresh()
    {
        if (groups == null)
            return;
        var now = Stamps();
        if (now.SequenceEqual(stamps))
            return;
        stamps = now;
        groups = null;
        Version++;
    }

    private IEnumerable<(Mod Mod, bool Active)> Mods() =>
        session.LowerMods.Select(m => (m, false)).Concat(session.ActiveMod is { } active ? [(active, true)] : []);

    private IReadOnlyList<PropGroup> Read()
    {
        var result = new List<PropGroup>();
        foreach (var (mod, active) in Mods())
        {
            foreach (var file in Files(mod))
                result.Add(PropGroupFile.Read(file, active ? null : mod.Id, active));
        }
        return result;
    }

    /// <summary>The group files of a mod, in name order.</summary>
    private static IEnumerable<string> Files(Mod mod)
    {
        var folder = Path.Combine(mod.Folder, Mod.PropsFolder);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*" + PropGroupFile.Extension).Order(StringComparer.OrdinalIgnoreCase)
            : [];
    }

    /// <summary>The length and the time of each file of the folders of the groups, so a refresh finds a change.</summary>
    private List<(string, long, DateTime)> Stamps()
    {
        var result = new List<(string, long, DateTime)>();
        foreach (var (mod, _) in Mods())
        {
            var folder = Path.Combine(mod.Folder, Mod.PropsFolder);
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                result.Add((file, info.Length, info.LastWriteTimeUtc));
            }
        }
        return result;
    }
}
