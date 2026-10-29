using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenLancer.Data.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The color of the mission layer (decision D557 of the M6a design) and the agent color (section 8.5 of the MCP1 design).
/// The hue of each one differs by 25 degrees or more from each other color of <see cref="EditorTheme"/> and
/// <see cref="ObjectKinds"/>. So no other meaning uses it. A grey has no hue, so the test leaves out the colors with a
/// saturation below 0.15.
/// </summary>
public sealed class ThemeColorTests
{
    private const float MinSaturation = 0.15f;

    /// <summary>The hue in degrees and the saturation of a color, as the HSV model gives them.</summary>
    internal static (float Hue, float Saturation) Hsv(Vector4 color)
    {
        var max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
        var min = MathF.Min(color.X, MathF.Min(color.Y, color.Z));
        var delta = max - min;
        var saturation = max <= 0 ? 0 : delta / max;
        if (delta <= 0)
            return (0, saturation);
        var hue = max == color.X ? (color.Y - color.Z) / delta % 6
            : max == color.Y ? (color.Z - color.X) / delta + 2
            : (color.X - color.Y) / delta + 4;
        return ((hue * 60 + 360) % 360, saturation);
    }

    /// <summary>The colors of the theme and of all kinds, without one color of the theme.</summary>
    private static IEnumerable<(string Name, Vector4 Color)> OtherColors(string except)
    {
        foreach (var field in typeof(EditorTheme).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(Vector4) && f.Name != except))
            yield return (field.Name, (Vector4)field.GetValue(null)!);
        foreach (var type in new[] { typeof(ObjectKinds) }.Concat(typeof(ObjectKinds).GetNestedTypes()))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(f => f.FieldType == typeof(ObjectKind)))
                yield return ($"{type.Name}.{field.Name}", ((ObjectKind)field.GetValue(null)!).Color);
        }
    }

    /// <summary>The mission color and the agent color each have a hue that no other color of the theme or of the kinds
    /// is near. The agent color is the only hue of the theme that is 25 degrees or more from each other color.</summary>
    [Theory]
    [InlineData(nameof(EditorTheme.MissionItem))]
    [InlineData(nameof(EditorTheme.Agent))]
    public void TheMissionColorAndTheAgentColorHaveTheirOwnHue(string own)
    {
        var (hue, saturation) = Hsv(ThemeColor(own));
        var others = OtherColors(own).ToList();

        Assert.True(saturation >= MinSaturation);
        Assert.True(others.Count > 40, $"The test found only {others.Count} colors.");
        foreach (var (name, color) in others)
        {
            var (otherHue, otherSaturation) = Hsv(color);
            if (otherSaturation < MinSaturation)
                continue;
            var distance = MathF.Abs(hue - otherHue);
            distance = MathF.Min(distance, 360 - distance);
            Assert.True(distance >= 25, $"{name} has a hue of {otherHue:0} degrees, only {distance:0} degrees from {own}.");
        }
    }

    [Theory]
    [InlineData(0xFF0000, 0)]
    [InlineData(0x00FF00, 120)]
    [InlineData(0x0000FF, 240)]
    [InlineData(0xF07AB4, 331)]
    public void TheHueOfAColor(uint rgb, float expected)
    {
        Assert.Equal(expected, MathF.Round(Hsv(EditorTheme.Rgb(rgb)).Hue));
    }

    /// <summary>Each kind of the mission layer has the mission color, so the user sees what belongs to the
    /// mission.</summary>
    [Fact]
    public void EachKindOfTheMissionLayerHasTheMissionColor()
    {
        foreach (var kind in Enum.GetValues<PlacementKind>())
            Assert.Equal(EditorTheme.MissionItem, MissionLayer.KindOf(kind).Color);
    }

    /// <summary>The violet of a prefab override (section 8.6 of the M6b design) differs in hue by 25 degrees or more from
    /// the colors of a selection, an error, a warning, a success and the mission items. So an override does not look
    /// like another state.</summary>
    [Theory]
    [InlineData(nameof(EditorTheme.Accent))]
    [InlineData(nameof(EditorTheme.Error))]
    [InlineData(nameof(EditorTheme.Warning))]
    [InlineData(nameof(EditorTheme.Success))]
    [InlineData(nameof(EditorTheme.MissionItem))]
    public void TheOverrideColorHasItsOwnHue(string other)
    {
        var (hue, saturation) = Hsv(EditorTheme.Override);
        var (otherHue, _) = Hsv(ThemeColor(other));
        var distance = MathF.Abs(hue - otherHue);
        distance = MathF.Min(distance, 360 - distance);

        Assert.True(saturation >= MinSaturation);
        Assert.True(distance >= 25, $"{other} has a hue of {otherHue:0} degrees, only {distance:0} degrees from the override color.");
    }

    /// <summary>The run color of the dry run (decision D761 of the M6c design) differs in hue by 25 degrees or more from
    /// the colors of a selection, an error, a warning, a success, the mission items, the prefab overrides and the agent
    /// marks.</summary>
    [Theory]
    [InlineData(nameof(EditorTheme.Accent))]
    [InlineData(nameof(EditorTheme.Error))]
    [InlineData(nameof(EditorTheme.Warning))]
    [InlineData(nameof(EditorTheme.Success))]
    [InlineData(nameof(EditorTheme.MissionItem))]
    [InlineData(nameof(EditorTheme.Override))]
    [InlineData(nameof(EditorTheme.Agent))]
    public void TheRunColorHasItsOwnHue(string other)
    {
        var (hue, saturation) = Hsv(EditorTheme.MissionRun);
        var (otherHue, _) = Hsv(ThemeColor(other));
        var distance = MathF.Abs(hue - otherHue);
        distance = MathF.Min(distance, 360 - distance);

        Assert.True(saturation >= MinSaturation);
        Assert.True(distance >= 25, $"{other} has a hue of {otherHue:0} degrees, only {distance:0} degrees from the run color.");
    }

    /// <summary>The violet bar of an override is a mark that is not text, so it needs a contrast of 3:1 or more against
    /// each background of a label cell (WCAG 1.4.11).</summary>
    [Theory]
    [InlineData(nameof(EditorTheme.Panel))]
    [InlineData(nameof(EditorTheme.Section))]
    [InlineData(nameof(EditorTheme.SectionHover))]
    [InlineData(nameof(EditorTheme.Field))]
    public void TheOverrideMarkHasContrastOnEachPanel(string background)
    {
        var contrast = Contrast(EditorTheme.Override, ThemeColor(background));

        Assert.True(contrast >= 3, $"The override color has a contrast of {contrast:0.00}:1 on {background}.");
    }

    /// <summary>The robot glyph of an agent edit and the pulse ring are marks that are not text, so they need a contrast
    /// of 3:1 or more against the Hierarchy and the selected row (WCAG 1.4.11).</summary>
    [Theory]
    [InlineData(nameof(EditorTheme.Panel))]
    [InlineData(nameof(EditorTheme.SectionHover))]
    [InlineData(nameof(EditorTheme.Pressed))]
    [InlineData(nameof(EditorTheme.Backdrop))]
    public void TheAgentMarkHasContrastOnEachPanel(string background)
    {
        var contrast = Contrast(EditorTheme.Agent, ThemeColor(background));

        Assert.True(contrast >= 3, $"The agent color has a contrast of {contrast:0.00}:1 on {background}.");
    }

    [Fact]
    public void TheContrastOfBlackAndWhiteIs21()
    {
        Assert.Equal(21, Contrast(EditorTheme.Rgb(0xFFFFFF), EditorTheme.Rgb(0x000000)), 2);
        Assert.Equal(1, Contrast(EditorTheme.Override, EditorTheme.Override), 2);
    }

    /// <summary>A color of the theme by its name.</summary>
    private static Vector4 ThemeColor(string name) =>
        (Vector4)typeof(EditorTheme).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>The contrast ratio of two colors from their relative luminance, as WCAG 2 gives it.</summary>
    internal static double Contrast(Vector4 a, Vector4 b)
    {
        static double Channel(float value) =>
            value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        static double Luminance(Vector4 color) =>
            0.2126 * Channel(color.X) + 0.7152 * Channel(color.Y) + 0.0722 * Channel(color.Z);

        var (light, dark) = (Luminance(a), Luminance(b));
        if (light < dark)
            (light, dark) = (dark, light);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>The highlight of the cross-links is a grey without a hue. So ten highlighted ships do not look
    /// selected. Blue means a selection or an unsaved edit (decision D537, UI rule 7).</summary>
    [Fact]
    public void TheHighlightHasNoStateColor()
    {
        Assert.True(Hsv(EditorTheme.Highlight).Saturation < MinSaturation);
        Assert.NotEqual(EditorTheme.Accent, EditorTheme.Highlight);
    }
}
