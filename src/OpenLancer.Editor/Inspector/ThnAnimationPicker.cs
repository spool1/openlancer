using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using LibreLancer.Thn;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Animation row of a START_MOTION event whose first target is a character (a DEFORMABLE entity), as section 12.1
/// of the M8b design plans it. The field opens the list picker with the clips of the data list <c>animation</c> of the
/// session, grouped by the skeletons of the costume of the target. The list and the costume come from W, so an unsaved
/// INI edit shows. The side pane plays the hovered clip on a character of the scene: the clip preview. The clip preview
/// plays the clips of the game data of the last save. The <b>Preview</b> button plays the cutscene preview from the
/// time of the event.
/// </summary>
/// <remarks>A COMPOUND plays the scripts of its own model, not the clips of the <c>.anm</c> files (GameObject.cs:602-605),
/// so the row of another target keeps its text field (risk R5 of the M8b design). The save gate checks the clip with
/// the same rule (<see cref="ThnTemplateRules.PlaysClips"/>) and the same text
/// (<see cref="ThnTemplateRules.ClipProblem"/>). When the gate blocks the clip, the field has the color and the frame
/// of an error.</remarks>
internal sealed class ThnAnimationPicker(EditorWindow window, PropertyGrid grid) : IDisposable
{
    /// <summary>The tooltip of the field when the user can pick a clip.</summary>
    public const string ClickToPick = "Click to pick a clip.";

    /// <summary>The tooltip of the <b>Preview</b> button when it is active.</summary>
    public const string PlaysTheEvent = "Plays the cutscene preview from the time of this event.";

    /// <summary>The reason of a disabled <b>Preview</b> button when the event has no clip.</summary>
    public const string NoClip = ThnTemplateRules.NoClip;

    /// <summary>The reason of a disabled <b>Preview</b> button when the cutscene has no preview and gives no
    /// reason.</summary>
    public const string NoPreview = "The cutscene has no preview.";

    /// <summary>The text of the clip preview for a clip that the list has and the game data of the last save does not
    /// have, for example a clip of an unsaved <c>[Animations]</c> line.</summary>
    public const string NotSaved = "The clip preview plays the clips of the game data of the last save. That data does " +
                                   "not have this clip. Save the mod, then the clip preview can play it.";

    private const string NotSet = "Not set";

    private const string Note =
        "The clip that the character plays (StartMotionEvent.cs:34). The list has the clips of the files that the " +
        "[Animations] lines of bodyparts.ini name. Hover over a clip to play it in the clip preview.";

    // The size of the side pane in unscaled points.
    private const float PaneWidth = 420;
    private const float PaneHeight = 540;

    private static readonly ClipPart[] Parts = Enum.GetValues<ClipPart>();

    private readonly ClipPickerState state = new();
    private readonly ThnAnimationPreview preview = new(window);

    // The frame in which the side pane drew last, or -1 when the clip preview has no character.
    private int paneFrame = -1;

    /// <summary>True when the clip preview frees its character at the start of a frame: the side pane drew before, but
    /// not in the last frame. The list then closed, or the row of the animation did not draw.</summary>
    /// <param name="paneFrame">The frame in which the side pane drew last, or -1.</param>
    /// <param name="frame">The frame that starts.</param>
    internal static bool Releases(int paneFrame, int frame) => paneFrame >= 0 && paneFrame < frame - 1;

    /// <summary>Frees the clip preview when the side pane did not draw in the last frame. Call it once in each frame,
    /// before the page draws.</summary>
    public void Frame()
    {
        if (Releases(paneFrame, ImGui.GetFrameCount()))
        {
            preview.Release();
            paneFrame = -1;
        }
    }

    /// <summary>True when the row of a value gets the picker: the animation of a START_MOTION event whose first target
    /// is a DEFORMABLE entity of the script. The save gate uses the same rule (see
    /// <see cref="ThnTemplateRules.PlaysClips"/>).</summary>
    /// <param name="script">The open script, or null.</param>
    /// <param name="targets">The targets of the event.</param>
    public static bool Applies(EventTypes? eventType, string path, ThnScriptModel? script,
        IReadOnlyList<string> targets) =>
        eventType == EventTypes.StartMotion && path == ThnTemplateRules.AnimationPath && script != null &&
        ThnTemplateRules.PlaysClips(script.Entities.Select(e => e.Table), targets, CutsceneWorkspace.Symbols);

    /// <summary>The names of the characters (DEFORMABLE entities) of a script, each name once, in the order of the
    /// script. For a name that two entities have, the last entity counts, as in the engine.</summary>
    internal static List<string> Characters(ThnScriptModel? script)
    {
        var last = new Dictionary<string, ThnElement>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var entity in script?.Entities ?? [])
        {
            if (ThnScriptModel.NameOf(entity.Table) is { } name)
            {
                if (!last.ContainsKey(name))
                {
                    names.Add(name);
                }

                last[name] = entity;
            }
        }

        return names.Where(name => TypeOf(last[name]) == ThnTemplateRules.CharacterType).ToList();
    }

    /// <summary>The <c>template_name</c> of the entity with a name, or null.</summary>
    internal static string? Template(ThnScriptModel? script, string name) =>
        script?.EntityNamed(name)?.Table["template_name"] is ThnText { Value: var template } ? template : null;

    /// <summary>The tooltip of the field. It has the clip and its skeletons, or the problem of an empty or an unknown
    /// clip, as the save gate gives it. Then it has the click hint, or the reason of the read-only state.</summary>
    /// <param name="current">The value of the field.</param>
    /// <param name="readOnly">Why the row cannot change the value, or null.</param>
    internal static string FieldTooltip(AnimationClips clips, AnimationClip? clip, string current, string? readOnly)
    {
        var action = readOnly ?? ClickToPick;
        return clip != null ? $"{clip.Name}\n{clips.SkeletonText(clip)}\n\n{action}"
            : $"{ThnTemplateRules.ClipProblem(current)}\n\n{action}";
    }

    /// <summary>True when the save gate blocks the clip of an event: the problems of the cutscene have one at the
    /// <c>props.animation</c> of the event.</summary>
    /// <param name="problems">The problems of the save gate (see <c>EditSession.CutsceneErrors</c>).</param>
    /// <param name="script">The version of W that gave the problems.</param>
    internal static bool Blocked(IReadOnlyList<Diagnostic> problems, ThnScriptModel script, ThnRef target) =>
        problems.Any(p => p.Cutscene is { Kind: ThnElementKind.Event, Value: ThnTemplateRules.AnimationPath } place &&
                          place.ElementIn(script)?.Id == target.Id);

    /// <summary>The text, the color and the red frame of the closed field. An empty or an unknown clip has the color
    /// and the frame of an error when the save gate blocks it, else the warning color, for example for a retail clip
    /// that the lower cutscene has too.</summary>
    /// <param name="current">The value of the field.</param>
    /// <param name="known">True when the game has the clip.</param>
    /// <param name="blocked">True when the save gate blocks the clip (see <see cref="Blocked"/>).</param>
    internal static (string Text, Vector4 Color, bool Error) FieldLook(string current, bool known, bool blocked) =>
        (known ? current : current.Length == 0 ? NotSet : $"Missing ({ObjectKinds.Thn.Clip.Label}): {current}",
            blocked ? EditorTheme.Error : known ? EditorTheme.Text : EditorTheme.Warning, blocked);

    /// <summary>Why the <b>Preview</b> button is disabled, or null when it is active.</summary>
    /// <param name="current">The value of the field.</param>
    /// <param name="hasPreview">True when the cutscene has a preview.</param>
    /// <param name="previewProblem">Why the cutscene has no preview, or null.</param>
    internal static string? PreviewDisabled(string current, bool hasPreview, string? previewProblem) =>
        current.Length == 0 ? NoClip : hasPreview ? null : previewProblem ?? NoPreview;

    /// <summary>
    /// Draws the row of the animation. When the clip list is not available, the row keeps its text field, and its note
    /// gives the reason.
    /// </summary>
    /// <param name="note">The note of the schema row, or null.</param>
    /// <param name="parse">Gives the value of a text.</param>
    public void Draw(CutsceneWorkspace workspace, string label, ThnFieldState field, TimelineEvent info, string? note,
        Func<string, ThnFieldParse> parse)
    {
        string? reason = ClipPickerState.NotLoaded;
        if (window.Session is not { } session || state.Get(session.Edits.Lists.Animations(), out reason) is not { } clips)
        {
            grid.ThnTextRow(label, field, note == null ? reason : $"{reason}\n\n{note}", parse, NotSet);
            return;
        }

        state.Show(field.Target);
        var script = workspace.Working;
        var target = info.Targets.Count > 0 ? info.Targets[0] : "";
        // The game finds the clip by the name as it is, so the value keeps its spaces.
        var current = ThnFieldEdit.Format(field.Kind, field.Value);
        var clip = clips.Find(current);
        var blocked = script != null && workspace.Path is { } path &&
                      Blocked(session.Edits.CutsceneErrors(path), script, field.Target);
        grid.ThnRow(label, field, note ?? Note, () =>
        {
            var style = ImGui.GetStyle();
            var button = ImGui.CalcTextSize("Preview").X + 2 * style.FramePadding.X;
            var (text, color, error) = FieldLook(current, clip != null, blocked);
            ImGui.BeginDisabled(field.ReadOnly != null);
            var clicked = PropertyGrid.PickerField(text, clip != null ? clips.LabelOf(clip) : "", color, error,
                clip != null ? ObjectKinds.Thn.Clip : null, -(button + style.ItemInnerSpacing.X));
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            {
                ImGui.SetTooltip(FieldTooltip(clips, clip, current, field.ReadOnly));
            }

            grid.ThnListPopup(field, clicked,
                () => clips.Model(current, CostumeOf(session.Edits, script, target)?.Names, target),
                ObjectKinds.Thn.Clip, parse, new ListPickerPane(PaneWidth, PaneHeight, PartFilter,
                    row => clips.Shows(row, state.Part), row => Pane(workspace, session, clips, row, target)));
            ImGui.SameLine(0, style.ItemInnerSpacing.X);
            // The button only plays the preview, so it stays active in the read-only state.
            var disabled = PreviewDisabled(current, workspace.Transport != null, workspace.Problem);
            ImGui.BeginDisabled(disabled != null);
            if (ImGui.Button("Preview"))
            {
                workspace.Transport!.Seek(info.Time);
                workspace.Transport.Play();
            }

            ImGui.EndDisabled();
            ImGui.SetItemTooltip(disabled ?? PlaysTheEvent);
            if (clip == null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextWrapped(ThnTemplateRules.ClipProblem(current));
                ImGui.PopStyleColor();
            }
        });
    }

    /// <summary>Forgets the game data and the choices of the session, and frees the clip preview. Call it when the
    /// profile closes.</summary>
    public void Reset()
    {
        state.Reset();
        preview.Dispose();
        paneFrame = -1;
    }

    /// <summary>Frees the clip preview.</summary>
    public void Dispose() => preview.Dispose();

    private static string? TypeOf(ThnElement entity) =>
        ThnSchema.TypeOf(ThnElementKind.Entity, entity.Table, CutsceneWorkspace.Symbols);

    /// <summary>
    /// The body parts of the costume of the entity with a name: its <c>template_name</c> in the costume list of W, with
    /// the unsaved edits. So a new costume orders the clip list too. Null when the entity has no template, or when the
    /// list has no such costume.
    /// </summary>
    internal static CostumeParts? CostumeOf(EditSession edits, ThnScriptModel? script, string name) =>
        Template(script, name) is { } template &&
        edits.Lists.List(ThnTemplateRules.Costume.Lists)?.Find(template)?.Section is { } section &&
        edits.Get(section) is { } costume
            ? CostumeParts.Of(costume)
            : null;

    /// <summary>Closes the dropdown that is open now on Esc. ImGui closes a popup on Esc only with keyboard navigation,
    /// which the editor does not use. The list picker then keeps the clip.</summary>
    private static void CloseOnEscape()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    /// <summary>The body part filter below the search field. Returns true when it changed.</summary>
    private bool PartFilter()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Body part");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(MathF.Round(160 * ImGuiHelper.Scale));
        var changed = false;
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##part", state.Part.ToString()))
        {
            CloseOnEscape();
            foreach (var part in Parts)
            {
                if (ImGui.Selectable(part.ToString(), part == state.Part) && part != state.Part)
                {
                    state.Part = part;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
        ImGui.SetItemTooltip("Shows the clips of the files of the skeletons with this body part. Other shows the clips " +
                             "of the files that no skeleton of bodyparts.ini lists.");
        return changed;
    }

    /// <summary>The side pane: the files of the list that do not read, the hovered clip, its files and its skeletons,
    /// the character of the clip preview, the hints, and the clip preview.</summary>
    private void Pane(CutsceneWorkspace workspace, EditorSession session, AnimationClips clips, ListPickerRow? row,
        string target)
    {
        paneFrame = ImGui.GetFrameCount();
        if (clips.Warnings.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
            foreach (var warning in clips.Warnings)
            {
                ImGui.TextWrapped($"{AnimationClips.FileName(warning.Source)}: {warning.Message}");
            }

            ImGui.PopStyleColor();
        }

        if (row == null)
        {
            ImGui.TextDisabled("Hover over a clip to see it here.");
            return;
        }

        if (clips.Find(row.Value) is not { } clip)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
            ImGui.TextWrapped(ThnTemplateRules.ClipProblem(row.Value));
            ImGui.PopStyleColor();
            return;
        }

        if (row.Note.Length > 0)
        {
            ImGui.Text(row.Note);
        }

        ImGui.Text(clip.Name);
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        ImGui.TextWrapped(AnimationClips.FileText(clip));
        ImGui.TextWrapped(clips.SkeletonText(clip));
        ImGui.PopStyleColor();
        var script = workspace.Working;
        var characters = Characters(script);
        var on = AnimationClips.PreviewOn(characters, state.Chosen, target);
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Preview on");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        EditorTheme.PushMenuColors();
        if (ImGui.BeginCombo("##preview-on", on ?? "No character"))
        {
            CloseOnEscape();
            foreach (var name in characters)
            {
                var selected = name.Equals(on, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(name, selected) && !selected)
                {
                    state.Chosen = name;
                }
            }

            ImGui.EndCombo();
        }

        EditorTheme.PopMenuColors();
        ImGui.SetItemTooltip("The character of the scene that plays the clip preview. The event keeps its target.");
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextDim);
        ImGui.TextWrapped("Labels come from words in the clip names. Look at each clip in the clip preview before you " +
                          "use it.");
        ImGui.TextWrapped(ThnAnimationPreview.Controls);
        ImGui.PopStyleColor();
        if (on == null)
        {
            ImGui.TextWrapped("The script has no character for the clip preview.");
            return;
        }

        // The clip preview plays the scripts of the game data of the last save, which can lack a clip of W.
        if (session.GameData?.GetCharacterAnimations().Scripts.ContainsKey(clip.Name) == false)
        {
            ImGui.TextWrapped(NotSaved);
            return;
        }

        var sex = clips.SexOf(CostumeOf(session.Edits, script, on));
        preview.Draw(workspace.Visible?.CharacterOf(on), clip.Name, clips.IdleFor(clip, sex));
    }
}
