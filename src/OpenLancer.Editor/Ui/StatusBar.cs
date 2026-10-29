using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Scene;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// The status bar at the bottom of the window. Segments at the right show the load task, the profile, the linked game,
/// the system and the game of Play. Then they show the game and the test server of a play copy, the agent server, the
/// selection, the camera and the frame rate. A thin line separates them, and each one has a tooltip with the full text. The left part
/// shows the newest warning or error of the Console, or the result of the last save (see
/// <see cref="ConsoleLog.AddStatus"/>).
/// </summary>
/// <remarks>
/// Each segment is made again only when the thing that it shows changes, so a frame without changes makes no text.
/// </remarks>
internal sealed class StatusBar(EditorWindow window)
{
    /// <summary>The height of the status bar, in unscaled points.</summary>
    public const float Height = 24;

    // Sizes in unscaled points.
    private const float PaddingX = 8;
    private const float SegmentGap = 8;
    private const float LineInset = 5;
    private const float IconGap = 5;
    private const float HoverRounding = 3;
    private static readonly Vector2 ProgressSize = new(120, 6);

    // The shown frame rate changes only at this interval, so that it does not flicker.
    private const double FrameRateInterval = 0.5;

    // The camera and the frame rate change while the user works. Their segments are at least as wide as these texts,
    // so that the segments at their left do not move when a number gets a digit more or less.
    private const string CameraWidthText = "-000,000  -000,000  -000,000   000,000 u/s";
    private const string FrameRateWidthText = "000 FPS";

    private const string ConsoleClickHint = "\n\nClick to show it in the Console.";

    // The camera controls for the tooltip of the camera segment.
    private static readonly string ControlsText =
        string.Join("\n", SceneView.Controls.Select(c => $"{c.Input}: {c.Action}"));

    private static readonly Segment NoProfile = new(EditorIcons.LayerGroup, "No profile", "No profile is open.");

    private readonly List<Segment> segments = [];
    private readonly List<float> widths = [];

    // The segments of the last frame, and the things that they show.
    private (EditorSession? Session, Segment? Task, Segment? Profile) profileSegments;
    private (EditorSession? Session, GameReport? Report, LinkedGame? Entry, bool Checking, Segment? Segment) gameSegment;
    private (StarSystem? System, Segment? Segment) systemSegment;
    private (string? Text, Segment? Segment) playSegment;
    private (string? Text, Segment? Segment) gameCopySegment;
    private (string? Text, Segment? Segment) serverSegment;
    private (string? Text, Segment? Segment) agentSegment;
    private (object? Item, SystemIndex? Index, Segment? Segment) selectionSegment;
    private ((long X, long Y, long Z, long Speed) Values, Segment? Segment) cameraSegment;
    private Segment frameRateSegment = new(EditorIcons.TachometerAlt, "", "", MinWidthText: FrameRateWidthText);
    private double nextFrameRateTime;

    // The tooltip of the last message, for the entry and the click hint in lastMessageTip.
    private (ConsoleEntry? Entry, bool CanShow, string Tooltip) lastMessageTip;

    /// <summary>One segment of the status bar.</summary>
    /// <param name="Color">The glyph color. Null gives the text color.</param>
    /// <param name="Click">The action of a click. Null for a segment that the user cannot click.</param>
    /// <param name="Progress">True for a segment with an animated progress bar before the glyph.</param>
    /// <param name="MinWidthText">A text that gives the segment its smallest width, or an empty text.</param>
    private sealed record Segment(char Icon, string Text, string Tooltip, Vector4? Color = null, Action? Click = null,
        bool Progress = false, string MinWidthText = "")
    {
        /// <summary>The glyph as a string, for the draw calls.</summary>
        public string Glyph { get; } = Icon.ToString();
    }

    /// <summary>Draws the status bar at the bottom of the window.</summary>
    /// <param name="position">The top-left corner, in pixels.</param>
    /// <param name="width">The width, in pixels.</param>
    public void Draw(Vector2 position, float width)
    {
        var scale = ImGuiHelper.Scale;
        var height = MathF.Truncate(Height * scale);
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, EditorTheme.Backdrop);
        // The status bar never takes the focus, so the focus overline stays on a panel.
        ImGui.Begin("##status", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus |
                                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        ImGui.PushFont(null, EditorTheme.CaptionSize);

        Collect();
        var segmentsLeft = DrawSegments(position, width, height);
        DrawLastMessage(position, segmentsLeft, height);

        ImGui.PopFont();
        ImGui.End();
    }

    /// <summary>Makes the list of segments for this frame, from left to right.</summary>
    private void Collect()
    {
        segments.Clear();
        var session = window.Session;
        if (session != profileSegments.Session)
        {
            profileSegments = (session, session == null ? null : TaskSegment(session),
                session == null ? null : ProfileSegment(session));
        }

        if (session is { IsLoading: true } && profileSegments.Task is { } task)
        {
            segments.Add(task);
        }

        segments.Add(profileSegments.Profile ?? NoProfile);
        if (session != null && LinkedGameSegment(session) is { } game)
        {
            segments.Add(game);
        }

        var scene = window.Scene;
        if (scene.System != systemSegment.System)
        {
            systemSegment = (scene.System, scene.System == null ? null : SystemSegment(scene.System));
        }

        if (systemSegment.Segment != null)
        {
            segments.Add(systemSegment.Segment);
        }

        var play = window.Play;
        var playText = play.StatusText;
        if (playText != playSegment.Text)
        {
            playSegment = (playText, playText == null ? null : PlaySegment(playText, play.Status));
        }

        if (playSegment.Segment != null)
        {
            segments.Add(playSegment.Segment);
        }

        // The game and the test server of a play copy (section 8.8 of the M11 design).
        var games = window.GamePlay;
        var gameText = games.GameStatus;
        if (gameText != gameCopySegment.Text)
        {
            gameCopySegment = (gameText, gameText == null ? null : new Segment(EditorIcons.Rocket, gameText,
                $"{gameText}\nStop or Shift+F5 closes the game. Your game folder does not change.",
                games.GameRuns ? EditorTheme.Success : null));
        }

        var serverText = games.ServerStatus;
        if (serverText != serverSegment.Text)
        {
            serverSegment = (serverText, serverText == null ? null : new Segment(EditorIcons.SatelliteDish, serverText,
                $"{serverText}\nPlay > Stop test server closes FLServer.", games.ServerRuns ? EditorTheme.Success : null));
        }

        foreach (var segment in new[] { gameCopySegment.Segment, serverSegment.Segment })
        {
            if (segment != null)
            {
                segments.Add(segment);
            }
        }

        // The agent server of the editor (section 10 of the MCP1 design). A click opens the menu "Agent".
        // A pause of the agent edits shows the glyph of Pause in the warning color (decision D734). A click opens the
        // menu with "Resume agent edits".
        var agentText = window.AgentStatus;
        if (agentText != agentSegment.Text)
        {
            var paused = window.AgentServerOn && window.AgentPaused;
            agentSegment = (agentText, new Segment(paused ? EditorIcons.Pause : EditorIcons.Comments, agentText,
                $"{agentText}\nClick to open the Agent menu.",
                paused ? EditorTheme.Warning : window.AgentServerOn ? EditorTheme.Success : null, window.OpenAgentMenu));
        }

        segments.Add(agentSegment.Segment!);

        var selection = window.Selection;
        if (selection != selectionSegment.Item || scene.Index != selectionSegment.Index)
        {
            selectionSegment = (selection, scene.Index, SelectionSegment(selection));
        }

        if (selectionSegment.Segment != null)
        {
            segments.Add(selectionSegment.Segment);
        }

        if (scene.System != null)
        {
            var camera = scene.CameraPosition;
            var values = (Round(camera.X), Round(camera.Y), Round(camera.Z), Round(scene.CameraSpeed));
            var segment = cameraSegment.Segment;
            if (segment == null || values != cameraSegment.Values)
            {
                segment = CameraSegment(values);
                cameraSegment = (values, segment);
            }

            segments.Add(segment);
        }

        // The real time, because ImGui time advances by 0.1 s at most for each frame.
        var time = window.TotalTime;
        if (time >= nextFrameRateTime)
        {
            frameRateSegment = frameRateSegment with
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"{window.RenderFrequency:0} FPS"),
                Tooltip = "Frames per second, averaged.",
            };
            nextFrameRateTime = time + FrameRateInterval;
        }

        segments.Add(frameRateSegment);
    }

    private static Segment TaskSegment(EditorSession session) =>
        new(EditorIcons.Database, "Loading game data", $"Loading the game data of the profile {session.Profile.Name}.",
            Progress: true);

    private static Segment ProfileSegment(EditorSession session)
    {
        var profile = session.Profile;
        var mods = profile.Mods.Count == 1 ? "1 mod" : $"{profile.Mods.Count} mods";
        var modList = profile.Mods.Count == 0 ? "none" : string.Join(", ", profile.Mods.Select(m => m.Id));
        return new Segment(EditorIcons.LayerGroup, $"{profile.Name} - {mods}",
            $"Profile: {profile.Name}\nFile: {profile.FilePath}\nBase: {profile.BaseFolder}\nMods: {modList}");
    }

    /// <summary>
    /// The segment of the linked game of the open profile (section 9 of the M11a design): the Link glyph in the color of
    /// the severity and the short name of the game. It is made again only when the report, the entry or the check
    /// changes. Null in the bench, which checks no game.
    /// </summary>
    private Segment? LinkedGameSegment(EditorSession session)
    {
        var links = window.Links;
        var root = session.Profile.BaseFolder;
        var (report, entry, checking) = (links.ReportOf(root), links.Find(root), links.IsChecking(root));
        if (session != gameSegment.Session || !ReferenceEquals(report, gameSegment.Report) || entry != gameSegment.Entry ||
            checking != gameSegment.Checking)
        {
            gameSegment = (session, report, entry, checking, GameSegment(root, report, entry, checking));
        }

        return gameSegment.Segment;
    }

    private const string ClickHint = "Click to open Game link.";

    private Segment? GameSegment(string root, GameReport? report, LinkedGame? entry, bool checking)
    {
        if (report == null && entry == null && !checking)
        {
            return null;
        }

        var state = GameLook.State(report, checking);
        var name = GameLook.Name(report, entry);
        Action click = window.ShowGameLink;
        if (entry == null)
        {
            var reason = report is { IsGame: false } ? state : "The game folder of this profile is not linked.";
            return checking && report == null
                ? new Segment(EditorIcons.Link, "Checking game", $"Folder: {root}\nState: {state}\n{ClickHint}",
                    Click: click)
                : new Segment(EditorIcons.Link, "Not linked", $"Folder: {root}\nState: {reason}\n{ClickHint}",
                    EditorTheme.Warning, click);
        }

        var text = report == null ? entry.LastName
            : report.FolderMissing ? "Not found"
            : !report.IsGame ? "Not valid"
            : report.ShortName;
        var color = report == null ? (Vector4?)null : GameLook.Of(report).Color;
        return new Segment(EditorIcons.Link, text, $"Game: {name}\nFolder: {root}\nState: {state}\n{ClickHint}", color,
            click);
    }

    private Segment SystemSegment(StarSystem system)
    {
        var name = window.Session?.ItemName(system) is { Length: > 0 } systemName ? systemName : system.Nickname;
        var text = $"{name} ({system.Nickname})";
        return new Segment(EditorIcons.Map, text, $"System: {text}\nFile: {system.SourceFile}");
    }

    /// <summary>The segment of the game of Play (section 11.3 of the M4 design). A game that does not respond has a
    /// warning glyph.</summary>
    private static Segment PlaySegment(string text, PlayStatus status) => status == PlayStatus.NotResponding
        ? new Segment(EditorIcons.ExclamationTriangle, text,
            "The game of Play does not respond. Stop or Shift+F5 ends its process.", EditorTheme.Warning)
        : new Segment(EditorIcons.Play, text, $"{text}\nStop or Shift+F5 ends the game.",
            status == PlayStatus.Running ? EditorTheme.Success : null);

    /// <summary>The segment of the selected item, or null when nothing is selected. A nebula or a field shows the
    /// nickname of its zone (see <see cref="SceneItems.Nickname"/>).</summary>
    private Segment? SelectionSegment(object? selection)
    {
        var scene = window.Scene;
        if (selection is not (NicknameItem or Nebula or AsteroidField) ||
            SceneItems.Nickname(selection) is not { } nickname || scene.KindOf(selection) is not { } kind)
        {
            return null;
        }

        var name = window.Session?.ItemName(selection);
        var title = string.IsNullOrEmpty(name) ? nickname : $"{name} ({nickname})";
        var inScene = scene.Contains(selection);
        return new Segment(kind.Icon, nickname,
            inScene ? $"{kind.Label}: {title}\nClick to focus the Scene camera on it." : $"{kind.Label}: {title}",
            kind.Color, inScene ? () => scene.Focus(selection) : null);
    }

    private static Segment CameraSegment((long X, long Y, long Z, long Speed) values)
    {
        var speed = $"{Number(values.Speed)} u/s";
        return new Segment(EditorIcons.Video,
            $"{Number(values.X)}  {Number(values.Y)}  {Number(values.Z)}   {speed}",
            $"Camera position: X {Number(values.X)}, Y {Number(values.Y)}, Z {Number(values.Z)}\n" +
            $"Fly speed: {speed}\n\n{ControlsText}", MinWidthText: CameraWidthText);
    }

    /// <summary>
    /// Draws the segments, aligned to the right edge, with a line between each two segments. Returns the left edge of
    /// the first segment.
    /// </summary>
    private float DrawSegments(Vector2 position, float width, float height)
    {
        var scale = ImGuiHelper.Scale;
        var gap = MathF.Truncate(SegmentGap * scale);
        widths.Clear();
        var total = (segments.Count - 1) * (2 * gap + 1);
        foreach (var segment in segments)
        {
            var segmentWidth = Width(segment);
            widths.Add(segmentWidth);
            total += segmentWidth;
        }

        var x = MathF.Truncate(position.X + width - PaddingX * scale - total);
        var left = x;
        var drawList = ImGui.GetWindowDrawList();
        var lineColor = ImGui.GetColorU32(EditorTheme.InnerLine);
        var inset = MathF.Truncate(LineInset * scale);
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0)
            {
                x += gap;
                PixelLine.Vertical(drawList, x, position.Y + inset, position.Y + height - inset, lineColor);
                x += 1 + gap;
            }

            DrawSegment(i, segments[i], new Vector2(x, position.Y), new Vector2(widths[i], height));
            x += widths[i];
        }

        return left;
    }

    /// <summary>
    /// Draws the newest warning, error or status entry of the Console at the left: its severity glyph and the first
    /// line of its message, cut before the segments. While a profile is open, a click shows it in the Console. Without
    /// a profile the Console does not show, so the message is only text.
    /// </summary>
    private void DrawLastMessage(Vector2 position, float right, float height)
    {
        if (window.Log.Last is not { } entry)
        {
            return;
        }

        var scale = ImGuiHelper.Scale;
        var min = new Vector2(position.X + MathF.Truncate(PaddingX * scale), position.Y);
        var (glyph, color) = ConsolePanel.Look(entry.Severity);
        var textX = ImGui.CalcTextSize(glyph).X + MathF.Truncate(IconGap * scale);
        var width = MathF.Min(MathF.Ceiling(textX + ImGui.CalcTextSize(entry.FirstLine).X),
            right - MathF.Truncate(2 * SegmentGap * scale) - min.X);
        if (width <= textX)
        {
            return;
        }

        var canShow = window.Session != null;
        var size = new Vector2(width, height);
        ImGui.SetCursorScreenPos(min);
        if (ImGui.InvisibleButton("##last-message", size) && canShow)
        {
            window.ShowConsole(entry);
        }

        if (canShow && ImGui.IsItemHovered())
        {
            HoverFill(min, size);
        }

        if (!ReferenceEquals(lastMessageTip.Entry, entry) || lastMessageTip.CanShow != canShow)
        {
            lastMessageTip = (entry, canShow, canShow ? entry + ConsoleClickHint : entry.ToString());
        }

        ImGui.SetItemTooltip(lastMessageTip.Tooltip);
        var drawList = ImGui.GetWindowDrawList();
        var textY = min.Y + MathF.Truncate((height - ImGui.GetTextLineHeight()) / 2);
        drawList.AddText(new Vector2(min.X, textY), ImGui.GetColorU32(color), glyph);
        TextFit.AddText(drawList, ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(min.X + textX, textY),
            ImGui.GetColorU32(EditorTheme.Label), entry.FirstLine, width - textX);
    }

    /// <summary>
    /// The hover fill of an item that the user can click. It reaches a little past the item, so the text does not
    /// touch its edge.
    /// </summary>
    private static void HoverFill(Vector2 min, Vector2 size)
    {
        var scale = ImGuiHelper.Scale;
        var pad = new Vector2(MathF.Truncate(4 * scale), MathF.Truncate(3 * scale));
        ImGui.GetWindowDrawList().AddRectFilled(min + new Vector2(-pad.X, pad.Y),
            min + size + new Vector2(pad.X, -pad.Y), ImGui.GetColorU32(EditorTheme.ToolbarHover),
            HoverRounding * scale);
    }

    private static void DrawSegment(int index, Segment segment, Vector2 min, Vector2 size)
    {
        var scale = ImGuiHelper.Scale;
        ImGui.SetCursorScreenPos(min);
        ImGui.PushID(index);
        var clicked = ImGui.InvisibleButton("##segment", size);
        ImGui.PopID();
        var hovered = ImGui.IsItemHovered();
        ImGui.SetItemTooltip(segment.Tooltip);
        if (segment.Click != null)
        {
            if (hovered)
            {
                HoverFill(min, size);
            }

            if (clicked)
            {
                segment.Click();
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        var lineHeight = ImGui.GetTextLineHeight();
        var x = min.X;
        if (segment.Progress)
        {
            // An indeterminate bar: a negative fraction makes ImGui animate it.
            var barSize = new Vector2(MathF.Truncate(ProgressSize.X * scale), MathF.Truncate(ProgressSize.Y * scale));
            ImGui.SetCursorScreenPos(new Vector2(x, min.Y + MathF.Truncate((size.Y - barSize.Y) / 2)));
            ImGui.ProgressBar(-(float)ImGui.GetTime(), barSize, "");
            ImGuiHelper.AnimatingElement();
            x += barSize.X + MathF.Truncate(IconGap * scale);
        }

        var textY = min.Y + MathF.Truncate((size.Y - lineHeight) / 2);
        drawList.AddText(new Vector2(x, textY), ImGui.GetColorU32(segment.Color ?? EditorTheme.Label), segment.Glyph);
        x += ImGui.CalcTextSize(segment.Glyph).X + MathF.Truncate(IconGap * scale);
        drawList.AddText(new Vector2(x, textY), ImGui.GetColorU32(EditorTheme.Label), segment.Text);
    }

    /// <summary>
    /// The width of a segment in pixels: the progress bar, the glyph and the text, or the width text when it is wider.
    /// </summary>
    private static float Width(Segment segment)
    {
        var scale = ImGuiHelper.Scale;
        var textWidth = ImGui.CalcTextSize(segment.Text).X;
        if (segment.MinWidthText.Length > 0)
        {
            textWidth = MathF.Max(textWidth, ImGui.CalcTextSize(segment.MinWidthText).X);
        }

        var width = ImGui.CalcTextSize(segment.Glyph).X + MathF.Truncate(IconGap * scale) + textWidth;
        if (segment.Progress)
        {
            width += MathF.Truncate(ProgressSize.X * scale) + MathF.Truncate(IconGap * scale);
        }

        return MathF.Ceiling(width);
    }

    private static long Round(float value) => (long)MathF.Round(value);

    /// <summary>A whole number with group separators, for example "-5,472".</summary>
    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
