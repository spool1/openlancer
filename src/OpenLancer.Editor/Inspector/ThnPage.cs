using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ImGuiNET;
using LibreLancer.Thn;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of the open cutscene, of its entities and events, and of a set of events (section 7.5 of the M8
/// design). An element shows the rows that <see cref="ThnSchema"/> lists for its type, in the groups of its records,
/// and then the keys that the schema does not know as Lua text. Each row edits W: a text field is one edit, and a
/// checkbox or a list is one command. A rename asks first when other data uses the name.
/// </summary>
internal sealed class ThnPage(EditorWindow window, PropertyGrid grid)
{
    private const string NotSet = "Not set";

    // The items of the list of an event (ThnEvent.cs:43-65): time, type, targets and props.
    private const string TargetsSlot = "targets";
    private const int PropsSlot = 3;
    private const int EventSlots = 4;

    /// <summary>The record of the start pose of an entity (ThnScript.cs:371-384).</summary>
    private const string TransformRecord = "spatialprops";

    private static readonly PropertyGrid.ComponentLabel[] QuaternionLabels =
    [
        new("W", EditorTheme.TextDim), new("X", EditorTheme.AxisX), new("Y", EditorTheme.AxisY),
        new("Z", EditorTheme.AxisZ),
    ];

    private readonly ThnCurveEditor curves = new(window, grid);
    private readonly ThnPathKeysEditor pathKeys = new(window, grid);
    private readonly ThnAnimationPicker animations = new(window, grid);

    // The text of the time shift of a set of events.
    private string shift = "0";

    /// <summary>True while a drag of a curve point is open.</summary>
    public bool IsDragging => curves.IsDragging;

    /// <summary>Ends a drag of a curve point on the release of the left button, and frees the clip preview after the list
    /// closes. Call it once in each frame, before the page draws.</summary>
    public void Frame()
    {
        curves.Frame();
        animations.Frame();
    }

    /// <summary>Ends an open drag of a curve point, for example when the window closes.</summary>
    public void EndDrag() => curves.End();

    /// <summary>Forgets the game data of the session and frees the clip preview. Call it when the profile
    /// closes.</summary>
    public void Reset() => animations.Reset();

    /// <summary>Frees the clip preview.</summary>
    public void Dispose() => animations.Dispose();

    private static IReadOnlyDictionary<string, ThnSymbolInfo> Symbols => CutsceneWorkspace.Symbols;

    public void Draw(object item)
    {
        var workspace = window.Cutscenes;
        switch (item)
        {
            case CutsceneRoot root when workspace.Valid(root) != null:
                Cutscene(workspace, root);
                break;
            case ThnEventSet set when workspace.Valid(set) is ThnEventSet valid:
                Events(workspace, valid);
                break;
            case ThnRef { Kind: ThnElementKind.Entity } entity when workspace.Element(entity) is { } element:
                Entity(workspace, entity, element.Table);
                break;
            case ThnRef { Kind: ThnElementKind.Event } ev when workspace.Element(ev) is { } element &&
                                                              workspace.Timeline?.Event(ev.Id) is { } info:
                Event(workspace, ev, element.Table, info);
                break;
            default:
                if (grid.BeginSection("Thn.Missing", EditorIcons.Film, "Cutscene"))
                {
                    grid.Text("Element", "The open cutscene has no such element.");
                    grid.EndSection();
                }

                break;
        }
    }

    /// <summary>The cutscene page: the file, the state, the duration, the counts, the timeline, the INI lines that use
    /// it, and the scripts of the preview.</summary>
    private void Cutscene(CutsceneWorkspace workspace, CutsceneRoot root)
    {
        var path = workspace.Path!;
        var edits = window.Session!.Edits;
        var timeline = workspace.Timeline;
        var readOnly = window.Session.NoEditReason ?? edits.CutsceneReadOnlyReason(path);
        if (grid.BeginSection("Thn.Cutscene", EditorIcons.Film, "Cutscene"))
        {
            grid.Text("Path", path);
            var source = edits.CutsceneSource(path);
            var state = CutsceneCatalog.StateText(source);
            if (source.Kind == ThnFileKind.NewHere)
            {
                // A new cutscene is a text file, which the retail engine reads only after the M11 check.
                state += ", OpenLancer only until the M11 check";
                grid.SetNextNote(CutsceneBrowser.OpenLancerOnly);
            }

            grid.Text("State", edits.HasUnsavedCutsceneEdits(path) ? $"{state}, with unsaved edits" : state);
            grid.SetNextNote("The length of the script in seconds (ThnScript.cs:124). A drag of the end handle of the " +
                             "Timeline ruler changes it too.");
            grid.Row("Duration", () => Duration(root, path, workspace.Duration, readOnly));
            if (timeline != null)
            {
                grid.Number("Entities", edits.Cutscene(path)!.Entities.Count, "N0");
                grid.Number("Events", timeline.Events.Count, "N0");
            }

            if (readOnly != null)
            {
                grid.Text("Read-only", readOnly);
            }

            grid.EndSection();
        }

        // The errors that crash the game when it plays the cutscene (decision D137 of the M8b design). They block the
        // save and Play, so the page shows them above the other sections.
        var errors = edits.CutsceneErrors(path);
        if (errors.Count > 0 &&
            grid.BeginSection("Thn.Problems", EditorIcons.ExclamationTriangle, $"Problems ({errors.Count})"))
        {
            foreach (var error in errors.Take(100))
            {
                var text = error.Message[EditSession.CutsceneCrash.Length..].TrimStart();
                if (grid.Link("Error", text, $"{error.Message}\n\nThe save and Play stop until you correct this. Click to " +
                                             "select the element.") && error.Cutscene is { } place)
                {
                    workspace.Show(place);
                }
            }

            grid.EndSection();
        }

        if (timeline != null && grid.BeginSection("Thn.Timeline", EditorIcons.List, "Timeline"))
        {
            foreach (var lane in timeline.Lanes)
            {
                grid.SetNextNote(lane.Note);
                grid.Text(lane.Main ? "Main monitor" : "Monitor",
                    $"{lane.Name}: {lane.Cuts.Count} cuts{(lane.Applies ? "" : ", ignored by the engine")}");
            }

            var marked = timeline.MarkedCameras.Select(r => r.Name).ToList();
            grid.SetNextNote(TimelineModel.IgnoredMonitorMark);
            grid.Text("Ignored cameras", marked.Count == 0 ? "none" : string.Join(", ", marked));
            grid.Number("After the end", timeline.Events.Count(e => e.AfterEnd != null), "N0");
            grid.SetNextNote("The context of the preview, for example a set script or a fidget, can stop events.");
            grid.Number("Not run here", timeline.Events.Count(e => e.Ignored != null), "N0");
            grid.EndSection();
        }

        var uses = workspace.References().UsedBy(path);
        if (grid.BeginSection("Thn.UsedBy", EditorIcons.Link, $"Used by ({uses.Count})"))
        {
            if (uses.Count == 0)
            {
                grid.Text("INI lines", CutsceneBrowser.NoReferenceText);
            }

            foreach (var use in uses)
            {
                if (grid.Link(System.IO.Path.GetFileName(use.File), $"[{use.Section}] {use.Key}",
                        $"{use.File}\n[{use.Section}] {use.Key} = {use.Value}\nClick to show the section.") &&
                    workspace.SectionOf(use) is { } section)
                {
                    window.Selection = section;
                }
            }

            grid.EndSection();
        }

        if (workspace.Plan is { } plan && grid.BeginSection("Thn.Preview", EditorIcons.Video, "Preview"))
        {
            grid.Text("Context", workspace.Context?.Label);
            grid.Text("Set script", plan.SetScript);
            grid.Text("Scripts", string.Join(", ", plan.Scripts.Select(System.IO.Path.GetFileNameWithoutExtension)));
            grid.Text("Cutscene", plan.Cutscene);
            grid.Text("Room camera", plan.RoomCamera);
            grid.Text("Ship marker", plan.ShipMarker);
            grid.Text("Fidget", plan.Fidget);
            grid.EndSection();
        }
    }

    /// <summary>The field of the duration. The edit runs when the field ends.</summary>
    private void Duration(CutsceneRoot root, string path, double duration, string? readOnly)
    {
        var start = ThnFieldEdit.Number((float)duration);
        if (readOnly != null)
        {
            PropertyGrid.Field(start);
            return;
        }

        var text = start;
        ImGui.SetNextItemWidth(-1);
        var changed = ImGui.InputText("##duration", ref text, 64);
        grid.CommitField(new FieldKey(root, ThnScriptModel.DurationGlobal, FieldKey.AllLines), changed, start, text,
            (value, _) =>
            {
                var parse = ThnFieldEdit.Parse(ThnKeyKind.Number, value, ThornEvaluator.Instance);
                if (parse.Value is not ThnNumber number)
                {
                    return parse.Error;
                }

                window.Commands.Execute(() => ThnEdits.SetDuration(path, number.Value));
                return null;
            });
    }

    /// <summary>The page of a set of events: the count, a time shift and Delete.</summary>
    private void Events(CutsceneWorkspace workspace, ThnEventSet set)
    {
        var readOnly = window.Session!.NoEditReason ?? window.Session.Edits.CutsceneReadOnlyReason(set.Path);
        if (grid.BeginSection("Thn.Events", EditorIcons.Bolt, "Events"))
        {
            grid.Text("Selected", $"{set.Events.Length} events selected");
            grid.SetNextNote("Moves each selected event by this number of seconds, rounded to 1 ms. A negative shift " +
                             "moves the events back.");
            grid.Row("Time shift", () =>
            {
                ImGui.BeginDisabled(readOnly != null);
                var width = ImGui.GetContentRegionAvail().X - ToolbarItems.IconWidth * 3 - ImGui.GetStyle().ItemSpacing.X;
                ImGui.SetNextItemWidth(Math.Max(1, width));
                ImGui.InputText("##shift", ref shift, 32);
                ImGui.SameLine();
                if (ImGui.Button("Move", new System.Numerics.Vector2(ToolbarItems.IconWidth * 3, 0)) &&
                    float.TryParse(shift, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    window.Commands.Execute(() => ThnEdits.MoveEvents(set.Events, seconds, 0));
                }

                ImGui.EndDisabled();
            });
            grid.Row("", () =>
            {
                ImGui.BeginDisabled(readOnly != null);
                if (ImGui.Button($"{EditorIcons.TrashAlt}  Delete the events"))
                {
                    workspace.DeleteSelection();
                }

                ImGui.EndDisabled();
                if (readOnly != null)
                {
                    ImGui.SetItemTooltip(readOnly);
                }
            });
            foreach (var ev in set.Events.Select(e => workspace.Timeline?.Event(e.Id)).OfType<TimelineEvent>().Take(200))
            {
                if (grid.Link(Seconds(ev.Time), ev.Targets.Count > 0 ? $"{ev.Type} {ev.Targets[0]}" : ev.Type,
                        TimelineModel.Describe(ev) + "\n\nClick to select only this event."))
                {
                    window.Selection = ev.Ref;
                }
            }

            grid.EndSection();
        }
    }

    /// <summary>
    /// The page of an entity: its name and type, the rows of its type in the groups of its records, the keys that the
    /// schema does not know, and its events.
    /// </summary>
    private void Entity(CutsceneWorkspace workspace, ThnRef entity, ThnTable table)
    {
        var timeline = workspace.Timeline!;
        var type = ThnSchema.TypeOf(ThnElementKind.Entity, table, Symbols);
        var row = timeline.Groups.SelectMany(g => g.Rows).FirstOrDefault(r => entity.Equals(r.Entity));
        var lane = timeline.Lanes.FirstOrDefault(l => entity.Equals(l.Monitor));
        var rows = ThnSchema.RowsOf(ThnElementKind.Entity, type).ToList();
        if (grid.BeginSection("Thn.Entity", EditorIcons.CubeSolid, "Entity"))
        {
            Name(workspace, entity, table, row?.Mark);
            grid.Text("Type", table["type"] is { } typeValue ? ThnWriter.Value(typeValue) : null);
            if (lane != null)
            {
                grid.SetNextNote(lane.Note);
                grid.Text("Monitor", lane.Main ? "The main monitor" : "Ignored by the engine");
            }

            if (type == "SOUND")
            {
                SoundPreview(workspace, entity);
            }

            foreach (var key in rows.Where(r => IsTop(r.Path) && r.Kind != ThnKeyKind.Record &&
                                                r.Path.Text is not (ThnScriptModel.EntityNameKey or "type")))
            {
                Row(workspace, entity, ThnElementKind.Entity, table, key);
            }

            grid.EndSection();
        }

        Records(workspace, entity, ThnElementKind.Entity, table, rows, "", "Thn.Entity");
        OtherKeys(workspace, entity, ThnElementKind.Entity, type, table);

        var events = (row?.Events ?? []).Concat(lane?.Cuts.Select(c => c.Event) ?? []).ToList();
        if (grid.BeginSection("Thn.Entity.Events", EditorIcons.Bolt, $"Events ({events.Count})"))
        {
            foreach (var ev in events.Take(200))
            {
                if (grid.Link(Seconds(ev.Time), ev.SecondTarget is { } second ? $"{ev.Type} {second}" : ev.Type,
                        TimelineModel.Describe(ev) + "\n\nClick to select the event."))
                {
                    window.Selection = ev.Ref;
                }
            }

            grid.EndSection();
        }
    }

    /// <summary>The Preview row of a SOUND entity: Play plays its template once from its start, in 2D, with the voice
    /// rule of the preview, and Stop stops it (section 5.3 of the M8b design).</summary>
    private void SoundPreview(CutsceneWorkspace workspace, ThnRef entity)
    {
        grid.SetNextNote("Plays the sound of the template once from its start, in 2D. A voice line plays with the voice " +
                         "of its speaker, as in the preview. The Console tells when the game data has no such sound.");
        grid.Row("Preview", () =>
        {
            if (ImGui.Button($"{EditorIcons.PlayCircle}  Play") && !workspace.PlaySound(entity))
            {
                window.Log.Add(ConsoleSeverity.Warning, CutsceneSound.Source,
                    "This SOUND entity has no template_name, so it has no sound to play.");
            }

            ImGui.SameLine();
            if (ImGui.Button($"{EditorIcons.StopCircle}  Stop"))
            {
                workspace.StopSound();
            }
        });
    }

    /// <summary>The name row of an entity. A new name runs the rename when the field ends.</summary>
    private void Name(CutsceneWorkspace workspace, ThnRef entity, ThnTable table, string? mark)
    {
        var path = new ThnPath(ThnScriptModel.EntityNameKey);
        var field = workspace.Field(entity, path, ThnKeyKind.Text) with { Evidence = "ThnEntity.cs:42" };
        var note = mark ?? ThnSchema.Find(ThnElementKind.Entity, null, path)?.Note;
        grid.ThnRow("Name", field, note, () =>
        {
            var start = ThnScriptModel.NameOf(table) ?? "";
            if (field.ReadOnly != null)
            {
                PropertyGrid.Field(start);
                return;
            }

            var text = start;
            ImGui.SetNextItemWidth(-1);
            var changed = ImGui.InputText("##name", ref text, 256);
            grid.CommitField(field.Key, changed, start, text, (name, interactive) =>
            {
                if (name.Trim().Length == 0)
                {
                    return "The name cannot be empty.";
                }

                if (interactive)
                {
                    workspace.Rename(entity, name.Trim());
                }

                return null;
            });
            ImGui.SetItemTooltip("A new name renames the entity, its event targets and the speakers that name it in this " +
                                 "file. When other data uses the name, a dialog asks first.");
        });
    }

    /// <summary>
    /// The page of an event: its time, length, type and targets with the notes of the Timeline, then the rows of its
    /// props in the groups of their records, the curve, and the keys that the schema does not know.
    /// </summary>
    private void Event(CutsceneWorkspace workspace, ThnRef ev, ThnTable table, TimelineEvent info)
    {
        var timeline = workspace.Timeline!;
        var type = ThnSchema.TypeOf(ThnElementKind.Event, table, Symbols);
        var rows = ThnSchema.RowsOf(ThnElementKind.Event, type).ToList();
        if (grid.BeginSection("Thn.Event", EditorIcons.Bolt, "Event"))
        {
            // The time and the length change as a drag on the Timeline changes them: on the 1 ms grid and from 0.
            var time = workspace.Field(ev, new ThnPath("time"), ThnKeyKind.Number) with { Evidence = "ThnEvent.cs:45" };
            grid.ThnTextRow("Time", time, info.AfterEnd, Parser(ThnKeyKind.Number), NotSet,
                ThnFieldCommand.Time(ev, NumberOf(time)));
            grid.Text("Type", info.Type);
            if (rows.FirstOrDefault(r => r.Path.Text == "props.duration") is { } length && info.Kind != EventTypes.SetCamera)
            {
                var field = workspace.Field(ev, length.Path, length.Kind) with { Evidence = length.Evidence };
                grid.ThnTextRow(length.Label, field, length.Note, Parser(ThnKeyKind.Number), NotSet,
                    ThnFieldCommand.Length(ev, NumberOf(field)));
            }

            Targets(workspace, ev, table, info);
            var target = info.SecondTarget is { } name && timeline.RowOf(name) is { } at ? at.Group.Rows[at.Index].Entity : null;
            var reference = target != null && workspace.Element(target) is { } entity && ThnFlags.IsReference(entity.Table, Symbols);
            if (Note(info, timeline, reference) is { } note)
            {
                grid.Text("Note", note);
                if (info.Kind == EventTypes.StartSpatialPropAnim && target != null &&
                    grid.Link("Second target", info.SecondTarget!,
                        reference ? $"Select {info.SecondTarget}." : $"Select {info.SecondTarget}. The gizmo moves its start pose."))
                {
                    window.Selection = target;
                }
            }

            if (info.Ignored != null)
            {
                grid.Text("Not run here", info.Ignored);
            }

            for (var i = EventSlots; i < table.List.Length; i++)
            {
                grid.Text($"Item {i + 1}", ThnWriter.Value(table.List[i]));
            }

            grid.EndSection();
        }

        if (info.Kind == EventTypes.SetCamera)
        {
            OtherKeys(workspace, ev, ThnElementKind.Event, type, table);
            return;
        }

        if (grid.BeginSection("Thn.Event.props", EditorIcons.Table, "Props"))
        {
            foreach (var key in rows.Where(r => IsIn(r.Path, "props") && r.Kind is not (ThnKeyKind.Record or ThnKeyKind.Curve) &&
                                                r.Path.Text != "props.duration"))
            {
                Row(workspace, ev, ThnElementKind.Event, table, key);
            }

            grid.EndSection();
        }

        Records(workspace, ev, ThnElementKind.Event, table, rows, "props", "Thn.Event");
        if (ThnCurve.Path.ValueIn(ThnElementKind.Event, table) is ThnTable { IsRecord: true } curve)
        {
            var period = ThnCurve.PeriodPath.ValueIn(ThnElementKind.Event, table) is ThnNumber p ? p.Value : (float?)null;
            curves.Draw(ev, curve, period, info.Length,
                workspace.Field(ev, ThnCurve.Path, ThnKeyKind.Curve) with { Evidence = "ThnEvent.cs:49-53" });
        }

        OtherKeys(workspace, ev, ThnElementKind.Event, type, table);
    }

    /// <summary>The target rows of an event: a list of the entities that fit the role of each target, and a button
    /// that selects the entity.</summary>
    private void Targets(CutsceneWorkspace workspace, ThnRef ev, ThnTable table, TimelineEvent info)
    {
        var working = workspace.Working!;
        var role = ThnTemplates.Role(info.Type);
        var path = new ThnPath(TargetsSlot);
        var field = workspace.Field(ev, path, ThnKeyKind.Value) with { Evidence = "ThnEvent.cs:58-64" };
        for (var i = 0; i < info.Targets.Count; i++)
        {
            var index = i;
            var name = info.Targets[i];
            var entity = working.EntityNamed(name);
            var choices = working.Entities
                .Where(e => ReferenceEquals(working.EntityNamed(ThnScriptModel.NameOf(e.Table) ?? ""), e) &&
                            (role?.Fits(index + 1, ThnSchema.TypeOf(ThnElementKind.Entity, e.Table, Symbols)) ?? true))
                .Select(e => ThnScriptModel.NameOf(e.Table) ?? "").ToList();
            grid.ThnRow($"Target {i + 1}", field with { Line = i }, entity == null ? $"No entity of this script has the name {name}." : null, () =>
            {
                var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
                ImGui.SetNextItemWidth(Math.Max(1, ImGui.GetContentRegionAvail().X - ToolbarItems.IconWidth - spacing));
                ImGui.BeginDisabled(field.ReadOnly != null);
                EditorTheme.PushMenuColors();
                if (ImGui.BeginCombo("##target", name))
                {
                    foreach (var choice in choices)
                    {
                        var selected = choice.Equals(name, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(choice, selected) && !selected && table.List[2] is ThnTable targets)
                        {
                            window.Commands.Execute(() => ThnEdits.Set(ev, path, ThnFieldEdit.TargetsValue(targets, index, choice)));
                        }
                    }

                    ImGui.EndCombo();
                }

                EditorTheme.PopMenuColors();
                ImGui.EndDisabled();
                ImGui.SameLine(0, spacing);
                ImGui.BeginDisabled(entity == null);
                if (ImGui.Button($"{EditorIcons.UpRightFromSquare}##select", new System.Numerics.Vector2(ToolbarItems.IconWidth, ImGui.GetFrameHeight())))
                {
                    window.Selection = new ThnRef(ev.Path, ThnElementKind.Entity, entity!.Id);
                }

                ImGui.EndDisabled();
                ImGui.SetItemTooltip(entity == null ? "No entity of this script has this name." : $"Select the entity {name}.");
            });
        }
    }

    /// <summary>A section for each record of the element below <paramref name="prefix"/> that the schema lists, with
    /// the rows of the record. The keys of a motion path get the path keys editor.</summary>
    private void Records(CutsceneWorkspace workspace, ThnRef target, ThnElementKind kind, ThnTable table,
        IReadOnlyList<ThnKeyRow> rows, string prefix, string section)
    {
        foreach (var record in rows.Where(r => r.Kind == ThnKeyKind.Record && IsIn(r.Path, prefix)))
        {
            var children = rows.Where(r => IsIn(r.Path, record.Path.Text) && r.Kind != ThnKeyKind.Record).ToList();
            var present = record.Path.ValueIn(kind, table) != null;
            if (!present && !record.Always || children.Count == 0)
            {
                continue;
            }

            // The keys of a motion path have their own editor after the section of the other keys of the record.
            var data = children.FirstOrDefault(r => r.Kind == ThnKeyKind.PathData);
            if (grid.BeginSection($"{section}.{record.Path}", EditorIcons.Table, record.Label))
            {
                foreach (var child in children.Where(r => r != data))
                {
                    Row(workspace, target, kind, table, child);
                }

                grid.EndSection();
            }

            if (data != null)
            {
                pathKeys.Draw(target, workspace.Field(target, data.Path, data.Kind) with { Evidence = data.Evidence });
            }
        }
    }

    /// <summary>The keys of the element that the schema does not know, as Lua value text.</summary>
    private void OtherKeys(CutsceneWorkspace workspace, ThnRef target, ThnElementKind kind, string? type, ThnTable table)
    {
        var record = kind == ThnElementKind.Entity ? table
            : table.List.Length > PropsSlot ? table.List[PropsSlot] as ThnTable
            : null;
        if (record is not { IsRecord: true })
        {
            return;
        }

        var unknown = Leaves(record, kind == ThnElementKind.Entity ? "" : "props")
            .Where(p => ThnSchema.Find(kind, type, p) == null).ToList();
        if (unknown.Count == 0 || !grid.BeginSection($"Thn.{kind}.Other", EditorIcons.EllipsisV, "Other keys"))
        {
            return;
        }

        foreach (var path in unknown)
        {
            var field = workspace.Field(target, path, ThnKeyKind.Value);
            grid.ThnTextRow(path.Text, field, "The schema does not know this key. The text is a Lua value, and the " +
                                              "evaluator reads a new text.", Parser(ThnKeyKind.Value), NotSet);
        }

        grid.EndSection();
    }

    /// <summary>One row of a key of the schema. A row that is not <see cref="ThnKeyRow.Always"/> shows only when the
    /// element has the key.</summary>
    private void Row(CutsceneWorkspace workspace, ThnRef target, ThnElementKind kind, ThnTable table, ThnKeyRow row)
    {
        var path = ThnSchema.KeyIn(row, kind, table);
        var field = workspace.Field(target, path, row.Kind) with { Evidence = row.Evidence };
        if (!row.Always && field.Value == null)
        {
            return;
        }

        var label = path == row.Path ? row.Label : path.Text[(path.Text.LastIndexOf(ThnPath.Separator) + 1)..];
        var note = row.Note;
        if (kind == ThnElementKind.Entity && IsIn(row.Path, TransformRecord) && ThnFlags.IsReference(table, Symbols))
        {
            note = note == null ? ThnPose.ReferencePoseNote : $"{ThnPose.ReferencePoseNote}\n\n{note}";
        }

        // The animation of a START_MOTION event on a character gets the clip picker. A COMPOUND plays the scripts of its
        // model, so its row keeps the text field.
        if (kind == ThnElementKind.Event && path.Text == ThnAnimationPicker.AnimationPath &&
            workspace.Timeline?.Event(target.Id) is { } info &&
            ThnAnimationPicker.Applies(info.Kind, path.Text, workspace.Working, info.Targets))
        {
            animations.Draw(workspace, label, field, info, note, Parser(row.Kind));
            return;
        }

        switch (row.Kind)
        {
            case ThnKeyKind.Vector:
            case ThnKeyKind.Orient:
                grid.ThnComponentRow(label, field, note, PropertyGrid.Axes, Parser(row.Kind),
                    FieldDrag.KindOf(row.Kind, path.Text), row.Color);
                break;
            case ThnKeyKind.Quaternion:
                grid.ThnComponentRow(label, field, note, QuaternionLabels, Parser(row.Kind));
                break;
            case ThnKeyKind.Bool:
                grid.ThnRow(label, field, note, () => Bool(field));
                break;
            case ThnKeyKind.Symbol:
                grid.ThnRow(label, field, note, () => Symbol(field, row.Group));
                break;
            case ThnKeyKind.EntityFlags:
                EntityFlags(target, table, label, field, note);
                break;
            case ThnKeyKind.Flags:
                EventFlags(row, label, field, note);
                break;
            case ThnKeyKind.Record or ThnKeyKind.Curve or ThnKeyKind.PathData:
                break;
            default:
                grid.ThnTextRow(label, field, note, Parser(row.Kind), NotSet);
                break;
        }
    }

    /// <summary>A checkbox for <c>Y</c> or <c>N</c>. A stored number stays a number (0 or 1).</summary>
    private void Bool(ThnFieldState field)
    {
        var on = field.Value switch
        {
            ThnBool b => b.Value,
            ThnNumber n => n.Value != 0,
            _ => false,
        };
        ImGui.BeginDisabled(field.ReadOnly != null);
        if (ImGui.Checkbox("##value", ref on))
        {
            window.Commands.Execute(() => ThnEdits.Set(field.Target, field.Path, ThnFieldEdit.BoolValue(field.Value, on)));
        }

        ImGui.EndDisabled();
        if (field.Value == null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(NotSet);
        }
    }

    /// <summary>A list of the THN names of a group. A stored number stays a number.</summary>
    private void Symbol(ThnFieldState field, string? group)
    {
        var names = Symbols.Where(s => s.Value.Group == group).OrderBy(s => s.Value.Value).ToList();
        var current = field.Value switch
        {
            ThnSymbol s => s.Name,
            ThnNumber n => names.FirstOrDefault(s => s.Value.Value == (long)n.Value).Key ?? ThnWriter.Value(n),
            null => NotSet,
            _ => ThnWriter.Value(field.Value),
        };
        ImGui.SetNextItemWidth(-1);
        ImGui.BeginDisabled(field.ReadOnly != null);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##value", current))
        {
            foreach (var (name, _) in names)
            {
                var selected = name == current;
                if (ImGui.Selectable(name, selected) && !selected)
                {
                    window.Commands.Execute(() =>
                        ThnEdits.Set(field.Target, field.Path, ThnFieldEdit.SymbolValue(field.Value, name, Symbols)));
                }
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
        ImGui.EndDisabled();
    }

    /// <summary>The flags of an entity: a checkbox for each flag that the Inspector shows, with the write rules of
    /// <see cref="ThnEdits.SetFlags"/>.</summary>
    private void EntityFlags(ThnRef entity, ThnTable table, string label, ThnFieldState field, string? note)
    {
        var sound = ThnFlags.IsSound(table, Symbols);
        IReadOnlySet<string> on;
        try
        {
            on = ThnFlags.On(field.Value, sound, Symbols);
        }
        catch (InvalidOperationException e)
        {
            grid.ThnTextRow(label, field, ErrorText.Of(e), Parser(ThnKeyKind.Value), NotSet);
            return;
        }

        grid.ThnRow(label, field, note, () => Checkboxes(ThnFlags.Shown(sound), on, field.ReadOnly, chosen =>
            window.Commands.Execute(() => ThnEdits.SetFlags(entity, chosen))));
    }

    /// <summary>The bits of a flags key of an event, with the write rules of <see cref="ThnFlags.WriteBits"/>.</summary>
    private void EventFlags(ThnKeyRow row, string label, ThnFieldState field, string? note)
    {
        IReadOnlySet<string> on;
        try
        {
            on = ThnFlags.BitsOn(field.Value, row, Symbols);
        }
        catch (InvalidOperationException e)
        {
            grid.ThnTextRow(label, field, ErrorText.Of(e), Parser(ThnKeyKind.Value), NotSet);
            return;
        }

        grid.ThnRow(label, field, note, () => Checkboxes(row.Bits.Select(b => b.Name).ToList(), on, field.ReadOnly, chosen =>
            window.Commands.Execute(() => ThnFlags.WriteBits(field.Value, row, chosen, Symbols) is { } value
                ? ThnEdits.Set(field.Target, field.Path, value)
                : ThnEdits.Remove(field.Target, field.Path))));
    }

    /// <summary>A checkbox for each name, two in each line when they fit. A click gives the names that are on after
    /// it.</summary>
    private static void Checkboxes(IReadOnlyList<string> names, IReadOnlySet<string> on, string? readOnly,
        Action<IReadOnlySet<string>> change)
    {
        ImGui.BeginDisabled(readOnly != null);
        // Two columns when the widest checkbox fits in half the cell, else one column. Screen positions, because in a
        // table cell the offset of SameLine counts the column offset again.
        var column = ImGui.GetContentRegionAvail().X / 2;
        var widest = names.Max(n => ImGui.CalcTextSize(n).X) + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        var columns = widest <= column ? 2 : 1;
        var start = ImGui.GetCursorScreenPos().X;
        for (var i = 0; i < names.Count; i++)
        {
            if (columns == 2 && i % 2 == 1)
            {
                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new System.Numerics.Vector2(start + column, ImGui.GetCursorScreenPos().Y));
            }

            var value = on.Contains(names[i]);
            if (ImGui.Checkbox(names[i], ref value))
            {
                var chosen = new HashSet<string>(on, StringComparer.Ordinal);
                if (value)
                {
                    chosen.Add(names[i]);
                }
                else
                {
                    chosen.Remove(names[i]);
                }

                change(chosen);
            }
        }

        ImGui.EndDisabled();
    }

    /// <summary>The note of an event whose second target sets its end values (section 7.5), or null.</summary>
    /// <param name="reference">True when the second target is an entity with the REFERENCE flag, which the gizmo does
    /// not move.</param>
    private static string? Note(TimelineEvent ev, TimelineModel timeline, bool reference)
    {
        if (ev.SecondTarget is not { } target)
        {
            return null;
        }

        return ev.Kind switch
        {
            EventTypes.StartSpatialPropAnim =>
                $"The engine moves the entity to the pose of {target} in each frame, so spatialprops only choose which " +
                "parts move (StartSpatialPropAnimEvent.cs:127-139, :178). " +
                (reference
                    ? $"{target} is a REFERENCE entity. The engine uses the object with that name that another script " +
                      "makes (ThnScriptInstance.cs:70-71), so the gizmo does not move it."
                    : $"The gizmo moves the start pose of {target}."),
            EventTypes.StartCameraPropAnim when timeline.RowOf(target) is { } at &&
                                                at.Group.Rows[at.Index].Kind == ObjectKinds.Thn.Camera =>
                $"The engine takes the end values from the camera {target} in each frame, so cameraprops only choose " +
                "which values move (StartCameraPropAnimEvent.cs:65-71, :112-119).",
            _ => null,
        };
    }

    /// <summary>The paths of the values of a record and of the records in it, below a prefix.</summary>
    private static IEnumerable<ThnPath> Leaves(ThnTable record, string prefix)
    {
        foreach (var field in record.Record)
        {
            var path = prefix.Length == 0 ? field.Key : $"{prefix}{ThnPath.Separator}{field.Key}";
            if (field.Key.Length == 0 || field.Key.Contains(ThnPath.Separator))
            {
                continue;
            }

            if (field.Value is ThnTable { IsRecord: true, Record.IsEmpty: false } child)
            {
                foreach (var leaf in Leaves(child, path))
                {
                    yield return leaf;
                }
            }
            else
            {
                yield return new ThnPath(path);
            }
        }
    }

    private static Func<string, ThnFieldParse> Parser(ThnKeyKind kind) =>
        text => ThnFieldEdit.Parse(kind, text, ThornEvaluator.Instance);

    /// <summary>The number of a field, or 0 when the element has no number there.</summary>
    private static float NumberOf(ThnFieldState field) => field.Value is ThnNumber number ? number.Value : 0;

    /// <summary>True for a path of one key.</summary>
    private static bool IsTop(ThnPath path) => !path.Text.Contains(ThnPath.Separator);

    /// <summary>True for a path of a key in the record at <paramref name="prefix"/>, not deeper. The empty prefix is
    /// the element.</summary>
    private static bool IsIn(ThnPath path, string prefix) =>
        prefix.Length == 0
            ? IsTop(path)
            : path.Text.StartsWith(prefix + ThnPath.Separator, StringComparison.Ordinal) &&
              !path.Text[(prefix.Length + 1)..].Contains(ThnPath.Separator);

    private static string Seconds(float seconds) =>
        seconds.ToString("0.000", CultureInfo.InvariantCulture) + " s";
}
