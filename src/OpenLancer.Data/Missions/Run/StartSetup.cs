using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Missions.Run;

/// <summary>
/// The setup triggers and the save keys of a start plan (section 5.3, steps 9 to 11 of the M6c design, decisions D748
/// and D749). The schema gives each action of the replay a start role. The actions of the role "state" go into the setup
/// trigger of the start, and the actions of the role "world" into the setup trigger of the world, in replay order. The
/// actions of the role "save" give the keys of <c>[MissionState]</c>. Filters remove the lines whose effect the path
/// undoes, for example a spawn of a ship that the path destroys.
/// </summary>
internal static class StartSetup
{
    /// <summary>The nickname of the setup trigger of the start, when the script does not use it.</summary>
    public const string SetupName = "ol_play_setup";

    /// <summary>The nickname of the setup trigger of the world, when the script does not use it.</summary>
    public const string WorldName = "ol_play_world";

    private static readonly string[] Spawns =
        ["Act_SpawnShip", "Act_SpawnShipRel", "Act_SpawnFormation", "Act_SpawnSolar", "Act_SpawnLoot"];

    /// <summary>The objective lines: only the last one counts.</summary>
    private static readonly string[] Objectives = ["Act_SetNNObj", "Act_NNIds", "Act_NNPath"];

    /// <summary>The lines that change the objective of the last objective line. They stay after it.</summary>
    private static readonly string[] ObjectiveChanges = ["Act_SetNNState", "Act_SetNNHidden", "Act_GiveNNObjs"];

    /// <summary>Gives a plan its setup triggers, its saved triggers and its save keys, and checks the setup
    /// triggers.</summary>
    /// <param name="story">The story path of the chosen trigger.</param>
    /// <param name="check">False gives the plan without the mission checks of the setup triggers.</param>
    /// <param name="anchor">The start system and the start anchor, or null when the start system is not known. Then the
    /// setup of the world keeps each world line of the path.</param>
    public static StartPlan Apply(StartPlan plan, MissionIndex index, StartReplay replay, IReadOnlyList<MissionTrigger> story,
        bool check = true, StartAnchor? anchor = null)
    {
        switch (plan.Kind)
        {
            case StartKind.MissionStart:
                return plan;
            case StartKind.Checkpoint:
                var restore = replay.Chosen!;
                // The game puts the state lines after the trigger id into the condition. A start save holds no such
                // line, so a setup trigger starts the restore trigger fresh (D749).
                if (restore.Condition?.Name?.SavesState != true)
                    return plan with { SavedTriggers = [restore.Nickname] };
                var name = FreeName(index, SetupName, []);
                var only = new SetupTrigger(name, [Line("Cnd_True", "no_params"), Line("Act_ActTrig", restore.Nickname)]);
                var single = plan with { Setup = [only], SavedTriggers = [name] };
                return check ? Check(single, index) : single;
        }

        var forced = replay.Path.Where(t => t != replay.Chosen).ToList();
        var effects = replay.Effects.Select(e => e.Line).ToList();
        var keep = Keep(index, effects, forced);
        var lines = effects.Where((_, i) => keep[i]).ToList();
        var setupName = FreeName(index, SetupName, []);
        var worldName = FreeName(index, WorldName, [setupName]);
        // The world of the start system only, with the companions next to the player (D748, changed on 2026-10-28).
        var built = anchor != null ? StartWorld.Build(index, replay, keep, anchor) : null;
        var world = built?.Lines.ToList() ?? lines.Where(l => l.Name!.Start == StartRole.World).Select(Copy).ToList();
        var worldWarnings = built?.Warnings ??
                            (plan.Place.Kind == StartPlaceKind.Camera && world.Count > 0 ? [StartWorld.CameraWarning] : []);
        var setup = new List<(string Key, IReadOnlyList<string> Values)> { Line("Cnd_True", "no_params") };
        setup.AddRange(lines.Where(l => l.Name!.Start == StartRole.State).Select(Copy));
        if (world.Count > 0)
            setup.Add(Line("Act_ActTrig", worldName));
        setup.AddRange(plan.ActiveSet.Select(t => Line("Act_ActTrig", t)));
        var triggers = new List<SetupTrigger> { new(setupName, setup) };
        if (world.Count > 0)
        {
            // The world waits for the player in space, as 29 restore triggers of the retail scripts do. A base start
            // rebuilds the world at the launch. no_params is case-sensitive (TR16).
            var wait = plan.Place.Kind == StartPlaceKind.Base ? Line("Cnd_PlayerLaunch", "no_params") : Line("Cnd_SpaceEnter", "no_params");
            triggers.Add(new SetupTrigger(worldName, [wait, .. world]));
        }
        var saved = lines.Where(l => l.Name!.Start == StartRole.Save).ToList();
        var state = StateOf(index, saved, forced);
        if (UnknownAmbientBase(index, state) is { } reason)
            return plan with { Refusal = StartRefusal.SaveProblem, Reason = reason };
        var made = plan with
        {
            Setup = triggers,
            SavedTriggers = [setupName],
            State = state,
            StartSystem = built != null ? anchor!.System : null,
            LeftOut = built?.LeftOut ?? [],
            Companions = built?.Companions ?? [],
            Warnings = [.. plan.Warnings, .. worldWarnings],
        };
        return check ? Check(made, index) : made;
    }

    /// <summary>The reason when an ambient script of the start save names a base that the game data does not have, or
    /// null. The game finds the room of an <c>ambi_scene</c> line in its base, so it cannot load such a line. A world
    /// that does not know the bases finds no problem.</summary>
    private static string? UnknownAmbientBase(MissionIndex index, StartMissionState state)
    {
        if (!index.World.Knows(MissionValueKind.Base))
            return null;
        return state.Ambients.FirstOrDefault(a => !index.World.Has(MissionValueKind.Base, a.Base)) is { File: not null } bad
            ? $"The path adds the ambient script '{bad.File}' in base '{bad.Base}', which is not in the game data. The " +
              "game cannot load the start save with it. Correct the Act_AddAmbient line, or pick another trigger."
            : null;
    }

    /// <summary>
    /// The lines of the replay that the setup keeps, in replay order (section 5.3, step 9): the lines of the roles
    /// "world", "state" and "save", without these lines:
    /// <list type="bullet">
    /// <item><c>Act_RelocateShip</c> of the player. It is a place line of the start place.</item>
    /// <item>A spawn whose objects a later <c>Act_Destroy</c> of the replay removes, or a <c>Cnd_Destroyed</c> or a
    /// <c>Cnd_LootAcquired</c> line of the path names. An <c>Act_Destroy</c> of only such objects goes too.</item>
    /// <item>Each music line but the last.</item>
    /// <item>Each objective line but the last, and the changes of the objective before it.</item>
    /// </list>
    /// </summary>
    /// <param name="forced">The triggers of the path that ran: their conditions name what the path destroys and
    /// takes.</param>
    internal static IReadOnlyList<MissionLine> Filter(MissionIndex index, IReadOnlyList<MissionLine> lines,
        IReadOnlyList<MissionTrigger> forced)
    {
        var keep = Keep(index, lines, forced);
        return lines.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>For each line of the replay, true when <see cref="Filter"/> keeps it.</summary>
    internal static bool[] Keep(MissionIndex index, IReadOnlyList<MissionLine> lines, IReadOnlyList<MissionTrigger> forced)
    {
        var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var condition in forced.Select(t => t.Condition).OfType<MissionLine>())
        {
            if (Is(condition, "Cnd_Destroyed") && condition.ValueOf("target") is { } target)
                gone.UnionWith(Members(index, target));
            else if (Is(condition, "Cnd_LootAcquired") && condition.ValueOf("loot") is { } loot)
                gone.UnionWith(Members(index, loot));
        }
        // A move of the player is a place line: the start place has its result, so a later move must not undo the place.
        var keep = lines.Select(l => l.Name?.Start is StartRole.World or StartRole.State or StartRole.Save &&
                                     !StartPlaces.IsPlayerMove(l)).ToArray();
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!keep[i])
                continue;
            if (Spawns.Any(s => Is(line, s)) && line.Value(0) is { } spawned)
            {
                var made = Members(index, spawned);
                var later = lines.Skip(i + 1).Where(l => Is(l, "Act_Destroy") && l.Value(0) != null)
                    .SelectMany(l => Members(index, l.Value(0)!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (made.All(o => gone.Contains(o) || later.Contains(o)))
                {
                    keep[i] = false;
                    dropped.UnionWith(made);
                }
            }
            else if (Is(line, "Act_Destroy") && line.Value(0) is { } destroyed && Members(index, destroyed).All(dropped.Contains))
            {
                keep[i] = false;
            }
        }
        KeepLast(lines, keep, l => Is(l, "Act_PlayMusic"), _ => false);
        KeepLast(lines, keep, l => Objectives.Any(o => Is(l, o)), l => ObjectiveChanges.Any(o => Is(l, o)));
        return keep;
    }

    /// <summary>Keeps only the last line of a kind, and drops the lines of a second kind before it.</summary>
    private static void KeepLast(IReadOnlyList<MissionLine> lines, bool[] keep, Func<MissionLine, bool> kind,
        Func<MissionLine, bool> before)
    {
        var last = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i] && kind(lines[i]))
                last = i;
        }
        for (var i = 0; i < last; i++)
        {
            if (kind(lines[i]) || before(lines[i]))
                keep[i] = false;
        }
    }

    /// <summary>
    /// The net result of the save lines (section 5.3, step 9): the last title and offer, and the base scenes and ambient
    /// scripts that stay. The path of a scene compares as the game compares it:
    /// <list type="bullet">
    /// <item>Each <c>Act_AddRTC</c> and each <c>Act_AddAmbient</c> adds one entry, also for a path that the list has.</item>
    /// <item><c>Act_RemoveRTC</c> compares the path with letter case, and it removes each scene with that path.</item>
    /// <item><c>Act_RemoveAmbient</c> compares the path without letter case, and it removes only the first match.</item>
    /// <item>A scene that a <c>Cnd_RTCDone</c> line of the path names played, so it goes. That condition compares the
    /// path without letter case.</item>
    /// </list>
    /// No compare reads <c>/</c> as <c>\</c>.
    /// </summary>
    internal static StartMissionState StateOf(MissionIndex index, IReadOnlyList<MissionLine> lines,
        IReadOnlyList<MissionTrigger> forced)
    {
        var played = forced.Select(t => t.Condition).OfType<MissionLine>().Where(c => Is(c, "Cnd_RTCDone"))
            .Select(c => c.ValueOf("file")).OfType<string>().Select(f => f.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int title = 0, offer = 0;
        var scenes = new List<(string File, bool Repeats)>();
        var ambients = new List<(string File, string Room, string Base)>();
        foreach (var line in lines)
        {
            var file = line.ValueOf("file")?.Trim();
            if (Is(line, "Act_SetTitle"))
                title = Id(line.ValueOf("text"));
            else if (Is(line, "Act_SetOffer"))
                offer = Id(line.ValueOf("text"));
            else if (Is(line, "Act_AddRTC") && file != null)
                scenes.Add((file, MissionIndex.Is(line.ValueOf("repeats"), "repeatable")));
            else if (Is(line, "Act_RemoveRTC") && file != null)
                scenes.RemoveAll(s => string.Equals(s.File, file, StringComparison.Ordinal));
            else if (Is(line, "Act_AddAmbient") && file != null && line.ValueOf("room") is { } room &&
                     line.ValueOf("base") is { } dock)
                ambients.Add((file, room.Trim(), dock.Trim()));
            else if (Is(line, "Act_RemoveAmbient") && file != null &&
                     ambients.FindIndex(a => string.Equals(a.File, file, StringComparison.OrdinalIgnoreCase)) is >= 0 and var at)
                ambients.RemoveAt(at);
        }
        scenes.RemoveAll(s => played.Contains(s.File));
        return new StartMissionState(title, offer, scenes, ambients);
    }

    /// <summary>The INI text of setup triggers for a patch: each section with <c>ol.new = true</c> after its nickname,
    /// so the merge adds it at the end of the script (section 5.4 of the M6c design).</summary>
    public static string Patch(IReadOnlyList<SetupTrigger> triggers)
    {
        var text = new StringBuilder();
        foreach (var trigger in triggers)
        {
            if (text.Length > 0)
                text.Append(IniLines.LineEnd);
            foreach (var line in SectionLines(trigger, patch: true))
                text.Append(line).Append(IniLines.LineEnd);
        }
        return text.ToString();
    }

    /// <summary>The lines of a setup trigger: the header, the nickname, <c>ol.new</c> for a patch, then its
    /// lines.</summary>
    private static IEnumerable<string> SectionLines(SetupTrigger trigger, bool patch)
    {
        yield return "[Trigger]";
        yield return IniLines.Line("nickname", [new StringValue(trigger.Nickname)]);
        if (patch)
            yield return IniLines.Line("ol.new", [new StringValue("true")]);
        foreach (var (key, values) in trigger.Lines)
            yield return IniLines.Line(key, values.Select(v => (ValueBase)new StringValue(v)));
    }

    /// <summary>
    /// The checks of the setup triggers (section 5.3, step 11): the mission checks run on the script with the setup
    /// triggers at its end, as the merge puts them. A new error of a setup trigger refuses the start with its message. A
    /// setup line is a copy of a line of the path, so an error that the line of the path has too is not new: the game
    /// ignores both lines in the same way.
    /// </summary>
    internal static StartPlan Check(StartPlan plan, MissionIndex index)
    {
        var text = string.Join(IniLines.LineEnd + IniLines.LineEnd,
            plan.Setup.Select(t => string.Join(IniLines.LineEnd, SectionLines(t, patch: false))));
        var added = OpenLancer.Data.Ini.IniDocument.Parse(index.Document.Path, Encoding.UTF8.GetBytes(text)).Sections;
        var document = MissionDocument.Of(index.Document.Path,
            [.. index.Document.Sections.Select(s => s.Section), .. added], index.Document.IsBinary);
        var merged = MissionIndex.Build(document, index.Schema, index.World, index.Coverage);
        var setup = document.Sections.Skip(index.Document.Sections.Count).Select(s => s.Ref).ToHashSet();
        var errors = MissionChecks.Run(merged).Where(p => p.Severity == DiagnosticSeverity.Error && p.Target != null).ToList();
        var known = errors.Where(p => !setup.Contains(p.Target!)).Select(p => (p.Rule, p.Message)).ToHashSet();
        var error = errors.FirstOrDefault(p => setup.Contains(p.Target!) && !known.Contains((p.Rule, p.Message)));
        return error == null
            ? plan
            : plan with { Refusal = StartRefusal.SetupProblem, Reason = $"A setup trigger has a problem: {error.Message}" };
    }

    /// <summary>A trigger name that the script does not use by name or by game id (TR4): the name, or the name with
    /// <c>_2</c>, <c>_3</c> and so on.</summary>
    /// <param name="taken">Names that other setup triggers of the plan take.</param>
    internal static string FreeName(MissionIndex index, string wanted, IReadOnlyCollection<string> taken)
    {
        var names = index.Triggers.Select(t => t.Nickname).Concat(taken).ToList();
        var ids = names.Select(FLHash.CreateID).ToHashSet();
        for (var n = 1;; n++)
        {
            var name = n == 1 ? wanted : $"{wanted}_{n.ToString(CultureInfo.InvariantCulture)}";
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase) && !ids.Contains(FLHash.CreateID(name)))
                return name;
        }
    }

    /// <summary>
    /// The objects that a name of a spawn, a destroy or a condition stands for: the ships of a formation, the members of
    /// a label, or the name itself.
    /// </summary>
    internal static IReadOnlySet<string> Members(MissionIndex index, string name)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(MissionSection? section, string nickname)
        {
            if (section?.Kind == MissionSectionKind.Formation)
            {
                foreach (var ship in section.Section.Where(e => MissionIndex.Is(e.Name, "ship") && e.Count > 0))
                    result.Add(MissionIndex.Text(ship[0]));
                return;
            }
            result.Add(nickname);
        }
        if (index.Labels.TryGetValue(name.Trim(), out var members) && index.Item(MissionSectionKind.Ship, name) == null &&
            index.Item(MissionSectionKind.Formation, name) == null)
        {
            foreach (var member in members.Where(m => m.Nickname != null))
                Add(member, member.Nickname!);
            return result;
        }
        Add(index.Item(MissionSectionKind.Formation, name), name.Trim());
        return result;
    }

    private static (string Key, IReadOnlyList<string> Values) Line(string key, params string[] values) => (key, values);

    /// <summary>A line of the script as a setup line: its key and the text of each value, as the file has them.</summary>
    private static (string Key, IReadOnlyList<string> Values) Copy(MissionLine line) =>
        (line.Key.Trim(), line.Line.Select(RetailText.Of).ToList());

    private static bool Is(MissionLine line, string key) => MissionIndex.Is(line.Key, key);

    private static int Id(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
}
