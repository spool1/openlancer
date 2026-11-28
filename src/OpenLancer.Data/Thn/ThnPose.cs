using System;
using System.Numerics;
using LibreLancer;

namespace OpenLancer.Data.Thn;

/// <summary>A place and a rotation, as an entity or a spatial animation of a cutscene gives them.</summary>
public readonly record struct ThnPoseValue(Vector3 Position, Quaternion Rotation);

/// <summary>
/// The values of a pose in a cutscene, as the engine reads them: a vector <c>{ x, y, z }</c>, a rotation matrix of
/// three rows (ThnScript.cs:221-240), and a quaternion <c>{ w, x, y, z }</c> (ThnTypes.cs:68-71). The writes give the
/// values that an edit puts into W. A -0 becomes 0, as in each <see cref="ThnNumber"/>.
/// </summary>
public static class ThnPose
{
    /// <summary>The path of the start position of an entity.</summary>
    public static readonly ThnPath PositionPath = new("spatialprops.pos");

    /// <summary>The path of the start rotation of an entity.</summary>
    public static readonly ThnPath OrientPath = new("spatialprops.orient");

    /// <summary>The path of the target position of a spatial animation.</summary>
    public static readonly ThnPath EventPositionPath = new("props.spatialprops.pos");

    /// <summary>The path of the target rotation of a spatial animation.</summary>
    public static readonly ThnPath EventRotationPath = new("props.spatialprops.q_orient");

    /// <summary>Why the start pose of an entity with the REFERENCE flag (see <see cref="ThnFlags.IsReference"/>) does
    /// nothing.</summary>
    public const string ReferencePoseNote =
        "The engine does not make a REFERENCE entity. It uses the object with the same name that another script of " +
        "the scene makes, for example the set script of the room. So this pose does nothing " +
        "(ThnScriptInstance.cs:70-71).";

    /// <summary>The vector of a value, or null when the value is not a list of three numbers.</summary>
    public static Vector3? Vector(ThnValue? value) =>
        value is ThnTable { Record.IsEmpty: true, List: [ThnNumber x, ThnNumber y, ThnNumber z] }
            ? new Vector3(x.Value, y.Value, z.Value)
            : null;

    /// <summary>The list <c>{ x, y, z }</c>.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">A number is not finite.</exception>
    public static ThnTable Vector(Vector3 value) =>
        ThnTable.FromList(new ThnNumber(value.X), new ThnNumber(value.Y), new ThnNumber(value.Z));

    /// <summary>The rotation of a matrix of three rows, as the engine reads it, or null when the value is not such a
    /// matrix.</summary>
    public static Quaternion? Orient(ThnValue? value)
    {
        if (OrientMatrix(value) is not { } matrix)
            return null;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
    }

    /// <summary>The matrix of three rows of a value, or null when the value is not such a matrix.</summary>
    public static Matrix4x4? OrientMatrix(ThnValue? value)
    {
        if (value is not ThnTable { Record.IsEmpty: true, List: [var a, var b, var c] } ||
            Vector(a) is not { } r1 || Vector(b) is not { } r2 || Vector(c) is not { } r3)
            return null;
        return new Matrix4x4(r1.X, r1.Y, r1.Z, 0, r2.X, r2.Y, r2.Z, 0, r3.X, r3.Y, r3.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>The matrix of three rows of a rotation.</summary>
    public static ThnTable Orient(Quaternion rotation) => Orient(Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)));

    /// <summary>
    /// The rotation matrix of Euler angles in degrees, as the Inspector shows a rotation
    /// (<see cref="MathHelper.MatrixFromEulerDegrees(Vector3)"/>). The float math gives a small error, for example
    /// -4.371139E-08 for the cosine of 90 degrees. So each value within <see cref="SnapDistance"/> of 0, 1 or -1 becomes
    /// that value, and a save writes 0 where the angles give 0.
    /// </summary>
    public static Matrix4x4 Rotation(Vector3 degrees)
    {
        var m = MathHelper.MatrixFromEulerDegrees(degrees);
        return new Matrix4x4(Snap(m.M11), Snap(m.M12), Snap(m.M13), 0, Snap(m.M21), Snap(m.M22), Snap(m.M23), 0,
            Snap(m.M31), Snap(m.M32), Snap(m.M33), 0, 0, 0, 0, 1);
    }

    /// <summary>The distance below which a value of <see cref="Rotation"/> becomes 0, 1 or -1. The float error of a sine
    /// or a cosine is far smaller, and a real value of a rotation is far from these values at this distance.</summary>
    private const float SnapDistance = 1e-6f;

    private static float Snap(float value) =>
        MathF.Abs(value) < SnapDistance ? 0
        : MathF.Abs(value - 1) < SnapDistance ? 1
        : MathF.Abs(value + 1) < SnapDistance ? -1
        : value;

    /// <summary>The three rows of the rotation part of a matrix.</summary>
    public static ThnTable Orient(Matrix4x4 m) => ThnTable.FromList(Vector(new Vector3(m.M11, m.M12, m.M13)),
        Vector(new Vector3(m.M21, m.M22, m.M23)), Vector(new Vector3(m.M31, m.M32, m.M33)));

    /// <summary>The rotation of a list <c>{ w, x, y, z }</c>, or null when the value is not such a list.</summary>
    public static Quaternion? QOrient(ThnValue? value) =>
        value is ThnTable { Record.IsEmpty: true, List: [ThnNumber w, ThnNumber x, ThnNumber y, ThnNumber z] }
            ? new Quaternion(x.Value, y.Value, z.Value, w.Value)
            : null;

    /// <summary>The list <c>{ w, x, y, z }</c> of a rotation.</summary>
    public static ThnTable QOrient(Quaternion rotation) =>
        ThnTable.FromList(new ThnNumber(rotation.W), new ThnNumber(rotation.X), new ThnNumber(rotation.Y),
            new ThnNumber(rotation.Z));

    /// <summary>
    /// The start pose of an entity: <c>spatialprops.pos</c> and <c>spatialprops.orient</c>. The engine reads them from
    /// the record part of <c>spatialprops</c>, whatever its list part holds (ThnScript.cs:371-382). A missing value is
    /// the origin or no rotation, as the engine uses them (ThnEntity.cs:24-25).
    /// </summary>
    public static ThnPoseValue StartPose(ThnTable entity) => new(
        Vector(PositionPath.EngineValueIn(ThnElementKind.Entity, entity)) ?? Vector3.Zero,
        Orient(OrientPath.EngineValueIn(ThnElementKind.Entity, entity)) ?? Quaternion.Identity);
}
