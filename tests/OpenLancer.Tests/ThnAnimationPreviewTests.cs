using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Utf.Anm;
using OpenLancer.Editor.Inspector;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The camera controls and the loop length of the clip preview, without a render (section 12.1 of the M8b
/// design). The controls follow the Unity animation preview.</summary>
public sealed class ThnAnimationPreviewTests
{
    [Fact]
    public void TheButtonsFollowTheUnityAnimationPreview()
    {
        Assert.Equal(PreviewDrag.Pan, ThnAnimationPreview.DragOf(ImGuiMouseButton.Left, alt: false));
        Assert.Equal(PreviewDrag.Pan, ThnAnimationPreview.DragOf(ImGuiMouseButton.Middle, alt: false));
        Assert.Equal(PreviewDrag.Orbit, ThnAnimationPreview.DragOf(ImGuiMouseButton.Right, alt: false));
        Assert.Equal(PreviewDrag.Orbit, ThnAnimationPreview.DragOf(ImGuiMouseButton.Left, alt: true));
        Assert.Equal(PreviewDrag.Zoom, ThnAnimationPreview.DragOf(ImGuiMouseButton.Right, alt: true));
    }

    /// <summary>A pan moves the center in the plane of the view, by an amount that grows with the distance. With a yaw
    /// of 90 degrees, a horizontal drag moves the center along Z, not X.</summary>
    [Fact]
    public void APanMovesInThePlaneOfTheView()
    {
        var horizontal = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0), Quaternion.Identity, 2, 400);
        var vertical = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(0, 20), Quaternion.Identity, 2, 400);
        var farther = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0), Quaternion.Identity, 4, 400);
        var turned = ThnAnimationPreview.Pan(Vector3.Zero, new Vector2(20, 0),
            Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0), 2, 400);

        Assert.True(horizontal.X > 0);
        Assert.Equal(0, horizontal.Z, 5);
        Assert.True(vertical.Y > 0);
        Assert.Equal(horizontal.X * 2, farther.X, 5);
        Assert.Equal(0, turned.X, 5);
        Assert.Equal(-horizontal.X, turned.Z, 5);
    }

    /// <summary>
    /// An orbit follows the Unity animation preview. A drag across the view turns 140 degrees, whatever the size of the
    /// view, and Shift makes it 3 times faster. The pitch stops at 90 degrees up and down. The yaw has no limit.
    /// </summary>
    [Fact]
    public void AnOrbitTurnsAsInUnityAndStopsAtNinetyDegrees()
    {
        var across = 140 * MathF.PI / 180;
        var (yaw, pitch) = ThnAnimationPreview.Orbit(0, 0, new Vector2(400, -100), 400, fast: false);
        Assert.Equal(-across, yaw, 5);
        Assert.Equal(across / 4, pitch, 5);
        Assert.Equal(-across, ThnAnimationPreview.Orbit(0, 0, new Vector2(200, 0), 200, fast: false).Yaw, 5);
        Assert.Equal(-3 * across, ThnAnimationPreview.Orbit(0, 0, new Vector2(400, 0), 400, fast: true).Yaw, 5);

        Assert.Equal(MathF.PI / 2, ThnAnimationPreview.Orbit(0, 0, new Vector2(0, -1000), 400, fast: false).Pitch, 5);
        Assert.Equal(-MathF.PI / 2, ThnAnimationPreview.Orbit(0, 0, new Vector2(0, 1000), 400, fast: false).Pitch, 5);
        Assert.Equal(-10 * across, ThnAnimationPreview.Orbit(0, 0, new Vector2(4000, 0), 400, fast: false).Yaw, 4);
    }

    /// <summary>A wheel step up and a drag to the right or up move the camera closer. Shift makes a step 4 times larger,
    /// as in the Unity animation preview. The zoom has limits.</summary>
    [Fact]
    public void TheZoomMovesCloserAndHasLimits()
    {
        Assert.True(ThnAnimationPreview.Zoom(1, 1, fast: false) < 1);
        Assert.True(ThnAnimationPreview.Zoom(1, -1, fast: false) > 1);
        Assert.Equal(ThnAnimationPreview.Zoom(1, 4, fast: false), ThnAnimationPreview.Zoom(1, 1, fast: true), 5);
        Assert.Equal(0.35f, ThnAnimationPreview.Zoom(1, 100, fast: false));
        Assert.Equal(4f, ThnAnimationPreview.Zoom(1, -100, fast: false));
        Assert.True(ThnAnimationPreview.DragZoom(new Vector2(10, 0)) > 0);
        Assert.True(ThnAnimationPreview.DragZoom(new Vector2(0, -10)) > 0);
        Assert.True(ThnAnimationPreview.DragZoom(new Vector2(-10, 10)) < 0);
    }

    /// <summary>A reload after a save gives new game data, and the models of a character can stay the same. The game data
    /// is a part of the clip of the clip preview, so the clip preview then starts the new script.</summary>
    [Fact]
    public void NewGameDataStartsTheClipAgain()
    {
        var models = new CharacterModels(null!, null, null, null, null, null);
        var data = new object();
        var clip = new PreviewClip(data, models, "Sc_walk", null);

        Assert.Equal(clip, new PreviewClip(data, models with { }, "Sc_walk", null));
        Assert.NotEqual(clip, new PreviewClip(new object(), models, "Sc_walk", null));
        Assert.NotEqual(clip, new PreviewClip(data, models, "Sc_walk", "Sc_idle"));
        Assert.NotEqual(clip, new PreviewClip(data, models, "Sc_run", null));
    }

    /// <summary>F frames the character as in the Unity animation preview: the pan and the zoom go back, and the orbit
    /// stays. <b>Reset view</b> also puts back the orbit.</summary>
    [Fact]
    public void FPutsBackThePanAndTheZoomButKeepsTheOrbit()
    {
        var moved = new PreviewView(2.5f, 0.7f, -0.3f, new Vector3(1, 2, 3));

        Assert.Equal(new PreviewView(1, 0.7f, -0.3f, Vector3.Zero), moved.Framed());
        Assert.Equal(new PreviewView(1, 0, 0, Vector3.Zero), PreviewView.Start);
    }

    /// <summary>A loop lasts as long as the longest channel, also when an object map, for example the root motion, is
    /// longer than the joint maps.</summary>
    [Fact]
    public void ALoopCoversTheJointMapsAndTheObjectMaps()
    {
        var script = new Script("motion");
        script.JointMaps.Add(new JointMap { Channel = new Channel { FrameCount = 5, Interval = 0.5f } });
        script.JointMaps.Add(new JointMap { Channel = new Channel { FrameCount = 3, Interval = 0.25f } });
        Assert.Equal(2f, ThnAnimationPreview.ClipDuration(script));

        script.ObjectMaps.Add(new ObjectMap { Channel = new Channel { FrameCount = 7, Interval = 0.5f } });
        Assert.Equal(3f, ThnAnimationPreview.ClipDuration(script));
        Assert.Equal(0.1f, ThnAnimationPreview.ClipDuration(new Script("empty")));
    }
}
