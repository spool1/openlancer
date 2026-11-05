using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>The events that Ctrl+C copied: their tables, in the order of W.</summary>
internal sealed record TimelineClip(IReadOnlyList<ThnTable> Events)
{
    /// <summary>The time of the earliest event.</summary>
    public float Start => Events.Min(TimeOf);

    /// <summary>The end of the event that ends last: its time and its length.</summary>
    public float End => Events.Max(e => TimeOf(e) + LengthOf(e));

    /// <summary>The first target of the earliest event: the row of the clip. Null when it has no text target.</summary>
    public string? Row => FirstTarget(Events.MinBy(TimeOf)!);

    internal static float TimeOf(ThnTable ev) => ev.List.Length > 0 && ev.List[0] is ThnNumber time ? time.Value : 0;

    /// <summary>The length of an event: <c>props.duration</c>, or 0 when the event has no length above 0.</summary>
    internal static float LengthOf(ThnTable ev) =>
        ev.List.Length > 3 && ev.List[3] is ThnTable props && props["duration"] is ThnNumber d && d.Value > 0 ? d.Value : 0;

    internal static string? FirstTarget(ThnTable ev) =>
        ev.List.Length > 2 && ev.List[2] is ThnTable { List: [ThnText first, ..] } ? first.Value : null;
}

/// <summary>An event type that the Add event menu of a row offers.</summary>
/// <param name="Second">The second target that the new event gets, or null.</param>
/// <param name="Refusal">Why the menu cannot add the event, or null.</param>
internal sealed record TimelineAddChoice(ThnEventRole Role, string? Second, string? Refusal);

/// <summary>
/// The edits of the Timeline that are not drags (section 7.3 of the M8 design): the choices of Add event, copy, paste
/// and duplicate, and the delete of an entity with the events that name it. Each one is one command, so it is one entry
/// of the edit history. They have no ImGui call. The editor runs them through the plans of
/// <see cref="CutsceneCommands"/>.
/// </summary>
internal static class TimelineEdits
{
    /// <summary>Copies events of W, in the order of W.</summary>
    public static TimelineClip Copy(ThnScriptModel working, IEnumerable<ThnRef> events)
    {
        var ids = events.Select(e => e.Id).ToHashSet();
        return new TimelineClip(working.Events.Where(e => ids.Contains(e.Id)).Select(e => e.Table).ToList());
    }

    /// <summary>
    /// The command that adds the events of a clip, moved so that the earliest one starts at a time. The times round to
    /// 1 ms. With a row, each event whose first target is the row of the clip gets the row as its first target, so a
    /// paste onto another entity row moves the events to that entity.
    /// </summary>
    /// <param name="time">The start of the pasted events, for example the playhead.</param>
    /// <param name="row">The name of the entity of the row of the paste, or null to keep the targets.</param>
    /// <exception cref="ArgumentException">The time is not a finite number from 0, or an event is not an event
    /// list.</exception>
    public static EditCommand Paste(string path, TimelineClip clip, double time, string? row, string label = "Paste")
    {
        if (!double.IsFinite(time) || time < 0)
            throw new ArgumentException("The time of a paste is a number from 0.", nameof(time));
        var shift = time - clip.Start;
        var clipRow = clip.Row;
        var commands = clip.Events.Select(ev =>
        {
            var list = ev.List.SetItem(0, new ThnNumber(ThnEdits.OnGrid(TimelineClip.TimeOf(ev) + shift)));
            if (row != null && clipRow != null && TimelineClip.FirstTarget(ev) is { } first &&
                first.Equals(clipRow, StringComparison.OrdinalIgnoreCase) && list[2] is ThnTable targets)
                list = list.SetItem(2, new ThnTable(targets.List.SetItem(0, new ThnText(row)), targets.Record));
            return ThnEdits.AddEvent(path, new ThnTable(list, ev.Record));
        }).ToList();
        return EditCommand.Group(ClipLabel(label, clip.Events.Count), commands);
    }

    /// <summary>The label of the entry of a paste or a duplicate of events, for example "Paste event" or "Duplicate 3
    /// events".</summary>
    /// <param name="verb">"Paste" or "Duplicate".</param>
    public static string ClipLabel(string verb, int count) => count == 1 ? $"{verb} event" : $"{verb} {count} events";

    /// <summary>
    /// The command that adds a copy of each event right after the events, as Duplicate of Unity does: the copies keep
    /// their offsets, and the earliest copy starts at the end of the event that ends last. Events without a length move
    /// their copies one frame (<see cref="DuplicateStep"/>) later. So no copy has the time, the type and the targets of
    /// its source, and a later mod that names the source by that key still finds one event. The copies then take the
    /// selection, so a drag can move them.
    /// </summary>
    public static EditCommand Duplicate(string path, TimelineClip clip) =>
        Paste(path, clip, Math.Max(clip.End, clip.Start + DuplicateStep), null, "Duplicate");

    /// <summary>The smallest move of the copies of Duplicate: one frame of the preview (1/60 s) on the 1 ms grid. It is
    /// more than the float step of each time below 131,072 s, so a copy never gets the time of its source.</summary>
    internal const double DuplicateStep = 0.017;

    /// <summary>The events of W that name an entity as a target, without letter case, as the engine finds a target
    /// (Cutscene.cs:41).</summary>
    public static IReadOnlyList<ThnRef> EventsNaming(string path, ThnScriptModel working, string entityName) =>
        working.Events.Where(e => e.Table.List.Length > 2 && e.Table.List[2] is ThnTable targets &&
                                  targets.List.Any(t => t is ThnText text &&
                                                        text.Value.Equals(entityName, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new ThnRef(path, ThnElementKind.Event, e.Id)).ToList();

    /// <summary>The command that deletes events and an entity in one entry: the events first, then the entity.</summary>
    public static EditCommand DeleteWithEvents(ThnRef entity, IReadOnlyList<ThnRef> events) =>
        EditCommand.Group(DeleteEntityLabel(events.Count), [.. events.Select(ThnEdits.Delete), ThnEdits.Delete(entity)]);

    /// <summary>The label of the entry of a delete of an entity with the events that name it.</summary>
    public static string DeleteEntityLabel(int events) =>
        events == 0 ? "Delete entity" : $"Delete entity and {events} events";

    /// <summary>
    /// The events that the Add event menu of a row offers, in the order of the menu (section 7.3): the types whose first
    /// target fits the entity type. An event that needs a second target gets the first entity of the script that fits
    /// it. Without such an entity, the choice has the reason.
    /// </summary>
    /// <param name="entityName">The name of the entity of the row.</param>
    public static IReadOnlyList<TimelineAddChoice> AddChoices(ThnScriptModel working, string entityName,
        IReadOnlyDictionary<string, ThnSymbolInfo> symbols)
    {
        var entity = working.EntityNamed(entityName);
        var type = entity == null ? null : ThnSchema.TypeOf(ThnElementKind.Entity, entity.Table, symbols);
        var result = new List<TimelineAddChoice>();
        foreach (var role in ThnTemplates.RolesFor(type))
        {
            string? second = null;
            string? refusal = null;
            if (role.SecondRequired)
            {
                second = working.Entities.Where(e => e.Id != entity!.Id && ReferenceEquals(working.EntityNamed(Name(e)), e))
                    .FirstOrDefault(e => role.Fits(2, ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, symbols)))
                    is { } fit ? Name(fit) : null;
                if (second == null)
                    refusal = $"{role.Type} needs a second target, and no other entity of this script is a " +
                              $"{string.Join(" or ", role.Second)}.";
            }
            result.Add(new TimelineAddChoice(role, second, refusal));
        }
        return result;

        static string Name(ThnElement e) => ThnScriptModel.NameOf(e.Table) ?? "";
    }
}
