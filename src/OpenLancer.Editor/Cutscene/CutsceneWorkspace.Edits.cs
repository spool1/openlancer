using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Cutscene;

/// <summary>The edits of the open cutscene that the Timeline, the Inspector and the Cutscene view share (section 7 of
/// the M8 design).</summary>
internal sealed partial class CutsceneWorkspace
{
    /// <summary>The events that the last copy took, or null.</summary>
    public TimelineClip? Clip { get; private set; }

    /// <summary>The key of a motion path that the path keys editor selected, for the gizmo, or null.</summary>
    public (ThnRef Path, int Key)? SelectedPathKey { get; set; }

    /// <summary>W of the open cutscene, or null.</summary>
    public ThnScriptModel? Working => Path != null ? session?.Edits.Cutscene(Path) : null;

    /// <summary>The THN names, with their groups and numbers.</summary>
    public static IReadOnlyDictionary<string, ThnSymbolInfo> Symbols => ThornEvaluator.Instance.Symbols;

    /// <summary>The events of the selection in the open cutscene: one event, the events of a set, or none.</summary>
    public IReadOnlyList<ThnRef> SelectedEvents =>
        ThnEventSet.EventsOf(window.Selection).Where(e => VfsPath.Comparer.Equals(e.Path, Path)).ToList();

    /// <summary>Selects events of the open cutscene: one event, a set of events, or the cutscene for none.</summary>
    public void SelectEvents(IEnumerable<ThnElementId> events) =>
        window.Selection = Path == null ? null : ThnEventSet.Of(Path, events) ?? Root;

    /// <summary>
    /// The state of a value of an element for an Inspector row: the value in W, why the row cannot change it, and
    /// whether it differs from the lower cutscene.
    /// </summary>
    public ThnFieldState Field(ThnRef target, ThnPath path, ThnKeyKind kind)
    {
        var edits = window.Session!.Edits;
        var element = edits.Cutscene(target.Path)!.Element(target.Kind, target.Id);
        var value = element == null ? null : path.ValueIn(target.Kind, element.Table);
        var lower = element == null ? null : LowerTable(target.Path, target.Kind, element);
        var whole = edits.CutsceneKind(target.Path) is ThnFileKind.WholeFileHere or ThnFileKind.NewHere;
        // A whole file of the active mod sets each value, and so does an element that the mod adds.
        var changed = lower != null ? !Equals(path.ValueIn(target.Kind, lower), value) : value != null && (whole || element?.Origin < 0);
        return new ThnFieldState(target, path, kind, value,
            window.Session.NoEditReason ?? edits.CutsceneReadOnlyReason(target), changed,
            !whole && lower != null && changed);
    }

    /// <summary>The table of the element of the lower cutscene that an element of W comes from, or null.</summary>
    public ThnTable? LowerTable(string path, ThnElementKind kind, ThnElement element)
    {
        var edits = window.Session!.Edits;
        if (element.Origin < 0 || edits.CutsceneKind(path) is ThnFileKind.WholeFileHere or ThnFileKind.NewHere ||
            edits.LowerCutscene(path) is not { } lower)
        {
            return null;
        }

        var list = lower.Elements(kind);
        return element.Origin < list.Count ? list[element.Origin].Table : null;
    }

    /// <summary>Copies the selected events (Ctrl+C).</summary>
    public void Copy()
    {
        if (Working is { } working && SelectedEvents is { Count: > 0 } events)
        {
            Clip = TimelineEdits.Copy(working, events);
        }
    }

    /// <summary>Pastes the copied events at a time, onto the row of an entity or onto their own rows, and selects the
    /// new events (Ctrl+V).</summary>
    public void Paste(double time, string? row)
    {
        if (Path is not { } path || Clip is not { } clip)
        {
            return;
        }

        if (window.Commands.Execute(() => TimelineEdits.Paste(path, clip, time, row)))
        {
            SelectEvents(TimelineEdits.Added(Working!, clip.Events.Count));
        }
    }

    /// <summary>Adds a copy of each selected event right after the selected events, and selects the copies (Ctrl+D, see
    /// <see cref="TimelineEdits.Duplicate"/>).</summary>
    public void Duplicate()
    {
        if (Path is not { } path || Working is not { } working || SelectedEvents is not { Count: > 0 } events)
        {
            return;
        }

        var clip = TimelineEdits.Copy(working, events);
        if (window.Commands.Execute(() => TimelineEdits.Duplicate(path, clip)))
        {
            SelectEvents(TimelineEdits.Added(Working!, clip.Events.Count));
        }
    }

    /// <summary>Deletes the selected events (Delete), or the selected entity with the events that name it.</summary>
    public void DeleteSelection()
    {
        if (Path is not { } path)
        {
            return;
        }

        if (window.Selection is ThnRef { Kind: ThnElementKind.Entity } entity)
        {
            DeleteEntity(entity);
            return;
        }

        var events = SelectedEvents;
        if (events.Count > 0 && window.Commands.Execute(EventsDelete(events)))
        {
            window.Selection = Root;
        }
    }

    /// <summary>
    /// Deletes an entity. When events name it, a dialog lists them and offers Delete with events or Cancel (section
    /// 7.3). The engine cannot play an event whose target is gone.
    /// </summary>
    public void DeleteEntity(ThnRef entity)
    {
        if (Path is not { } path || Working is not { } working || working.Element(ThnElementKind.Entity, entity.Id) is not { } element)
        {
            return;
        }

        var name = ThnScriptModel.NameOf(element.Table) ?? "";
        // An earlier entity with the same name is not the entity that the events find.
        var events = ReferenceEquals(working.EntityNamed(name), element)
            ? TimelineEdits.EventsNaming(path, working, name)
            : [];
        if (events.Count == 0)
        {
            Run(TimelineEdits.DeleteWithEvents(entity, []));
            return;
        }

        var timeline = Timeline;
        var lines = new StringBuilder($"{events.Count} events name the entity {name}:\n\n");
        foreach (var ev in events.Take(20))
        {
            if (timeline?.Event(ev.Id) is { } info)
            {
                lines.Append($"  {info.Type} at {info.Time:0.000} s\n");
            }
        }

        if (events.Count > 20)
        {
            lines.Append($"  and {events.Count - 20} more\n");
        }

        lines.Append("\nThe engine cannot play an event whose target is gone. Delete the entity with these events?");
        window.Choice.Show("Delete entity", lines.ToString(),
        [
            new Choice("Delete with events", () => Run(TimelineEdits.DeleteWithEvents(entity, events))),
            new Choice("Cancel", () => { }),
        ]);

        void Run(Data.Editing.EditCommand command)
        {
            if (window.Commands.Execute(command))
            {
                window.Selection = Root;
            }
        }
    }

    /// <summary>
    /// Renames an entity (section 7.5). When other data uses the name, a dialog lists the uses and offers Rename in this
    /// file or Cancel. Without another use, the rename runs at once.
    /// </summary>
    public void Rename(ThnRef entity, string newName)
    {
        if (Path is not { } path || Working?.Element(ThnElementKind.Entity, entity.Id) is not { } element ||
            ThnScriptModel.NameOf(element.Table) is not { } oldName || newName == oldName)
        {
            return;
        }

        var uses = RenameUses(path, oldName);
        if (uses.Count == 0)
        {
            window.Commands.Execute(() => ThnEdits.RenameEntity(entity, newName));
            return;
        }

        window.Choice.Show("Rename entity", CutsceneRename.Message(oldName, newName, uses),
        [
            new Choice("Rename in this file", () => window.Commands.Execute(() => ThnEdits.RenameEntity(entity, newName))),
            new Choice("Cancel", () => { }),
        ]);
    }

    /// <summary>The other data that uses a name of an entity of a cutscene (see <see cref="CutsceneRename"/>).</summary>
    public IReadOnlyList<ThnNameUse> RenameUses(string path, string name) =>
        CutsceneRename.Uses(References(), path, name, ScriptModel, Symbols);

    /// <summary>The command that deletes events in one entry.</summary>
    private static Data.Editing.EditCommand EventsDelete(IReadOnlyList<ThnRef> events) =>
        Data.Editing.EditCommand.Group(events.Count == 1 ? "Delete event" : $"Delete {events.Count} events",
            [.. events.Select(ThnEdits.Delete)]);

    /// <summary>W of a script of the session, or the script of the edit view, or null when it cannot be read.</summary>
    private ThnScriptModel? ScriptModel(string path)
    {
        var edits = window.Session!.Edits;
        if (edits.OpenCutscenes.Contains(path, VfsPath.Comparer))
        {
            return edits.Cutscene(path);
        }

        try
        {
            var bytes = window.Session.GameData!.Items.VFS.ReadAllBytes(path);
            return ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(bytes, path), ThnElementId.Counter());
        }
        catch (Exception e) when (e is ThnEvaluationException or FormatException or System.IO.IOException or
                                       ArgumentException)
        {
            window.Log.Add(ConsoleSeverity.Warning, EditorWindow.EditorSource,
                $"The rename cannot read {path} to look for its REFERENCE entities: {e.Message}");
            return null;
        }
    }
}
