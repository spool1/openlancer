using System;
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.Graphics;
using LibreLancer.ImUI;
using LibreLancer.Render;
using LibreLancer.Render.Cameras;
using LibreLancer.Thn;
using LibreLancer.Utf.Anm;
using LibreLancer.World;
using OpenLancer.Editor.Assets;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Inspector;

/// <summary>What a drag on the clip preview does.</summary>
internal enum PreviewDrag
{
    None,
    Pan,
    Orbit,
    Zoom,
}

/// <summary>The camera of the clip preview.</summary>
/// <param name="Zoom">The factor of the distance of the camera.</param>
/// <param name="Yaw">The orbit angle around the vertical axis, in radians.</param>
/// <param name="Pitch">The orbit angle up or down, in radians.</param>
/// <param name="Pan">The move of the center of the view.</param>
internal readonly record struct PreviewView(float Zoom, float Yaw, float Pitch, Vector3 Pan)
{
    /// <summary>The view of a new clip preview, and the view after <b>Reset view</b>.</summary>
    public static PreviewView Start => new(1, 0, 0, Vector3.Zero);

    /// <summary>The view after F, as in the Unity animation preview: the pan and the zoom go back, and the orbit
    /// stays.</summary>
    public PreviewView Framed() => this with { Zoom = 1, Pan = Vector3.Zero };
}

/// <summary>
/// The clip that the clip preview plays. A change of a part starts the clip again. The game data is a part, because a
/// reload after a save can change a clip while the models stay the same.
/// </summary>
/// <param name="Data">The game data that gives the animation table.</param>
/// <param name="Models">The models of the character.</param>
/// <param name="Clip">The name of the clip.</param>
/// <param name="Idle">The name of the idle below the clip, or null.</param>
internal readonly record struct PreviewClip(object Data, CharacterModels Models, string Clip, string? Idle);

/// <summary>
/// The clip preview of the animation picker. It plays a clip in a loop on a copy of a character of the scene. The copy
/// is in its own world, with the lights of the asset thumbnails. The controls follow the Unity animation preview (see
/// <see cref="Controls"/>).
/// </summary>
internal sealed class ThnAnimationPreview(EditorWindow window) : IDisposable
{
    /// <summary>The controls, for the hint of the pane. They are the controls of the Unity animation preview.</summary>
    public const string Controls = "Drag or middle drag: pan. Right drag or Alt+drag: orbit. Wheel or Alt+right drag: " +
                                   "zoom. Shift: faster orbit and zoom. F: put back the pan and the zoom.";

    /// <summary>The size of the render target in pixels.</summary>
    private const int Size = 512;

    /// <summary>The vertical field of view of the camera in degrees, as <see cref="LookAtCamera"/> has it.</summary>
    private const float FieldOfView = 50;

    /// <summary>The angle of an orbit for a drag across the view, in degrees, as in the Unity animation
    /// preview.</summary>
    private const float OrbitAngle = 140;

    /// <summary>Shift makes an orbit this many times faster, as in the Unity animation preview.</summary>
    private const float FastOrbit = 3;

    /// <summary>Shift makes a zoom this many times faster, as in the Unity animation preview.</summary>
    private const float FastZoom = 4;

    /// <summary>The wheel steps of a zoom for each pixel of a drag.</summary>
    private const float DragZoomSpeed = 0.02f;

    /// <summary>The objects of the clip that the preview plays.</summary>
    /// <param name="Duration">The length of one loop in seconds.</param>
    /// <param name="Character">The copy of the character. Its object is the actor in the world.</param>
    private sealed record Scene(GameWorld World, ThnSceneObject Character, DfmSkeletonManager Skeleton, float Duration);

    private readonly LookAtCamera camera = new();
    private EditorSession? owner;
    private SystemRenderer? renderer;
    private RenderTarget2D? target;
    private ImTextureRef? image;
    private Scene? scene;
    private PreviewClip? current;
    private string? problem;
    private double playTime;
    private long lastFrame;
    private Vector3 cameraCenter;
    private float cameraDistance;
    private PreviewView view = PreviewView.Start;

    /// <summary>Plays a clip on a copy of a character and draws the view and its controls.</summary>
    /// <param name="models">The models of the character of the scene, or null when the scene has no such
    /// character.</param>
    /// <param name="clip">The name of the clip.</param>
    /// <param name="idle">The name of the idle that plays below the clip, or null (see
    /// <see cref="AnimationClips.IdleFor"/>).</param>
    public void Draw(CharacterModels? models, string clip, string? idle)
    {
        if (models == null)
        {
            ImGui.TextWrapped("The cutscene preview does not have this character now. Open the cutscene preview and " +
                              "wait until the scene loads.");
            return;
        }

        var session = window.Session;
        if (session?.GameData is not { } data)
        {
            ImGui.TextWrapped(ClipPickerState<GameDataManager>.NotLoaded);
            return;
        }

        try
        {
            if (current != new PreviewClip(data, models, clip, idle))
            {
                Start(session, data, models, clip, idle);
            }

            if (problem != null || scene == null)
            {
                ImGui.TextWrapped(problem ?? "");
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var elapsed = lastFrame == 0 ? 0 : Math.Min(0.1, Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds);
            lastFrame = now;
            playTime += elapsed;
            if (playTime >= scene.Duration)
            {
                // The clip starts again with a new copy of the character.
                Start(session, data, models, clip, idle);
                lastFrame = now;
            }

            var shown = scene!;
            Advance(shown.Skeleton, shown.Character, elapsed);
            var rotation = Quaternion.CreateFromYawPitchRoll(view.Yaw, view.Pitch, 0);
            var center = cameraCenter + shown.Character.Translate + view.Pan;
            var offset = Vector3.Transform(new Vector3(0, cameraDistance * 0.05f, -cameraDistance), rotation) *
                         view.Zoom;
            camera.Update(Size, Size, center + offset, center, Matrix4x4.CreateFromQuaternion(rotation));
            Render(shown.World, elapsed);
            if (ImGui.Button("Reset view"))
            {
                view = PreviewView.Start;
            }

            ImGui.SetItemTooltip("Puts back the pan, the zoom and the orbit of the clip preview. F puts back only the " +
                                 "pan and the zoom.");
            var available = ImGui.GetContentRegionAvail();
            var side = Math.Max(64, Math.Min(available.X, available.Y));
            var origin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##clip-preview", new Vector2(side),
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            var io = ImGui.GetIO();
            if (ImGui.IsItemHovered())
            {
                if (io.MouseWheel != 0)
                {
                    view = view with { Zoom = Zoom(view.Zoom, io.MouseWheel, io.KeyShift) };
                }

                if (!io.WantTextInput && ImGui.IsKeyPressed(ImGuiKey.F, false))
                {
                    view = view.Framed();
                }
            }

            if (ImGui.IsItemActive() && Dragged() is { } button)
            {
                switch (DragOf(button, io.KeyAlt))
                {
                    case PreviewDrag.Pan:
                        view = view with
                        {
                            Pan = Pan(view.Pan, io.MouseDelta, rotation, cameraDistance * view.Zoom, side),
                        };
                        break;
                    case PreviewDrag.Orbit:
                        var (yaw, pitch) = Orbit(view.Yaw, view.Pitch, io.MouseDelta, side, io.KeyShift);
                        view = view with { Yaw = yaw, Pitch = pitch };
                        break;
                    case PreviewDrag.Zoom:
                        view = view with { Zoom = Zoom(view.Zoom, DragZoom(io.MouseDelta), io.KeyShift) };
                        break;
                }
            }

            ImGui.GetWindowDrawList().AddImage(image!.Value, origin, origin + new Vector2(side),
                new Vector2(0, 1), new Vector2(1, 0));
        }
        catch (Exception e)
        {
            problem = $"The clip preview cannot play this clip: {e.Message}";
            FLLog.Warning("Clip preview", e.ToString());
            ImGui.TextWrapped(problem);
        }
    }

    /// <summary>
    /// What a drag with a mouse button does, as in the Unity animation preview. The left or the middle button pans. The
    /// right button, or Alt and the left button, orbits. Alt and the right button zoom.
    /// </summary>
    internal static PreviewDrag DragOf(ImGuiMouseButton button, bool alt) => button switch
    {
        ImGuiMouseButton.Left => alt ? PreviewDrag.Orbit : PreviewDrag.Pan,
        ImGuiMouseButton.Right => alt ? PreviewDrag.Zoom : PreviewDrag.Orbit,
        ImGuiMouseButton.Middle => PreviewDrag.Pan,
        _ => PreviewDrag.None,
    };

    /// <summary>
    /// The yaw and the pitch after an orbit drag, in radians, as in the Unity animation preview. A drag across the view
    /// turns 140 degrees, and Shift makes it 3 times faster. The pitch stays from -90 to 90 degrees.
    /// </summary>
    /// <param name="side">The size of the view in pixels.</param>
    /// <param name="fast">True while Shift is down.</param>
    internal static (float Yaw, float Pitch) Orbit(float yaw, float pitch, Vector2 delta, float side, bool fast)
    {
        var speed = OrbitAngle * MathF.PI / 180 / Math.Max(1, side) * (fast ? FastOrbit : 1);
        return (yaw - delta.X * speed, Math.Clamp(pitch - delta.Y * speed, -MathF.PI / 2, MathF.PI / 2));
    }

    /// <summary>The pan after a drag: the point under the pointer moves with the pointer, in the plane of the
    /// view.</summary>
    /// <param name="rotation">The rotation of the camera.</param>
    /// <param name="distance">The distance of the camera from the center.</param>
    /// <param name="side">The size of the view in pixels.</param>
    internal static Vector3 Pan(Vector3 current, Vector2 delta, Quaternion rotation, float distance, float side)
    {
        var amount = 2 * distance * MathF.Tan(FieldOfView / 2 * MathF.PI / 180) / Math.Max(1, side);
        var right = Vector3.Transform(-Vector3.UnitX, rotation);
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        return current + (-delta.X * right + delta.Y * up) * amount;
    }

    /// <summary>The zoom after wheel steps: a step up moves the camera closer. Shift makes a step 4 times larger, as in
    /// the Unity animation preview.</summary>
    /// <param name="fast">True while Shift is down.</param>
    internal static float Zoom(float current, float wheel, bool fast) =>
        Math.Clamp(current * MathF.Pow(0.85f, wheel * (fast ? FastZoom : 1)), 0.35f, 4f);

    /// <summary>The wheel steps of a zoom drag: a drag to the right or up moves the camera closer, as in Unity.</summary>
    internal static float DragZoom(Vector2 delta) => (delta.X - delta.Y) * DragZoomSpeed;

    /// <summary>Advances the clip and moves the character by the root motion of the clip.</summary>
    internal static void Advance(DfmSkeletonManager skeleton, ThnSceneObject character, double elapsed)
    {
        skeleton.UpdateScripts(elapsed);
        character.Update();
    }

    /// <summary>The length of a clip: the longest channel of its joint maps and its object maps, and at least
    /// 0.1 s.</summary>
    internal static float ClipDuration(Script script)
    {
        var duration = 0f;
        foreach (var joint in script.JointMaps)
        {
            duration = Math.Max(duration, joint.Channel.Duration);
        }

        foreach (var obj in script.ObjectMaps)
        {
            duration = Math.Max(duration, obj.Channel.Duration);
        }

        return Math.Max(0.1f, duration);
    }

    /// <summary>Frees the copy of the character and its world, and forgets the clip. The renderer stays. Call it when
    /// the list closes, so the clip preview does not keep the animations of the game data.</summary>
    public void Release()
    {
        scene?.World.Dispose();
        scene = null;
        current = null;
        problem = null;
    }

    /// <summary>Frees the render target and the renderer, and forgets the character and the session.</summary>
    public void Dispose()
    {
        Release();
        renderer?.Dispose();
        renderer = null;
        owner = null;
        if (target != null)
        {
            ImGuiHelper.DeregisterTexture(target.Texture);
            target.Dispose();
            target = null;
            image = null;
        }
    }

    /// <summary>The button of the drag that is active now, or null.</summary>
    private static ImGuiMouseButton? Dragged()
    {
        foreach (var button in new[] { ImGuiMouseButton.Left, ImGuiMouseButton.Right, ImGuiMouseButton.Middle })
        {
            if (ImGui.IsMouseDragging(button))
            {
                return button;
            }
        }

        return null;
    }

    /// <summary>Makes a new copy of the character and starts the clip on it. The script comes from the animation table of
    /// the engine, so it is the script that the game plays.</summary>
    private void Start(EditorSession session, GameDataManager data, CharacterModels models, string clip, string? idle)
    {
        if (!ReferenceEquals(owner, session))
        {
            // The renderer holds the resources of the session.
            Dispose();
            owner = session;
        }

        Release();
        current = new PreviewClip(data, models, clip, idle);
        lastFrame = 0;
        playTime = 0;
        var scripts = data.GetCharacterAnimations().Scripts;
        if (!scripts.TryGetValue(clip, out var script))
        {
            problem = ThnAnimationPicker.UnknownClip;
            return;
        }

        var skeleton = new DfmSkeletonManager(models.Body, models.Head, models.LeftHand, models.RightHand);
        if (idle != null && scripts.TryGetValue(idle, out var idleScript))
        {
            skeleton.StartScript(idleScript, 0, 1, 0, true);
        }

        var duration = ClipDuration(script);
        skeleton.StartScript(script, 0, 1, duration);
        renderer ??= new SystemRenderer(camera, session.Resources, window)
        {
            NullColor = ThumbnailRenderer.NullColor,
            SystemLighting = ThumbnailRenderer.Lighting,
        };
        target ??= new RenderTarget2D(window.RenderContext, Size, Size);
        image ??= ImGuiHelper.RegisterTexture(target.Texture);
        var world = new GameWorld(renderer, null, session.Resources, null, false);
        var actor = new GameObject
        {
            RenderComponent = new CharacterRenderer(skeleton)
            {
                Accessory = models.Accessory,
                AccessoryModel = models.AccessoryModel,
            },
        };
        actor.SetTransform(Transform3D.Identity);
        world.AddObject(actor);
        var character = new ThnSceneObject { Name = "Clip preview", Object = actor, Rotate = Quaternion.Identity };
        scene = new Scene(world, character, skeleton, duration);
        var bounds = skeleton.Bounds;
        cameraCenter = (bounds.Min + bounds.Max) * 0.5f;
        cameraDistance = Math.Max(0.5f, Vector3.Distance(bounds.Min, bounds.Max) * 0.5f) * ThumbnailRenderer.CameraDistance;
    }

    private void Render(GameWorld world, double elapsed)
    {
        var context = window.RenderContext;
        var previous = context.RenderTarget;
        context.RenderTarget = target;
        context.PushViewport(0, 0, Size, Size);
        try
        {
            context.Cull = true;
            world.RenderUpdate(elapsed);
            renderer!.Draw(Size, Size);
        }
        finally
        {
            context.PopViewport();
            context.DepthEnabled = false;
            context.BlendMode = BlendMode.Normal;
            context.Cull = false;
            context.RenderTarget = previous;
        }
    }
}
