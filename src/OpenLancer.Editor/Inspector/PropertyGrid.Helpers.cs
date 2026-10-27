using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The field helpers of the grid (milestone M20): a drag on the label of a value, and a color picker on the swatch of a
/// color row. Both go through the field edits, so one drag and one opening of the picker are each one entry of the edit
/// history (see <see cref="LabelDrag"/> and <see cref="PopupSession"/>).
/// </summary>
/// <remarks>
/// <para>A drag on a label changes its value by steps for each point of mouse motion: Shift gives fine steps, and Ctrl
/// gives large steps. Esc or the right mouse button cancels the drag. The numbers follow the rules of a gizmo drag (see
/// <see cref="NumberLineGesture"/>).</para>
/// <para>A click on the swatch opens the picker. The picker keeps the form of the key (see
/// <see cref="ColorForms"/>). A click outside the picker keeps the color, and Esc puts back the color of the
/// opening.</para>
/// </remarks>
internal sealed partial class PropertyGrid
{
    // Sizes in unscaled points.
    private const float PickerWidth = 220;
    private const float SwatchAspect = 1.5f;

    /// <summary>How much a hovered or active drag label moves its color to white.</summary>
    private const float HotTint = 0.4f;

    private const int ColorChannels = 3;

    private const ImGuiColorEditFlags SwatchFlags =
        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.AlphaOpaque;

    private const ImGuiColorEditFlags PickerFlags = ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.DisplayRGB |
                                                   ImGuiColorEditFlags.InputRGB | ImGuiColorEditFlags.PickerHueBar |
                                                   ImGuiColorEditFlags.NoOptions;

    /// <summary>The swatch of a line that has no picker.</summary>
    private static readonly Vector3 NoColor = new(0.25f);

    private readonly LabelDrag labelDrag = new();
    private readonly PopupSession pickerSession = new();

    // The color of the picker, and the color at the opening of the picker. ImGui reads the four floats of the
    // reference from its X. The picker keeps its float color for the whole opening, so its hue does not move.
    private Vector4 pickerColor;
    private Vector4 pickerReference;

    /// <summary>The lighter color of a drag label that is hovered or active.</summary>
    private static Vector4 HotLabel(Vector4 color) =>
        new(Vector3.Lerp(new Vector3(color.X, color.Y, color.Z), Vector3.One, HotTint), color.W);

    /// <summary>The width of a color swatch.</summary>
    private static float SwatchWidth => MathF.Round(ImGui.GetFrameHeight() * SwatchAspect);

    /// <summary>The drag button of a label of a bound vector or zone size row.</summary>
    private bool BoundDragLabel(FieldBinding field, DragKind kind, IReadOnlyList<ComponentLabel> labels, int index,
        Vector2 size)
    {
        var count = labels.Count;
        float[]? numbers = null;
        var reason = field.Lines is [{ Values: var values }]
            ? FieldDrag.NumbersOf(values, count, out numbers)
            : "The key needs exactly one line.";
        return DragLabel(field.FieldKey, kind, labels[index].Text, reason == null ? numbers : null, reason, index,
            () => NumberLineGesture.Open(window.Commands, field, count), size);
    }

    /// <summary>
    /// Draws the drag button of a label in place of its prefix space, and runs the label drag. A disabled button tells
    /// why in its tooltip. Returns true when the button is hovered or active, so the label gets a lighter color.
    /// </summary>
    /// <param name="key">The key that the row edits.</param>
    /// <param name="name">The text of the label, for the tooltip.</param>
    /// <param name="numbers">The numbers of the values now, for the steps in the tooltip, or null with a
    /// reason.</param>
    /// <param name="reason">Why the label cannot drag, or null.</param>
    /// <param name="index">The value of the label.</param>
    /// <param name="begin">Starts the field edit of the drag. Its start text holds the numbers of the values.</param>
    private bool DragLabel(FieldKey key, DragKind kind, string name, float[]? numbers, string? reason, int index,
        Func<FieldGesture> begin, Vector2 size)
    {
        ImGui.BeginDisabled(reason != null);
        ImGui.InvisibleButton("##drag", size);
        ImGui.EndDisabled();
        var id = ImGui.GetItemID();
        var activated = ImGui.IsItemActivated();
        var active = ImGui.IsItemActive();
        var released = ImGui.IsItemDeactivated();
        var hot = reason == null && (active || ImGui.IsItemHovered());
        if (hot)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        }

        if (!active)
        {
            ImGui.SetItemTooltip(reason ?? DragTooltip(kind, name, numbers!, index));
        }

        if (reason != null || numbers == null)
        {
            return false;
        }

        var io = ImGui.GetIO();
        var input = new LabelDragInput(id, ImGui.GetFrameCount(), activated, active, released,
            ImGui.IsMousePosValid() ? ImGui.GetMousePos().X : float.NaN, ImGui.IsKeyPressed(ImGuiKey.Escape, false),
            ImGui.IsMouseClicked(ImGuiMouseButton.Right), io.KeyShift, io.KeyCtrl, ImGuiHelper.Scale,
            io.MouseDragThreshold / 2, fieldEdits.IsOpenFor(id));
        var (events, units) = labelDrag.Frame(input);
        var count = numbers.Length;
        RouteEvents(id, key, events, begin, start => FieldDrag.DragText(kind, start, count, index, units));
        return hot;
    }

    /// <summary>The tooltip of a drag label: how the drag works and the steps of the value.</summary>
    private static string DragTooltip(DragKind kind, string name, float[] numbers, int index)
    {
        var step = FieldDrag.BaseStep(kind, numbers, index);
        var unit = kind == DragKind.Rotation ? " degrees" : "";
        var text = $"Drag left or right to change {name}. Step: {StepText(step)}{unit} for each point. " +
                   $"Shift: {StepText(step * 0.1m)}. Ctrl: {StepText(step * 10)}. Esc or the right mouse button " +
                   "cancels.";
        return kind == DragKind.Size ? text + " A changed size is 1 or more." : text;
    }

    private static string StepText(decimal step) => step.ToString("0.##############", CultureInfo.InvariantCulture);

    /// <summary>A bound one-line color key: the swatch with the picker, and the text field of the line.</summary>
    private void ColorField(FieldBinding field, ColorForm form, string hint)
    {
        var (color, reason) = field.Lines is [{ Values: var values }]
            ? ColorForms.ToPicker(values, form)
            : (null, "The key needs exactly one line.");
        ColorSwatch(field.FieldKey, form, color, reason,
            () => NumberLineGesture.Open(window.Commands, field, ColorChannels));
        ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
        TextField(field, 1, hint);
    }

    /// <summary>
    /// A color swatch that opens the picker. A line without a picker gets a disabled swatch, and its tooltip tells
    /// why.
    /// </summary>
    /// <param name="color">The picker color of the line now, or null with a reason.</param>
    /// <param name="begin">Starts the field edit of the picker. Its start text holds the three numbers of the
    /// color.</param>
    private void ColorSwatch(FieldKey key, ColorForm form, Vector3? color, string? reason, Func<FieldGesture> begin)
    {
        ImGui.BeginDisabled(reason != null);
        var clicked = ImGui.ColorButton("##swatch", new Vector4(color ?? NoColor, 1), SwatchFlags,
            new Vector2(SwatchWidth, ImGui.GetFrameHeight()));
        ImGui.EndDisabled();
        ImGui.SetItemTooltip(reason ?? (form == ColorForm.Byte
            ? "Click to pick a color. The line keeps 0-255 integers."
            : "Click to pick a color. The line keeps 0-1 numbers with 3 decimals."));
        if (clicked && reason == null)
        {
            ImGui.OpenPopup("##picker");
        }

        Picker(key, form, color ?? NoColor, begin);
    }

    /// <summary>
    /// The popup of the picker of a row. One opening of the popup is one field edit. In the frame that the popup opens,
    /// the opening goes to the field edits first, and the picker starts from the line of the open edit.
    /// </summary>
    /// <param name="current">The picker color of the line now, for an opening whose edit did not start.</param>
    private void Picker(FieldKey key, ColorForm form, Vector3 current, Func<FieldGesture> begin)
    {
        var popup = ImGui.GetID("##picker");
        var mine = pickerSession.IsOpenFor(popup);
        var open = ImGui.BeginPopup("##picker");
        if (!open && !mine)
        {
            return;
        }

        var frame = ImGui.GetFrameCount();
        if (open && !mine)
        {
            RouteEvents(popup, key, pickerSession.Frame(popup, frame, true, false, false, fieldEdits.IsOpenFor(popup)),
                begin, PickedText);
            var start = fieldEdits.IsOpenFor(popup) && FieldDrag.TryParse(fieldEdits.Open!.StartText, ColorChannels,
                out var numbers)
                ? ColorForms.ColorOf(numbers, form)
                : current;
            pickerColor = new Vector4(start, 1);
            pickerReference = pickerColor;
            DrawPicker();
            ImGui.EndPopup();
            return;
        }

        var changed = open && DrawPicker();
        var escape = ImGui.IsKeyPressed(ImGuiKey.Escape, false);
        var events = pickerSession.Frame(popup, frame, open, escape, changed, fieldEdits.IsOpenFor(popup));
        if (open)
        {
            // Escape, or an edit that ended from outside. ImGui closes a popup only from inside it.
            if (!pickerSession.IsOpenFor(popup))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        RouteEvents(popup, key, events, begin, PickedText);
        return;

        // The numbers of the picker color, from the numbers at the start of the edit.
        string PickedText(string startText) => FieldDrag.TryParse(startText, ColorChannels, out var startNumbers)
            ? FieldDrag.Text(ColorForms.FromPicker(new Vector3(pickerColor.X, pickerColor.Y, pickerColor.Z),
                ColorForms.ColorOf(startNumbers, form), startNumbers, form))
            : startText;

        bool DrawPicker()
        {
            ImGui.SetNextItemWidth(MathF.Round(PickerWidth * ImGuiHelper.Scale));
            return ImGui.ColorPicker4("##color", ref pickerColor,
                PickerFlags | (form == ColorForm.Byte ? ImGuiColorEditFlags.Uint8 : ImGuiColorEditFlags.Float),
                ref pickerReference.X);
        }
    }
}
