using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Render.Cameras;

namespace OpenLancer.Editor.Scene;

/// <summary>The sizes of a fly camera for one scale of the world.</summary>
/// <param name="MinDistance">The nearest orbit distance.</param>
/// <param name="Speed">The fly speed at the start, in world units per second.</param>
/// <param name="MinSpeed">The lowest fly speed that the wheel sets.</param>
/// <param name="MaxSpeed">The highest fly speed that the wheel sets.</param>
/// <param name="MinFrameSpeed">The lowest fly speed that a frame of a sphere sets.</param>
/// <param name="MaxFrameSpeed">The highest fly speed that a frame of a sphere sets.</param>
/// <param name="NearFactor">The near plane is this part of the orbit distance, between the two limits.</param>
/// <param name="MinNear">The nearest near plane.</param>
/// <param name="MaxNear">The farthest near plane.</param>
/// <param name="Far">The far plane.</param>
internal sealed record CameraScale(float MinDistance, float Speed, float MinSpeed, float MaxSpeed, float MinFrameSpeed,
    float MaxFrameSpeed, float NearFactor, float MinNear, float MaxNear, float Far)
{
    /// <summary>A star system, in game units.</summary>
    public static readonly CameraScale System = new(10, 1000, 10, 1_000_000, 100, 100_000, 0.001f, 1, 1000, 10_000_000);

    /// <summary>
    /// A room or a cutscene, in meters: 5 m/s at the start and a near plane of 0.05 m (section 7.6 of the M8 design). A
    /// space cutscene can have ships kilometers away, so the wheel can still fly fast.
    /// </summary>
    public static readonly CameraScale Room = new(0.1f, 5, 0.25f, 50_000, 1, 1000, 0.001f, 0.05f, 50, 1_000_000);
}

/// <summary>
/// The scene view camera, with Unity-style controls:
/// hold the right mouse button to fly (mouse look, WASD, Q and E, the wheel sets the speed),
/// Alt and the left mouse button to orbit, the middle mouse button to pan, and the wheel to zoom.
/// </summary>
/// <param name="scale">The sizes of the camera, or null for a star system.</param>
internal sealed class SceneCamera(CameraScale? scale = null)
{
    private const float MouseTurnRate = 0.005f;

    // Straight up or down, so the top and bottom views of the axis widget are exact. The up vector of the camera turns
    // with the pitch, so the view stays valid at this limit.
    private const float MaxPitch = MathF.PI / 2;

    // A view direction nearer than this to straight up or down has no yaw of its own (see SetPose).
    private const float VerticalLimit = 0.9999f;

    private readonly CameraScale scale = scale ?? CameraScale.System;
    private Vector3 position;
    private Vector3 target;
    private float yaw;
    private float pitch;

    // The turn about the view direction. Only SetPose sets it, and a turn of the mouse levels the view again.
    private float roll;

    /// <summary>The fly speed, in world units per second.</summary>
    public float Speed { get; private set; } = (scale ?? CameraScale.System).Speed;

    public Vector3 Position => position;

    /// <summary>The orbit point of the camera. Create in the Scene puts a new item there, as Unity puts a new object at
    /// the pivot of the Scene view (decision D234 of the M3 design).</summary>
    public Vector3 Pivot => target;

    /// <summary>
    /// The turn of the camera. It turns -Z into the view direction and +Y into the up of the view. A ship flies along its
    /// -Z too, so a ship with this orientation points where the camera looks (section 9.5 of the M4 design). The camera
    /// has no roll, except after <see cref="SetPose"/>.
    /// </summary>
    public Quaternion Orientation => Quaternion.CreateFromRotationMatrix(Rotation);

    /// <summary>The near and the far plane. The near plane follows the orbit distance, so the depth stays precise at
    /// each distance.</summary>
    public Vector2 ZRange => new(Math.Clamp(Distance * scale.NearFactor, scale.MinNear, scale.MaxNear), scale.Far);

    private Matrix4x4 Rotation =>
        Matrix4x4.CreateRotationZ(roll) * Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationY(yaw);
    private Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);
    private float Distance => Math.Max(scale.MinDistance, Vector3.Distance(position, target));

    /// <summary>Looks at a sphere from above and to the front, so that all of it is in view.</summary>
    public void Frame(Vector3 center, float radius)
    {
        yaw = 0;
        pitch = -0.6f;
        roll = 0;
        target = center;
        position = center - Forward * Math.Max(radius * 1.6f, scale.MinDistance);
        Speed = Math.Clamp(radius / 6, scale.MinFrameSpeed, scale.MaxFrameSpeed);
    }

    /// <summary>
    /// Puts the camera at a pose, for example the pose of a cutscene camera, with its roll. The orbit point stays at the
    /// same distance in front of the camera. A view straight up or down takes the turn about the vertical from the up
    /// of the pose, and has no roll.
    /// </summary>
    public void SetPose(Vector3 at, Quaternion orientation)
    {
        var distance = Distance;
        var forward = Vector3.Transform(-Vector3.UnitZ, orientation);
        var up = Vector3.Transform(Vector3.UnitY, orientation);
        roll = 0;
        if (MathF.Abs(forward.Y) > VerticalLimit)
        {
            // Straight down, the up of the view is (-sin yaw, 0, -cos yaw). Straight up, it is (sin yaw, 0, cos yaw).
            pitch = MathF.CopySign(MaxPitch, forward.Y);
            yaw = forward.Y < 0 ? MathF.Atan2(-up.X, -up.Z) : MathF.Atan2(up.X, up.Z);
        }
        else
        {
            pitch = MathF.Asin(forward.Y);
            yaw = MathF.Atan2(-forward.X, -forward.Z);
            // The roll turns the up of the level view toward its left: up = cos(roll) up0 - sin(roll) right0.
            var level = Rotation;
            roll = MathF.Atan2(-Vector3.Dot(up, Vector3.Transform(Vector3.UnitX, level)),
                Vector3.Dot(up, Vector3.Transform(Vector3.UnitY, level)));
        }

        position = at;
        target = at + Forward * distance;
    }

    /// <summary>Moves the camera to look at a point from a distance, and keeps the direction.</summary>
    public void Focus(Vector3 point, float distance)
    {
        target = point;
        position = point - Forward * Math.Max(distance, scale.MinDistance);
    }

    /// <summary>
    /// Turns the camera to look in a direction at the orbit point, from the same distance. A view straight down or up
    /// has -Z at the top of the screen.
    /// </summary>
    public void LookAlong(Vector3 direction)
    {
        var distance = Distance;
        direction = Vector3.Normalize(direction);
        roll = 0;
        pitch = MathF.Asin(Math.Clamp(direction.Y, -1, 1));
        // Straight down or up, the yaw does not change the direction. Yaw 0 then puts -Z at the top of the screen.
        yaw = MathF.Abs(direction.Y) > 0.9999f ? 0 : MathF.Atan2(-direction.X, -direction.Z);
        position = target - Forward * distance;
    }

    /// <summary>Reads the mouse and the keyboard. Call this right after the scene view's invisible button.</summary>
    public void HandleInput(Keyboard keyboard, bool hovered, bool active, double elapsed)
    {
        var io = ImGui.GetIO();
        var delta = io.MouseDelta;

        if (active && ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            Fly(keyboard, delta, io.MouseWheel, io.KeyShift, (float)elapsed);
        }
        else if (active && io.KeyAlt && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            var distance = Distance;
            Turn(delta);
            position = target - Forward * distance;
        }
        else if (active && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            var scale = Distance * 0.0015f;
            var move = (Vector3.Transform(Vector3.UnitY, Rotation) * delta.Y -
                        Vector3.Transform(Vector3.UnitX, Rotation) * delta.X) * scale;
            position += move;
            target += move;
        }
        else if (hovered && io.MouseWheel != 0)
        {
            var distance = Distance * MathF.Pow(0.85f, io.MouseWheel);
            position = target - Forward * Math.Max(distance, scale.MinDistance);
        }
    }

    /// <summary>
    /// Updates the scene camera and the sky camera for a view of the given size. The sky camera has the same position,
    /// direction, up and field of view as the scene camera. Only its near and far planes are different (see
    /// <see cref="StarspherePass"/>).
    /// </summary>
    public void Apply(LookAtCamera camera, LookAtCamera sky, int width, int height)
    {
        // A near plane that follows the distance keeps depth precision over the size of a whole system. At the
        // whole-system view, this near plane is farther than the starsphere models, so they need the sky camera.
        camera.ZRange = ZRange;
        sky.ZRange = StarspherePass.SkyRange;
        sky.GameFOV = camera.GameFOV;
        camera.Update(width, height, position, position + Forward, Rotation);
        sky.Update(width, height, position, position + Forward, Rotation);
    }

    private void Fly(Keyboard keyboard, Vector2 delta, float wheel, bool boost, float elapsed)
    {
        var distance = Distance;
        Turn(delta);
        if (wheel != 0)
        {
            Speed = Math.Clamp(Speed * MathF.Pow(1.25f, wheel), scale.MinSpeed, scale.MaxSpeed);
        }

        var move = Vector3.Zero;
        if (keyboard.IsKeyDown(Keys.W)) move -= Vector3.UnitZ;
        if (keyboard.IsKeyDown(Keys.S)) move += Vector3.UnitZ;
        if (keyboard.IsKeyDown(Keys.A)) move -= Vector3.UnitX;
        if (keyboard.IsKeyDown(Keys.D)) move += Vector3.UnitX;
        if (keyboard.IsKeyDown(Keys.E)) move += Vector3.UnitY;
        if (keyboard.IsKeyDown(Keys.Q)) move -= Vector3.UnitY;
        if (move != Vector3.Zero)
        {
            position += Vector3.Transform(Vector3.Normalize(move), Rotation) * Speed * (boost ? 4 : 1) * elapsed;
        }

        // The orbit point stays in front of the camera at the same distance.
        target = position + Forward * distance;
    }

    /// <summary>
    /// Turns the camera by a move of the mouse, in pixels. A move levels the view, so the roll of <see cref="SetPose"/>
    /// goes. Fly and orbit call it in each frame while a button is down, so a frame without a move keeps the view: a
    /// press of the right button after Align view to camera does not level it.
    /// </summary>
    internal void Turn(Vector2 delta)
    {
        if (delta == Vector2.Zero)
        {
            return;
        }

        roll = 0;
        yaw -= delta.X * MouseTurnRate;
        pitch = Math.Clamp(pitch - delta.Y * MouseTurnRate, -MaxPitch, MaxPitch);
    }
}
