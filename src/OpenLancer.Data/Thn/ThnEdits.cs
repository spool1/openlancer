using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Thn;

/// <summary>
/// The edit commands of cutscene scripts. <see cref="EditSession.Execute"/> runs a command as one entry of the edit
/// history, and an <see cref="EditTransaction"/> runs many steps as one entry, as for the INI commands of
/// <see cref="EditCommand"/>.
/// </summary>
/// <remarks>
/// <para>A command changes W of the cutscene (see <see cref="EditSession.Cutscene"/>). It replaces only the values
/// that it changes, so all other tables stay the same objects. A save writes the patch that changes B into W.</para>
/// <para>A command finds an element by its id. It throws an <see cref="ArgumentException"/> when an argument is not
/// valid, for example a number that is not finite or an element that the cutscene does not have. It throws an
/// <see cref="InvalidOperationException"/> when the edit is not allowed: the session or the cutscene is read-only,
/// a later entity has the same name (only <see cref="Delete"/> changes such an entity), or a patch cannot give the
/// change.</para>
/// <para>An edit of a path removes the pin of the path and the pins in its value (see <see cref="ThnPin"/>).</para>
/// </remarks>
public static class ThnEdits
{
    /// <summary>The steps in one second of the grid of times and lengths. All retail event times and lengths are on
    /// a 1 ms grid.</summary>
    private const double StepsPerSecond = 1000;

    /// <summary>The refusal of a move that puts an event before the time 0.</summary>
    public const string StartsBeforeZero = "An event cannot start before 0.";

    /// <summary>The refusal of a change that gives an event a negative length.</summary>
    public const string NegativeLength = "The length of an event cannot be negative.";

    /// <summary>The refusal of a negative duration of a script.</summary>
    public const string NegativeDuration = "The duration cannot be negative.";

    /// <summary>The refusal of an edit of events of more than one cutscene.</summary>
    public const string OneCutscene = "The events must be in one cutscene.";

    /// <summary>The path of the time of an event.</summary>
    private static readonly ThnPath EventTimePath = new(ThnElementPaths.EventSlots[0]);

    /// <summary>The path of the length of an event.</summary>
    private static readonly ThnPath EventLengthPath = new("props.duration");

    /// <summary>Sets a value. Each missing record on the path is made.</summary>
    /// <remarks>Use <see cref="RenameEntity"/> for <c>entity_name</c>, and <see cref="SetFlags"/> for the flags of the
    /// Inspector.</remarks>
    public static EditCommand Set(ThnRef target, ThnPath path, ThnValue value)
    {
        CheckPath(target, path, remove: false);
        CheckValue(value, nameof(value));
        return new ValueCommand($"Set {path}", target, path, value);
    }

    /// <summary>Removes the value at a path. A path without a value changes nothing, but loses its pin.</summary>
    public static EditCommand Remove(ThnRef target, ThnPath path)
    {
        CheckPath(target, path, remove: true);
        return new ValueCommand($"Remove {path}", target, path, null);
    }

    /// <summary>
    /// Sets the flags of an entity that the Inspector shows: REFERENCE, LIT_DYNAMIC, LIT_AMBIENT and HIDDEN, and on a
    /// SOUND, SPATIAL in place of LIT_DYNAMIC. Other flags keep their state. A stored number stays a number, one flag
    /// is written as its name when the entity has a name or no flags, and else the value is the number that the
    /// engine reads.
    /// </summary>
    /// <param name="flagNames">The flags that are on after the edit.</param>
    public static EditCommand SetFlags(ThnRef entity, IReadOnlySet<string> flagNames)
    {
        CheckKind(entity, ThnElementKind.Entity);
        ArgumentNullException.ThrowIfNull(flagNames);
        return new FlagsCommand(entity, flagNames.ToFrozenSet());
    }

    /// <summary>Sets the <c>duration</c> global: the length of the script in seconds.</summary>
    public static EditCommand SetDuration(string path, float seconds)
    {
        CheckNumber(seconds, nameof(seconds));
        if (seconds < 0)
            throw new ArgumentException(NegativeDuration, nameof(seconds));
        return new DurationCommand(path, seconds);
    }

    /// <summary>Adds an entity at the end of the entities. Its name must differ from the names of the other
    /// entities in more than letter case.</summary>
    /// <param name="entity">The record of the entity, with a text <c>entity_name</c>.</param>
    public static EditCommand AddEntity(string path, ThnTable entity)
    {
        CheckValue(entity, nameof(entity));
        if (!entity.IsRecord || EntityName(entity) is not { Length: > 0 } name)
            throw new ArgumentException("An entity is a record with a text entity_name.", nameof(entity));
        return new AddCommand($"Add {name}", path, ThnElementKind.Entity, entity);
    }

    /// <summary>Adds an event at the end of the events. The engine runs it after the events of the same
    /// time.</summary>
    /// <param name="ev">The list <c>{ time, TYPE, { targets }, { props } }</c> of the event.</param>
    public static EditCommand AddEvent(string path, ThnTable ev)
    {
        CheckValue(ev, nameof(ev));
        if (ev is not { Record.IsEmpty: true, List: [ThnNumber, ThnNumber or ThnSymbol, ThnTable { Record.IsEmpty: true } targets, ..] } ||
            targets.List.Any(t => t is not ThnText) || ev.List.Length > ThnElementPaths.EventSlots.Length ||
            ev.List.Length > ThnElementPaths.PropsSlot && ev.List[ThnElementPaths.PropsSlot] is not ThnTable { IsRecord: true })
            throw new ArgumentException("An event is the list { time, TYPE, { targets }, { props } }.", nameof(ev));
        return new AddCommand($"Add {ThnWriter.Value(ev.List[1])}", path, ThnElementKind.Event, ev);
    }

    /// <summary>Deletes an entity or an event. A delete of an entity does not delete the events that name it.</summary>
    public static EditCommand Delete(ThnRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new DeleteCommand($"Delete {ThnPatch.Singular(target.Kind)}", target);
    }

    /// <summary>
    /// Gives an entity a new name. The command also changes each event target and each <c>userprops.speaker</c> of
    /// the cutscene that names the entity, without regard to letter case, as the engine finds them. Such a reference
    /// keeps the text of the lower cutscene when that text names the new name, so a rename back gives the lower
    /// cutscene. The command does not change <c>userprops.Actor</c>, which names an RTC character or a fixture role, not
    /// an entity of the script. It changes no other file.
    /// </summary>
    /// <param name="newName">ASCII text. It must differ from the names of the other entities in more than letter
    /// case.</param>
    public static EditCommand RenameEntity(ThnRef entity, string newName)
    {
        CheckKind(entity, ThnElementKind.Entity);
        if (string.IsNullOrEmpty(newName))
            throw new ArgumentException("The name cannot be empty.", nameof(newName));
        CheckValue(new ThnText(newName), nameof(newName));
        return new RenameCommand(entity, newName);
    }

    /// <summary>
    /// Moves events in time and changes their length (<c>props.duration</c>). The new times and lengths round to 1 ms,
    /// the grid of all retail times. A delta of 0 leaves that value as it is.
    /// </summary>
    /// <param name="events">Events of one cutscene.</param>
    /// <param name="timeDelta">The change of each time, in seconds.</param>
    /// <param name="lengthDelta">The change of each length, in seconds. An event without a length has the length
    /// 0.</param>
    public static EditCommand MoveEvents(IReadOnlyList<ThnRef> events, float timeDelta, float lengthDelta)
    {
        ArgumentNullException.ThrowIfNull(events);
        CheckNumber(timeDelta, nameof(timeDelta));
        CheckNumber(lengthDelta, nameof(lengthDelta));
        if (events.Count == 0)
            throw new ArgumentException("There is no event to move.", nameof(events));
        foreach (var ev in events)
            CheckKind(ev, ThnElementKind.Event);
        if (!InOneCutscene(events))
            throw new ArgumentException(OneCutscene, nameof(events));
        return new MoveCommand(MoveLabel(events.Count, timeDelta, lengthDelta), events, timeDelta, lengthDelta);
    }

    /// <summary>True when all elements are in one cutscene, whatever the letter case or the separators of their
    /// paths. A list without elements is in one cutscene.</summary>
    public static bool InOneCutscene(IEnumerable<ThnRef> elements) =>
        elements.Select(e => VfsPath.Normalize(e.Path)).Distinct(VfsPath.Comparer).Count() <= 1;

    /// <summary>
    /// Why an entity cannot have a name in W, or null: another entity has the name, without regard to letter case. The
    /// engine finds an entity by its name without letter case (Cutscene.cs:41), so two such entities give one entity.
    /// <see cref="AddEntity"/> and <see cref="RenameEntity"/> refuse with this reason when they run.
    /// </summary>
    /// <param name="except">The entity that gets the name, or null for a new entity.</param>
    public static string? NameTakenRefusal(ThnScriptModel working, string name, ThnElementId? except = null)
    {
        ArgumentNullException.ThrowIfNull(working);
        return working.Entities.Any(e => e.Id != except && Names(e.Table[ThnKeys.EntityName], name))
            ? $"Another entity has the name {name}."
            : null;
    }

    /// <summary>The reason why an edit cannot find an element in W, for example after an undo removed it.</summary>
    public static string NoSuchElement(ThnElementKind kind) =>
        $"The cutscene has no such {ThnPatch.Singular(kind)}. An undo can have removed it.";

    /// <summary>The label of the entry of a move of events (see <see cref="MoveEvents"/>), for example "Move 2 events"
    /// or "Change the length of event".</summary>
    public static string MoveLabel(int count, float timeDelta, float lengthDelta) =>
        $"{(timeDelta == 0 && lengthDelta != 0 ? "Change the length of" : "Move")} {(count == 1 ? "event" : $"{count} events")}";

    /// <summary>
    /// Why <see cref="MoveEvents"/> cannot give the events of W their new values now, or null when it can. The reasons
    /// are: a time or a length to change is not a number, an event starts before 0, or a length is negative. The run of
    /// the move refuses with the same reason, so a caller can refuse before the run.
    /// </summary>
    /// <remarks>This check looks only at the values. The run can refuse an event for another reason first: W does not
    /// have the event, the cutscene or the entity is read-only, or a patch cannot change the event.</remarks>
    public static string? MoveRefusal(EditSession session, IReadOnlyList<ThnRef> events, float timeDelta,
        float lengthDelta)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(events);
        var working = events.Count > 0 ? session.Cutscene(events[0].Path) : null;
        foreach (var ev in events)
        {
            if (working?.Element(ThnElementKind.Event, ev.Id)?.Table is { } table &&
                Moved(table, timeDelta, lengthDelta).Refusal is { } refusal)
                return refusal;
        }
        return null;
    }

    /// <summary>
    /// Why <see cref="Reorder"/> cannot move <paramref name="ev"/> before <paramref name="before"/> now, or null when it
    /// can.
    /// </summary>
    /// <remarks>
    /// The engine runs the events of one time in their order in the list (ThnScript.cs:150-152). A patch adds a new
    /// event at the end of the list. So in a patch, the events that must run after the moved event become a delete
    /// and a new event. Reorder refuses this for an event whose key repeats in the lower cutscene: a later mod that
    /// names such an event by its place (<c>ol_nth</c>) could then name another event (decision D72).
    /// </remarks>
    public static string? ReorderRefusal(EditSession session, ThnRef ev, ThnRef before)
    {
        ArgumentNullException.ThrowIfNull(session);
        CheckKind(ev, ThnElementKind.Event);
        CheckKind(before, ThnElementKind.Event);
        return session.CutsceneReadOnlyReason(ev.Path) ?? Reordered(session.CutsceneDocument(ev.Path)!, ev, before, out _);
    }

    /// <summary>Moves an event before another event of the same time (see <see cref="ReorderRefusal"/>). The command
    /// throws an <see cref="InvalidOperationException"/> with the reason when it runs where Reorder is refused.</summary>
    public static EditCommand Reorder(ThnRef ev, ThnRef before)
    {
        CheckKind(ev, ThnElementKind.Event);
        CheckKind(before, ThnElementKind.Event);
        return new ReorderCommand(ev, before);
    }

    /// <summary>
    /// Puts back the values of the lower cutscene B and removes the pins: of the whole cutscene when
    /// <paramref name="target"/> is null, of one element, or of one path of an element. With a null target, the
    /// value path can be <c>duration</c>. After a revert of the whole cutscene, a save deletes the patch of the active
    /// mod.
    /// </summary>
    /// <remarks>
    /// <para>A revert of an entity whose name differs from its name in B also changes the references to the entity
    /// back, as <see cref="RenameEntity"/> does. A revert of a path removes the records that a set made for its value
    /// when B has no value there. When the loaded patch sets a record that holds the path, the other values of the
    /// record stay in the patch.</para>
    /// <para>The command throws an <see cref="InvalidOperationException"/> when it runs on a whole file of the active
    /// mod, on an element that the active mod adds, or on a lower event that the patch deletes and adds again (see
    /// <see cref="Reorder"/>). B has no values for them in the file.</para>
    /// </remarks>
    public static EditCommand Revert(string path, ThnRef? target = null, ThnPath? valuePath = null)
    {
        if (target == null && valuePath != null && valuePath != ThnPin.Duration.Path)
            throw new ArgumentException($"Without an element, the only value to revert is '{ThnPin.Duration.Path}'.",
                nameof(valuePath));
        if (target != null && valuePath is { } p)
            CheckPath(target, p, remove: false);
        if (target != null && !VfsPath.Comparer.Equals(VfsPath.Normalize(target.Path), VfsPath.Normalize(path)))
            throw new ArgumentException("The element is in another cutscene.", nameof(target));
        var what = valuePath?.Text ?? (target == null ? "cutscene" : ThnPatch.Singular(target.Kind));
        return new RevertCommand($"Revert {what}", path, target, valuePath);
    }

    /// <summary>The refusal of a new cutscene at a path that the edit view or W has.</summary>
    public const string PathInUse = "A cutscene with this path exists. Type another name.";

    /// <summary>The refusal of a new cutscene without an active mod.</summary>
    public const string NoActiveMod = "Make a mod active first. A new cutscene goes into the active mod.";

    /// <summary>The refusal of a name with other characters.</summary>
    public const string BadName = "Use only letters, digits, \"_\" and \"-\" in the name.";

    /// <summary>
    /// Why the user cannot make a new cutscene at an install path, or null (decision D134 of the M8b design): the
    /// session has no active mod or is read-only, the path is not <c>DATA\MODS\&lt;active mod id&gt;\CUTSCENES\&lt;name&gt;.thn</c>,
    /// the name has a character other than letters, digits, <c>_</c> and <c>-</c>, the edit view or W has a file at the
    /// path in any letter case, or the active mod is in the OpenLancer repo (decision D70).
    /// </summary>
    public static string? NewCutsceneRefusal(EditSession session, string path) =>
        NewCutsceneProblem(session, path)?.Message;

    /// <summary>
    /// The reason of <see cref="NewCutsceneRefusal"/> with the detail code of its error (see
    /// <see cref="MissionErrors"/>), or null. A path that the edit view or W has gives
    /// <c>PathInUse</c>. A name or a folder that the rules do not allow gives <c>NameInvalid</c>. A session without an
    /// active mod, a read-only session and a mod in the OpenLancer repo give <c>NotEditable</c>.
    /// </summary>
    internal static (string Message, string Detail)? NewCutsceneProblem(EditSession session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ActiveMod is not { } mod)
            return (NoActiveMod, MissionErrors.NotEditable);
        if (session.ReadOnlyReason is { } reason)
            return (reason, MissionErrors.NotEditable);
        if (NewCutscenePathProblem(mod, path) is { } problem)
            return (problem, MissionErrors.NameInvalid);
        if (session.HasCutsceneFile(VfsPath.Normalize(path)!))
            return (PathInUse, MissionErrors.PathInUse);
        return EditSession.WholeCutsceneInRepo(mod) is { } inRepo ? (inRepo, MissionErrors.NotEditable) : null;
    }

    /// <summary>
    /// Makes the script of a new cutscene at an install path of the active mod (see
    /// <see cref="NewCutsceneRefusal"/>). W gets the globals, and a save writes W as a whole file. An undo takes the
    /// cutscene back, and a save after the undo deletes its file.
    /// </summary>
    /// <param name="globals">The globals of the script, for example <see cref="ThnTemplates.NewScript"/>.</param>
    /// <exception cref="ArgumentException">The globals are not a script that the engine can read.</exception>
    public static EditCommand NewCutscene(string path, ThnTable globals)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(globals);
        try
        {
            ThnScriptModel.From(globals, ThnElementId.Counter());
            ThnWriter.Write(globals);
        }
        catch (FormatException e)
        {
            throw new ArgumentException(e.Message, nameof(globals), e);
        }

        return new NewCutsceneCommand(path, globals);
    }

    /// <summary>Why the path is not a path of a new cutscene of a mod, or null: the path and the name rules of
    /// <see cref="NewCutsceneRefusal"/>. The restore of the recovery file uses them too.</summary>
    internal static string? NewCutscenePathProblem(Mods.Mod mod, string path)
    {
        var normal = VfsPath.Normalize(path);
        var folder = EditSession.NewCutsceneFolder(mod.Id);
        if (normal == null || !VfsPath.Comparer.Equals(VfsPath.Parent(normal), folder) ||
            !normal.EndsWith(".thn", StringComparison.OrdinalIgnoreCase))
            return $@"A new cutscene goes into the folder {folder} of the active mod.";
        var name = System.IO.Path.GetFileNameWithoutExtension(normal);
        if (name.Length == 0)
            return "Type a name for the cutscene.";
        return name.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? null
            : BadName;
    }

    private static void CheckKind(ThnRef target, ThnElementKind kind)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != kind)
            throw new ArgumentException($"The element must be an {ThnPatch.Singular(kind)}.", nameof(target));
    }

    private static void CheckPath(ThnRef target, ThnPath path, bool remove)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (path.Text == null)
            throw new ArgumentException("The path is empty.", nameof(path));
        if (ThnElementPaths.Problem(target.Kind, path, remove) is { } problem)
            throw new ArgumentException(problem, nameof(path));
        if (target.Kind == ThnElementKind.Entity && path.Text == ThnKeys.EntityName)
            throw new ArgumentException("Use RenameEntity to change the name of an entity.", nameof(path));
    }

    private static void CheckNumber(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentException(ThnNumber.NotFiniteMessage, name);
    }

    /// <summary>Refuses a value that a save cannot write, for example a text that is not ASCII.</summary>
    private static void CheckValue(ThnValue value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        try
        {
            ThnWriter.Value(value);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException(e.Message, name, e);
        }
    }

    private static string? EntityName(ThnTable entity) => (entity[ThnKeys.EntityName] as ThnText)?.Value;

    /// <summary>The document, the index and the element of a reference, to change the element.</summary>
    /// <param name="delete">True for a delete, which is allowed on an entity that a later entity hides.</param>
    private static (ThnDocument Document, int Index, ThnElement Element) Find(EditSession.CommandScope scope,
        ThnRef target, bool delete = false)
    {
        var document = scope.Cutscene(target.Path);
        var index = document.IndexOf(target.Kind, target.Id);
        if (index < 0)
            throw new ArgumentException($"{target.Path} has no such {ThnPatch.Singular(target.Kind)}. An undo can have " +
                                        "removed it.", nameof(target));
        var element = document.Working.Elements(target.Kind)[index];
        if (!delete && document.ReadOnlyReasonOf(target.Kind, target.Id) is { } reason)
            throw new InvalidOperationException(reason);
        if (document.PatchProblem(target.Kind, element) is { } problem)
            throw new InvalidOperationException(problem);
        return (document, index, element);
    }

    /// <summary>W with another list of elements of one kind.</summary>
    private static ThnScriptModel With(ThnScriptModel model, ThnElementKind kind, ImmutableList<ThnElement> elements) =>
        kind == ThnElementKind.Entity ? model with { Entities = elements } : model with { Events = elements };

    /// <summary>Puts a new table of an element into W, removes the pins that <paramref name="unpinned"/> selects, and
    /// adds the pins of <paramref name="pinned"/>.</summary>
    private static void Replace(ThnDocument document, ThnElementKind kind, int index, ThnTable table,
        Func<ThnPin, bool> unpinned, IReadOnlyCollection<ThnPin>? pinned = null)
    {
        var list = document.Working.Elements(kind);
        var working = ReferenceEquals(table, list[index].Table)
            ? document.Working
            : With(document.Working, kind, list.SetItem(index, list[index] with { Table = table }));
        document.Change(working, unpinned, pinned);
    }

    /// <summary>A pin of an element at a path or in its value.</summary>
    private static Func<ThnPin, bool> PinsIn(ThnElementId element, ThnPath path) =>
        p => p.Element == element && p.Path.IsWithin(path);

    /// <summary>A pin at one of the paths or in its value.</summary>
    private static Func<ThnPin, bool> PinsIn(IReadOnlyCollection<ThnPin> paths) =>
        p => paths.Any(u => u.Element == p.Element && p.Path.IsWithin(u.Path));

    /// <summary>True when a value is a text that names an entity. The engine compares names without letter case
    /// (ThnScript.cs:107, Cutscene.cs:41).</summary>
    private static bool Names(ThnValue? value, string name) =>
        value is ThnText text && text.Value.Equals(name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// W with a new name for an entity: its <c>entity_name</c>, and each event target and each
    /// <c>userprops.speaker</c> of the cutscene that names the old name. A reference gets the text of the lower
    /// cutscene at its place when that text names the new name, and else the new name. So a rename back to the lower
    /// name gives the lower texts, also in their letter case.
    /// </summary>
    /// <param name="changed">Gets the paths that the rename changes, as pins to remove.</param>
    private static ThnScriptModel Renamed(ThnDocument document, ThnElement entity, string newName, List<ThnPin> changed)
    {
        const string speaker = "speaker";
        const string userProps = "userprops";
        var oldName = EntityName(entity.Table) ?? throw new InvalidOperationException("The entity has no text name to change.");
        var working = document.Working;
        if (NameTakenRefusal(working, newName, entity.Id) is { } taken)
            throw new InvalidOperationException(taken);

        var namePath = new ThnPath(ThnKeys.EntityName);
        changed.Add(new ThnPin(entity.Id, namePath));
        var entities = working.Entities.ToBuilder();
        entities[document.IndexOf(ThnElementKind.Entity, entity.Id)] = entity with
        {
            Table = ThnElementPaths.Set(ThnElementKind.Entity, entity.Table, namePath, new ThnText(newName)),
        };
        // The voice of a line comes from the entity that userprops.speaker names (ThnSound.cs:81-99). The engine reads
        // the userprops keys without letter case (ThnScript.cs:305-313). An entity that a later entity hides does not
        // change (decision D64).
        for (var i = 0; i < entities.Count; i++)
        {
            if (entities[i].Table[userProps] is not ThnTable { IsRecord: true } props ||
                document.ReadOnlyReasonOf(ThnElementKind.Entity, entities[i].Id) != null)
                continue;
            var lower = document.LowerTable(ThnElementKind.Entity, entities[i]);
            var table = entities[i].Table;
            foreach (var (key, value) in props.Record)
            {
                if (!key.Equals(speaker, StringComparison.OrdinalIgnoreCase) || !Names(value, oldName))
                    continue;
                var path = new ThnPath($"{userProps}{ThnPath.Separator}{key}");
                var text = Reference(lower == null ? null : ThnElementPaths.Get(ThnElementKind.Entity, lower, path));
                if (text.Equals(value))
                    continue;
                table = ThnElementPaths.Set(ThnElementKind.Entity, table, path, text);
                changed.Add(new ThnPin(entities[i].Id, path));
            }
            if (!ReferenceEquals(table, entities[i].Table))
                entities[i] = Patchable(ThnElementKind.Entity, entities[i]) with { Table = table };
        }
        // The engine finds a target by its name without letter case (Cutscene.cs:41).
        var targetsPath = new ThnPath(ThnElementPaths.EventSlots[2]);
        var events = working.Events.ToBuilder();
        for (var i = 0; i < events.Count; i++)
        {
            if (ThnElementPaths.Get(ThnElementKind.Event, events[i].Table, targetsPath) is not ThnTable { IsRecord: false } targets ||
                !targets.List.Any(t => Names(t, oldName)))
                continue;
            var lower = document.LowerTable(ThnElementKind.Event, events[i]) is { } lowerEvent
                ? ThnElementPaths.Get(ThnElementKind.Event, lowerEvent, targetsPath) as ThnTable
                : null;
            var renamed = new ThnTable([.. targets.List.Select((t, k) =>
                Names(t, oldName) ? Reference(lower != null && k < lower.List.Length ? lower.List[k] : null) : t)], targets.Record);
            if (renamed.Equals(targets))
                continue;
            events[i] = Patchable(ThnElementKind.Event, events[i]) with
            {
                Table = ThnElementPaths.Set(ThnElementKind.Event, events[i].Table, targetsPath, renamed),
            };
            changed.Add(new ThnPin(events[i].Id, targetsPath));
        }
        return working with { Entities = entities.ToImmutable(), Events = events.ToImmutable() };

        ThnValue Reference(ThnValue? lower) => Names(lower, newName) ? lower! : new ThnText(newName);

        // A patch must be able to change the element.
        ThnElement Patchable(ThnElementKind kind, ThnElement element) =>
            document.PatchProblem(kind, element) is { } problem ? throw new InvalidOperationException(problem) : element;
    }

    /// <summary>Rounds a time or a length to the 1 ms grid of all retail times. The float is the float of the double of
    /// the decimal text, as the compiler and the VM make a retail time (Undump.cs:125).</summary>
    public static float OnGrid(double seconds) =>
        (float)(Math.Round(seconds * StepsPerSecond, MidpointRounding.AwayFromZero) / StepsPerSecond);

    /// <summary>
    /// The new time and length of an event after a move, on the 1 ms grid, or why the move cannot change the event. A
    /// delta of 0 keeps that value, so it gives null. <see cref="MoveRefusal"/> and the run of the move use this one
    /// rule.
    /// </summary>
    private static (float? Time, float? Length, string? Refusal) Moved(ThnTable ev, float timeDelta, float lengthDelta)
    {
        float? time = null;
        if (timeDelta != 0)
        {
            if (ev.List.FirstOrDefault() is not ThnNumber oldTime)
                return (null, null, "The time of the event is not a number.");
            time = OnGrid((double)oldTime.Value + timeDelta);
            if (time < 0)
                return (null, null, StartsBeforeZero);
        }
        if (lengthDelta == 0)
            return (time, null, null);
        // An event without props.duration has the length 0 (ThnEvent.cs:14, :55).
        var oldLength = ThnElementPaths.Get(ThnElementKind.Event, ev, EventLengthPath) switch
        {
            null => 0f,
            ThnNumber number => number.Value,
            _ => (float?)null,
        };
        if (oldLength is not { } length)
            return (null, null, "The length of the event (props.duration) is not a number.");
        var newLength = OnGrid((double)length + lengthDelta);
        return newLength < 0 ? (null, null, NegativeLength) : (time, newLength, null);
    }

    /// <summary>Applies a change to a table with a path command. A path that cannot take the change throws an
    /// <see cref="InvalidOperationException"/>.</summary>
    private static ThnTable PathChange(Func<ThnTable> change)
    {
        try
        {
            return change();
        }
        catch (ThnPathException e)
        {
            throw new InvalidOperationException(e.Message);
        }
    }

    /// <summary>
    /// The events of W with <paramref name="ev"/> before <paramref name="before"/>, or the reason why Reorder cannot
    /// give them (see <see cref="ReorderRefusal"/>).
    /// </summary>
    private static string? Reordered(ThnDocument document, ThnRef ev, ThnRef before, out ImmutableList<ThnElement>? result)
    {
        result = null;
        if (ev.Kind != ThnElementKind.Event || before.Kind != ThnElementKind.Event)
            return "Only events have an order at one time.";
        if (!VfsPath.Comparer.Equals(VfsPath.Normalize(ev.Path), VfsPath.Normalize(before.Path)))
            return "The two events are in different cutscenes.";
        if (ev.Id == before.Id)
            return "An event cannot move before itself.";
        var events = document.Working.Events;
        var from = document.IndexOf(ThnElementKind.Event, ev.Id);
        var to = document.IndexOf(ThnElementKind.Event, before.Id);
        if (from < 0 || to < 0)
            return NoSuchElement(ThnElementKind.Event);
        var time = events[from].Table.List.FirstOrDefault();
        if (time == null || !time.Equals(events[to].Table.List.FirstOrDefault()))
            return "Move before changes the order of the events of one time. These events have different times.";

        // The events of the time in list order, which the engine keeps (ThnScript.cs:150-152), and the order to make.
        var group = Enumerable.Range(0, events.Count).Where(i => time.Equals(events[i].Table.List.FirstOrDefault())).ToList();
        var order = group.Where(i => i != from).ToList();
        order.Insert(order.IndexOf(to), from);
        if (order.SequenceEqual(group))
            return "The event is already just before that event.";
        if (document.IsWholeFile)
        {
            var moved = events.RemoveAt(from);
            result = moved.Insert(moved.IndexOf(events[to]), events[from]);
            return null;
        }

        // A patch keeps the lower events in their order, and adds new events at the end. So from the first event of
        // the new order that is new or out of the lower order, each event goes to the end as a new event.
        var tail = 0;
        for (var last = -1; tail < order.Count; tail++)
        {
            var origin = events[order[tail]].Origin;
            if (origin <= last)
                break;
            last = origin;
        }
        var keys = ThnKeys.Of(ThnElementKind.Event, document.Base!.Events.Select(e => e.Table), document.Symbols);
        foreach (var index in order.Skip(tail))
        {
            var element = events[index];
            if (document.PatchProblem(ThnElementKind.Event, element) is { } problem)
                return problem;
            if (element.Origin >= 0 && keys[element.Origin] is { Count: > 1 } key)
            {
                var match = ThnKeys.Match(ThnElementKind.Event, document.Base.Events[element.Origin].Table)!;
                return $"In a patch, Move before deletes the event {ThnKeys.Text(match)} and adds it again. The key of " +
                       $"that event repeats {key.Count} times in the lower cutscene, so a later mod that names one of " +
                       "these events by its place (ol_nth) could then name another event.";
            }
        }
        var tailIds = order.Skip(tail).Select(i => events[i].Id).ToHashSet();
        result = [.. events.Where(e => !tailIds.Contains(e.Id)), .. order.Skip(tail).Select(i => events[i] with { Origin = -1 })];
        return null;
    }

    /// <summary>Sets or removes one value.</summary>
    private sealed class ValueCommand(string label, ThnRef target, ThnPath path, ThnValue? value) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index, element) = Find(scope, target);
            var table = PathChange(() => value == null
                ? ThnElementPaths.Remove(target.Kind, element.Table, path) ?? element.Table
                : ThnElementPaths.Set(target.Kind, element.Table, path, value));
            Replace(document, target.Kind, index, table, PinsIn(element.Id, path));
        }
    }

    private sealed class FlagsCommand(ThnRef entity, IReadOnlySet<string> flags) : EditCommand("Set flags")
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index, element) = Find(scope, entity);
            var stored = element.Table[ThnFlags.Key];
            ThnValue? value;
            try
            {
                value = ThnFlags.Write(stored, ThnFlags.IsSound(element.Table, document.Symbols), flags, document.Symbols);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException(e.Message, e);
            }
            var path = new ThnPath(ThnFlags.Key);
            var table = ReferenceEquals(value, stored) || value == null
                ? element.Table
                : ThnElementPaths.Set(ThnElementKind.Entity, element.Table, path, value);
            Replace(document, ThnElementKind.Entity, index, table, PinsIn(element.Id, path));
        }
    }

    private sealed class NewCutsceneCommand(string path, ThnTable globals)
        : EditCommand($"New cutscene {System.IO.Path.GetFileNameWithoutExtension(path)}")
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var document = scope.NewCutscene(path);
            // The elements get the ids of the document, so a later new element gets an id that W does not have.
            var working = ThnDocument.WithoutOrigins(ThnScriptModel.From(globals, document.NewId));
            document.Restore(document.State with { Working = working, Exists = true });
        }
    }

    private sealed class DurationCommand(string path, float seconds) : EditCommand("Set duration")
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var document = scope.Cutscene(path);
            var working = document.Working.Duration.Equals(seconds) ? document.Working : document.Working with { Duration = seconds };
            document.Change(working, p => p == ThnPin.Duration);
        }
    }

    private sealed class AddCommand(string label, string path, ThnElementKind kind, ThnTable table) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var document = scope.Cutscene(path);
            if (kind == ThnElementKind.Entity && EntityName(table) is { } name &&
                NameTakenRefusal(document.Working, name) is { } taken)
                throw new InvalidOperationException(taken);
            var elements = document.Working.Elements(kind).Add(new ThnElement(document.NewId(), -1, table));
            document.Change(With(document.Working, kind, elements));
        }
    }

    private sealed class DeleteCommand(string label, ThnRef target) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, index, element) = Find(scope, target, delete: true);
            document.Change(With(document.Working, target.Kind, document.Working.Elements(target.Kind).RemoveAt(index)),
                p => p.Element == element.Id);
        }
    }

    private sealed class RenameCommand(ThnRef entity, string newName) : EditCommand($"Rename to {newName}")
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, _, element) = Find(scope, entity);
            var changed = new List<ThnPin>();
            document.Change(Renamed(document, element, newName, changed), PinsIn(changed));
        }
    }

    private sealed class MoveCommand(string label, IReadOnlyList<ThnRef> events, float timeDelta, float lengthDelta)
        : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var document = scope.Cutscene(events[0].Path);
            var list = document.Working.Events;
            var moved = new HashSet<ThnElementId>();
            foreach (var ev in events)
            {
                if (!moved.Add(ev.Id))
                    continue;
                // The loop changes only tables, so each index of W is an index of the list.
                var (_, index, element) = Find(scope, ev);
                var (time, length, refusal) = Moved(element.Table, timeDelta, lengthDelta);
                if (refusal != null)
                    throw new InvalidOperationException(refusal);
                var table = element.Table;
                if (time is { } newTime)
                    table = ThnElementPaths.Set(ThnElementKind.Event, table, EventTimePath, new ThnNumber(newTime));
                if (length is { } newLength)
                {
                    // When the lower event has no length, the length 0 removes the key, and the props that a move
                    // made, so the patch gets no value for them.
                    var lower = document.LowerTable(ThnElementKind.Event, element);
                    table = PathChange(() =>
                        newLength == 0 && lower != null &&
                        ThnElementPaths.Get(ThnElementKind.Event, lower, EventLengthPath) == null
                            ? ThnElementPaths.RemoveWithMadeRecords(ThnElementKind.Event, table, EventLengthPath, lower)
                            : ThnElementPaths.Set(ThnElementKind.Event, table, EventLengthPath, new ThnNumber(newLength)));
                }
                list = list.SetItem(index, element with { Table = table });
            }
            document.Change(document.Working with { Events = list }, p => p.Element is { } id && moved.Contains(id) &&
                                                   (timeDelta != 0 && p.Path.IsWithin(EventTimePath) ||
                                                    lengthDelta != 0 && p.Path.IsWithin(EventLengthPath)));
        }
    }

    private sealed class ReorderCommand(ThnRef ev, ThnRef before) : EditCommand("Move event before")
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var (document, _, _) = Find(scope, ev);
            if (Reordered(document, ev, before, out var events) is { } refusal)
                throw new InvalidOperationException(refusal);
            // An event that becomes a new event needs no pin, because the patch adds it whole.
            var renewed = events!.Where(e => e.Origin < 0).Select(e => e.Id).ToHashSet();
            renewed.ExceptWith(document.Working.Events.Where(e => e.Origin < 0).Select(e => e.Id));
            document.Change(document.Working with { Events = events! },
                p => p.Element is { } id && renewed.Contains(id));
        }
    }

    private sealed class RevertCommand(string label, string path, ThnRef? target, ThnPath? valuePath) : EditCommand(label)
    {
        internal override void Run(EditSession.CommandScope scope)
        {
            var document = scope.Cutscene(path);
            if (document.IsWholeFile || document.Base is not { } lower)
                throw new InvalidOperationException("The active mod has a whole file for this cutscene, so there are no " +
                                                    "lower values to put back.");
            if (target == null)
            {
                if (valuePath == null)
                    document.Change(lower, _ => true);
                else
                    document.Change(document.Working with { Duration = lower.Duration }, p => p == ThnPin.Duration);
                return;
            }
            var (_, index, element) = Find(scope, target);
            if (document.LowerTable(target.Kind, element) is not { } lowerTable)
                throw new InvalidOperationException(NoLowerValues(document, target.Kind, element));
            if (valuePath is not { } valueAt)
            {
                // A revert of the name also puts back the references to the entity, as a rename back does.
                var changed = new List<ThnPin>();
                var working = target.Kind == ThnElementKind.Entity && EntityName(lowerTable) is { } lowerName &&
                              EntityName(element.Table) is { } name && !string.Equals(name, lowerName, StringComparison.Ordinal)
                    ? Renamed(document, element, lowerName, changed)
                    : document.Working;
                var list = working.Elements(target.Kind);
                var renamed = PinsIn(changed);
                document.Change(With(working, target.Kind, list.SetItem(index, list[index] with { Table = lowerTable })),
                    p => p.Element == element.Id || renamed(p));
                return;
            }
            var table = RevertPath(target.Kind, lowerTable, element.Table, valueAt);
            // A pin of a record that holds the path splits into the pins of the other values of the record. So the
            // path follows the lower value, and the other values that the loaded patch sets stay in the patch.
            var pinned = new List<ThnPin>();
            try
            {
                foreach (var holder in document.State.Pins.Where(p => p.Element == element.Id && valueAt.IsWithin(p.Path) && p.Path != valueAt))
                    pinned.AddRange(ThnElementPaths.Beside(target.Kind, table, holder.Path, valueAt).Select(p => new ThnPin(element.Id, p)));
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException(e.Message, e);
            }
            Replace(document, target.Kind, index, table,
                p => p.Element == element.Id && (p.Path.IsWithin(valueAt) || valueAt.IsWithin(p.Path)), pinned);
        }

        /// <summary>Why an element of a patch has no lower values to put back: the active mod adds it, or the patch
        /// deletes the lower event and adds it again at the end, as Move before does.</summary>
        private static string NoLowerValues(ThnDocument document, ThnElementKind kind, ThnElement element) =>
            kind == ThnElementKind.Event && AddedAgain(document, element)
                ? "The patch deletes the lower event and adds it again at the end, as Move before does. So the lower " +
                  "cutscene has no values for this event in the patch. Revert the whole cutscene to put back the lower " +
                  "events and their order."
                : $"The active mod adds this {ThnPatch.Singular(kind)}, so the lower cutscene has no values for it. " +
                  "Delete it instead.";

        /// <summary>
        /// True when a new event of W is an event of B that the patch deletes and adds again. Reorder keeps the id of
        /// such an event. An event that a loaded patch adds again has the key of an event of B that W does not keep.
        /// </summary>
        private static bool AddedAgain(ThnDocument document, ThnElement ev)
        {
            var lower = document.Base!.Events;
            if (lower.Any(e => e.Id == ev.Id))
                return true;
            var kept = document.Working.Events.Where(e => e.Origin >= 0).Select(e => e.Origin).ToHashSet();
            return Signature(ev) is { } signature &&
                   lower.Where((_, i) => !kept.Contains(i)).Any(e => Signature(e) == signature);

            string? Signature(ThnElement e) =>
                ThnKeys.Signature(ThnElementKind.Event, ThnKeys.Match(ThnElementKind.Event, e.Table), document.Symbols);
        }

        /// <summary>
        /// The working table with the lower value at a path. The table is the lower table with each change of the
        /// working table that is not at the path or in its value. So the value goes back to its place in the lower
        /// record. When a change holds the path in its value, the lower value goes into that value. When the lower
        /// element has no value at the path, the records that a set made for the value go with it.
        /// </summary>
        private static ThnTable RevertPath(ThnElementKind kind, ThnTable lower, ThnTable working, ThnPath path)
        {
            var sets = new List<(ThnPath Path, ThnValue Value)>();
            var removes = new List<ThnPath>();
            try
            {
                ThnElementPaths.Diff(kind, lower, working, sets, removes);
            }
            catch (ArgumentException e)
            {
                throw new InvalidOperationException(e.Message, e);
            }
            return PathChange(() =>
            {
                var result = lower;
                foreach (var remove in removes.Where(r => !r.IsWithin(path)))
                    result = ThnElementPaths.Remove(kind, result, remove) ?? result;
                foreach (var (at, value) in sets.Where(s => !s.Path.IsWithin(path)))
                    result = ThnElementPaths.Set(kind, result, at, value);
                if (!sets.Any(s => path.IsWithin(s.Path)) && !removes.Any(path.IsWithin))
                    return result;
                return ThnElementPaths.Get(kind, lower, path) is { } lowerValue
                    ? ThnElementPaths.Set(kind, result, path, lowerValue)
                    : ThnElementPaths.RemoveWithMadeRecords(kind, result, path, lower);
            });
        }
    }
}
