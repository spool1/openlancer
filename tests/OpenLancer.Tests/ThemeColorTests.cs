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
/// The color of the mission layer (decision D557 of the M6a design). Its hue differs by 25 degrees or more from each
/// color of <see cref="EditorTheme"/> and <see cref="ObjectKinds"/>. So no other meaning uses it. A grey has no hue, so
/// the test leaves out the colors with a saturation below 0.15.
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

    /// <summary>The colors of the theme and of all kinds, without the mission color.</summary>
    private static IEnumerable<(string Name, Vector4 Color)> OtherColors()
    {
        foreach (var field in typeof(EditorTheme).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(Vector4) && f.Name != nameof(EditorTheme.MissionItem)))
            yield return (field.Name, (Vector4)field.GetValue(null)!);
        foreach (var type in new[] { typeof(ObjectKinds) }.Concat(typeof(ObjectKinds).GetNestedTypes()))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(f => f.FieldType == typeof(ObjectKind)))
                yield return ($"{type.Name}.{field.Name}", ((ObjectKind)field.GetValue(null)!).Color);
        }
    }

    [Fact]
    public void TheMissionColorHasItsOwnHue()
    {
        var (hue, saturation) = Hsv(EditorTheme.MissionItem);
        var others = OtherColors().ToList();

        Assert.True(saturation >= MinSaturation);
        Assert.True(others.Count > 40, $"The test found only {others.Count} colors.");
        foreach (var (name, color) in others)
        {
            var (otherHue, otherSaturation) = Hsv(color);
            if (otherSaturation < MinSaturation)
                continue;
            var distance = MathF.Abs(hue - otherHue);
            distance = MathF.Min(distance, 360 - distance);
            Assert.True(distance >= 25, $"{name} has a hue of {otherHue:0} degrees, only {distance:0} degrees from the mission color.");
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
    /// the colors of a selection, an error, a warning, a success, the mission items and the prefab overrides. The theme
    /// has no agent color yet, so the test cannot check it.</summary>
    [Theory]
    [InlineData(nameof(EditorTheme.Accent))]
    [InlineData(nameof(EditorTheme.Error))]
    [InlineData(nameof(EditorTheme.Warning))]
    [InlineData(nameof(EditorTheme.Success))]
    [InlineData(nameof(EditorTheme.MissionItem))]
    [InlineData(nameof(EditorTheme.Override))]
    public void TheRunColorHasItsOwnHue(string other)
    {
        var (hue, saturation) = Hsv(EditorTheme.MissionRun);
        var (otherHue, _) = Hsv(ThemeColor(other));
        var distance = MathF.Abs(hue - otherHue);
        distance = MathF.Min(distance, 360 - distance);

        Assert.True(saturation >= MinSaturation);
        Assert.True(distance >= 25, $"{other} has a hue of {otherHue:0} degrees, only {distance:0} degrees from the run color.");
        // When the theme gets the agent color of MCP1, this test must check it too (decision D761).
        Assert.True(typeof(EditorTheme).GetField("Agent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) == null,
            "Add the agent color to the cases of this test.");
    }

    /// <summary>The green of the Play button of a trigger node (note of 2026-10-27 in section 8.1 of the M6c design)
    /// differs in hue by 25 degrees or more from the run states of the dry run and the live view, a selection, a warning,
    /// an error, the mission items and the prefab overrides. So the button does not look like a run state.</summary>
    [Theory]
    [InlineData(nameof(EditorTheme.MissionRun))]
    [InlineData(nameof(EditorTheme.Accent))]
    [InlineData(nameof(EditorTheme.Warning))]
    [InlineData(nameof(EditorTheme.Error))]
    [InlineData(nameof(EditorTheme.MissionItem))]
    [InlineData(nameof(EditorTheme.Override))]
    public void ThePlayColorHasItsOwnHue(string other)
    {
        var (hue, saturation) = Hsv(EditorTheme.Play);
        var (otherHue, _) = Hsv(ThemeColor(other));
        var distance = MathF.Abs(hue - otherHue);
        distance = MathF.Min(distance, 360 - distance);

        Assert.True(saturation >= MinSaturation);
        Assert.True(distance >= 25, $"{other} has a hue of {otherHue:0} degrees, only {distance:0} degrees from the Play color.");
    }

    /// <summary>The Play glyph is a mark that is not text, so it needs a contrast of 3:1 or more (WCAG 1.4.11): on the
    /// node header, on the fill under the mouse (the green at alpha 0.25 over the header), and as the dark glyph on the
    /// green fill of a press.</summary>
    [Fact]
    public void ThePlayButtonHasContrastInEachState()
    {
        var header = EditorTheme.GraphNodeHeader;
        var hover = Vector4.Lerp(header, EditorTheme.Play, 0.25f) with { W = 1 };

        Assert.True(Contrast(EditorTheme.Play, header) >= 3, $"Normal: {Contrast(EditorTheme.Play, header):0.00}:1.");
        Assert.True(Contrast(EditorTheme.Play, hover) >= 3, $"Under the mouse: {Contrast(EditorTheme.Play, hover):0.00}:1.");
        Assert.True(Contrast(header, EditorTheme.Play) >= 3, $"Pressed: {Contrast(header, EditorTheme.Play):0.00}:1.");
    }

    /// <summary>The warning chip of a graph line and of the play start is the grey chip with the warning color for its
    /// text.</summary>
    [Fact]
    public void TheWarningChipIsTheGreyChipWithWarningText()
    {
        Assert.Equal(ChipLook.Grey with { Text = EditorTheme.Warning }, ChipLook.GreyWarning);
        Assert.NotEqual(ChipLook.Grey.Text, ChipLook.GreyWarning.Text);
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
