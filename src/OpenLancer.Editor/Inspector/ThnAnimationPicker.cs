using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Data.GameData;
using LibreLancer.Data.Schema;
using LibreLancer.ImUI;
using LibreLancer.Thn;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Animation row of a START_MOTION event whose first target is a character (a DEFORMABLE entity), as section 12.1
/// of the M8b design plans it. The field opens the list picker with the clips of the game data, grouped by the
/// skeletons of the costume of the target. The side pane plays the hovered clip on a character of the scene: the clip
/// preview. The <b>Preview</b> button plays the cutscene preview from the time of the event.
/// </summary>
/// <remarks>A COMPOUND plays the scripts of its own model, not the clips of the <c>.anm</c> files (GameObject.cs:602-605),
/// so the row of another target keeps its text field (risk R5 of the M8b design).</remarks>
internal sealed class ThnAnimationPicker(EditorWindow window, PropertyGrid grid) : IDisposable
{
    /// <summary>The path of the animation of a START_MOTION event.</summary>
    public const string AnimationPath = "props.animation";

    /// <summary>The warning of a clip name that the game does not have.</summary>
    public const string UnknownClip = "The game has no animation with this name. The character does not move.";

    /// <summary>The tooltip of the field when the user can pick a clip.</summary>
    public const string ClickToPick = "Click to pick a clip.";

    /// <summary>The tooltip of the <b>Preview</b> button when it is active.</summary>
    public const string PlaysTheEvent = "Plays the cutscene preview from the time of this event.";

    /// <summary>The reason of a disabled <b>Preview</b> button when the event has no clip.</summary>
    public const string NoClip = "The event has no clip.";

    /// <summary>The reason of a disabled <b>Preview</b> button when the cutscene has no preview and gives no
    /// reason.</summary>
    public const string NoPreview = "The cutscene has no preview.";

    private const string Character = "DEFORMABLE";
    private const string NotSet = "Not set";

    private const string Note =
        "The clip that the character plays (StartMotionEvent.cs:34). The list has the clips of the files that the " +
        "[Animations] lines of bodyparts.ini name. Hover over a clip to play it in the clip preview.";

    // The size of the side pane in unscaled points.
    private const float PaneWidth = 420;
    private const float PaneHeight = 540;

    private static readonly ClipPart[] Parts = Enum.GetValues<ClipPart>();

    private readonly ClipPickerState<GameDataManager> state = new(AnimationClips.Read);
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
    /// is a DEFORMABLE entity of the script.</summary>
    /// <param name="script">The open script, or null.</param>
    /// <param name="targets">The targets of the event.</param>
    public static bool Applies(EventTypes? eventType, string path, ThnScriptModel? script,
        IReadOnlyList<string> targets) =>
        eventType == EventTypes.StartMotion && path == AnimationPath && TargetType(script, targets) == Character;

    /// <summary>The type of the entity of the first target of an event, for example "DEFORMABLE". Null when the event has
    /// no target, or when the target names no entity of the script. For a name that two entities have, the last entity
    /// counts, as in the engine.</summary>
    internal static string? TargetType(ThnScriptModel? script, IReadOnlyList<string> targets) =>
        targets.Count > 0 ? TypeOf(script, targets[0]) : null;

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

        return names.Where(name => TypeOf(last[name]) == Character).ToList();
    }

    /// <summary>The <c>template_name</c> of the entity with a name, or null.</summary>
    internal static string? Template(ThnScriptModel? script, string name) =>
        script?.EntityNamed(name)?.Table["template_name"] is ThnText { Value: var template } ? template : null;

    /// <summary>The tooltip of the field. It has the clip and its skeletons, or the warning of an unknown clip. Then it
    /// has the click hint, or the reason of the read-only state.</summary>
    /// <param name="current">The value of the field.</param>
    /// <param name="readOnly">Why the row cannot change the value, or null.</param>
    internal static string FieldTooltip(AnimationClips clips, AnimationClip? clip, string current, string? readOnly)
    {
        var action = readOnly ?? ClickToPick;
        return clip != null ? $"{clip.Name}\n{clips.SkeletonText(clip)}\n\n{action}"
            : current.Length > 0 ? $"{UnknownClip}\n\n{action}"
            : action;
    }

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
        var data = window.Session?.GameData;
        if (state.Get(data, out var reason) is not { } clips)
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
        grid.ThnRow(label, field, note ?? Note, () =>
        {
            var style = ImGui.GetStyle();
            var button = ImGui.CalcTextSize("Preview").X + 2 * style.FramePadding.X;
            var (text, dim, color) = current.Length == 0 ? (NotSet, "", EditorTheme.TextDim)
                : clip != null ? (current, clips.LabelOf(clip), EditorTheme.Text)
                : ($"Missing ({ObjectKinds.Thn.Clip.Label}): {current}", "", EditorTheme.Warning);
            ImGui.BeginDisabled(field.ReadOnly != null);
            var clicked = PropertyGrid.PickerField(text, dim, color, false, clip != null ? ObjectKinds.Thn.Clip : null,
                -(button + style.ItemInnerSpacing.X));
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            {
                ImGui.SetTooltip(FieldTooltip(clips, clip, current, field.ReadOnly));
            }

            grid.ThnListPopup(field, clicked,
                () => clips.Model(current, AnimationClips.Bodyparts(CostumeOf(script, data, target)), target),
                ObjectKinds.Thn.Clip, parse, new ListPickerPane(PaneWidth, PaneHeight, PartFilter,
                    row => clips.Shows(row, state.Part), row => Pane(workspace, data, clips, row, target)));
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
            if (current.Length > 0 && clip == null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
                ImGui.TextWrapped(UnknownClip);
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

    /// <summary>The type of the entity that the engine uses for a name, or null when no entity has the name.</summary>
    private static string? TypeOf(ThnScriptModel? script, string name) =>
        script?.EntityNamed(name) is { } entity ? TypeOf(entity) : null;

    private static string? TypeOf(ThnElement entity) =>
        ThnSchema.TypeOf(ThnElementKind.Entity, entity.Table, CutsceneWorkspace.Symbols);

    /// <summary>The costume of the entity with a name: its <c>template_name</c> in the costumes of the game data, or
    /// null.</summary>
    private static Costume? CostumeOf(ThnScriptModel? script, GameDataManager? data, string name) =>
        Template(script, name) is { } template ? data?.Items.Costumes.Get(template) : null;

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

    /// <summary>The side pane: the hovered clip, its files and its skeletons, the character of the clip preview, the
    /// hints, and the clip preview.</summary>
    private void Pane(CutsceneWorkspace workspace, GameDataManager? data, AnimationClips clips, ListPickerRow? row,
        string target)
    {
        paneFrame = ImGui.GetFrameCount();
        if (row == null)
        {
            ImGui.TextDisabled("Hover over a clip to see it here.");
            return;
        }

        if (clips.Find(row.Value) is not { } clip)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
            ImGui.TextWrapped($"{row.Value}: {UnknownClip}");
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

        var sex = CostumeOf(script, data, on)?.Body?.Sex ?? FLGender.unset;
        preview.Draw(workspace.Visible?.CharacterOf(on), clip.Name, clips.IdleFor(clip, sex));
    }
}
