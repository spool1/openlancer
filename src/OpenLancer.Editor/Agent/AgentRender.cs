using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using LibreLancer;
using LibreLancer.Data.GameData.World;
using LibreLancer.Graphics;
using LibreLancer.Render.Cameras;
using LibreLancer.Thn;
using LibreLancer.World;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Scene;
using OpenLancer.Runtime.Thn;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Editor.Agent;

/// <summary>An image of a cutscene of the agent and the state that it shows.</summary>
/// <param name="Time">The script time of the image, in seconds: the last step of the preview clock at or before the time
/// of the call.</param>
/// <param name="Camera">The scene object that the image looks through.</param>
internal sealed record CutsceneShotDone(double Time, string Camera, ToolImage Image);

/// <summary>An image of a cutscene that steps over the next frames.</summary>
/// <param name="Result">The image. A refusal ends it with an <see cref="EditException"/>, and <see cref="Cancel"/> ends
/// it as cancelled in the next frame.</param>
/// <param name="Cancel">Stops the image. It can run on any thread, for example when the client cancels the
/// call.</param>
internal sealed record CutsceneShotJob(Task<CutsceneShotDone> Result, Action Cancel);

/// <summary>
/// The offscreen render service of the agent server (section 12.3 of the MCP1 design, decision D721). It makes the images
/// of a system and of the cutscene preview from a camera of the agent. It also captures a panel as the user sees it. It
/// runs on the UI thread. An image never moves the camera of the user, never opens a system in the Scene and never
/// changes the preview of the user.
/// </summary>
/// <remarks>
/// <para>The read of the pixels of an image runs on the UI thread and waits for the GPU (see <see cref="ReadBack"/>). The
/// PNG encode runs on a worker, so a caller awaits the image in the tail of its call (decision D765).</para>
/// <para>An image of the open system uses the stage of the Scene. An image of another system uses a stage of the service,
/// which the service keeps for the next image of that system.</para>
/// <para>A cutscene image builds a player of its own. The player steps to the time of the call over several frames,
/// with a time budget in each frame, as the preview of the user catches up (section 8.4 of the M8 design). One cutscene
/// image steps at a time, and the others wait for their turn.</para>
/// <para>A capture asks the window for its back buffer. The window reads the buffer at the end of the frame, after the
/// panels drew, so the capture shows the panel of that frame with its overlays and marks.</para>
/// </remarks>
internal sealed class AgentRender : IDisposable
{
    /// <summary>The time that the cutscene images can use in one frame. The preview of the user uses this budget while
    /// the user drags.</summary>
    internal static readonly TimeSpan FrameBudget = TimeSpan.FromMilliseconds(12);

    /// <summary>The most cutscene images that wait or render at a time. Each one keeps the scripts of its call, and a
    /// call over this number gives <c>busy</c>.</summary>
    internal const int MaxCutsceneImages = 4;

    /// <summary>The file name of a screenshot of the window that a capture asks for. The window gives each screenshot to
    /// each handler, so the service takes only its own.</summary>
    private const string CaptureTag = "OpenLancer.AgentCapture";

    private readonly EditorWindow window;
    private readonly FrameJobs cutscenes = new(MaxCutsceneImages, FrameBudget, e => new EditError(
        EditErrorKind.StepFailed, "ToolFailed",
        AgentTexts.Error("tool_failed", ("tool", "render_cutscene"), ("reason", e.Message))));

    private readonly List<(string PanelId, TaskCompletionSource<(byte[] Bgra, int Width, int Height)> Done)> captures = [];

    // The stage of the last system that an image showed and the Scene did not show, or null.
    private SceneStage? kept;

    public AgentRender(EditorWindow window)
    {
        this.window = window;
        window.ScreenshotSave += Captured;
    }

    /// <summary>
    /// The stage that an image of a system renders. It is the stage of the Scene when the Scene shows the system, else a
    /// stage of the service. The service loads that stage on the UI thread and keeps it. So the next image of the system
    /// does not load it again.
    /// </summary>
    /// <param name="system">A system of the loaded game data.</param>
    public SceneStage StageOf(StarSystem system)
    {
        if (window.Scene.Stage is { } open && Same(open, system.Nickname))
            return open;
        var session = window.Session ?? throw new InvalidOperationException("No profile is open.");
        if (kept != null && Same(kept, system.Nickname) && ReferenceEquals(kept.Data, session.GameData))
            return kept;
        kept?.Dispose();
        kept = null;
        // The engine lines of the load form a batch of the Console, as the open of a system in the Scene does.
        var batch = window.BeginConsoleBatch();
        try
        {
            var stage = new SceneStage(window, system, session, new LookAtCamera(), window.Scene.Grid,
                new Dictionary<SystemObject, GameObject>(), mainView: false, indexChanged: null);
            try
            {
                stage.CatchUp();
            }
            catch
            {
                stage.Dispose();
                throw;
            }

            kept = stage;
        }
        finally
        {
            ConsoleLog.EndBatch(batch);
        }

        return kept;
    }

    /// <summary>
    /// Renders a stage from a camera and reads the image. The image has the view toggles of the user, except the Zones
    /// toggle (see <see cref="SceneStage.RenderShot"/>).
    /// </summary>
    /// <returns>The image. Its PNG encode ends on a worker.</returns>
    public static Task<ToolImage> RenderScene(SceneStage stage, ShotCamera camera, int width, int height, bool zones) =>
        ReadBack(stage.RenderShot(camera, width, height, zones), width, height);

    /// <summary>
    /// Starts an image of a cutscene at a time. The image steps a player of its own to the time over the next frames,
    /// then renders it. A space script plays without its system, because the stage of the system belongs to the preview
    /// of the user.
    /// </summary>
    /// <param name="camera">The name of a CAMERA entity, or null for the active camera at the time.</param>
    /// <exception cref="InvalidOperationException">No profile is open.</exception>
    /// <exception cref="EditException"><see cref="EditErrorKind.Busy"/> while <see cref="MaxCutsceneImages"/> images
    /// wait or render.</exception>
    public CutsceneShotJob RenderCutscene(CutsceneShotPlan plan, double time, string? camera, int width, int height)
    {
        var session = window.Session ?? throw new InvalidOperationException("No profile is open.");
        // The job builds its player in its first frame, so a job that does not get a place costs little.
        var job = new CutsceneJob(window, session, plan, time, camera, width, height);
        if (!cutscenes.TryAdd(job))
        {
            job.Dispose();
            throw new EditException(new EditError(EditErrorKind.Busy, "CutsceneImages",
                AgentTexts.Error("render_cutscene_busy", ("count", MaxCutsceneImages))));
        }

        return new CutsceneShotJob(job.Result, job.Cancel);
    }

    /// <summary>
    /// Asks for an image of a panel as the user sees it at the end of this frame. Call it before the panels draw.
    /// </summary>
    /// <param name="panelId">The id of the panel in the layout, for example <c>scene</c>.</param>
    /// <param name="maxSide">The most pixels on each side of the image. A larger panel gives a smaller image.</param>
    /// <returns>The image. It ends with an <see cref="EditException"/> when the panel does not show at the end of the
    /// frame.</returns>
    public Task<ToolImage> Capture(string panelId, int maxSide)
    {
        var done = new TaskCompletionSource<(byte[] Bgra, int Width, int Height)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        captures.Add((panelId, done));
        window.Screenshot(CaptureTag);
        return Encode(done.Task, maxSide);
    }

    /// <summary>
    /// Steps the first cutscene image within the budget of the frame, and renders it when it reached its time. It also
    /// frees the kept stage when the Scene shows its system or the game data loaded again. Call it once in each frame,
    /// before the panels draw.
    /// </summary>
    public void Frame()
    {
        if (kept != null && (!ReferenceEquals(kept.Data, window.Session?.GameData) ||
                             window.Scene.Stage is { } open && Same(open, kept.System.Nickname)))
        {
            kept.Dispose();
            kept = null;
        }

        cutscenes.Frame();
    }

    /// <summary>Ends each image that waits, and frees the kept stage, for example when the profile closes. The calls of
    /// the images give an error.</summary>
    public void Reset()
    {
        var closed = new EditError(EditErrorKind.NotAvailable, "ProfileClosed", AgentTexts.Error("render_closed"));
        cutscenes.Fail(closed);
        foreach (var (_, done) in captures)
            done.TrySetException(new EditException(closed));
        captures.Clear();
        kept?.Dispose();
        kept = null;
    }

    public void Dispose()
    {
        Reset();
        window.ScreenshotSave -= Captured;
    }

    /// <summary>The script time of the last step of the preview clock at or before a time. The clock steps whole steps,
    /// so a cutscene image shows this time.</summary>
    internal static double LastStep(double time) =>
        Math.Floor(time / CutsceneClock.StepSeconds + 1e-6) * CutsceneClock.StepSeconds;

    /// <summary>
    /// The PNG image of a texture of a render. The read of the pixels runs now, on the UI thread, and waits for the GPU to
    /// finish the image. So the session part of a call holds the whole GPU time of its image, and the render bench
    /// measures it. The texture goes at once. The PNG encode runs on a worker.
    /// </summary>
    /// <remarks>The read does not use <c>Texture2D.GetDataAsync</c>. That read ends in a later frame, so the session part
    /// of the call and the bench would not hold the GPU time of the image.</remarks>
    internal static Task<ToolImage> ReadBack(Texture2D texture, int width, int height)
    {
        var pixels = new byte[4 * width * height];
        try
        {
            texture.GetData(pixels);
        }
        finally
        {
            texture.Dispose();
        }

        return Task.Run(() => AgentImages.Png(width, height, pixels));
    }

    /// <summary>True when a stage shows the system with a nickname.</summary>
    private static bool Same(SceneStage stage, string nickname) =>
        stage.System.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase);

    /// <summary>The PNG image of the pixels of a capture, made smaller to the largest side and encoded on a
    /// worker.</summary>
    private static async Task<ToolImage> Encode(Task<(byte[] Bgra, int Width, int Height)> pixels, int maxSide)
    {
        var (bgra, width, height) = await pixels.ConfigureAwait(false);
        var (fit, w, h) = AgentImages.Fit(bgra, width, height, maxSide);
        return AgentImages.Png(w, h, fit);
    }

    /// <summary>The window read its back buffer at the end of a frame. Each capture of that frame gets the part of its
    /// panel, or an error when its panel did not show.</summary>
    private void Captured(string? name, int width, int height, Bgra8[] data)
    {
        if (name != CaptureTag || captures.Count == 0)
            return;
        foreach (var (panelId, done) in captures)
        {
            var place = window.PanelPlace(panelId);
            var crop = place is var (min, size)
                ? AgentImages.Crop(data, width, height, new Rectangle((int)min.X, (int)min.Y, (int)size.X, (int)size.Y))
                : null;
            if (crop is { } pixels)
            {
                done.TrySetResult(pixels);
            }
            else
            {
                done.TrySetException(new EditException(new EditError(EditErrorKind.Refused, "PanelHidden",
                    AgentTexts.Error("capture_hidden", ("panel", panelId))) { Field = "panel" }));
            }
        }
        captures.Clear();
    }

    /// <summary>
    /// One image of a cutscene: a clock of its own with a player of its own, at the time of the call. The plan holds the
    /// scripts of the call, so an edit of the user after the call does not change the image. When the game data loads
    /// again, the image stops, because the player uses the resources of the session.
    /// </summary>
    private sealed class CutsceneJob : IFrameJob
    {
        private readonly EditorWindow window;
        private readonly EditorSession session;
        private readonly CutsceneShotPlan plan;
        private readonly CutsceneClock clock;
        private readonly double target;
        private readonly string? camera;
        private readonly int width;
        private readonly int height;
        private readonly TaskCompletionSource<CutsceneShotDone> done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool cancelled;

        public CutsceneJob(EditorWindow window, EditorSession session, CutsceneShotPlan plan, double time, string? camera,
            int width, int height)
        {
            this.window = window;
            this.session = session;
            this.plan = plan;
            this.camera = camera;
            this.width = width;
            this.height = height;
            var items = plan.Data.Items;
            var viewport = new Rectangle(0, 0, width, height);
            clock = new CutsceneClock(() => CutscenePlayer.Build(plan.Plan,
                path => new ThnScript(plan.Scripts.TryGetValue(path, out var bytes) ? bytes : items.VFS.ReadAllBytes(path),
                    items.ThornReadCallback, path),
                plan.Data, session.Resources, window, viewport));
            target = LastStep(time);
            clock.Seek(target);
        }

        public Task<CutsceneShotDone> Result => done.Task;

        public bool Cancelled => cancelled;

        /// <summary>Notes a cancel of the call. It can run on any thread.</summary>
        public void Cancel() => cancelled = true;

        /// <summary>Steps the player within a budget, and renders it when it reached its time.</summary>
        public bool Frame(TimeSpan budget)
        {
            if (cancelled)
            {
                EndCancelled();
                return true;
            }

            if (!ReferenceEquals(window.Session, session) || !ReferenceEquals(session.GameData, plan.Data))
            {
                Fail(EditErrorKind.Refused, "DataReloaded", AgentTexts.Error("render_data_changed"));
                return true;
            }

            // A build and a step load models with the resources of the session, so they wait for a reload.
            if (session.IsBusy)
                return false;
            clock.Frame(0, budget);
            if (clock.Problem is { } problem)
            {
                Fail(EditErrorKind.Refused, "PreviewFailed", AgentTexts.Error("render_preview_failed",
                    ("time", Seconds(clock.ProblemTime)), ("reason", ErrorText.Of(problem))));
                return true;
            }

            if (clock is not { CatchingUp: false, Visible: CutscenePlayer player } || Math.Abs(player.Time - target) > 1e-6)
                return false;
            Render(player);
            return true;
        }

        public void Fail(EditError error) => done.TrySetException(new EditException(error));

        public void EndCancelled() => done.TrySetCanceled();

        public void Dispose() => clock.Dispose();

        private void Fail(EditErrorKind kind, string detail, string message, string? field = null) =>
            Fail(new EditError(kind, detail, message) { Field = field });

        /// <summary>Renders the player through the camera of the call into a texture and reads the image.</summary>
        private void Render(CutscenePlayer player)
        {
            if (camera != null)
            {
                if (player.Find(camera) is not { } entity)
                {
                    Fail(EditErrorKind.NotFound, "CameraNotFound",
                        AgentTexts.Error("render_camera_none", ("camera", camera)), "camera");
                    return;
                }

                if (entity.Type != EntityTypes.Camera || !player.UseCamera(camera))
                {
                    Fail(EditErrorKind.InvalidValue, "NotACamera",
                        AgentTexts.Error("render_camera_type", ("camera", camera)), "camera");
                    return;
                }
            }
            else if (player.CameraName == null)
            {
                Fail(EditErrorKind.Refused, "NoCamera", AgentTexts.Error("render_no_camera", ("time", Seconds(player.Time))),
                    "camera");
                return;
            }

            var context = window.RenderContext;
            var restoreTarget = context.RenderTarget;
            var clearColor = context.ClearColor;
            var viewport = new Rectangle(0, 0, width, height);
            var texture = new RenderTarget2D(context, width, height);
            var drawn = false;
            context.RenderTarget = texture;
            context.PushViewport(0, 0, width, height);
            context.PushScissor(viewport, false);
            try
            {
                context.Cull = true;
                context.DepthEnabled = true;
                // The image is the screen of the game, so the camera takes the ratio of the image.
                player.Draw(CutsceneClock.StepSeconds, viewport, width / (float)height, null);
                drawn = true;
            }
            catch (Exception e)
            {
                Fail(EditErrorKind.Refused, "DrawFailed", AgentTexts.Error("render_draw_failed",
                    ("time", Seconds(player.Time)), ("reason", ErrorText.Of(e))));
            }
            finally
            {
                context.PopViewport();
                context.PopScissor();
                context.ClearColor = clearColor;
                context.DepthEnabled = false;
                context.BlendMode = BlendMode.Normal;
                context.Cull = false;
                context.RenderTarget = restoreTarget;
                texture.Dispose(keepTexture: drawn);
            }

            if (drawn)
                _ = Finish(ReadBack(texture.Texture, width, height), player.Time, player.CameraName!);
        }

        private async Task Finish(Task<ToolImage> image, double time, string name)
        {
            try
            {
                done.TrySetResult(new CutsceneShotDone(time, name, await image.ConfigureAwait(false)));
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        }

        private static string Seconds(double time) => time.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
