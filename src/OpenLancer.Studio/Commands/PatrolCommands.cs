using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Runtime.Scene;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The commands of the patrol and trade routes (decision D731 of the MCP1 design). A route is a chain of cylinder zones
/// with the same path label, one zone for each leg. The agent tool <c>scene_place_patrol_route</c> calls them.
/// </summary>
/// <remarks>
/// Retail has 866 routes with 3,038 leg zones. Each zone has its center at the middle of its leg and its local Y axis
/// along the leg, from the start of the leg to its end. Its <c>size</c> is the radius and the length of the leg. Its
/// <c>path_label</c> is the name of the route and the number of the leg, from 1. The end of a leg is the start of the
/// next leg.
/// </remarks>
public static class PatrolCommands
{
    /// <summary>The shortest leg in meters. The direction of a shorter leg is not clear.</summary>
    public const float MinLeg = 1;

    /// <summary>
    /// Plans a new route in a system, as one entry of the edit history: one cylinder zone for each leg between two next
    /// points. Each zone gets the lines of the route template of the usage (see <see cref="SceneBuilders"/>), the path
    /// label with the number of its leg, and the encounter block of the request (see <see cref="EncounterCommands"/>).
    /// Each zone gets the next free nickname <c>&lt;system&gt;_path_&lt;name&gt;_&lt;n&gt;</c> (decision D235 of the M3
    /// design).
    /// </summary>
    /// <param name="system">The nickname of the system.</param>
    /// <param name="name">The path label of the route. A path label that the system has refuses with the detail
    /// <see cref="MissionErrors.NameTaken"/>.</param>
    /// <param name="usage">The usage of the route: a usage of a route template of the rule data.</param>
    /// <param name="points">The points of the route, two or more.</param>
    /// <param name="radius">The radius of each zone, or null for the radius of the template.</param>
    /// <exception cref="EditException">A value is not valid (<see cref="EditErrorKind.InvalidValue"/>).</exception>
    public static EditPlan Place(EditSession session, string system, string name, string usage,
        IReadOnlyList<Vector3> points, float? radius, IReadOnlyList<EncounterGroup> encounters,
        IReadOnlyList<FactionShare> factionWeights, IReadOnlyList<DensityRestriction> densityRestrictions)
    {
        var label = name.Trim();
        var plan = new BuildPlan($"Place route {label}");
        if (session.Lists.RuleData.Builders.Route(usage) is not { } template)
            return plan.Refuse("", $"The rule data has no route template for the usage '{usage}'.");
        if (ScenePlans.SystemFileProblem(session, system, out var path) is { } fileProblem)
            return plan.Refuse(fileProblem.Source, fileProblem.Message);
        var binary = session.IsBinaryFile(path);
        if (PatchFormatter.KeyError(label, binary) is { } labelError)
            throw Value("name", $"The name of a route follows the rules of a nickname: {labelError}");
        if (Route(session, path, label) is var (used, spelled))
        {
            return plan.Refuse(path, $"The system {system} has the route {spelled}: the zone {used.Id.Key} has it. Use " +
                                     "another name.", used, MissionErrors.NameTaken);
        }
        if (points.Count < 2)
            throw Value("points", "A route needs two or more points.");
        for (var i = 1; i < points.Count; i++)
        {
            if (Vector3.Distance(points[i - 1], points[i]) < MinLeg)
                throw Value($"points[{i}]", $"The points {i - 1} and {i} are less than 1 meter apart. Move one of them.");
        }
        var size = radius ?? template.Radius;
        if (!float.IsFinite(size) || size < 1)
            throw Value("radius", "The radius must be 1 or more.");

        var block = EncounterCommands.Lines(session, path, encounters, factionWeights, densityRestrictions,
            out var parameters);
        var stem = $"{system}_path_{label}".ToLowerInvariant();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zones = new List<(string Nickname, List<EntryView> Lines)>();
        for (var leg = 1; leg < points.Count; leg++)
        {
            var nickname = session.Nicknames.FirstFree(stem, 1, reserved);
            reserved.Add(nickname);
            var (center, rotation, length) = Leg(points[leg - 1], points[leg]);
            var lines = new List<(string Name, string Text)>
            {
                ("nickname", nickname),
                ("pos", ScenePlans.PositionText(center)),
                ("rotate", BuilderText.Angles(rotation)),
                ("shape", "CYLINDER"),
                ("size", $"{BuilderText.Number(size)}, {Math.Round(length).ToString(CultureInfo.InvariantCulture)}"),
            };
            foreach (var line in template.Lines)
            {
                lines.Add((line.Key, line.Key switch
                {
                    SceneBuilders.PathLabel => $"{label}, {leg}",
                    SceneBuilders.Usage => template.Usage,
                    _ => line.Value!,
                }));
            }
            lines.AddRange(block);
            if (BuilderText.Entries(session, path, lines, out var error) is not { } entries)
                throw Value(null, error!);
            zones.Add((nickname, entries));
        }

        // The encounters that the plan adds to the file count as present for the list checks.
        var planned = reserved.Concat(parameters.Select(p => p.Encounter)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<Diagnostic>();
        foreach (var (nickname, lines) in zones)
        {
            var errors = BuilderText.ListErrors(session, path, SceneSections.ZoneType, nickname, lines, planned, warnings);
            if (errors.Count > 0)
                throw BuilderText.ListError(null, errors);
        }
        var (paramAdds, fixes, commands) = EncounterCommands.Added(session, path, parameters);
        commands.AddRange(zones.Select(z => EditCommand.AddSection(path, SceneSections.ZoneType, z.Lines)));
        var adds = zones.Select(z => new SectionRef(path, new SectionIdentity(SceneSections.ZoneType, z.Nickname))).ToList();
        foreach (var add in adds)
            warnings.AddRange(ScenePlans.DanglingWarnings(session, add.Id.Key!, path));
        return plan.Runs(EditCommand.Group(plan.Label, commands, session.Snapshot()), [.. adds, .. paramAdds], [],
            adds[0], warnings, fixes);
    }

    /// <summary>
    /// The zone of a leg from one point to the next: its center, its rotation in degrees and its length. The rotation
    /// turns the local Y axis to the direction of the leg, in the engine form of the <c>rotate</c> key
    /// (<see cref="MathHelper.MatrixFromEulerDegrees(Vector3)"/>).
    /// </summary>
    internal static (Vector3 Center, Vector3 Rotation, float Length) Leg(Vector3 from, Vector3 to)
    {
        var along = to - from;
        var length = along.Length();
        var direction = along / length;
        var dot = Vector3.Dot(Vector3.UnitY, direction);
        Quaternion turn;
        if (dot >= 1 - 1e-6f)
            turn = Quaternion.Identity;
        else if (dot <= -1 + 1e-6f)
            turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        else
            turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction)), MathF.Acos(dot));
        var rotation = TransformValues.EulerNear(Matrix4x4.CreateFromQuaternion(turn), Vector3.Zero);
        return ((from + to) / 2, rotation, length);
    }

    /// <summary>The start and the end of the leg of a route zone: the ends of its local Y axis, as the engine turns the
    /// zone (<see cref="MathHelper.MatrixFromEulerDegrees(Vector3)"/>).</summary>
    /// <param name="rotation">The angles of the <c>rotate</c> line in degrees.</param>
    /// <param name="length">The length of the zone: the second value of its <c>size</c> line.</param>
    internal static (Vector3 Start, Vector3 End) Ends(Vector3 center, Vector3 rotation, float length)
    {
        var axis = Vector3.TransformNormal(Vector3.UnitY, MathHelper.MatrixFromEulerDegrees(rotation));
        return (center - axis * length / 2, center + axis * length / 2);
    }

    /// <summary>The first zone of a system file with a path label, with the label as the zone spells it, or
    /// null.</summary>
    private static (SectionRef Zone, string Label)? Route(EditSession session, string path, string label)
    {
        foreach (var section in session.Sections(path).Where(s => SceneSections.Is(s.Id.Type, SceneSections.ZoneType)))
        {
            if (session.Get(section)?.Lines(SceneBuilders.PathLabel).Select(l => l.Values.FirstOrDefault()?.ToString()?.Trim())
                    .FirstOrDefault(v => label.Equals(v, StringComparison.OrdinalIgnoreCase)) is { } spelled)
                return (section, spelled);
        }
        return null;
    }

    private static EditException Value(string? field, string message) =>
        new(new EditError(EditErrorKind.InvalidValue, MissionErrors.FieldValue, message) { Field = field });
}
