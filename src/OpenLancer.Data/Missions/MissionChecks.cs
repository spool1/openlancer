using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data;
using OpenLancer.Data.Editing;

namespace OpenLancer.Data.Missions;

/// <summary>The options of a run of the mission checks.</summary>
/// <param name="TargetsOpenLancer">True when the <c>targets</c> of the active mod hold <c>openlancer</c>. Then a name
/// that OpenLancer cannot load is a problem (rule M11, decision D419). Else it is a mark only.</param>
public sealed record MissionCheckOptions(bool TargetsOpenLancer = false);

/// <summary>
/// The checks of a mission script (section 12 of the M6a design): the rules of MSD section 3.12 on the index. Each
/// problem has the section as its target, the key and the index of its line, its rule and a subject without its place.
/// The severity is the severity of the rule for a line of the active mod. <see cref="MissionOrigins"/> gives each
/// problem its origin, and makes a problem of a lower mod or of the base game a warning (decision D410).
/// </summary>
/// <remarks>Each message says what is wrong, what the retail game does, and the fix (AGENTS.md style rule 10). A message
/// never says what OpenLancer does, except rule M11 (review finding RV21).</remarks>
public static class MissionChecks
{
    /// <summary>The Console source of the mission problems (section 12.4 of the M6a design).</summary>
    public const string Source = "Mission";

    /// <summary>The ids of the rules, in the order of section 13.6 of the M6a design. M24, M25 and M26 are the lint rules
    /// of section 11.5: unknown keys, unknown section types and words outside a closed set of words. M27 and M28 come
    /// from section 6.10 of the M6b design: two triggers with the same game id, and a name with no retail
    /// example. P01, P02 and P03 are the warnings of the linked prefab instances (section 6.10 of the M6b design). The
    /// graph file of the mod gives them, so <see cref="Prefabs.PrefabLinks"/> adds them, not <see cref="Run"/>.</summary>
    public static readonly IReadOnlyList<string> Rules =
    [
        "M01", "M02", "M03", "M04", "M05", "M06", "M07", "M08", "M09", "M10", "M11", "M12", "M13", "M14", "M15", "M16",
        "M17", "M18", "M19", "M20", "M21", "M22", "M23", "M24", "M25", "M26", "M27", "M28", "G01", "G02", "G03", "G04",
        "G05", "G06", "P01", "P02", "P03",
    ];

    /// <summary>The rules whose problems are errors for a line of the active mod. The others are warnings.</summary>
    private static readonly HashSet<string> ErrorRules =
    [
        "M01", "M02", "M05", "M06", "M07", "M08", "M09", "M11", "M12", "M13", "M16", "M17", "M18", "M21", "M22", "M23",
        "M27",
    ];

    /// <summary>The severity of a rule for a line of the active mod.</summary>
    public static DiagnosticSeverity SeverityOf(string rule) =>
        ErrorRules.Contains(rule) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;

    /// <summary>Runs all rules on the index of one script.</summary>
    public static IReadOnlyList<Diagnostic> Run(MissionIndex index, MissionCheckOptions? options = null)
    {
        options ??= new MissionCheckOptions();
        var result = new List<Diagnostic>();
        var context = new Context(index, result);
        context.Triggers();
        context.References();
        context.Values();
        context.Formations();
        context.Objectives();
        context.Conversations();
        context.Lint();
        context.Graph();
        context.OpenLancer(options);
        return result;
    }

    /// <summary>The word of a kind in the messages of rules M06 and M23.</summary>
    internal static string KindWord(MissionValueKind kind) => kind switch
    {
        MissionValueKind.Object => "ship, solar or label",
        MissionValueKind.Group => "ship or formation",
        MissionValueKind.ObjList => "order list",
        MissionValueKind.Dialog => "conversation",
        MissionValueKind.Npc => "NPC",
        MissionValueKind.RandomEncounter => "random encounter",
        MissionValueKind.MetaBehavior => "meta behavior",
        MissionValueKind.SystemObject => "system object",
        MissionValueKind.ShipArch => "ship archetype",
        MissionValueKind.NpcShipArch => "ship build",
        MissionValueKind.FormationShape => "formation shape",
        MissionValueKind.Head or MissionValueKind.Body or MissionValueKind.Accessory => "costume part",
        MissionValueKind.CommLine => "comm line",
        MissionValueKind.Equipment => "equipment item",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>A count with the singular or the plural form of a noun, for example "1 instance" or "3 instances".</summary>
    public static string Count(int n, string one, string many) =>
        n == 1 ? $"1 {one}" : $"{n.ToString(CultureInfo.InvariantCulture)} {many}";

    /// <summary>A word with "a" or "an" before it, for example "an order list".</summary>
    internal static string WithArticle(string word) =>
        (word.Length > 0 && ("aeiou".Contains(char.ToLowerInvariant(word[0])) ||
                             word.StartsWith("NPC", StringComparison.Ordinal))
            ? "an "
            : "a ") + word;

    private sealed class Context(MissionIndex index, List<Diagnostic> result)
    {
        private readonly MissionDocument document = index.Document;

        private void Add(string rule, MissionSection? section, int entry, string message, string subject)
        {
            var key = section != null && entry >= 0 && entry < section.Section.Count ? section.Section[entry].Name : null;
            result.Add(new Diagnostic(SeverityOf(rule), Source, message)
            {
                Target = section?.Ref,
                Key = key,
                EntryIndex = entry,
                Rule = rule,
                Subject = $"{rule}|{subject}",
            });
        }

        /// <summary>The name of a section in a subject: the nickname, or the identity.</summary>
        private static string Id(MissionSection section) => section.Nickname ?? section.Ref.Id.ToString();

        /// <summary>Rules M01 to M04, M10 and M27.</summary>
        public void Triggers()
        {
            // The game finds a trigger by the FLHash id of its name (risk R14 of the M6b design).
            var ids = new Dictionary<uint, string>();
            foreach (var trigger in index.Triggers)
            {
                var section = trigger.Section;
                if (trigger.IsDuplicate)
                {
                    Add("M02", section, trigger.NicknameLine,
                        $"Two triggers have the name '{trigger.Nickname}'. The game uses the first one and skips the " +
                        "second. Give each trigger its own name.", trigger.Nickname);
                    continue;
                }
                var id = FLHash.CreateID(trigger.Nickname);
                if (ids.TryGetValue(id, out var first))
                    Add("M27", section, trigger.NicknameLine,
                        $"Triggers '{first}' and '{trigger.Nickname}' have the same game id. The game keeps only the first " +
                        "of the two. Rename one of them.", $"{first}|{trigger.Nickname}");
                else
                    ids[id] = trigger.Nickname;
                if (trigger.Condition == null)
                    Add("M01", section, -1, TriggerRules.NoConditionMessage(trigger.Nickname), trigger.Nickname);
                if (trigger.Conditions.Count > 1)
                {
                    var last = trigger.Condition!.Key;
                    foreach (var line in trigger.Conditions.Take(trigger.Conditions.Count - 1))
                        Add("M03", section, line.Entry,
                            $"Trigger '{trigger.Nickname}' has {trigger.Conditions.Count} conditions. The game uses only the " +
                            $"last one, {last}. Keep one condition.", $"{trigger.Nickname}|{line.Key}");
                }
                foreach (var line in trigger.LinesBeforeNickname)
                {
                    var key = section.Section[line].Name;
                    Add("M04", section, line,
                        $"Line '{key}' comes before the nickname of trigger '{trigger.Nickname}'. The game ignores this " +
                        "line. Move it after the nickname.", $"{trigger.Nickname}|{key}");
                }
            }
            foreach (var cancel in index.Cancels)
                Add("M10", cancel.Source.Section, cancel.Line.Entry,
                    $"Trigger '{cancel.Source.Nickname}' starts '{cancel.TargetName}' and then stops it. The game does not " +
                    $"start '{cancel.TargetName}'. Remove one of the two lines.", $"{cancel.Source.Nickname}|{cancel.TargetName}");
        }

        /// <summary>Rules M05, M06, M07, M13, M21, M22 and M23.</summary>
        public void References()
        {
            foreach (var line in index.Lines)
            {
                foreach (var reference in line.References)
                {
                    var owner = line.Trigger?.Nickname ?? Id(line.Section);
                    switch (reference.State)
                    {
                        case MissionReferenceState.WrongCase:
                            Add("M13", line.Section, line.Entry,
                                $"'{reference.Name}' must be '{reference.Expected}'. The game compares this word with letter " +
                                $"case, so the line does nothing. Write '{reference.Expected}'.",
                                $"{owner}|{line.Key}|{reference.Name}");
                            break;
                        case MissionReferenceState.Missing:
                            Missing(line, reference, owner);
                            break;
                        case MissionReferenceState.Found when reference.Kind == MissionValueKind.Group &&
                                                              reference.ValueIndex == 0 &&
                                                              index.TakenShips.TryGetValue(reference.Name, out var formation) &&
                                                              reference.Section?.Kind == MissionSectionKind.Ship:
                            Add("M07", line.Section, line.Entry,
                                $"Formation '{formation.Nickname}' takes ship '{reference.Name}', so this spawn spawns " +
                                $"nothing. Spawn formation '{formation.Nickname}' instead.", $"{owner}|{reference.Name}");
                            break;
                    }
                }
            }
        }

        private void Missing(MissionLine line, MissionReference reference, string owner)
        {
            var name = reference.Name;
            switch (reference.Spec.Scope)
            {
                case MissionKindScope.Value:
                    // Only a word outside a closed set of words is missing. The game reads a word outside an open set
                    // as its default.
                    var words = reference.Spec.Tokens is { } set && index.Schema.Tokens.TryGetValue(set, out var tokens)
                        ? string.Join(", ", tokens.Values)
                        : "";
                    Add("M26", line.Section, line.Entry,
                        $"'{name}' is not a word of '{reference.Spec.Label}'. The effect in the game is not known. Write " +
                        $"one of these words: {words}.", $"{owner}|{line.Key}|{name}");
                    return;
                case MissionKindScope.Script when reference.Kind == MissionValueKind.Trigger:
                    Add("M05", line.Section, line.Entry,
                        $"'{line.Plain}' names trigger '{name}'. This script has no such trigger. The game ignores this " +
                        "line. Pick a trigger of this script.", $"{owner}|{name}");
                    return;
                case MissionKindScope.Script when reference.Kind == MissionValueKind.Object:
                    // A name of the label rule can also name an object of a system (review finding RV39).
                    Add("M06", line.Section, line.Entry,
                        $"'{line.Plain}' names '{name}'. This script has no ship, formation, solar, loot or label with this " +
                        "name, and no system has an object with this name. The game ignores this line. Pick a name from " +
                        "the list.", $"{owner}|{reference.Kind}|{name}");
                    return;
                case MissionKindScope.Script:
                    var kind = KindWord(reference.Kind);
                    Add("M06", line.Section, line.Entry,
                        $"'{line.Plain}' names {kind} '{name}'. This script has no such {kind}. The game ignores this line. " +
                        $"Pick {WithArticle(kind)} of this script.", $"{owner}|{reference.Kind}|{name}");
                    return;
                case MissionKindScope.File:
                    Add("M22", line.Section, line.Entry,
                        $"File '{name}' does not exist. The game loads nothing for this line. Pick a file that exists.",
                        $"{owner}|{name}");
                    return;
                case MissionKindScope.World when reference.Kind == MissionValueKind.CommLine:
                    Add("M21", line.Section, line.Entry,
                        $"Comm line '{name}' is in no voices file. The game plays no sound for this line. Pick a comm line " +
                        "from the list.", $"{owner}|{name}");
                    return;
                case MissionKindScope.World when index.IsShipFile:
                    var part = KindWord(reference.Kind);
                    Add("M23", line.Section, line.Entry,
                        $"Ship build '{owner}' names {part} '{name}'. The game data has no {part} with this name, so the " +
                        $"game drops a mission ship with this build. Pick {WithArticle(part)} from the list.",
                        $"{owner}|{reference.Kind}|{name}");
                    return;
                default:
                    var world = KindWord(reference.Kind);
                    Add("M23", line.Section, line.Entry,
                        $"'{line.Plain}' names {world} '{name}'. The game data has no {world} with this name. The game " +
                        $"ignores this line. Pick {WithArticle(world)} from the list.", $"{owner}|{reference.Kind}|{name}");
                    return;
            }
        }

        /// <summary>Rules M12, M14, M15 and M20: the values of the lines.</summary>
        public void Values()
        {
            foreach (var line in index.Lines)
            {
                if (line.Section.Kind == MissionSectionKind.Trigger &&
                    (line.Trigger == null || line.Entry < line.Trigger.NicknameLine || line.Trigger.IsDuplicate))
                    continue;
                var owner = line.Trigger?.Nickname ?? Id(line.Section);
                foreach (var spec in line.Specs)
                {
                    var end = Math.Min(spec.End, line.Count);
                    for (var i = spec.Index; i < end; i++)
                    {
                        if (line.Value(i) is not { } value)
                            continue;
                        if (spec.Buffer != null && index.Schema.Buffers.TryGetValue(spec.Buffer, out var buffer) &&
                            value.Length > buffer.Max)
                        {
                            var what = buffer.What;
                            var shorter = what.StartsWith("The ", StringComparison.Ordinal) ? what[4..] : what;
                            Add("M12", line.Section, line.Entry,
                                $"{what} '{value}' has {value.Length} characters. The game reads at most {buffer.Max} " +
                                $"characters, so it does not find the item. Use a shorter {shorter}.",
                                $"{owner}|{line.Key}|{value}");
                        }
                        if (spec.Kind == MissionValueKind.Bool && !TriggerRules.ReadsAsBool(value))
                            Add("M14", line.Section, line.Entry, TriggerRules.BooleanMessage(value),
                                $"{owner}|{line.Key}|{value}");
                        if ((spec.Kind == MissionValueKind.WholeNumber || spec.Whole) &&
                            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                            number != Math.Truncate(number))
                            Add("M15", line.Section, line.Entry,
                                $"The game reads only the whole part of the {spec.Label.ToLowerInvariant()}. So {value} " +
                                $"becomes {MissionIndex.Number(Math.Truncate(number))}. Use a whole number.",
                                $"{owner}|{line.Key}|{value}");
                    }
                }
                if (Delay(line) is { } delay && line.Value(delay) is { } text &&
                    int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) &&
                    seconds != -1 && seconds is < 1 or > 10)
                    Add("M20", line.Section, line.Entry,
                        $"The delay {text} is not -1 and not a value from 1 to 10. The effect in the game is not known. " +
                        "Use -1 or a value from 1 to 10.", $"{owner}|{line.Key}|{text}");
            }
        }

        /// <summary>The index of the delay value of a line whose delay the game reads as -1 or 1 to 10, or null.</summary>
        private static int? Delay(MissionLine line) => line.Key.ToLowerInvariant() switch
        {
            "act_startdialog" => 1,
            "act_sendcomm" => 3,
            "act_ethercomm" => 4,
            _ => null,
        };

        /// <summary>Rules M08 and M09: the ship lines of formations and the order of the ship keys.</summary>
        public void Formations()
        {
            foreach (var (formation, line, ship) in index.LaterMembers)
                Add("M08", formation, line,
                    $"Formation '{formation.Nickname}' names ship '{ship}', which comes later in the file. The game does " +
                    $"not put the ship in the formation. Move ship '{ship}' before the formation.", $"{Id(formation)}|{ship}");
            foreach (var section in document.Sections.Where(s => s.Kind is MissionSectionKind.Ship or MissionSectionKind.Formation))
            {
                string? after = null;
                for (var i = 0; i < section.Section.Count; i++)
                {
                    var key = section.Section[i].Name;
                    if (after != null && MissionIndex.IsShipKey(key))
                        Add("M09", section, i,
                            $"Line '{key}' comes after the '{after}' line in '{Id(section)}'. The game can stop with this " +
                            $"order. Move '{key}' before '{after}'.", $"{Id(section)}|{key}");
                    if (MissionIndex.Is(key, "formation") || MissionIndex.Is(key, "ship"))
                        after ??= key;
                }
            }
        }

        /// <summary>Rules M16 and M17: the type of each objective.</summary>
        public void Objectives()
        {
            foreach (var objective in document.OfKind(MissionSectionKind.Objective))
            {
                var name = Id(objective);
                var valid = false;
                for (var i = 0; i < objective.Section.Count; i++)
                {
                    var entry = objective.Section[i];
                    if (!MissionIndex.Is(entry.Name, "type") || entry.Count == 0)
                        continue;
                    var type = MissionIndex.Text(entry[0]);
                    if (type.Length > 0 && (char.IsAsciiDigit(type[0]) || type[0] == '-'))
                    {
                        // The game reads the number with atoi, so it reads the leading digits only.
                        var number = double.TryParse(type, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                            ? Math.Truncate(n)
                            : 0;
                        if (number == 0)
                            continue;
                        if (number is < 1 or > 8)
                        {
                            Add("M17", objective, i,
                                $"Objective '{name}' has type {type}. The game stops with a type outside 1 to 8. Pick a type " +
                                "from 1 to 8.", $"{name}|{type}");
                            continue;
                        }
                        valid = true;
                    }
                    else if (type.ToLowerInvariant() is "navmarker" or "base" or "ids" or "rep_inst")
                        valid = true;
                }
                if (!valid)
                    Add("M16", objective, -1,
                        $"Objective '{name}' has no valid type. The game drops this objective. Pick a type.", name);
            }
        }

        /// <summary>Rules M18 and M19: the start and the system of each conversation, and the systems of its
        /// speakers.</summary>
        public void Conversations()
        {
            foreach (var dialog in document.OfKind(MissionSectionKind.Conversation))
            {
                var name = Id(dialog);
                var system = dialog.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "system") && e.Count > 0);
                if (dialog.Section.Count == 0 || !MissionIndex.Is(dialog.Section[0].Name, "nickname") || system == null)
                {
                    Add("M18", dialog, -1,
                        $"Conversation '{name}' does not start with its nickname, or it has no system. The game drops this " +
                        "conversation. Put the nickname first, and add a system.", name);
                    continue;
                }
                var systemName = MissionIndex.Text(system[0]);
                var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < dialog.Section.Count; i++)
                {
                    var line = dialog.Section[i];
                    if (!MissionIndex.Is(line.Name, "line") || line.Count == 0)
                        continue;
                    var speaker = MissionIndex.Text(line[0]);
                    var item = index.Item(MissionSectionKind.Ship, speaker) ?? index.Item(MissionSectionKind.Formation, speaker) ??
                               index.Item(MissionSectionKind.Solar, speaker);
                    // Only a system line of the speaker proves its system. A ship that jumps can be in each system.
                    if (item == null || FixedSystem(item) is not { } itemSystem ||
                        itemSystem.Equals(systemName, StringComparison.OrdinalIgnoreCase) || !warned.Add(speaker))
                        continue;
                    Add("M19", dialog, i,
                        $"Speaker '{speaker}' of conversation '{name}' is not in system {systemName}. The conversation " +
                        $"does not start. Spawn the speaker in {systemName}.", $"{name}|{speaker}");
                }
            }
        }

        /// <summary>The value of the system line of an item that does not jump, or null.</summary>
        private static string? FixedSystem(MissionSection item)
        {
            if (item.Section.Any(e => MissionIndex.Is(e.Name, "jumper") && e.Count > 0 &&
                                      TriggerRules.ReadsAsBool(MissionIndex.Text(e[0])) &&
                                      MissionIndex.Text(e[0]).ToLowerInvariant() is "true" or "1"))
                return null;
            return item.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, "system") && e.Count > 0) is { } line
                ? MissionIndex.Text(line[0])
                : null;
        }

        /// <summary>Rules M24 and M25: keys that the game does not read in a section type, and section types that the
        /// game does not read.</summary>
        public void Lint()
        {
            foreach (var section in document.Sections)
            {
                var spec = index.SpecOf(section);
                // The game reads only the ship builds of a ship build file, so another section type has no effect.
                if (spec == null && index.IsShipFile)
                    continue;
                if (spec == null)
                {
                    Add("M25", section, -1,
                        $"Section type [{section.Type}] is not a known type of a mission script. The editor does not " +
                        "check this section. Make sure that the type name is correct.", section.Ref.Id.ToString());
                    continue;
                }
                foreach (var line in index.LinesOf(section).Where(l => l.Name is { NoRetailExample: true }))
                {
                    Add("M28", section, line.Entry,
                        $"The game data has no example of '{line.Name!.Plain}'. Its effect is not checked. Test the mission " +
                        "in the game.", $"{Id(section)}|{line.Key}");
                }
                if (spec.OpenKeys)
                    continue;
                foreach (var line in index.LinesOf(section).Where(l => !l.Known))
                {
                    Add("M24", section, line.Entry,
                        $"Line '{line.Key}' is not a key of [{section.Type}]. The game ignores this line. Check the name of " +
                        "the key.", $"{Id(section)}|{line.Key}");
                }
            }
        }

        /// <summary>Rules G01 to G06: the graph of the triggers.</summary>
        public void Graph()
        {
            var triggers = index.Triggers.Where(t => !t.IsDuplicate).ToList();
            if (triggers.Count > 0 && index.StartTriggers.Count == 0)
                Add("G01", null, -1, "This mission has no start trigger. Nothing in the mission runs. Make one trigger start " +
                                     "with the mission.", "");
            var ends = triggers.SelectMany(t => t.ActionsOf("Act_ChangeState").Select(a => (Trigger: t, Line: a))).ToList();
            var successes = ends.Where(e => MissionIndex.Is(e.Line.Value(0), "SUCCEED")).ToList();
            if (triggers.Count > 0 && successes.Count == 0)
                Add("G02", null, -1, "No trigger ends this mission with success. The story cannot go to the next mission. " +
                                     "Add \"End mission: success\" to one trigger.", "");
            else if (triggers.Count > 0 && !successes.Any(s => index.Reached.Contains(s.Trigger)))
            {
                var (first, line) = successes[0];
                Add("G02", first.Section, line.Entry,
                    $"No start trigger leads to the success line of trigger '{first.Nickname}'. The story cannot go to " +
                    $"the next mission. Start trigger '{first.Nickname}' from a trigger that runs.", first.Nickname);
            }
            if (successes.Count > 1)
                Add("G03", null, -1, $"This mission has {successes.Count} success lines. The story moves on at the first " +
                                     "one that runs. Keep one success line.", successes.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var (trigger, line) in ends.Where(e => MissionIndex.Is(e.Line.Value(0), "FAIL") && e.Line.Value(1) == null))
                Add("G04", trigger.Section, line.Entry,
                    $"Trigger '{trigger.Nickname}' fails the mission without a text. The player sees no reason for the " +
                    "fail. Add a fail text.", trigger.Nickname);
            foreach (var trigger in triggers)
            {
                if (!index.NothingStarts(trigger))
                    continue;
                Add("G05", trigger.Section, -1,
                    $"Nothing starts trigger '{trigger.Nickname}'. The game never runs it. Start it from another trigger, " +
                    "or remove it.", trigger.Nickname);
            }
            foreach (var trigger in triggers)
            {
                foreach (var call in trigger.ActionsOf("Act_CallThorn"))
                {
                    if (CameraComesBack(trigger, call))
                        continue;
                    var file = call.Value(0) ?? "";
                    Add("G06", trigger.Section, call.Entry,
                        $"No trigger after '{trigger.Nickname}' ends the camera of cutscene '{file}'. The camera does not " +
                        "come back to the player. Add \"End camera\" to a later trigger.", $"{trigger.Nickname}|{file}");
                }
            }
        }

        /// <summary>
        /// True when an <c>Act_RevertCam</c> line follows the cutscene line in its trigger, or a trigger that can run after
        /// it has one: a trigger that the trigger starts, or that a trigger that starts the trigger starts, over Start
        /// links. Retail often starts the trigger that ends the camera together with the trigger of the cutscene.
        /// </summary>
        private bool CameraComesBack(MissionTrigger trigger, MissionLine call)
        {
            if (trigger.ActionsOf("Act_RevertCam").Any(a => a.Entry > call.Entry))
                return true;
            var seen = new HashSet<MissionTrigger> { trigger };
            var queue = new Queue<MissionTrigger>();
            queue.Enqueue(trigger);
            foreach (var parent in index.StartsOf(trigger).Select(l => l.Source).Where(seen.Add))
                queue.Enqueue(parent);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var link in index.Links.Where(l => l.Source == current && l.Kind == MissionLinkKind.Start &&
                                                            l.Target != null))
                {
                    if (!seen.Add(link.Target!))
                        continue;
                    if (link.Target!.ActionsOf("Act_RevertCam").Any())
                        return true;
                    queue.Enqueue(link.Target);
                }
            }
            return false;
        }

        /// <summary>Rule M11: a name that OpenLancer cannot load, when the targets of the active mod hold
        /// OpenLancer.</summary>
        public void OpenLancer(MissionCheckOptions options)
        {
            if (!options.TargetsOpenLancer)
                return;
            foreach (var trigger in index.Triggers.Where(t => !t.IsDuplicate))
            {
                foreach (var line in trigger.Rows.Where(r => r.Mark == EngineMark.CannotLoad))
                    Add("M11", trigger.Section, line.Entry,
                        $"OpenLancer cannot load '{line.Plain}'. The mission does not start in OpenLancer. Remove the line, " +
                        "or remove OpenLancer from the targets of the mod.", $"{trigger.Nickname}|{line.Key}");
            }
        }
    }
}
