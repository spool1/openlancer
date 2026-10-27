using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Runtime.Scene;

/// <summary>How a transform reads its values.</summary>
public enum TransformMode
{
    /// <summary>A value is the new value.</summary>
    Absolute,

    /// <summary>A value adds to the value that the engine reads now.</summary>
    Relative,
}

/// <summary>The new values of a transform of one object, zone or light. A null part keeps its key.</summary>
/// <param name="Position">The position in meters.</param>
/// <param name="Rotation">The angles in degrees about X, Y and Z, as the <c>rotate</c> key holds them.</param>
/// <param name="Size">The values of the <c>size</c> line of a zone, in the order of the line: one for a sphere, two for a
/// cylinder (radius, height), and three for the other shapes. A ring has the order outer radius, inner radius,
/// height.</param>
public sealed record TransformChange(Vector3? Position = null, Vector3? Rotation = null, IReadOnlyList<float>? Size = null);

/// <summary>
/// The core of a transform of an object, a zone or a light as a plan of the command layer (section 4.0.8 of the M6b
/// design): the <c>pos</c>, <c>rotate</c> or <c>size</c> line of a section of W, made by the number rules of
/// <see cref="TransformValues"/> and <see cref="ZoneSizeAxes"/>. A drag of the gizmo makes one plan for each step, and
/// the transform command of the command layer makes one plan for each item. So both use one rule.
/// </summary>
/// <remarks>
/// <para>A plan reads the lines of W when it is made, so make it and run it at once. A line that equals the line of W
/// changes nothing, so a transform to the values of W gives a plan without a change.</para>
/// <para>A transform changes only the section that it names. Its children do not move, as with the gizmo, until the
/// move with children of milestone M3b.</para>
/// </remarks>
public static class TransformPlans
{
    /// <summary>The label of a transform of a section with one tool, for example <c>Move Li01_14_depot_water</c>. A drag
    /// of the gizmo has it too.</summary>
    public static string Label(GizmoTool tool, SectionRef section) =>
        $"{tool} {section.Id.Key ?? section.Id.ToString()}";

    /// <summary>
    /// The values of the line of the key of a tool after a transform to a value: <see cref="TransformValues.Line(IReadOnlyList{ValueBase}?, Vector3)"/>
    /// for a move and a rotate, and <see cref="ZoneSizeAxes.Line"/> for a scale.
    /// </summary>
    /// <param name="start">The values of the line now, or null when the section has no line of the key.</param>
    /// <param name="axes">The scale handles of a zone. A scale needs them.</param>
    /// <param name="value">The position in meters, the angles in degrees, or the scale of the gizmo (see
    /// <see cref="ZoneSizeAxes.GizmoScale"/>).</param>
    /// <exception cref="System.InvalidOperationException">A scale without handles, or a zone that the scale tool cannot
    /// change.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException">A new number is not finite.</exception>
    public static IReadOnlyList<ValueBase> Line(GizmoTool tool, IReadOnlyList<ValueBase>? start, ZoneSizeAxes? axes,
        Vector3 value) => tool == GizmoTool.Scale
        ? (axes ?? throw new System.InvalidOperationException("Only a zone has a size.")).Line(start, value)
        : TransformValues.Line(start, value);

    /// <summary>
    /// The plan of one step of a drag of the gizmo: the line of the key of the tool, made from an absolute value. The
    /// value is the position in meters for a move, the angles in degrees for a rotate, and the scale of the gizmo for a
    /// scale (see <see cref="ZoneSizeAxes.GizmoScale"/>).
    /// </summary>
    public static EditPlan Plan(EditSession session, SectionRef section, GizmoTool tool, Vector3 value)
    {
        var label = Label(tool, section);
        return Target(session, section, label, [tool], out var target) ??
               Lines(section, label, target!, [(tool, value)]);
    }

    /// <summary>
    /// The plan of a transform of one section: the lines of the parts of the change. A part that the section does not
    /// take refuses the plan, for example a rotation of a light or a size of an object. A relative position or rotation
    /// adds to the value that the engine reads now. A relative size adds each value to the value of the same place in
    /// the line.
    /// </summary>
    public static EditPlan Plan(EditSession session, SectionRef section, TransformChange change, TransformMode mode)
    {
        var tools = new List<GizmoTool>();
        if (change.Position != null)
            tools.Add(GizmoTool.Move);
        if (change.Rotation != null)
            tools.Add(GizmoTool.Rotate);
        if (change.Size != null)
            tools.Add(GizmoTool.Scale);
        var label = tools.Count == 1 ? Label(tools[0], section) : $"Transform {section.Id.Key ?? section.Id.ToString()}";
        if (Target(session, section, label, tools, out var target) is { } refused)
            return refused;
        var relative = mode == TransformMode.Relative;
        var values = new List<(GizmoTool, Vector3)>();
        if (change.Position is { } position)
            values.Add((GizmoTool.Move, relative ? target!.Position + position : position));
        if (change.Rotation is { } rotation)
            values.Add((GizmoTool.Rotate, relative ? target!.Degrees + rotation : rotation));
        if (change.Size is { } size)
        {
            var axes = target!.Axes!;
            if (size.Count != axes.LineCount)
            {
                var count = axes.LineCount == 1 ? "one value" : $"{axes.LineCount} values";
                return CommandPlan.Refused(label,
                    $"The size of a {target.Shape.ToString()!.ToLowerInvariant()} zone has {count}. Give {count}.",
                    section);
            }
            var line = relative ? axes.LineValues(axes.GizmoScale).Zip(size, (a, b) => a + b).ToList() : size;
            values.Add((GizmoTool.Scale, axes.ScaleOf(line)));
        }
        return Lines(section, label, target!, values);
    }

    /// <summary>The target of a section that takes each tool, or the plan that refuses.</summary>
    private static EditPlan? Target(EditSession session, SectionRef section, string label, IReadOnlyList<GizmoTool> tools,
        out GizmoTarget? target)
    {
        target = GizmoTarget.Of(session, section, null, out var reason);
        if (target == null)
            return CommandPlan.Refused(label, reason!, section);
        var t = target;
        return tools.Select(tool => t.Tools[tool]).FirstOrDefault(r => r != null) is { } disabled
            ? CommandPlan.Refused(label, disabled, section)
            : null;
    }

    /// <summary>The plan of the lines of some tools on a section, each from its absolute value. A line that equals the
    /// line of W is not in the plan. A value that is not finite refuses the plan.</summary>
    private static EditPlan Lines(SectionRef section, string label, GizmoTarget target,
        IReadOnlyList<(GizmoTool Tool, Vector3 Value)> values)
    {
        var commands = new List<EditCommand>();
        foreach (var (tool, value) in values)
        {
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                return CommandPlan.Refused(label, "A value of a transform must be a finite number.", section);
            var start = target.StartLine(tool);
            var line = Line(tool, start, target.Axes, value);
            if (!SceneLines.Same(line, start ?? []))
                commands.Add(EditCommand.SetLines(section, GizmoTarget.KeyOf(tool), [line]));
        }
        return new CommandPlan(EditCommand.Group(label, commands), commands.Count > 0 ? [section] : []);
    }
}
