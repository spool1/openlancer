using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>The lock of a jump gate or a jump hole at the start of a new game.</summary>
public enum JumpLock
{
    /// <summary>No lock.</summary>
    None,

    /// <summary>The player cannot use the gate (<c>locked_gate</c>). The route tables leave it out.</summary>
    Player,

    /// <summary>The NPCs do not use the gate (<c>npc_locked_gate</c>).</summary>
    Npc,

    /// <summary>Both locks.</summary>
    Both,
}

/// <summary>One end of a new jump connection (see <see cref="ConnectionPlans.CreatePair"/>).</summary>
/// <param name="System">The nickname of the system of the end.</param>
public sealed record ConnectionEnd(string System)
{
    /// <summary>The nickname of an existing object of the system without a working pair, or null for a new object.
    /// The plan then writes only the <c>goto</c> line of the object.</summary>
    public string? Object { get; init; }

    /// <summary>The position of a new object, or null for a place toward the other system on the nav map.</summary>
    public Vector3? Position { get; init; }

    /// <summary>The rotation of a new object in degrees, or null for a rotation whose front looks at the center of the
    /// system.</summary>
    public Vector3? Rotation { get; init; }

    /// <summary>The nickname of a new object, or null for the retail form <c>&lt;From&gt;_to_&lt;To&gt;</c>.</summary>
    public string? Nickname { get; init; }

    /// <summary>The name of a new object, or null for the name pattern of the rule data.</summary>
    public string? Name { get; init; }
}

/// <summary>The options of a new jump connection (see <see cref="ConnectionPlans.CreatePair"/>).</summary>
public sealed record ConnectionOptions
{
    /// <summary>A jump gate or a jump hole whose lines the new objects copy: the archetype, the infocard, the jump
    /// effect, the behavior, the difficulty, the visit flag, the loadout, the pilot and the tunnel. Null for the rule
    /// data.</summary>
    public SectionRef? Like { get; init; }

    /// <summary>The solar archetype of the new objects, or null for the archetype of <see cref="Like"/> or of the rule
    /// data. Its type must match the kind.</summary>
    public string? Archetype { get; init; }

    /// <summary>The faction of a new jump gate, or null for the local faction of its system.</summary>
    public string? Faction { get; init; }

    /// <summary>The loadout of a new jump gate, or null for the loadout of <see cref="Like"/>, of the gates of the same
    /// faction, or of the house of the faction.</summary>
    public string? Loadout { get; init; }

    /// <summary>The pilot of a new jump gate, or null as for <see cref="Loadout"/>.</summary>
    public string? Pilot { get; init; }

    /// <summary>The lock of both ends at the start of a new game.</summary>
    public JumpLock Lock { get; init; }

    /// <summary>True to add a traffic zone at each new object, where NPCs come and go.</summary>
    public bool TrafficZones { get; init; } = true;

    /// <summary>Gives the name of a system by its nickname, for the names of the new objects, or null. The host knows
    /// the texts of the game. Without it, the plan uses the text of the mod or the nickname.</summary>
    public Func<string, string?>? SystemName { get; init; }
}

/// <summary>
/// The plans of the jump connections: a new jump gate pair or jump hole pair, and the gate locks of a gate. Each plan
/// gives one command, which runs as one entry of the edit history, also when it changes two system files, the gate locks
/// and the text of the mod. The agent tools and the hand tools call the same plans.
/// </summary>
/// <remarks>
/// <para>Retail facts: an object with <c>goto = &lt;system&gt;, &lt;exit object&gt;, &lt;tunnel&gt;</c> is one jump
/// connection of its system, and the type of its solar archetype gives the kind. Each pair of the game goes both ways
/// and has one kind. The game crashes with more than 10 objects with <c>goto</c> in one system. Most retail gates have
/// the nickname <c>&lt;A&gt;_to_&lt;B&gt;</c> and most holes <c>&lt;A&gt;_to_&lt;B&gt;_hole</c>. The ends of one kind
/// share one infocard. The loadout of a gate follows the house of its faction, and without a loadout the gate has no
/// lights. NPCs come and go at a gate or a hole through a separate traffic zone with the population type
/// <c>jumpgate</c> or <c>wormhole</c>.</para>
/// <para>A new end goes toward the other system on the nav map, at the distance of the farthest object of its system
/// from the center. The rotation of retail ends turns their front to the center of the system.</para>
/// </remarks>
public static class ConnectionPlans
{
    /// <summary>The detail code of a system that would have more than 10 jump objects.</summary>
    public const string JumpLimit = "JumpLimit";

    /// <summary>The detail code of a jump gate pair inside one system.</summary>
    public const string GateSameSystem = "GateSameSystem";

    /// <summary>The detail code of an archetype or an object of another kind.</summary>
    public const string KindMismatch = "KindMismatch";

    /// <summary>The detail code of an existing end that has a working pair.</summary>
    public const string EndPaired = "EndPaired";

    /// <summary>The detail code of an object that is not a jump gate or a jump hole.</summary>
    public const string NotConnection = "NotConnection";

    /// <summary>The longest nickname of a new end.</summary>
    public const int MaxNicknameLength = 47;

    /// <summary>The lines of a new end in their order. <c>nickname</c> is always first, because the game records the
    /// jump with the nickname that the object has before its <c>goto</c> line.</summary>
    private static readonly string[] LineOrder =
    [
        "nickname", "ids_name", "pos", "rotate", "archetype", "msg_id_prefix", "ids_info", "jump_effect", "reputation",
        "behavior", "difficulty_level", "goto", "loadout", "pilot", "visit",
    ];

    /// <summary>
    /// Plans a new jump gate pair or jump hole pair between two systems, as one entry of the edit history. A jump hole
    /// pair can stay in one system.
    /// </summary>
    /// <remarks>
    /// <para>Each new end gets the lines of <see cref="LineOrder"/> that it needs, a name in the text of the active mod
    /// and, with <see cref="ConnectionOptions.TrafficZones"/>, a traffic zone <c>Zone_&lt;object&gt;_traffic</c> without
    /// encounters. An end that names an existing object without a working pair gets only the missing <c>goto</c>
    /// line.</para>
    /// <para>The plan refuses a system without a file that the editor can change, a jump gate pair inside one system,
    /// an archetype or an existing object of another kind, an existing object with a working pair, a nickname that is
    /// taken, and a system that would have more than 10 jump objects.</para>
    /// </remarks>
    public static ConnectionPlan CreatePair(EditSession session, ConnectionKind kind, ConnectionEnd from,
        ConnectionEnd to, ConnectionOptions? options = null)
    {
        options ??= new ConnectionOptions();
        var words = kind == ConnectionKind.Hole ? "jump hole" : "jump gate";
        var plan = new ConnectionPlan($"Create {words} pair {from.System.Trim()} to {to.System.Trim()}");
        if (session.ReadOnlyReason is { } locked)
            return plan.Refuse("", locked);
        if (kind == ConnectionKind.Airlock)
            return plan.Refuse("", "OpenLancer makes only jump gate pairs and jump hole pairs.", detail: KindMismatch);
        if (session.Lists.RuleData.Builders.Connection is not { } rule)
            return plan.Refuse("", "The rule data has no rules for a new jump connection.");

        var ends = new End[2];
        foreach (var (request, i) in new[] { (from, 0), (to, 1) })
        {
            if (session.SystemSection(request.System.Trim()) is not { Id.Key: { } system } systemSection)
                return plan.Refuse(session.UniversePath ?? "", $"No [system] has the nickname {request.System.Trim()}.",
                    detail: MissionErrors.ItemNotFound);
            if (ScenePlans.SystemFileProblem(session, system, out var path) is { } fileProblem)
                return plan.Refuse(fileProblem.Source, fileProblem.Message, systemSection);
            ends[i] = new End(request, system, path, systemSection);
        }
        var same = FLHash.CreateID(ends[0].System) == FLHash.CreateID(ends[1].System);
        if (kind == ConnectionKind.Gate && same)
            return plan.Refuse(ends[1].Path, "A jump gate pair needs two systems. Use the kind jump hole for a hole inside " +
                                             "one system.", ends[1].SystemSection, GateSameSystem);

        SectionView? like = null;
        if (options.Like is { } likeRef)
        {
            if (SceneSections.KindOf(session, likeRef) != SceneKind.Object || session.Get(likeRef) is not { } likeView)
                return plan.Refuse(likeRef.Path, $"{likeRef.Id} is not an object of a system file. Pick a jump gate or a " +
                                                 "jump hole.", likeRef, MissionErrors.ItemNotFound);
            like = likeView;
        }
        var endRule = rule.For(kind);
        var needed = kind == ConnectionKind.Hole ? "jump_hole" : "jump_gate";
        var archetype = options.Archetype?.Trim() is { Length: > 0 } given ? given
            : like != null && SceneSections.Last(like, "archetype") is { } likeArchetype ? likeArchetype
            : endRule.Archetype;
        if (ends.Any(e => e.Request.Object == null) && ScenePlans.TypeOf(session, archetype) is var archetypeType &&
            archetypeType != needed)
            return plan.Refuse(ends[0].Path, (archetypeType == null
                ? $"No solar archetype has the name {archetype}."
                : $"The archetype {archetype} is a {archetypeType.ToUpperInvariant()}.") +
                $" The kind {words} needs an archetype of the type {needed.ToUpperInvariant()}.", detail: KindMismatch);

        // The existing ends.
        foreach (var end in ends.Where(e => e.Request.Object != null))
        {
            var name = end.Request.Object!.Trim();
            if (session.Sections(end.Path).FirstOrDefault(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) &&
                                                               string.Equals(s.Id.Key, name, StringComparison.OrdinalIgnoreCase))
                is not { } existing || session.Get(existing) is not { } view)
                return plan.Refuse(end.Path, $"{end.System} has no object {name}.", detail: MissionErrors.ItemNotFound);
            if (view.ReadOnlyReason is { } reason)
                return plan.Refuse(end.Path, $"{existing.Id.Key}: {reason}", existing);
            var type = SceneSections.ArchetypeType(session, view);
            if (type != needed)
                return plan.Refuse(end.Path, $"{existing.Id.Key} is " + (type == null
                    ? "not an object with an archetype of the solar archetypes."
                    : $"a {type.ToUpperInvariant()}.") + $" The kind {words} needs an object of the type " +
                    $"{needed.ToUpperInvariant()}.", existing, KindMismatch);
            if (PartnerOf(session, view) is { } partner && GoesBack(session, partner.View, end.System, existing.Id.Key!))
                return plan.Refuse(end.Path, $"{existing.Id.Key} already goes to {partner.Section.Id.Key}, and " +
                                             $"{partner.Section.Id.Key} goes back. Pick an object without a pair.", existing,
                    EndPaired);
            end.Existing = existing;
            end.View = view;
            end.Nickname = existing.Id.Key!;
        }
        if (ends[0].Existing is { } first && first.Equals(ends[1].Existing))
            return plan.Refuse(ends[0].Path, "The two ends name the same object. Pick two objects.", first,
                MissionErrors.FieldValue);

        // The nicknames of the new ends.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (end, other) in new[] { (ends[0], ends[1]), (ends[1], ends[0]) })
        {
            if (end.Existing != null)
                continue;
            var binary = session.IsBinaryFile(end.Path);
            var stem = kind == ConnectionKind.Hole ? $"{end.System}_to_{other.System}_hole" : $"{end.System}_to_{other.System}";
            var name = end.Request.Nickname?.Trim() is { Length: > 0 } wanted ? wanted : Free(session, stem, reserved);
            if (name.Length > MaxNicknameLength)
                return plan.Refuse(end.Path, $"The nickname {name} has more than {MaxNicknameLength} characters. Give a " +
                                             "shorter nickname.", detail: MissionErrors.NameInvalid);
            if (PatchFormatter.KeyError(name, binary) is { } keyError)
                return plan.Refuse(end.Path, keyError, detail: MissionErrors.NameInvalid);
            if (session.Nicknames.Taken(name) is { } taken)
                return plan.Refuse(end.Path, $"The nickname {name} is taken: {taken.Name} has the same nickname or the " +
                                             "same FLHash.", taken.Section, MissionErrors.NameTaken);
            if (reserved.Any(r => FLHash.CreateID(r) == FLHash.CreateID(name)))
                return plan.Refuse(end.Path, $"The two new ends cannot both have the nickname {name}.",
                    detail: MissionErrors.NameTaken);
            reserved.Add(name);
            end.Nickname = name;
        }

        // The jump objects of each system after the plan.
        var counts = new Dictionary<string, int>(VfsPath.Comparer);
        foreach (var path in ends.Select(e => e.Path).Distinct(VfsPath.Comparer))
        {
            var count = ConnectionChecks.JumpObjects(session.WorkingSections(path) ?? []) +
                        ends.Count(e => VfsPath.Comparer.Equals(e.Path, path) &&
                                        (e.View == null || !ConnectionChecks.IsJump(e.View)));
            var end = ends.First(e => VfsPath.Comparer.Equals(e.Path, path));
            if (count > ConnectionChecks.MaxJumpObjects)
                return plan.Refuse(path, $"{end.System} would have {count} jump objects. The game crashes with more than " +
                                         $"{ConnectionChecks.MaxJumpObjects} in one system. Delete a jump gate or a jump " +
                                         $"hole of {end.System} first.", end.SystemSection, JumpLimit);
            counts[path] = count;
        }

        // The new ends, their traffic zones, the goto lines of the existing ends and the locks.
        var problems = new List<string>();
        var texts = new SectionCopies(session, problems.Add);
        var planned = new PlannedSections(session);
        var commands = new List<EditCommand>();
        var zones = new List<EditCommand>();
        var adds = new List<SectionRef>();
        var trafficZones = new List<SectionRef>();
        var changes = new List<SectionRef>();
        var results = new List<ConnectionEndPlan>();
        var defaultPlaces = DefaultPlaces(session, ends, same, rule.MinRadius);
        foreach (var (end, other, i) in new[] { (ends[0], ends[1], 0), (ends[1], ends[0], 1) })
        {
            var tunnel = end.View != null && TunnelOf(end.View) is { } own ? own
                : like != null && TunnelOf(like) is { } copied ? copied
                : rule.Tunnel;
            var jump = $"{other.System}, {other.Nickname}, {tunnel}";
            var objectRef = end.Existing ?? new SectionRef(end.Path, new SectionIdentity(SceneSections.ObjectType, end.Nickname));
            if (end.Existing is { } existing)
            {
                if (!GoesTo(end.View!, other.System, other.Nickname))
                {
                    commands.Add(EditCommand.SetLinesFromText(existing, "goto", [jump]));
                    planned.Replace(existing, SectionEdits.WithLines(session.WorkingSection(existing)!, "goto",
                        [Values(other.System, other.Nickname, tunnel)]));
                    changes.Add(existing);
                }
                results.Add(new ConnectionEndPlan(existing, false, null, null, null));
                continue;
            }

            var position = end.Request.Position ?? defaultPlaces[i];
            var rotation = end.Request.Rotation ?? new Vector3(0, MathF.Round(LookAtCenter(position)), 0);
            var id = new SectionIdentity(SceneSections.ObjectType, end.Nickname);
            var name = end.Request.Name?.Trim() is { Length: > 0 } givenName ? givenName
                : endRule.Name.Replace("{system}", SystemName(session, other.System, options), StringComparison.Ordinal);
            var lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["nickname"] = end.Nickname,
                ["pos"] = ScenePlans.PositionText(position),
                ["rotate"] = ScenePlans.PositionText(rotation),
                ["archetype"] = archetype,
                ["goto"] = jump,
            };
            if (texts.New(StringKind.String, name, id, "ids_name") is { } nameText)
                lines["ids_name"] = nameText.ToString()!;
            lines["ids_info"] = like?.Lines("ids_info").LastOrDefault(l => l.Values.Count > 0) is { } info
                ? texts.Copy(SceneSections.ObjectType, "ids_info", 0, info.Values[0], id).ToString()!
                : endRule.Infocard.ToString(CultureInfo.InvariantCulture);
            if (session.Get(other.SystemSection) is { } otherSystem && SceneSections.Last(otherSystem, "msg_id_prefix") is { } prefix)
                lines["msg_id_prefix"] = prefix;
            foreach (var line in endRule.Lines)
                lines[line.Key] = line.Value!;
            foreach (var key in ConnectionRule.EndKeys)
            {
                if (like?.Lines(key).LastOrDefault(l => l.Values.Count > 0) is { } likeLine)
                    lines[key] = string.Join(", ", likeLine.Values.Select(v => v.ToString()));
            }
            string? reputation = null, loadout = null, pilot = null;
            if (kind == ConnectionKind.Gate)
            {
                reputation = options.Faction?.Trim() is { Length: > 0 } faction ? faction : LocalFaction(session, end.Path);
                var frequent = Frequent(session, reputation);
                var house = rule.HouseOf(reputation);
                loadout = options.Loadout?.Trim() is { Length: > 0 } l ? l
                    : like != null && SceneSections.Last(like, "loadout") is { } likeLoadout ? likeLoadout
                    : frequent.Loadout ?? house?.Loadout;
                pilot = options.Pilot?.Trim() is { Length: > 0 } p ? p
                    : like != null && SceneSections.Last(like, "pilot") is { } likePilot ? likePilot
                    : frequent.Pilot ?? house?.Pilot;
                foreach (var (key, value) in new[] { ("reputation", reputation), ("loadout", loadout), ("pilot", pilot) })
                {
                    if (value != null)
                        lines[key] = value;
                }
            }
            if (ScenePlans.Entries(LineOrder.Where(lines.ContainsKey).Select(k => (k, lines[k])),
                    session.IsBinaryFile(end.Path), out var lineError) is not { } entries)
                return plan.Refuse(end.Path, lineError!);
            commands.Add(EditCommand.AddSection(end.Path, SceneSections.ObjectType, entries));
            planned.Add(end.Path, SectionEdits.Build(SceneSections.ObjectType, entries));
            adds.Add(objectRef);
            results.Add(new ConnectionEndPlan(objectRef, true, reputation, loadout, pilot));

            if (!options.TrafficZones)
                continue;
            var zoneName = Free(session, $"Zone_{end.Nickname}_traffic", reserved);
            reserved.Add(zoneName);
            var zoneLines = new List<(string, string)>
            {
                ("nickname", zoneName), ("pos", ScenePlans.PositionText(position)), ("shape", "SPHERE"),
                ("size", endRule.TrafficSize.ToString("R", CultureInfo.InvariantCulture)),
            };
            zoneLines.AddRange(endRule.Traffic.Select(t => (t.Key, t.Value!)));
            if (ScenePlans.Entries(zoneLines, session.IsBinaryFile(end.Path), out var zoneError) is not { } zoneEntries)
                return plan.Refuse(end.Path, zoneError!);
            zones.Add(EditCommand.AddSection(end.Path, SceneSections.ZoneType, zoneEntries));
            var zone = new SectionRef(end.Path, new SectionIdentity(SceneSections.ZoneType, zoneName));
            trafficZones.Add(zone);
        }
        if (problems.Count > 0)
            return plan.Refuse(ends[0].Path, problems[0]);

        var warnings = new List<Diagnostic>();
        if (options.Lock != JumpLock.None)
        {
            var lockEdit = GateLocks.Set(session, [ends[0].Nickname, ends[1].Nickname],
                options.Lock is JumpLock.Player or JumpLock.Both, options.Lock is JumpLock.Npc or JumpLock.Both, planned);
            if (lockEdit.Refusal is { } lockProblem)
                return plan.Refuse("", lockProblem);
            commands.AddRange(lockEdit.Commands);
            changes.AddRange(lockEdit.Changes);
            if (options.Lock is JumpLock.Player or JumpLock.Both)
                warnings.AddRange(ends.Select(e => LockWarning(e.Existing ?? results[Array.IndexOf(ends, e)].Object,
                    e.View is { IsNew: false })));
        }

        plan.Ends = results;
        plan.TrafficZones = trafficZones;
        plan.JumpObjects = ends.Select(e => new JumpObjectCount(e.System, counts[e.Path], ConnectionChecks.MaxJumpObjects))
            .ToList();
        plan.Routes = RouteTable.Diff(session.Routes.Tables.All, planned.Routes().All);
        plan.Adds = [.. adds, .. trafficZones];
        plan.Changes = changes;
        plan.Warnings = warnings;
        plan.SelectAfter = results[0].Object;
        plan.Command = EditCommand.Group(plan.Label, [.. commands, .. zones, .. texts.Commands], session.Snapshot());
        return plan;
    }

    /// <summary>
    /// Plans the locks of a jump gate or a jump hole at the start of a new game, as one entry of the edit history. With
    /// <paramref name="pair"/>, the partner that its <c>goto</c> line names gets the same locks. A gate of the game or of
    /// a lower mod works too: the patch of the gate lock file changes.
    /// </summary>
    /// <remarks>A player lock gives a warning, because the route tables leave out the gate, also after a mission unlocks
    /// it. For a gate of the game or of a lower mod, the warning tells that the story can depend on the lock.</remarks>
    public static ConnectionPlan SetLock(EditSession session, SectionRef gate, JumpLock locked, bool pair = true)
    {
        var plan = new ConnectionPlan($"Lock {gate.Id.Key}");
        if (session.ReadOnlyReason is { } closed)
            return plan.Refuse("", closed);
        if (SceneSections.KindOf(session, gate) != SceneKind.Object || session.Get(gate) is not { } view ||
            gate.Id.Key is not { } name || SceneSections.ArchetypeType(session, view) is not ("jump_gate" or "jump_hole"))
            return plan.Refuse(gate.Path, $"{gate.Id.Key ?? gate.Id.ToString()} is not a jump gate or a jump hole.", gate,
                NotConnection);
        var gates = new List<SectionRef> { gate };
        if (pair && PartnerOf(session, view) is { } partner &&
            SceneSections.ArchetypeType(session, partner.View) is "jump_gate" or "jump_hole")
            gates.Add(partner.Section);
        var player = locked is JumpLock.Player or JumpLock.Both;
        var planned = new PlannedSections(session);
        var edit = GateLocks.Set(session, gates.Select(g => g.Id.Key!).ToList(), player,
            locked is JumpLock.Npc or JumpLock.Both, planned);
        if (edit.Refusal is { } refusal)
            return plan.Refuse(gate.Path, refusal, gate);
        plan.Label = locked == JumpLock.None ? $"Unlock {name}" : $"Lock {name}";
        plan.Changes = edit.Changes;
        plan.Routes = RouteTable.Diff(session.Routes.Tables.All, planned.Routes().All);
        plan.Warnings = player ? gates.Select(g => LockWarning(g, session.Get(g)?.IsNew == false)).ToList() : [];
        plan.SelectAfter = gate;
        plan.Command = EditCommand.Group(plan.Label, edit.Commands, session.Snapshot());
        return plan;
    }

    /// <summary>One end of a new pair while the plan reads it.</summary>
    private sealed class End(ConnectionEnd request, string system, string path, SectionRef systemSection)
    {
        public ConnectionEnd Request { get; } = request;

        /// <summary>The nickname of the system as <c>universe.ini</c> writes it.</summary>
        public string System { get; } = system;

        public string Path { get; } = path;

        public SectionRef SystemSection { get; } = systemSection;

        /// <summary>The existing object of the end, or null for a new object.</summary>
        public SectionRef? Existing { get; set; }

        public SectionView? View { get; set; }

        /// <summary>The nickname of the object of the end.</summary>
        public string Nickname { get; set; } = "";
    }

    /// <summary>The warning of a player lock on a gate (see <see cref="ConnectionChecks.LockText"/>).</summary>
    /// <param name="lowerGate">True for a gate of the game or of a lower mod.</param>
    private static Diagnostic LockWarning(SectionRef gate, bool lowerGate) =>
        Diagnostic.Warning(gate.Path, ConnectionChecks.LockText(gate.Id.Key!, lowerGate)) with { Target = gate, Key = "goto" };

    /// <summary>
    /// The default positions of the new ends: toward the other system on the nav map, at the distance of the farthest
    /// object of the system from the center, and at least <paramref name="minRadius"/>. A hole pair inside one system,
    /// and two systems at one place of the nav map, have no direction. Their ends go to the two sides of the center on
    /// the X axis.
    /// </summary>
    private static Vector3[] DefaultPlaces(EditSession session, End[] ends, bool same, float minRadius)
    {
        var graph = session.Routes.Graph;
        var a = graph.System(ends[0].System)?.MapPos;
        var b = graph.System(ends[1].System)?.MapPos;
        var direction = !same && a is { } from && b is { } to && Vector2.Distance(from, to) > 0
            ? Vector2.Normalize(to - from)
            : (Vector2?)null;
        var result = new Vector3[2];
        for (var i = 0; i < 2; i++)
        {
            var radius = Math.Max(minRadius, Farthest(session, ends[i].Path));
            var toward = direction is { } d ? (i == 0 ? d : -d) : new Vector2(i == 0 ? 1 : -1, 0);
            // The nav map draws the x of pos along the x axis of space, and its y along the z axis.
            result[i] = new Vector3(toward.X, 0, toward.Y) * radius;
        }
        return result;
    }

    /// <summary>The yaw in degrees that turns the front of an object at a position to the center of its system. The
    /// front of an object looks along its negative z axis.</summary>
    private static float LookAtCenter(Vector3 position) =>
        MathF.Atan2(position.X, position.Z) * 180 / MathF.PI;

    /// <summary>The distance of the farthest object of a system file from the center, or 0.</summary>
    private static float Farthest(EditSession session, string path) =>
        session.Sections(path).Where(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType))
            .Select(s => session.Get(s)?.Lines("pos").LastOrDefault(l => l.Values.Count >= 3)?.Values)
            .Select(p => p != null && p[0].TryToSingle(out var x) && p[1].TryToSingle(out var y) && p[2].TryToSingle(out var z)
                ? new Vector3(x, y, z).Length()
                : 0)
            .DefaultIfEmpty(0).Max();

    /// <summary>The partner that the last <c>goto</c> line of an object names, or null when its system or its object
    /// does not exist.</summary>
    private static (SectionRef Section, SectionView View)? PartnerOf(EditSession session, SectionView view)
    {
        if (view.Lines("goto").LastOrDefault(l => l.Values.Count > 1) is not { } line ||
            session.SystemPath(line.Values[0].ToString().Trim()) is not { } path)
            return null;
        var exit = line.Values[1].ToString().Trim();
        return session.Sections(path).FirstOrDefault(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) &&
                                                          string.Equals(s.Id.Key, exit, StringComparison.OrdinalIgnoreCase))
            is { } section && session.Get(section) is { } partner
            ? (section, partner)
            : null;
    }

    /// <summary>True when the last <c>goto</c> line of an object names a system and an object.</summary>
    private static bool GoesTo(SectionView view, string system, string exit) =>
        view.Lines("goto").LastOrDefault(l => l.Values.Count > 1) is { } line &&
        line.Values[0].ToString().Trim().Equals(system, StringComparison.OrdinalIgnoreCase) &&
        line.Values[1].ToString().Trim().Equals(exit, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the partner goes back to the object of a system.</summary>
    private static bool GoesBack(EditSession session, SectionView partner, string system, string exit) =>
        GoesTo(partner, system, exit) || session.SystemSection(system) is { Id.Key: { } canonical } &&
        GoesTo(partner, canonical, exit);

    /// <summary>Value 2 of the last <c>goto</c> line of an object, or null.</summary>
    private static string? TunnelOf(SectionView view) =>
        view.Lines("goto").LastOrDefault(l => l.Values.Count > 2)?.Values[2].ToString().Trim() is { Length: > 0 } tunnel
            ? tunnel
            : null;

    /// <summary>The local faction of the <c>[SystemInfo]</c> of a system file in W, or null.</summary>
    private static string? LocalFaction(EditSession session, string path) =>
        session.Sections(path).FirstOrDefault(s => SceneSections.Is(s.Id.Type, "SystemInfo")) is { } info &&
        session.Get(info) is { } view
            ? SceneSections.Last(view, "local_faction")
            : null;

    /// <summary>
    /// The loadout and the pilot that the jump gates of W with a faction have most often, as a pair, or nulls. On a
    /// tie, the first pair in the order of the jump graph counts.
    /// </summary>
    private static (string? Loadout, string? Pilot) Frequent(EditSession session, string? reputation)
    {
        if (reputation == null)
            return (null, null);
        var counts = new List<((string? Loadout, string? Pilot) Pair, int Count)>();
        foreach (var connection in session.Routes.Graph.Connections.Where(c => c.Kind == ConnectionKind.Gate))
        {
            if (session.SystemPath(connection.System) is not { } path ||
                session.Get(new SectionRef(path, new SectionIdentity(SceneSections.ObjectType, connection.Object))) is not { } view ||
                !string.Equals(SceneSections.Last(view, "reputation"), reputation, StringComparison.OrdinalIgnoreCase))
                continue;
            var pair = (SceneSections.Last(view, "loadout"), SceneSections.Last(view, "pilot"));
            if (pair is (null, null))
                continue;
            var at = counts.FindIndex(c => string.Equals(c.Pair.Loadout, pair.Item1, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(c.Pair.Pilot, pair.Item2, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
                counts.Add((pair, 1));
            else
                counts[at] = (counts[at].Pair, counts[at].Count + 1);
        }
        return counts.Count == 0 ? (null, null) : counts.MaxBy(c => c.Count).Pair;
    }

    /// <summary>The name of a system for the name of a new end: from the host, else the text of the active mod, else
    /// the nickname.</summary>
    private static string SystemName(EditSession session, string system, ConnectionOptions options)
    {
        if (options.SystemName?.Invoke(system) is { Length: > 0 } name)
            return name;
        if (session.SystemSection(system) is { } section && session.Get(section) is { } view &&
            SceneSections.Last(view, "strid_name") is { } value && StringPlaceholder.TryParse(value, out var placeholder) &&
            session.Text(placeholder) is { Length: > 0 } text)
            return text;
        return system;
    }

    /// <summary>A nickname of an object or a zone that no object or zone and no reserved name has: the name, or the first
    /// free <c>&lt;name&gt;_&lt;n&gt;</c>.</summary>
    private static string Free(EditSession session, string name, IReadOnlySet<string> reserved) =>
        session.Nicknames.Taken(name) == null && !reserved.Any(r => FLHash.CreateID(r) == FLHash.CreateID(name))
            ? name
            : session.Nicknames.FirstFree(name, 1, reserved);

    private static IReadOnlyList<ValueBase> Values(params string[] texts) =>
        texts.Select(t => (ValueBase)new StringValue(t)).ToList();
}
