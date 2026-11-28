using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The checks of the jump connections: the objects with a <c>goto</c> line and the gate locks of
/// <c>[locked_gates]</c>. Two errors stop the save, because the game crashes or records a wrong jump. One is more
/// than 10 jump objects in one system. The other is a <c>goto</c> line before the <c>nickname</c> line. The other
/// checks are warnings.
/// </summary>
/// <remarks>
/// <para>Retail facts: each object with a <c>goto</c> line becomes one connection of its system, also an airlock gate.
/// The game keeps the connections of a system in 10 places and does not check the count, so an 11th connection
/// crashes the game. The type of the solar archetype gives the kind: <c>JUMP_GATE</c>, <c>JUMP_HOLE</c> or
/// <c>AIRLOCK_GATE</c>. An object of another type is no jump. The object reader records a jump with the nickname that
/// the object has before its <c>goto</c> line. The route tables leave out each gate that a <c>locked_gate</c> line
/// names, also after a mission unlocks it.</para>
/// <para>The count error stops the save of a data mod (tier 1) when the active mod makes the count go above 10. The
/// order error stops the save when the active mod writes the <c>goto</c> line. Each other case is a warning.</para>
/// </remarks>
internal static class ConnectionChecks
{
    /// <summary>The most jump objects that one system can have in the game.</summary>
    public const int MaxJumpObjects = TargetChecks.MaxJumpObjects;

    private const string LockSection = "locked_gates";

    /// <summary>The problems of an object of a system file or of a <c>[locked_gates]</c> section, also of the lower
    /// layer. Empty for another section.</summary>
    public static IReadOnlyList<Diagnostic> Check(EditSession session, SectionRef section)
    {
        if (session.Get(section) is not { } view)
            return [];
        if (IsLockSection(session, section))
            return Locks(session, section, view).ToList();
        if (SceneSections.KindOf(session, section) != SceneKind.Object || section.Id.Key is not { } name)
            return [];
        var result = new List<Diagnostic>();
        if (IsJump(view))
        {
            if (IndexOf(view, "goto") < IndexOf(view, "nickname"))
                result.Add(OrderProblem(session, section, name));
            if (CountProblem(session, section) is { } count)
                result.Add(count);
            result.AddRange(Partner(session, section, view, name));
            if (Crowded(session, section, view, name) is { } crowded)
                result.Add(crowded);
        }
        var type = SceneSections.ArchetypeType(session, view);
        if (type is "jump_gate" or "jump_hole" && GateLocks.PlayerLocked(session, name) &&
            !GateLocks.PlayerLocked(session, name, lower: true))
            result.Add(Warning(section, LockText(name, IsLowerGate(session, section))));
        return result;
    }

    /// <summary>
    /// The other sections whose problems of <see cref="Check"/> read a section. For an object, these are the partner
    /// of a jump object, because its check reads the <c>goto</c> line of the object, and each <c>[locked_gates]</c>
    /// section with a lock line of the object. For a <c>[locked_gates]</c> section, these are the jump objects of the
    /// jump graph that its lock lines name, because the lock warning of an object reads the lines. Empty for another
    /// section.
    /// </summary>
    public static IReadOnlyList<SectionRef> Linked(EditSession session, SectionRef section)
    {
        if (session.Get(section) is not { } view)
            return [];
        if (IsLockSection(session, section))
        {
            var hashes = view.Entries.Where(e => IsLockLine(e) && e.Values.Count > 0)
                .Select(e => ItemLists.HashOf(e.Values[0])).OfType<uint>().ToHashSet();
            return session.Routes.Graph.Connections.Where(c => hashes.Contains(FLHash.CreateID(c.Object)))
                .Select(c => ConnectionPlans.ObjectOf(session, c.System, c.Object)).OfType<SectionRef>().ToList();
        }
        if (SceneSections.KindOf(session, section) != SceneKind.Object || section.Id.Key is not { } name)
            return [];
        var linked = GateLocks.Of(session, name).Select(l => l.Section).ToList();
        if (IsJump(view) && ConnectionPlans.PartnerOf(session, view) is { } partner)
            linked.Add(partner.Section);
        return linked;
    }

    /// <summary>
    /// The errors of the jump connections for the save gate, in a system file that the active mod changes. A file with
    /// more than 10 jump objects gives one error. Each object that the active mod adds or changes can give the order
    /// error.
    /// </summary>
    public static IEnumerable<Diagnostic> SaveErrors(EditSession session, string path)
    {
        var jumps = session.Sections(path)
            .Where(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) && session.Get(s) is { } view && IsJump(view))
            .ToList();
        if (jumps.Count > MaxJumpObjects && CountProblem(session, jumps[^1]) is { Severity: DiagnosticSeverity.Error } count)
            yield return count;
        foreach (var section in jumps)
        {
            if (session.Get(section) is { IsChanged: true } view && section.Id.Key is { } name &&
                IndexOf(view, "goto") < IndexOf(view, "nickname") &&
                OrderProblem(session, section, name) is { Severity: DiagnosticSeverity.Error } order)
                yield return order;
        }
    }

    /// <summary>The count of the objects whose first <c>goto</c> line has a value, as the content checks of the retail
    /// targets count them.</summary>
    public static int JumpObjects(IEnumerable<Section> sections) =>
        sections.Count(s => s.Name.Equals(SceneSections.ObjectType, StringComparison.OrdinalIgnoreCase) &&
                            ItemIndex.First(s, "goto") != null);

    /// <summary>True for a section whose first <c>goto</c> line has a value: a jump object of its system (see
    /// <see cref="JumpObjects"/>).</summary>
    public static bool IsJump(SectionView view) => view.Lines("goto") is [{ Values.Count: > 0 }, ..];

    /// <summary>The kind of a <c>[Solar] type</c> in lowercase in plain words, for example "jump gate", or null for
    /// a type that is no jump.</summary>
    public static string? KindWords(string? type) => type switch
    {
        "jump_gate" => "jump gate",
        "jump_hole" => "jump hole",
        "airlock_gate" => "airlock gate",
        _ => null,
    };

    /// <summary>
    /// The warning of a player lock: the route tables leave out the gate. For a gate of the game or of a lower mod, it
    /// tells that the story can depend on the lock.
    /// </summary>
    public static string LockText(string gate, bool lowerGate) =>
        $"The route tables leave out {gate}, because a new game locks it for the player. After a mission unlocks it with " +
        "Act_LockDock, nav map routes and NPC routes still do not use it. Set its lock to npc or none to keep it on the " +
        $"routes. Use {SceneSections.LockTool}." + (lowerGate ? " The story can depend on this lock." : "");

    /// <summary>
    /// True for a jump object of the game or of a mod that loads before the active mod. The story of the game or of
    /// that mod can lock it. A jump object that the active mod adds is not one. This includes each object of a system
    /// file in the folder of the active mod, also after a save: B of such a whole file is the saved file of the mod,
    /// and the lower layer does not have the file. False when the edit view has no such section, for example for a new
    /// end of a plan.
    /// </summary>
    public static bool IsLowerGate(EditSession session, SectionRef gate) =>
        session.Get(gate) is { IsNew: false } && session.OpenDocument(gate.Path) is { IsWholeFile: false };

    /// <summary>True for a <c>[locked_gates]</c> section of a file of the role <c>groups</c>.</summary>
    private static bool IsLockSection(EditSession session, SectionRef section) =>
        SceneSections.Is(section.Id.Type, LockSection) &&
        session.DataFiles.Paths(UniverseGraph.LockRole).Contains(section.Path, VfsPath.Comparer);

    /// <summary>The error of a <c>goto</c> line before the <c>nickname</c> line.</summary>
    private static Diagnostic OrderProblem(EditSession session, SectionRef section, string name)
    {
        var message = $"{name} has its goto line before its nickname line. The game then records the jump under another " +
                      "object. In the raw INI block, delete the goto line, then click outside the block. Then add the goto " +
                      "line again. A new line goes at the end of the section.";
        return session.Origin(section, "goto").Origin == ValueOrigin.ActiveMod
            ? Error(section, message)
            : Warning(section, message);
    }

    /// <summary>
    /// The problem of a system file with more than 10 jump objects, on a section, or null. It is an error when the active
    /// mod is a data mod and the file of the lower layer has at most 10 jump objects. Else it is a warning.
    /// </summary>
    internal static Diagnostic? CountProblem(EditSession session, SectionRef target)
    {
        var count = JumpObjects(session.WorkingSections(target.Path) ?? []);
        if (count <= MaxJumpObjects)
            return null;
        var system = session.SystemNicknameOf(target.Path) ?? SceneSections.FileName(target.Path);
        var message = $"{system} has {count} jump objects. The game crashes with more than {MaxJumpObjects} in one " +
                      "system. " + (count - MaxJumpObjects == 1
                          ? $"Delete a jump gate or a jump hole of {system}."
                          : $"Delete {count - MaxJumpObjects} jump gates or jump holes of {system}.");
        var lower = JumpObjects(session.OpenDocument(target.Path)?.Base ?? []);
        return session.ActiveMod?.Manifest.Tier == ModManifest.DataTier && lower <= MaxJumpObjects
            ? Error(target, message)
            : Warning(target, message);
    }

    /// <summary>The problems of the partner of an object with a <c>goto</c> line: the partner does not go back, or it is
    /// another kind. An object whose archetype is no jump gets a warning too. A partner or a system that does not exist
    /// gives no problem here, because the list checks of the <c>goto</c> line report it.</summary>
    private static IEnumerable<Diagnostic> Partner(EditSession session, SectionRef section, SectionView view, string name)
    {
        // The game reads the last goto line, as the jump graph does.
        var jump = view.Lines("goto").Last(l => l.Values.Count > 0).Values;
        var type = SceneSections.ArchetypeType(session, view);
        var kind = KindWords(type);
        if (type != null && kind == null && SceneSections.Last(view, "archetype") is { } archetype)
            yield return Warning(section, $"{name} has a goto line, but its archetype {archetype} is a " +
                                          $"{type.ToUpperInvariant()}. The player cannot use it as a jump.");
        if (jump.Count < 2 || jump[0].ToString().Trim() is not { Length: > 0 } system ||
            jump[1].ToString().Trim() is not { Length: > 0 } exit || session.SystemPath(system) is not { } path ||
            session.Sections(path).FirstOrDefault(s => SceneSections.Is(s.Id.Type, SceneSections.ObjectType) &&
                                                       string.Equals(s.Id.Key, exit, StringComparison.OrdinalIgnoreCase))
                is not { } partner || session.Get(partner) is not { } partnerView)
            yield break;
        var own = session.SystemNicknameOf(section.Path);
        var back = partnerView.Lines("goto").LastOrDefault(l => l.Values.Count > 1)?.Values;
        if (own == null || back == null || !Same(back[0], own) || !Same(back[1], name))
        {
            yield return Warning(section, NoReturnText(name, exit, system));
            yield break;
        }
        var other = KindWords(SceneSections.ArchetypeType(session, partnerView));
        if (kind != null && other != null && kind != other)
            yield return Warning(section, KindText(name, kind, exit, other));
    }

    /// <summary>The text of an object whose partner does not go back. The completeness of a jump connection shows it
    /// too.</summary>
    internal static string NoReturnText(string name, string exit, string system) =>
        $"{name} goes to {exit} in {system}, but {exit} does not go back. The player cannot jump back. To write the " +
        $"missing goto line, use {SceneSections.PairTool} with {name} as one end.";

    /// <summary>The text of an object whose partner is another kind. The completeness of a jump connection shows it
    /// too.</summary>
    internal static string KindText(string name, string kind, string exit, string other) =>
        $"{name} is {SceneSections.WithArticle(kind)} and its partner {exit} is {SceneSections.WithArticle(other)}. The " +
        "game counts each end in another route table. Delete " +
        $"one end, and its pair goes too. Then make the pair again with {SceneSections.PairTool}.";

    /// <summary>
    /// The warning of a jump object of the active mod near another jump object of its system, or null. The active mod
    /// adds the object or sets its <c>pos</c>. Two jump objects closer than the spacing of the rule data have traffic
    /// zones that overlap. Retail has 7 such pairs, so a jump object that the active mod does not place gets no warning.
    /// </summary>
    private static Diagnostic? Crowded(EditSession session, SectionRef section, SectionView view, string name)
    {
        if (session.Lists.RuleData.Builders.Connection?.Spacing is not { } spacing ||
            SceneSections.Position(view) is not { } at || session.Origin(section, "pos").Origin != ValueOrigin.ActiveMod)
            return null;
        foreach (var other in session.Sections(section.Path))
        {
            if (other.Equals(section) || !SceneSections.Is(other.Id.Type, SceneSections.ObjectType) ||
                session.Get(other) is not { } otherView || !IsJump(otherView) ||
                SceneSections.Position(otherView) is not { } there)
                continue;
            var distance = Vector3.Distance(at, there);
            if (distance < spacing)
                return Warning(section, $"{name} is {Meters(distance)} m from the jump object {other.Id.Key}. Keep jump " +
                                        $"objects at least {Meters(spacing)} m apart, so that their traffic zones do not " +
                                        "overlap.") with
                {
                    Key = "pos",
                };
        }
        return null;
    }

    /// <summary>A distance in whole meters, for a message.</summary>
    internal static string Meters(float distance) => MathF.Round(distance).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The warnings of a <c>[locked_gates]</c> section: each lock line that names no jump object of the jump
    /// graph. A jump object is a jump gate, a jump hole or an airlock gate with a <c>goto</c> line.</summary>
    private static IEnumerable<Diagnostic> Locks(EditSession session, SectionRef section, SectionView view)
    {
        var gates = session.Routes.Graph.Connections.Select(c => FLHash.CreateID(c.Object)).ToHashSet();
        for (var i = 0; i < view.Entries.Count; i++)
        {
            var entry = view.Entries[i];
            if (!IsLockLine(entry) || entry.Values.Count == 0)
                continue;
            if (ItemLists.HashOf(entry.Values[0]) is { } hash && !gates.Contains(hash))
                yield return Warning(section,
                    $"The gate lock {hash} in {SceneSections.FileName(section.Path)} names no jump gate or jump hole. The " +
                    "game ignores the line.") with { Key = entry.Name, EntryIndex = i };
        }
    }

    /// <summary>True for a <c>locked_gate</c> or an <c>npc_locked_gate</c> line.</summary>
    private static bool IsLockLine(EntryView entry) =>
        SceneSections.Is(entry.Name, "locked_gate") || SceneSections.Is(entry.Name, "npc_locked_gate");

    private static bool Same(ValueBase value, string text) =>
        value.ToString().Trim().Equals(text, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(SectionView view, string name)
    {
        for (var i = 0; i < view.Entries.Count; i++)
        {
            if (SceneSections.Is(view.Entries[i].Name, name))
                return i;
        }
        return int.MaxValue;
    }

    private static Diagnostic Error(SectionRef section, string message) =>
        new(DiagnosticSeverity.Error, section.Path, message) { Target = section, Key = "goto" };

    private static Diagnostic Warning(SectionRef section, string message) =>
        new(DiagnosticSeverity.Warning, section.Path, message) { Target = section, Key = "goto" };
}
