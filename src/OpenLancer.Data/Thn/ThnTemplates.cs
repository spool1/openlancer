using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Thn;

/// <summary>The targets of an event type, from the roles of the retail events (section 7.3 of the M8 design).</summary>
/// <param name="Type">The event type, as a THN name.</param>
/// <param name="First">The entity types of the first target.</param>
/// <param name="Second">The entity types of the second target, or empty when the event has one target.</param>
/// <param name="SecondRequired">True when the event needs a second target, false when it can have one.</param>
public sealed record ThnEventRole(string Type, IReadOnlyList<string> First, IReadOnlyList<string> Second,
    bool SecondRequired)
{
    /// <summary>True when an entity type fits a target. Target 1 is the first.</summary>
    public bool Fits(int target, string? entityType) =>
        entityType != null && (target == 1 ? First : Second).Contains(entityType, StringComparer.Ordinal);

    /// <summary>True when the event can have a target at a place, from 1.</summary>
    public bool Has(int target) => target == 1 || target == 2 && Second.Count > 0;
}

/// <summary>
/// The events that the Add event menu of a Timeline row offers, and the event that each one adds. A new event has the
/// usual props of its type in retail, so the engine plays it at once, and the Inspector shows its rows. The entities
/// that an agent adds, and the script of a new cutscene, come from here too.
/// </summary>
public static class ThnTemplates
{
    /// <summary>The length of a new event with a length, in seconds.</summary>
    public const float NewLength = 1;

    /// <summary>The roles of the event types that retail uses, in the order of the menu. A MONITOR takes only
    /// SET_CAMERA, and the SCENE entity only START_FOG_PROP_ANIM.</summary>
    public static readonly ImmutableArray<ThnEventRole> Roles =
    [
        new("SET_CAMERA", ["MONITOR"], ["CAMERA"], true),
        new("START_MOTION", ["DEFORMABLE", "COMPOUND"], [], false),
        new("START_IK", ["DEFORMABLE"], Movable, true),
        new("ATTACH_ENTITY", Movable, Movable, true),
        new("START_SPATIAL_PROP_ANIM", Movable, Movable, false),
        new("START_PATH_ANIMATION", Movable, ["MOTION_PATH"], true),
        new("CONNECT_HARDPOINTS", ["COMPOUND"], ["DEFORMABLE", "COMPOUND"], true),
        new("START_SOUND", ["SOUND"], [], false),
        new("START_AUDIO_PROP_ANIM", ["SOUND"], [], false),
        new("START_PSYS", ["PSYS"], [], false),
        new("START_PSYS_PROP_ANIM", ["PSYS"], [], false),
        new("START_LIGHT_PROP_ANIM", ["LIGHT"], ["LIGHT"], false),
        new("START_CAMERA_PROP_ANIM", ["CAMERA"], ["CAMERA"], false),
        new("START_FLR_HEIGHT_ANIM", ["DEFORMABLE"], ["MARKER"], false),
        new("START_FOG_PROP_ANIM", ["SCENE"], [], false),
    ];

    /// <summary>The entity types with a place in the scene other than SCENE: "any entity" in the roles of section 7.3.
    /// In retail, no attachment, IK event, spatial animation or path animation names a MONITOR or the SCENE entity as
    /// a target.</summary>
    private static string[] Movable =>
        ["CAMERA", "COMPOUND", "DEFORMABLE", "LIGHT", "MARKER", "MOTION_PATH", "PSYS", "SOUND"];

    /// <summary>The role of an event type, or null for a type that retail does not use.</summary>
    public static ThnEventRole? Role(string? eventType) => Roles.FirstOrDefault(r => r.Type == eventType);

    /// <summary>The events whose first target can be an entity of a type, in the order of the menu.</summary>
    public static IEnumerable<ThnEventRole> RolesFor(string? entityType) => Roles.Where(r => r.Fits(1, entityType));

    /// <summary>
    /// A new event: <c>{ time, TYPE, { targets }, { props } }</c> with the usual props of the type. An animation of
    /// props (a START_*_PROP_ANIM) has an empty record of its props. The engine changes only the values that such an
    /// event has (for example StartSpatialPropAnimEvent.cs:67-101, StartCameraPropAnimEvent.cs:33-48), so the new
    /// animation changes nothing until an edit sets a value. A value from the entity would not be enough: the
    /// animation moves from the values at its time, which an earlier event can have changed.
    /// </summary>
    /// <param name="first">The first target.</param>
    /// <param name="second">The second target, or null.</param>
    /// <exception cref="ArgumentException">The type is not in <see cref="Roles"/>, the time is not a finite number or is
    /// below 0, or a required second target is missing.</exception>
    public static ThnTable NewEvent(string type, float time, string first, string? second)
    {
        var role = Role(type) ?? throw new ArgumentException($"{type} is not an event type of the Add event menu.", nameof(type));
        if (!float.IsFinite(time) || time < 0)
            throw new ArgumentException("The time of an event is a number from 0.", nameof(time));
        if (role.SecondRequired && second == null)
            throw new ArgumentException($"{type} needs a second target.", nameof(second));
        var targets = second == null ? ThnTable.FromList(new ThnText(first))
            : ThnTable.FromList(new ThnText(first), new ThnText(second));
        var head = new ThnValue[] { new ThnNumber(time), new ThnSymbol(type), targets };
        return Props(type) is { } props ? ThnTable.FromList([.. head, props]) : ThnTable.FromList(head);
    }

    /// <summary>The entity types that the editor adds, in the order of the types of the agent tool
    /// <c>cutscene_add_entity</c>. These are the entity types of the retail scripts.</summary>
    public static readonly ImmutableArray<string> EntityTypes =
        ["CAMERA", "MARKER", "DEFORMABLE", "COMPOUND", "LIGHT", "SOUND", "PSYS", "MOTION_PATH", "MONITOR", "SCENE"];

    /// <summary>
    /// True when the engine puts an entity of a type at its place in the scene (<c>spatialprops</c>). The engine makes
    /// no object for a MONITOR or for the SCENE entity (ThnScriptInstance.cs:206-213, :290-293), so their place has no
    /// effect.
    /// </summary>
    public static bool HasPlace(string type) => type is not ("MONITOR" or "SCENE");

    /// <summary>The reason why an entity of a type takes no position and no rotation (see
    /// <see cref="HasPlace"/>).</summary>
    public static string NoPlace(string type) =>
        $"The engine does not put a {type} at a place in the scene. So it takes no position and no rotation.";

    /// <summary>
    /// A new entity: <c>entity_name</c>, <c>type</c>, the <c>template_name</c> when the caller gives one, and the
    /// records that the engine needs for the type. The engine plays it at once, and the Inspector shows its rows.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Each type with a place gets <c>spatialprops</c> with the position and the rotation, as each such entity of
    /// the retail scripts has them.</item>
    /// <item>A CAMERA gets the median lens of the retail cameras of the bases, as the camera of
    /// <see cref="NewScript"/>.</item>
    /// <item>A LIGHT gets <c>lightprops</c> (see <see cref="NewLight"/>). A LIGHT without a type does not load
    /// (ThnScript.cs:452-453), and a light without a color gives no light.</item>
    /// <item>The SCENE gets the ambient color 0, 0, 0, as in <see cref="NewScript"/>.</item>
    /// <item>A COMPOUND gets no <c>userprops.category</c>, because the category depends on the template. The checks
    /// report a COMPOUND with a template and no category. A DEFORMABLE without a template and without
    /// <c>userprops.Actor</c> loads no costume, and the checks report it too.</item>
    /// <item>A MOTION_PATH gets no path. The keys of the path depend on the shot.</item>
    /// </list>
    /// </remarks>
    /// <param name="type">The THN name of the entity type: one of <see cref="EntityTypes"/>.</param>
    /// <param name="template">The template, for example the costume of a DEFORMABLE, or null or empty for none.</param>
    /// <param name="position">The position, or null for the origin.</param>
    /// <param name="rotation">The rotation, or null for no turn.</param>
    /// <exception cref="ArgumentException">The type is not in <see cref="EntityTypes"/>, the name is empty, the name or
    /// the template is not a THN text (see <see cref="ThnWriter.TextProblem"/>), or a type without a place gets a
    /// position or a rotation.</exception>
    public static ThnTable NewEntity(string type, string name, string? template, Vector3? position, Matrix4x4? rotation)
    {
        if (NewEntityProblem(type, name, template, position != null || rotation != null) is { } problem)
            throw new ArgumentException(problem.Message, nameof(type));
        var fields = new List<ThnField> { Field("entity_name", new ThnText(name)), Field("type", new ThnSymbol(type)) };
        if (!string.IsNullOrEmpty(template))
            fields.Add(Field("template_name", new ThnText(template)));
        if (type == "SCENE")
            fields.Add(Field("ambient", Vector(Vector3.Zero)));
        if (HasPlace(type))
        {
            fields.Add(Field("spatialprops", Record(Field("pos", Vector(position ?? Vector3.Zero)),
                Field("orient", ThnPose.Orient(rotation ?? Matrix4x4.Identity)))));
        }
        if (type == "CAMERA")
        {
            fields.Add(Field("cameraprops", Record(Field("fovh", Number(MedianFovH)),
                Field("hvaspect", Number(MedianHvAspect)), Field("nearplane", Number(MedianNearPlane)),
                Field("farplane", Number(MedianFarPlane)))));
        }
        if (type == "LIGHT")
            fields.Add(Field("lightprops", NewLight()));
        return Record([.. fields]);
    }

    /// <summary>
    /// Why <see cref="NewEntity"/> cannot make an entity, with the detail code of the error (see
    /// <see cref="MissionErrors"/>), or null. A type that is not in <see cref="EntityTypes"/> gives
    /// <c>FieldValue</c>. An empty name, or a name that is not a THN text, gives <c>NameInvalid</c>. A template that is
    /// not a THN text gives <c>FieldValue</c>. A place for a type without a place gives <c>NoPlace</c>.
    /// </summary>
    /// <param name="placed">True when the caller gives a position or a rotation.</param>
    internal static (string Message, string Detail)? NewEntityProblem(string type, string name, string? template,
        bool placed)
    {
        if (!EntityTypes.Contains(type, StringComparer.Ordinal))
        {
            return ($"{type} is not an entity type that the editor can add. The types are {string.Join(", ", EntityTypes)}.",
                MissionErrors.FieldValue);
        }
        if (string.IsNullOrWhiteSpace(name))
            return ("The name of an entity cannot be empty. Give a name.", MissionErrors.NameInvalid);
        if (ThnWriter.TextProblem(name) is { } badName)
            return (badName, MissionErrors.NameInvalid);
        if (template != null && ThnWriter.TextProblem(template) is { } badTemplate)
            return (badTemplate, MissionErrors.FieldValue);
        return placed && !HasPlace(type) ? (NoPlace(type), MissionErrors.NoPlace) : null;
    }

    /// <summary>
    /// The <c>lightprops</c> of a new LIGHT. Each key that the engine reads for a directional and a point light gets
    /// the value that most LIGHT entities of the retail scripts have: a directional light that is on, that points
    /// along its Z axis, with the range 2000 and the attenuation 1, 0, 0.000004. Most retail lights have the diffuse
    /// color black, and such a light gives no light. So a new light gets the color that most of the other retail
    /// lights have: white.
    /// </summary>
    private static ThnTable NewLight() => Record(
        Field("type", new ThnSymbol(NewLightType)),
        Field("on", new ThnBool(true)),
        Field("diffuse", Vector(Vector3.One)),
        Field("direction", Vector(Vector3.UnitZ)),
        Field("range", Number(NewLightRange)),
        Field("atten", Vector(NewLightAttenuation)));

    /// <summary>The light type of a new LIGHT.</summary>
    internal const string NewLightType = "L_DIRECT";

    /// <summary>The range of a new LIGHT.</summary>
    internal const float NewLightRange = 2000;

    /// <summary>The attenuation of a new LIGHT: constant, linear and squared.</summary>
    internal static readonly Vector3 NewLightAttenuation = new(1, 0, 0.000004f);

    /// <summary>The length of a new cutscene, in seconds.</summary>
    public const float NewScriptDuration = 10;

    /// <summary>The camera of a new cutscene.</summary>
    public const string NewCamera = "Camera_01";

    /// <summary>The monitor of a new cutscene.</summary>
    public const string NewMonitor = "Monitor";

    /// <summary>The lens of the camera of a new cutscene: the medians of the CAMERA entities of the retail scripts in
    /// <c>SCRIPTS\BASES</c>: 7,050 cameras in 635 files (retail test 3 of the M8b design).</summary>
    private const float MedianFovH = 25;
    private const float MedianHvAspect = 1.85f;
    private const float MedianNearPlane = 0.3f;
    private const float MedianFarPlane = 4000;

    /// <summary>
    /// The globals of a new cutscene (decision D134 of the M8b design): 10 s long, a SCENE, a MONITOR and a CAMERA, and a
    /// SET_CAMERA at 0 that cuts the monitor to the camera. The types are THN names, not numbers.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The SCENE has the ambient color 0, 0, 0. The engine skips a zero ambient, so a room keeps the ambient of
    /// its set script (ThnScriptInstance.cs:206-213).</item>
    /// <item>The MONITOR has no <c>Priority</c>. It is the only monitor, so the engine uses it
    /// (ThnScriptInstance.cs:309-312).</item>
    /// <item>The CAMERA is at the origin with no turn, and has the median lens of the retail cameras of the
    /// bases.</item>
    /// <item>The script uses none of the 9 names that only Librelancer defines (section 4.6 of the publishing
    /// plan).</item>
    /// </list>
    /// </remarks>
    public static ThnTable NewScript()
    {
        var scene = NewEntity("SCENE", "Scene", null, null, null);
        var monitor = NewEntity("MONITOR", NewMonitor, null, null, null);
        var camera = NewEntity("CAMERA", NewCamera, null, null, null);
        var cut = NewEvent("SET_CAMERA", 0, NewMonitor, NewCamera);
        return Record(Field(ThnScriptModel.DurationGlobal, Number(NewScriptDuration)),
            Field(ThnScriptModel.EntitiesGlobal, ThnTable.FromList(scene, monitor, camera)),
            Field(ThnScriptModel.EventsGlobal, ThnTable.FromList(cut)));
    }

    /// <summary>The props of a new event of a type, or null for SET_CAMERA, which has none.</summary>
    private static ThnTable? Props(string type)
    {
        var length = Field("duration", Number(NewLength));
        var root = Field("target_type", new ThnSymbol("ROOT"));
        var part = Field("target_part", new ThnText(""));
        var zero = Field("offset", Vector(Vector3.Zero));
        return type switch
        {
            "SET_CAMERA" => null,
            "START_MOTION" => Record(Field("animation", new ThnText("")), length, Field("time_scale", Number(1))),
            "START_IK" => Record(length, Field("end_effector", new ThnText("")), Field("count_to_root", Number(1)), part,
                root, Field("damping", Number(1)), Field("point_at", Number(1)), Field("move_to", Number(0)),
                Field("transition_duration", Number(0.1f))),
            // Without flags the engine uses POSITION and ORIENTATION (AttachEntityEvent.cs:60). A new event says so.
            "ATTACH_ENTITY" => Record(Field("duration", Number(0)), zero, Field("up", new ThnSymbol("Y_AXIS")),
                Field("front", new ThnSymbol("NEG_Z_AXIS")), part, root, Field("flags", Number(6))),
            "START_SPATIAL_PROP_ANIM" => Record(length, part, root, Field("spatialprops", Record())),
            "START_PATH_ANIMATION" => Record(length, Field("start_percent", Number(0)), Field("stop_percent", Number(1)),
                zero, Field("flags", Number(6))),
            "CONNECT_HARDPOINTS" => Record(Field("hardpoint", new ThnText("")), Field("parent_hardpoint", new ThnText(""))),
            "START_AUDIO_PROP_ANIM" => Record(length, Field("audioprops", Record())),
            "START_PSYS_PROP_ANIM" => Record(length, Field("psysprops", Record())),
            "START_LIGHT_PROP_ANIM" => Record(length, Field("lightprops", Record())),
            "START_CAMERA_PROP_ANIM" => Record(length, Field("cameraprops", Record())),
            "START_FLR_HEIGHT_ANIM" => Record(length, root, Field("floor_height", Number(0))),
            "START_FOG_PROP_ANIM" => Record(length, Field("fogprops", Record())),
            _ => Record(length),
        };
    }

    private static ThnNumber Number(float value) => new(value);

    private static ThnField Field(string key, ThnValue value) => new(key, value);

    private static ThnTable Record(params ThnField[] fields) => ThnTable.FromRecord(fields);

    private static ThnTable Vector(Vector3 v) => ThnPose.Vector(v);
}
