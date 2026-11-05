using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenLancer.Data.Editing;

namespace OpenLancer.Data.Thn;

/// <summary>The kind of the value of a key of an entity or an event.</summary>
public enum ThnKeyKind
{
    /// <summary>A record that holds other keys.</summary>
    Record,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A text.</summary>
    Text,

    /// <summary><c>Y</c> or <c>N</c>. The engine also reads a number: 0 is false (ThnTypes.cs:72-76).</summary>
    Bool,

    /// <summary>The list <c>{ x, y, z }</c>.</summary>
    Vector,

    /// <summary>The list <c>{ w, x, y, z }</c> (ThnTypes.cs:68-71).</summary>
    Quaternion,

    /// <summary>A 3x3 rotation matrix: three lists of three numbers, one for each row (ThnScript.cs:221-240).</summary>
    Orient,

    /// <summary>A THN name of <see cref="ThnKeyRow.Group"/>, or its number.</summary>
    Symbol,

    /// <summary>The <c>flags</c> of an entity, with the rules of <see cref="ThnEdits.SetFlags"/>.</summary>
    EntityFlags,

    /// <summary>Bits of an event: a number, or the THN name of one bit (see <see cref="ThnKeyRow.Bits"/>).</summary>
    Flags,

    /// <summary>The text of the keys of a motion path (see <see cref="ThnMotionPath"/>).</summary>
    PathData,

    /// <summary>A parameter curve: a record with <c>CLSID</c> and <c>points</c> (see <see cref="ThnCurve"/>).</summary>
    Curve,

    /// <summary>The list <c>{ degrees, AXIS }</c> of a turn about an axis.</summary>
    AxisRotation,

    /// <summary>Any value, as Lua text.</summary>
    Value,
}

/// <summary>
/// How the engine reads the value of a key, and so which values stop the script (see <see cref="ThnChecks"/>). An
/// entity reads its keys with casts (ThnScript.cs:253-487). An event reads its props with <c>ThnTypes.Convert</c>
/// (ThnEvent.cs:67-77), which also takes some texts.
/// </summary>
internal enum ThnRead
{
    /// <summary>Any value: the engine reads it with <c>ToString</c> or <c>TryGetVector3</c>
    /// (ThornTable.cs:412-433), or it does not read the key.</summary>
    Any,

    /// <summary>A cast <c>(float)</c>: a number.</summary>
    Number,

    /// <summary>A cast <c>(string)</c>, or a conversion to a text (ThnTypes.cs:59-77): a text.</summary>
    Text,

    /// <summary>A cast or a conversion to a table: a table.</summary>
    Table,

    /// <summary>A conversion to a number (ThnTypes.cs:48-58, ThnScript.cs:242-251): a number, or a text that holds
    /// a number.</summary>
    ToNumber,

    /// <summary>A conversion to Y or N (ThnTypes.cs:59, :72-76): Y, N, a number, or the text Y, N, y or n.</summary>
    ToBool,

    /// <summary>A conversion to a THN name of <see cref="ThnKeyRow.Group"/> (ThnTypes.cs:59, :72-77): a name of the
    /// group, a number, or a text that holds a name of the group.</summary>
    ToName,

    /// <summary>An axis (ThnTypes.cs:24-38): as <see cref="ToName"/>, or a list whose first three items are
    /// numbers.</summary>
    ToAxis,

    /// <summary>A vector (ThnTypes.cs:60-67, ThornTable.cs:435-445): a list whose first three items are
    /// numbers.</summary>
    ToVector,

    /// <summary>A quaternion (ThnTypes.cs:68-71): a list whose first four items are numbers.</summary>
    ToQuaternion,

    /// <summary>A rotation matrix (ThnScript.cs:221-240): a list whose first three items are lists whose first three
    /// items are numbers.</summary>
    Rotation,

    /// <summary>A <c>Matrix4x4</c> (ThnTypes.cs:44-47, :80-99): no value. The engine reads the rows from index 0, but a
    /// Lua list starts at 1.</summary>
    Matrix,

    /// <summary>The flags of an entity (ThnScript.cs:293-302): a number, or a name of <c>ThnObjectFlags</c>.</summary>
    Flags,

    /// <summary>A turn about an axis (StartSpatialPropAnimEvent.cs:82-96): a list whose first item is a number, and
    /// whose second item, when it has one, is an axis.</summary>
    AxisRotation,

    /// <summary>The text of a motion path (MotionPath.cs:54-103, see <see cref="ThnMotionPath.Problem"/>).</summary>
    Path,

    /// <summary>The points of a curve (ParameterCurve.cs:81-87): a list of lists whose first four items are
    /// numbers.</summary>
    CurvePoints,
}

/// <summary>A bit of a flags key, for a checkbox of the Inspector.</summary>
/// <param name="Name">The name of the bit. When the row has a <see cref="ThnKeyRow.Group"/>, it is the THN name of
/// the bit.</param>
public readonly record struct ThnFlagBit(string Name, int Bit);

/// <summary>
/// A key of an entity or an event: its kind, the label of its Inspector row, and the line of the engine that reads it.
/// </summary>
/// <param name="Element">Entities or events.</param>
/// <param name="Types">The entity or event types that the row applies to, as THN names, or null for all types.</param>
/// <param name="Path">The path of the key in the element. An event path starts with <c>props</c>.</param>
/// <param name="Label">The label of the Inspector row.</param>
/// <param name="Evidence">The engine file and the lines that read the key.</param>
public sealed record ThnKeyRow(ThnElementKind Element, IReadOnlyList<string>? Types, ThnPath Path, ThnKeyKind Kind,
    string Label, string Evidence)
{
    private readonly ThnRead? engineRead;

    /// <summary>The group of the THN names of a <see cref="ThnKeyKind.Symbol"/> key, or of the bits of a
    /// <see cref="ThnKeyKind.Flags"/> key that can be written as a name. Null when the key has no names.</summary>
    public string? Group { get; init; }

    /// <summary>The bits of a <see cref="ThnKeyKind.Flags"/> key, from low to high.</summary>
    public ImmutableArray<ThnFlagBit> Bits { get; init; } = [];

    /// <summary>The bits that the engine uses when the element has no <see cref="ThnKeyKind.Flags"/> key.</summary>
    public int DefaultBits { get; init; }

    /// <summary>False when the engine does not read the key. <see cref="Note"/> then tells why.</summary>
    public bool Read { get; init; } = true;

    /// <summary>A note for the Inspector row, or null.</summary>
    public string? Note { get; init; }

    /// <summary>The form of the channels of a <see cref="ThnKeyKind.Vector"/> key that is a color, or null when the
    /// key is not a color. The row of such a key has a color picker that writes the channels in this form.</summary>
    public ColorForm? Color { get; init; }

    /// <summary>True when the Inspector shows the row also for an element without the key, so that a value can be
    /// set. Else the row shows only when the element has the key.</summary>
    public bool Always { get; init; } = true;

    /// <summary>True when the last key of a <c>userprops</c> path compares with letter case. The engine reads the
    /// other <c>userprops</c> keys without letter case (ThnScript.cs:305-314).</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>How the engine reads the value. A key that the engine does not read (<see cref="Read"/> false) takes any
    /// value. By default, the engine reads the key as its kind: an entity with a cast, and an event with a
    /// conversion.</summary>
    internal ThnRead EngineRead
    {
        get => !Read ? ThnRead.Any : engineRead ?? DefaultRead();
        init => engineRead = value;
    }

    /// <summary>True when the engine stops the script without the key, when the record of the key is there.</summary>
    internal bool Required { get; init; }

    /// <summary>The engine reads the key only when the record of the key also has this key, or null.</summary>
    internal string? ReadWith { get; init; }

    /// <summary>The engine reads the key only when the record of the key does not have this key, or null.</summary>
    internal string? ReadWithout { get; init; }

    /// <summary>The text of the note and of the evidence, for a tooltip.</summary>
    public string Tooltip => Note == null ? $"Read by {Evidence}." : $"{Note} ({Evidence})";

    private ThnRead DefaultRead() => (Element, Kind) switch
    {
        (_, ThnKeyKind.Record or ThnKeyKind.Curve) => ThnRead.Table,
        (_, ThnKeyKind.Text) => ThnRead.Text,
        (_, ThnKeyKind.Bool) => ThnRead.ToBool,
        (_, ThnKeyKind.Symbol) => Group == ThnSchema.AxisGroup ? ThnRead.ToAxis : ThnRead.ToName,
        (ThnElementKind.Entity, ThnKeyKind.Number) => ThnRead.Number,
        (ThnElementKind.Entity, ThnKeyKind.Orient) => ThnRead.Rotation,
        (ThnElementKind.Entity, ThnKeyKind.EntityFlags) => ThnRead.Flags,
        (ThnElementKind.Entity, ThnKeyKind.PathData) => ThnRead.Path,
        (ThnElementKind.Event, ThnKeyKind.Number) => ThnRead.ToNumber,
        (ThnElementKind.Event, ThnKeyKind.Flags) => Group != null ? ThnRead.ToName : ThnRead.ToNumber,
        (ThnElementKind.Event, ThnKeyKind.Vector) => ThnRead.ToVector,
        (ThnElementKind.Event, ThnKeyKind.Quaternion) => ThnRead.ToQuaternion,
        (ThnElementKind.Event, ThnKeyKind.Orient) => ThnRead.Matrix,
        (ThnElementKind.Event, ThnKeyKind.AxisRotation) => ThnRead.AxisRotation,
        // An entity reads its vectors with TryGetVector3, which skips a value of another kind.
        _ => ThnRead.Any,
    };

    /// <summary>True when the row applies to an element of a type.</summary>
    public bool AppliesTo(string? type) => Types == null || type != null && Types.Contains(type, StringComparer.Ordinal);
}

/// <summary>
/// The keys of the entities and the events of a cutscene, from the retail corpus and the reads of the engine (section 9
/// of the M8 design). The Inspector draws an element with the rows of its type. Each row names the engine lines that
/// read the key, so a change of the engine shows which rows to check.
/// </summary>
/// <remarks>
/// <para>The engine reads the <c>userprops</c> keys without letter case (ThnScript.cs:305-314), except
/// <c>TextStart</c>. Retail writes <c>Actor</c> and <c>actor</c>, and <c>nofog</c> and <c>NoFog</c>. So a row of a
/// <c>userprops</c> key matches each spelling of the key. The engine does not read <c>No_Fog</c>.</para>
/// <para>START_CAMERA_PROP_ANIM reads its keys by place, not by name (StartCameraPropAnimEvent.cs:38-47): <c>aspect</c>
/// sets the near plane, <c>near</c> sets the far plane, and <c>far</c> sets the aspect. The labels say so.</para>
/// </remarks>
public static class ThnSchema
{
    /// <summary>The entity types that have a place in the scene. A MONITOR has no place.</summary>
    private static readonly string[] Placed =
        ["CAMERA", "COMPOUND", "DEFORMABLE", "LIGHT", "MARKER", "MOTION_PATH", "PSYS", "SCENE", "SOUND"];

    /// <summary>The entity types that have rows: the placed types and MONITOR.</summary>
    private static readonly string[] EntityTypes = [.. Placed, "MONITOR"];

    private const string UserPropsKey = "userprops";

    private const string Spatial = "StartSpatialPropAnimEvent.cs";
    private const string CameraAnim = "StartCameraPropAnimEvent.cs";

    /// <summary>The THN group of the axis names, for <c>up</c> and <c>front</c>.</summary>
    internal const string AxisGroup = "ThnAxis";

    private const string TargetGroup = "TargetTypes";

    private const string NotRead = "The engine does not read this key.";

    /// <summary>The bits of an attachment: the values of <c>AttachFlags</c> (AttachFlags.cs:10-17).</summary>
    private static readonly ImmutableArray<ThnFlagBit> AttachBits =
    [
        new("POSITION", 2), new("ORIENTATION", 4), new("LOOK_AT", 8), new("ENTITY_RELATIVE", 32),
        new("PARENT_CHILD", 64), new("ORIENTATION_RELATIVE", 128),
    ];

    /// <summary>The bits of an attachment without <c>flags</c>: POSITION and ORIENTATION (AttachEntityEvent.cs:60).</summary>
    private const int AttachDefault = 2 | 4;

    /// <summary>The bits of a sound event: the values of <c>SoundFlags</c> (SoundFlags.cs:9-12).</summary>
    private static readonly ImmutableArray<ThnFlagBit> SoundBits = [new("STREAM", 4), new("LOOP", 8)];

    /// <summary>The bits of <c>event_flags</c> of a motion (StartMotionEvent.cs:18-22). THN has no names for
    /// them.</summary>
    private static readonly ImmutableArray<ThnFlagBit> MotionBits = [new("Loop", 2), new("Overlay", 128)];

    /// <summary>The bits of <c>event_flags</c> of an IK event (StartIKEvent.cs:25-28).</summary>
    private static readonly ImmutableArray<ThnFlagBit> IkBits = [new("Overlay", 128)];

    /// <summary>All rows, in the order of the Inspector: the rows of each record follow the record.</summary>
    public static readonly ImmutableArray<ThnKeyRow> Rows = [.. EntityRows(), .. EventRows()];

    /// <summary>The row of a key of an element of a type, or null when the schema does not know the key.</summary>
    /// <param name="type">The type of the element as a THN name, for example <c>CAMERA</c>, or null when the type is
    /// not known.</param>
    public static ThnKeyRow? Find(ThnElementKind element, string? type, ThnPath path) =>
        Rows.FirstOrDefault(r => r.Element == element && r.AppliesTo(type) && Matches(r, path));

    /// <summary>The rows of the elements of a type, in the order of the Inspector.</summary>
    public static IEnumerable<ThnKeyRow> RowsOf(ThnElementKind element, string? type) =>
        Rows.Where(r => r.Element == element && r.AppliesTo(type));

    /// <summary>
    /// The path of the key of a row in an element: the path of the row, or for a <c>userprops</c> key the spelling
    /// that the element has. When the element has two spellings, the engine uses the last one (ThnScript.cs:311-314),
    /// so the path names that one.
    /// </summary>
    public static ThnPath KeyIn(ThnKeyRow row, ThnElementKind kind, ThnTable element)
    {
        if (row.CaseSensitive || !IsUserProp(row.Path) ||
            ThnElementPaths.Get(kind, element, Parent(row.Path)) is not ThnTable { IsRecord: true } props)
            return row.Path;
        var key = row.Path.Keys[^1];
        var last = props.Record.LastOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key;
        return last == null ? row.Path : new ThnPath(Parent(row.Path).Text + ThnPath.Separator + last);
    }

    /// <summary>True when a value fits the kind of a row, as the engine reads it.</summary>
    public static bool Fits(ThnKeyRow row, ThnValue value, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        row.Kind switch
        {
            ThnKeyKind.Record => value is ThnTable { IsRecord: true },
            ThnKeyKind.Number => value is ThnNumber,
            ThnKeyKind.Text or ThnKeyKind.PathData => value is ThnText,
            ThnKeyKind.Bool => value is ThnBool or ThnNumber,
            ThnKeyKind.Vector => value is ThnTable { Record.IsEmpty: true, List: [ThnNumber, ThnNumber, ThnNumber] },
            ThnKeyKind.Quaternion => value is ThnTable { Record.IsEmpty: true, List: [ThnNumber, ThnNumber, ThnNumber, ThnNumber] },
            ThnKeyKind.Orient => value is ThnTable { Record.IsEmpty: true, List.Length: 3 } rows &&
                                 rows.List.All(r => r is ThnTable { Record.IsEmpty: true, List: [ThnNumber, ThnNumber, ThnNumber] }),
            ThnKeyKind.Symbol => value is ThnNumber ||
                                 value is ThnSymbol s && symbols.TryGetValue(s.Name, out var info) && info.Group == row.Group,
            ThnKeyKind.EntityFlags => value is ThnNumber ||
                                      value is ThnSymbol f && symbols.TryGetValue(f.Name, out var flag) &&
                                      flag.Group == "ThnObjectFlags",
            ThnKeyKind.Flags => value is ThnNumber || row.Group != null && value is ThnSymbol b &&
                                symbols.TryGetValue(b.Name, out var bit) && bit.Group == row.Group,
            ThnKeyKind.Curve => value is ThnTable { IsRecord: true },
            ThnKeyKind.AxisRotation => value is ThnTable { Record.IsEmpty: true, List: [ThnNumber, ..] } axis &&
                                       axis.List.Length <= 2,
            _ => true,
        };

    /// <summary>The type of an element as a THN name: the name of a type that the file gives as a name or as a
    /// number, for example <c>CAMERA</c> for <c>3</c>. Null when the value is not a type of the group.</summary>
    /// <param name="group">The THN group: <c>EntityTypes</c> or <c>EventTypes</c>.</param>
    public static string? TypeName(ThnValue? type, string group, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        type switch
        {
            ThnSymbol symbol when symbols.TryGetValue(symbol.Name, out var info) && info.Group == group => symbol.Name,
            ThnNumber number => symbols.FirstOrDefault(s => s.Value.Group == group && s.Value.Value == (long)number.Value).Key,
            _ => null,
        };

    /// <summary>The type of an entity or an event as a THN name, or null.</summary>
    public static string? TypeOf(ThnElementKind kind, ThnTable element, IReadOnlyDictionary<string, ThnSymbolInfo> symbols) =>
        kind == ThnElementKind.Entity
            ? TypeName(element["type"], "EntityTypes", symbols)
            : element.List.Length > 1 ? TypeName(element.List[1], "EventTypes", symbols) : null;

    /// <summary>
    /// True when an event of a type has a length (<c>props.duration</c>) that the editor shows and changes. A
    /// SET_CAMERA cuts to its camera at its time and has no props, so it has no length. An event of a type that the
    /// editor does not know has a length.
    /// </summary>
    /// <param name="type">The THN name of the event type, for example <c>START_SOUND</c>, or null when the type is not
    /// known (see <see cref="TypeOf"/>).</param>
    public static bool HasLength(string? type) => type != "SET_CAMERA";

    private static bool IsUserProp(ThnPath path) => path.Keys is [UserPropsKey, _];

    private static ThnPath Parent(ThnPath path) => path.Parent ?? path;

    /// <summary>True when a path names the key of a row (see <see cref="ThnKeyRow.CaseSensitive"/>).</summary>
    private static bool Matches(ThnKeyRow row, ThnPath path)
    {
        if (row.Path == path)
            return true;
        return !row.CaseSensitive && IsUserProp(row.Path) && IsUserProp(path) &&
               string.Equals(row.Path.Text, path.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ThnKeyRow> EntityRows()
    {
        // The keys of each entity type (ThnScript.cs:253-487, ThnEntity.cs:40-44).
        yield return Entity(null, "entity_name", ThnKeyKind.Text, "ThnEntity.cs:42, ThnScript.cs:107", "Name") with
        {
            Required = true,
            Note = "The engine finds an entity by its name without letter case. A change of the name renames the " +
                   "references in this file.",
        };
        yield return Entity(null, "type", ThnKeyKind.Symbol, "ThnEntity.cs:43, ThnTypes.cs:41-78", "Type") with
        {
            Group = "EntityTypes", Required = true,
        };
        yield return Entity(null, "template_name", ThnKeyKind.Text, "ThnScript.cs:288-291", "Template");
        yield return Entity(null, "flags", ThnKeyKind.EntityFlags, "ThnScript.cs:203-218, :293-302", "Flags");
        yield return Entity(null, "lt_grp", ThnKeyKind.Number, "ThnScript.cs:268-271, Cutscene.cs:378", "Light group");
        yield return Entity(null, "srt_grp", ThnKeyKind.Number, "ThnScript.cs:258-261, Cutscene.cs:331", "Sort group");
        yield return Entity(null, "usr_flg", ThnKeyKind.Number, "ThnScript.cs:263-266", "User flag");
        yield return Entity(null, "template_id", ThnKeyKind.Number, "ThnScript.cs:253-487", "Template id") with
        {
            Read = false, Note = NotRead, Always = false,
        };

        yield return Entity(Placed, "spatialprops", ThnKeyKind.Record, "ThnScript.cs:371-384", "Transform");
        yield return Entity(Placed, "spatialprops.pos", ThnKeyKind.Vector, "ThnScript.cs:375-378", "Position");
        yield return Entity(Placed, "spatialprops.orient", ThnKeyKind.Orient, "ThnScript.cs:380-383, :221-240",
            "Rotation") with
        {
            Note = "Euler angles in degrees. The file holds a 3x3 matrix.",
        };

        string[] camera = ["CAMERA"];
        yield return Entity(camera, "cameraprops", ThnKeyKind.Record, "ThnScript.cs:386-409", "Camera");
        yield return Entity(camera, "cameraprops.fovh", ThnKeyKind.Number, "ThnScript.cs:390-393, ThnCamera.cs:68-75",
            "fovh") with
        {
            Note = "Half the horizontal angle in degrees for a 4:3 screen.",
        };
        yield return Entity(camera, "cameraprops.hvaspect", ThnKeyKind.Number, "ThnScript.cs:395-398", "hvaspect");
        yield return Entity(camera, "cameraprops.nearplane", ThnKeyKind.Number, "ThnScript.cs:400-403", "nearplane");
        yield return Entity(camera, "cameraprops.farplane", ThnKeyKind.Number, "ThnScript.cs:405-408", "farplane");

        string[] light = ["LIGHT"];
        yield return Entity(light, "lightprops", ThnKeyKind.Record, "ThnScript.cs:421-474", "Light");
        yield return Entity(light, "lightprops.type", ThnKeyKind.Symbol, "ThnScript.cs:438-453", "type") with
        {
            Group = "LightTypes", Required = true,
            Note = "A LIGHT without a type does not load (ThnScript.cs:452-453).",
        };
        yield return Entity(light, "lightprops.on", ThnKeyKind.Bool, "ThnScript.cs:426-430", "on");
        // The engine applies the light colors of a cutscene as they are, so their channels go from 0 to 1.
        yield return Entity(light, "lightprops.diffuse", ThnKeyKind.Vector, "ThnScript.cs:455-456", "diffuse") with
        {
            Color = ColorForm.Unit,
        };
        yield return Entity(light, "lightprops.ambient", ThnKeyKind.Vector, "ThnScript.cs:457-458", "ambient") with
        {
            Color = ColorForm.Unit,
        };
        yield return Entity(light, "lightprops.specular", ThnKeyKind.Vector, "ThnScript.cs:421-474", "specular") with
        {
            Read = false, Note = NotRead,
        };
        yield return Entity(light, "lightprops.color", ThnKeyKind.Vector, "ThnScript.cs:421-474", "color") with
        {
            Read = false, Note = NotRead,
        };
        yield return Entity(light, "lightprops.range", ThnKeyKind.Number, "ThnScript.cs:461-462", "range");
        yield return Entity(light, "lightprops.atten", ThnKeyKind.Vector, "ThnScript.cs:468-471", "atten");
        yield return Entity(light, "lightprops.theta", ThnKeyKind.Number, "ThnScript.cs:463-464", "theta");
        yield return Entity(light, "lightprops.cutoff", ThnKeyKind.Number, "ThnScript.cs:465-466", "cutoff");
        yield return Entity(light, "lightprops.direction", ThnKeyKind.Vector, "ThnScript.cs:459-460", "direction");

        string[] sound = ["SOUND"];
        yield return Entity(sound, "audioprops", ThnKeyKind.Record, "ThnScript.cs:357-369", "Sound");
        foreach (var (key, line) in new[]
                 {
                     ("ain", 362), ("aout", 367), ("atout", 364), ("attenuation", 368), ("dmax", 363), ("dmin", 366),
                     ("pan", 365), ("rmix", 361),
                 })
            yield return Entity(sound, $"audioprops.{key}", ThnKeyKind.Number, $"ThnScript.cs:{line}", key);

        string[] character = ["DEFORMABLE"];
        yield return Entity(character, "compoundprops", ThnKeyKind.Record, "ThnScript.cs:411-418", "Character");
        yield return Entity(character, "compoundprops.floor_height", ThnKeyKind.Number, "ThnScript.cs:415-418",
            "floor_height");

        string[] path = ["MOTION_PATH"];
        yield return Entity(path, "pathprops", ThnKeyKind.Record, "ThnScript.cs:476-483", "Path");
        yield return Entity(path, "pathprops.path_data", ThnKeyKind.PathData, "ThnScript.cs:480-483, MotionPath.cs:54-55",
            "Keys");
        yield return Entity(path, "pathprops.path_type", ThnKeyKind.Text, "ThnScript.cs:476-483", "path_type") with
        {
            Read = false, Note = NotRead,
        };

        string[] effect = ["PSYS"];
        // The engine does not read psysprops on an entity, so any value loads.
        yield return Entity(effect, "psysprops", ThnKeyKind.Record, "ThnScript.cs:253-487", "Effect") with
        {
            EngineRead = ThnRead.Any,
        };
        yield return Entity(effect, "psysprops.sparam", ThnKeyKind.Number, "ThnScript.cs:253-487", "sparam") with
        {
            Read = false,
            Note = "The engine does not read sparam on the entity. A START_PSYS_PROP_ANIM sets it " +
                   "(StartPSysPropAnimEvent.cs:23-28).",
        };

        string[] scene = ["SCENE"];
        // The engine divides the ambient color of a SCENE by 255 (Cutscene.cs:84).
        yield return Entity(scene, "ambient", ThnKeyKind.Vector, "ThnScript.cs:273-276, ThnScriptInstance.cs:211-213",
            "ambient") with { Color = ColorForm.Byte };
        yield return Entity(scene, "up", ThnKeyKind.Symbol, "ThnScript.cs:278-281", "up") with { Group = AxisGroup };
        yield return Entity(scene, "front", ThnKeyKind.Symbol, "ThnScript.cs:283-286", "front") with { Group = AxisGroup };
        foreach (var (key, kind) in new[]
                 {
                     ("fogmode", ThnKeyKind.Symbol), ("fogon", ThnKeyKind.Bool), ("fogcolor", ThnKeyKind.Vector),
                     ("fogdensity", ThnKeyKind.Number), ("fogstart", ThnKeyKind.Number), ("fogend", ThnKeyKind.Number),
                     ("fogtable", ThnKeyKind.Bool),
                 })
        {
            yield return Entity(scene, key, kind, "ThnScript.cs:253-487", key) with
            {
                Group = kind == ThnKeyKind.Symbol ? "FogModes" : null,
                // The engine does not read it here. A START_FOG_PROP_ANIM divides its fogcolor by 255.
                Color = key == "fogcolor" ? ColorForm.Byte : null,
                Read = false,
                Note = "The engine does not read the fog of a SCENE entity. A START_FOG_PROP_ANIM sets the fog " +
                       "(StartFogPropAnimEvent.cs:42-55).",
                Always = false,
            };
        }

        // The user props (ThnScript.cs:305-355). The engine reads them without letter case, except TextStart.
        yield return Entity(null, UserPropsKey, ThnKeyKind.Record, "ThnScript.cs:305-355", "User props") with
        {
            Always = false,
        };
        string[] actors = ["DEFORMABLE"];
        yield return Entity(["DEFORMABLE", "COMPOUND", "SOUND"], "userprops.category", ThnKeyKind.Text,
            "ThnScript.cs:316-319, ThnScriptInstance.cs:120-155", "category");
        yield return Entity(actors, "userprops.Actor", ThnKeyKind.Text, "ThnScript.cs:326-329, ThnScriptInstance.cs:255-279",
            "Actor") with
        {
            EngineRead = ThnRead.Any,
            Note = "The RTC character or the room role that plays this entity. It names no entity of this file.",
        };
        yield return Entity(["DEFORMABLE", "SOUND"], "userprops.speaker", ThnKeyKind.Text,
            "ThnScript.cs:331-334, ThnSound.cs:81-99", "speaker") with
        {
            EngineRead = ThnRead.Any,
            Note = "The entity whose voice speaks the line.",
        };
        // A key that some types always show has a second row for the other types, which shows only when the entity
        // has the key. Each type has one row for a key, so the Inspector draws the key once.
        string[] priority = ["MONITOR", "DEFORMABLE", "COMPOUND"];
        yield return Entity(priority, "userprops.Priority", ThnKeyKind.Text,
            "ThnScript.cs:346-349, ThnScriptInstance.cs:309-312", "Priority") with
        {
            EngineRead = ThnRead.Any,
            Note = "The engine sorts the monitors by Priority and uses only the first one.",
        };
        yield return Entity(Except(priority), "userprops.Priority", ThnKeyKind.Text, "ThnScript.cs:346-349", "Priority") with
        {
            EngineRead = ThnRead.Any, Always = false,
        };
        string[] nofog = ["COMPOUND"];
        yield return Entity(nofog, "userprops.nofog", ThnKeyKind.Text, "ThnScript.cs:321-324, ThnScriptInstance.cs:87",
            "nofog") with
        {
            EngineRead = ThnRead.ToBool,
        };
        yield return Entity(Except(nofog), "userprops.nofog", ThnKeyKind.Text, "ThnScript.cs:321-324", "nofog") with
        {
            EngineRead = ThnRead.ToBool, Always = false,
        };
        yield return Entity(null, "userprops.No_Fog", ThnKeyKind.Text, "ThnScript.cs:321-324", "No_Fog") with
        {
            Read = false, Always = false,
            Note = "The engine reads nofog without letter case, so it does not read No_Fog.",
        };
        foreach (var key in new[] { "loadout", "running_lights" })
        {
            yield return Entity(["COMPOUND"], $"userprops.{key}", ThnKeyKind.Text, "ThnScript.cs:305-355", key) with
            {
                Read = false, Note = NotRead,
            };
        }

        yield return Entity(["MARKER"], "userprops.main_object", ThnKeyKind.Text,
            "ThnScript.cs:351-354, ThnScriptInstance.cs:237-243", "main_object") with
        {
            EngineRead = ThnRead.Any, Always = false,
            Note = "In space, the marker takes the object of the mission (main_object) when the context has one.",
        };
        yield return Entity(scene, "userprops.TextString", ThnKeyKind.Text, "ThnScript.cs:336-340", "TextString") with
        {
            EngineRead = ThnRead.ToNumber,
        };
        yield return Entity(scene, "userprops.TextStart", ThnKeyKind.Text, "ThnScript.cs:342-343", "TextStart") with
        {
            EngineRead = ThnRead.ToNumber, ReadWith = "TextString", CaseSensitive = true,
            Note = "The engine reads TextStart with this letter case.",
        };
    }

    private static IEnumerable<ThnKeyRow> EventRows()
    {
        // The props of each event (ThnEvent.cs:43-65). SET_CAMERA has no props.
        yield return Event(null, "props.duration", ThnKeyKind.Number, "ThnEvent.cs:55", "Length");
        yield return Event(null, "props.param_curve", ThnKeyKind.Curve, "ThnEvent.cs:49-53, ParameterCurve.cs:42-88",
            "Curve") with
        {
            Always = false,
            Note = "The curve gives the progress of the event over its length.",
        };
        yield return Event(null, "props.param_curve.CLSID", ThnKeyKind.Text, "ParameterCurve.cs:44-80", "CLSID") with
        {
            Required = true, Always = false,
        };
        yield return Event(null, "props.param_curve.points", ThnKeyKind.Value, "ParameterCurve.cs:81-87", "points") with
        {
            EngineRead = ThnRead.CurvePoints, Required = true, Always = false,
        };
        yield return Event(null, "props.pcurve_period", ThnKeyKind.Number, "ThnEvent.cs:52, ParameterCurve.cs:106-110",
            "pcurve_period") with
        {
            ReadWith = "param_curve", Always = false,
            Note = "The period of the curve in milliseconds. Below 0, the curve spans the length of the event.",
        };

        const string motion = "StartMotionEvent.cs";
        string[] start = ["START_MOTION"];
        yield return Event(start, "props.animation", ThnKeyKind.Text, $"{motion}:34", "animation");
        yield return Event(start, "props.start_time", ThnKeyKind.Number, $"{motion}:35", "start_time");
        yield return Event(start, "props.time_scale", ThnKeyKind.Number, $"{motion}:36", "time_scale");
        yield return Event(start, "props.trans_time", ThnKeyKind.Number, $"{motion}:38", "trans_time");
        yield return Event(start, "props.event_flags", ThnKeyKind.Flags, $"{motion}:18-22, :37", "event_flags") with
        {
            Bits = MotionBits,
        };
        foreach (var key in new[] { "weight", "trans_scale", "heading" })
            yield return Event(start, $"props.{key}", ThnKeyKind.Number, $"{motion}:30-39", key) with { Read = false, Note = NotRead };
        yield return Event(start, "props.locked_bone", ThnKeyKind.Text, $"{motion}:30-39", "locked_bone") with
        {
            Read = false, Note = NotRead, Always = false,
        };

        const string ik = "StartIKEvent.cs";
        string[] startIk = ["START_IK"];
        yield return Event(startIk, "props.end_effector", ThnKeyKind.Text, $"{ik}:39", "end_effector");
        yield return Event(startIk, "props.count_to_root", ThnKeyKind.Number, $"{ik}:40", "count_to_root");
        yield return Event(startIk, "props.target_type", ThnKeyKind.Symbol, $"{ik}:41", "target_type") with { Group = TargetGroup };
        yield return Event(startIk, "props.target_part", ThnKeyKind.Text, $"{ik}:42", "target_part");
        // The engine reads point_at and move_to as Y or N (StartIKEvent.cs:20-21).
        yield return Event(startIk, "props.point_at", ThnKeyKind.Number, $"{ik}:44", "point_at") with
        {
            EngineRead = ThnRead.ToBool,
        };
        yield return Event(startIk, "props.move_to", ThnKeyKind.Number, $"{ik}:45", "move_to") with
        {
            EngineRead = ThnRead.ToBool,
        };
        yield return Event(startIk, "props.damping", ThnKeyKind.Number, $"{ik}:43", "damping");
        yield return Event(startIk, "props.transition_duration", ThnKeyKind.Number, $"{ik}:46", "transition_duration");
        yield return Event(startIk, "props.event_flags", ThnKeyKind.Flags, $"{ik}:25-28, :47", "event_flags") with
        {
            Bits = IkBits,
        };
        yield return Event(startIk, "props.up", ThnKeyKind.Symbol, $"{ik}:49-53", "up") with { Group = AxisGroup };
        yield return Event(startIk, "props.front", ThnKeyKind.Symbol, $"{ik}:54-58", "front") with { Group = AxisGroup };

        const string attach = "AttachEntityEvent.cs";
        string[] attachEntity = ["ATTACH_ENTITY"];
        yield return Event(attachEntity, "props.flags", ThnKeyKind.Flags, $"{attach}:60, :93-98", "flags") with
        {
            Group = "AttachFlags", Bits = AttachBits, DefaultBits = AttachDefault,
            Note = "Without flags, the engine uses POSITION and ORIENTATION.",
        };
        yield return Event(attachEntity, "props.target_type", ThnKeyKind.Symbol, $"{attach}:59", "target_type") with { Group = TargetGroup };
        yield return Event(attachEntity, "props.target_part", ThnKeyKind.Text, $"{attach}:61", "target_part");
        yield return Event(attachEntity, "props.offset", ThnKeyKind.Vector, $"{attach}:62", "offset");
        yield return Event(attachEntity, "props.up", ThnKeyKind.Symbol, $"{attach}:64-68", "up") with { Group = AxisGroup };
        yield return Event(attachEntity, "props.front", ThnKeyKind.Symbol, $"{attach}:69-73", "front") with { Group = AxisGroup };

        string[] spatial = ["START_SPATIAL_PROP_ANIM"];
        yield return Event(spatial, "props.spatialprops", ThnKeyKind.Record, $"{Spatial}:67-70", "Target pose");
        yield return Event(spatial, "props.spatialprops.pos", ThnKeyKind.Vector, $"{Spatial}:98-101", "pos");
        yield return Event(spatial, "props.spatialprops.q_orient", ThnKeyKind.Quaternion, $"{Spatial}:72-75", "q_orient");
        yield return Event(spatial, "props.spatialprops.orient", ThnKeyKind.Orient, $"{Spatial}:77-80, ThnTypes.cs:80-92",
            "orient") with
        {
            Always = false,
            Note = "The engine cannot load a script with this key. It reads the rows of the matrix from index 0, but a " +
                   "Lua list starts at 1. Retail does not use the key. Use q_orient.",
        };
        yield return Event(spatial, "props.spatialprops.axisrot", ThnKeyKind.AxisRotation, $"{Spatial}:82-96", "axisrot") with
        {
            Always = false,
        };
        yield return Event(spatial, "props.target_type", ThnKeyKind.Symbol, $"{Spatial}:64", "target_type") with { Group = TargetGroup };
        yield return Event(spatial, "props.target_part", ThnKeyKind.Text, $"{Spatial}:65", "target_part");

        const string path = "StartPathAnimationEvent.cs";
        string[] pathAnim = ["START_PATH_ANIMATION"];
        yield return Event(pathAnim, "props.start_percent", ThnKeyKind.Number, $"{path}:25", "start_percent");
        yield return Event(pathAnim, "props.stop_percent", ThnKeyKind.Number, $"{path}:26", "stop_percent");
        yield return Event(pathAnim, "props.flags", ThnKeyKind.Flags, $"{path}:27, :41-50", "flags") with
        {
            Group = "AttachFlags", Bits = AttachBits,
        };
        yield return Event(pathAnim, "props.offset", ThnKeyKind.Vector, $"{path}:28", "offset");
        yield return Event(pathAnim, "props.up", ThnKeyKind.Symbol, $"{path}:21-30", "up") with
        {
            Group = AxisGroup, Read = false, Note = NotRead,
        };
        yield return Event(pathAnim, "props.front", ThnKeyKind.Symbol, $"{path}:21-30", "front") with
        {
            Group = AxisGroup, Read = false, Note = NotRead,
        };

        string[] startSound = ["START_SOUND"];
        yield return Event(startSound, "props.flags", ThnKeyKind.Flags, "StartSoundEvent.cs:20, :36", "flags") with
        {
            Group = "SoundFlags", Bits = SoundBits,
        };
        yield return Event(startSound, "props.start_time", ThnKeyKind.Number, "StartSoundEvent.cs:21, ThnSound.cs:23-27",
            "start_time") with
        {
            Note = "The start offset of the sound in milliseconds.",
        };

        // START_CAMERA_PROP_ANIM reads its keys by place (StartCameraPropAnimEvent.cs:38-47).
        string[] cameraAnim = ["START_CAMERA_PROP_ANIM"];
        yield return Event(cameraAnim, "props.cameraprops", ThnKeyKind.Record, $"{CameraAnim}:33-36", "Camera");
        yield return Event(cameraAnim, "props.cameraprops.fovh", ThnKeyKind.Number, $"{CameraAnim}:42",
            "fovh (sets the field of view)");
        yield return Event(cameraAnim, "props.cameraprops.aspect", ThnKeyKind.Number, $"{CameraAnim}:43",
            "aspect (sets the near plane)") with
        {
            Note = "The engine reads the keys of this event by place, not by name. So aspect sets the near plane.",
        };
        yield return Event(cameraAnim, "props.cameraprops.near", ThnKeyKind.Number, $"{CameraAnim}:45",
            "near (sets the far plane)") with
        {
            Note = "The engine reads the keys of this event by place, not by name. So near sets the far plane.",
        };
        yield return Event(cameraAnim, "props.cameraprops.far", ThnKeyKind.Number, $"{CameraAnim}:47",
            "far (sets the aspect)") with
        {
            Note = "The engine reads the keys of this event by place, not by name. So far sets the aspect.",
        };
        foreach (var (key, place, effect, line) in new[]
                 {
                     ("nearplane", "aspect", "the near plane", 43), ("farplane", "near", "the far plane", 45),
                     ("hvaspect", "far", "the aspect", 47),
                 })
        {
            yield return Event(cameraAnim, $"props.cameraprops.{key}", ThnKeyKind.Number, $"{CameraAnim}:{line}",
                $"{key} (sets {effect})") with
            {
                ReadWithout = place, Always = false,
                Note = $"The engine reads {key} only when the event has no key at its place.",
            };
        }

        const string lightAnim = "StartLightPropAnimEvent.cs";
        string[] startLight = ["START_LIGHT_PROP_ANIM"];
        yield return Event(startLight, "props.lightprops", ThnKeyKind.Record, $"{lightAnim}:39", "Light");
        yield return Event(startLight, "props.lightprops.on", ThnKeyKind.Bool, $"{lightAnim}:41", "on");
        yield return Event(startLight, "props.lightprops.diffuse", ThnKeyKind.Vector, $"{lightAnim}:42", "diffuse") with
        {
            Color = ColorForm.Unit,
        };
        yield return Event(startLight, "props.lightprops.ambient", ThnKeyKind.Vector, $"{lightAnim}:43", "ambient") with
        {
            Color = ColorForm.Unit,
        };
        yield return Event(startLight, "props.lightprops.theta", ThnKeyKind.Number, $"{lightAnim}:44", "theta");
        yield return Event(startLight, "props.lightprops.cutoff", ThnKeyKind.Number, $"{lightAnim}:45", "cutoff");
        yield return Event(startLight, "props.lightprops.range", ThnKeyKind.Number, $"{lightAnim}:46", "range");
        foreach (var key in new[] { "atten", "direction" })
        {
            yield return Event(startLight, $"props.lightprops.{key}", ThnKeyKind.Vector, $"{lightAnim}:37-47", key) with
            {
                Read = false, Note = NotRead, Always = false,
            };
        }

        const string fog = "StartFogPropAnimEvent.cs";
        string[] startFog = ["START_FOG_PROP_ANIM"];
        yield return Event(startFog, "props.fogprops", ThnKeyKind.Record, $"{fog}:42", "Fog");
        yield return Event(startFog, "props.fogprops.fogon", ThnKeyKind.Bool, $"{fog}:44", "fogon");
        yield return Event(startFog, "props.fogprops.fogmode", ThnKeyKind.Symbol, $"{fog}:45", "fogmode") with { Group = "FogModes" };
        yield return Event(startFog, "props.fogprops.fogcolor", ThnKeyKind.Vector, $"{fog}:47-52", "fogcolor") with
        {
            Color = ColorForm.Byte,
        };
        yield return Event(startFog, "props.fogprops.fogdensity", ThnKeyKind.Number, $"{fog}:53", "fogdensity");
        yield return Event(startFog, "props.fogprops.fogstart", ThnKeyKind.Number, $"{fog}:54", "fogstart");
        yield return Event(startFog, "props.fogprops.fogend", ThnKeyKind.Number, $"{fog}:55", "fogend");
        yield return Event(startFog, "props.fogprops.fogtable", ThnKeyKind.Bool, $"{fog}:40-56", "fogtable") with
        {
            Read = false, Note = NotRead, Always = false,
        };

        const string audio = "StartAudioPropAnimEvent.cs";
        string[] audioAnim = ["START_AUDIO_PROP_ANIM"];
        yield return Event(audioAnim, "props.audioprops", ThnKeyKind.Record, $"{audio}:48", "Sound");
        yield return Event(audioAnim, "props.audioprops.attenuation", ThnKeyKind.Number, $"{audio}:53", "attenuation");
        yield return Event(audioAnim, "props.audioprops.pan", ThnKeyKind.Number, $"{audio}:40-56", "pan") with
        {
            Read = false, Note = NotRead, Always = false,
        };

        string[] psysAnim = ["START_PSYS_PROP_ANIM"];
        yield return Event(psysAnim, "props.psysprops", ThnKeyKind.Record, "StartPSysPropAnimEvent.cs:23", "Effect");
        yield return Event(psysAnim, "props.psysprops.sparam", ThnKeyKind.Number, "StartPSysPropAnimEvent.cs:28", "sparam");

        string[] connect = ["CONNECT_HARDPOINTS"];
        // The engine reads the hardpoints with ToString, so any value loads.
        yield return Event(connect, "props.hardpoint", ThnKeyKind.Text, "ConnectHardpointsEvent.cs:30", "hardpoint") with
        {
            EngineRead = ThnRead.Any,
        };
        yield return Event(connect, "props.parent_hardpoint", ThnKeyKind.Text, "ConnectHardpointsEvent.cs:35",
            "parent_hardpoint") with
        {
            EngineRead = ThnRead.Any,
        };

        const string floor = "StartFloorHeightAnimEvent.cs";
        string[] floorAnim = ["START_FLR_HEIGHT_ANIM"];
        yield return Event(floorAnim, "props.floor_height", ThnKeyKind.Number, $"{floor}:26", "floor_height");
        yield return Event(floorAnim, "props.target_type", ThnKeyKind.Symbol, $"{floor}:28", "target_type") with { Group = TargetGroup };
        yield return Event(floorAnim, "props.target_part", ThnKeyKind.Text, $"{floor}:27", "target_part");
    }

    /// <summary>The entity types with rows, other than <paramref name="types"/>.</summary>
    private static string[] Except(string[] types) => [.. EntityTypes.Except(types, StringComparer.Ordinal)];

    private static ThnKeyRow Entity(string[]? types, string path, ThnKeyKind kind, string evidence, string label) =>
        new(ThnElementKind.Entity, types, new ThnPath(path), kind, label, evidence);

    private static ThnKeyRow Event(string[]? types, string path, ThnKeyKind kind, string evidence, string label) =>
        new(ThnElementKind.Event, types, new ThnPath(path), kind, label, evidence);
}
