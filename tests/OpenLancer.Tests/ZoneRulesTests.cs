using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Runtime.Scene;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The lane zone rule has one place (<see cref="LaneZones.IsLaneZone(string, ShapeKind, Vector3)"/>). The rule of the
/// engine zones of the Scene (<see cref="ZoneRules.IsTradeLaneZone"/>) and the rule of a <c>[zone]</c> of W, which the
/// lane plans read, call it. So the three agree, also at the limits of the sizes.
/// </summary>
public sealed class ZoneRulesTests
{
    [Theory]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 999, 999, 10_000, true)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 1_000, 500, 10_000, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 500, 1_000, 10_000, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 181, 181, 2_000, true)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Box, 200, 200, 2_000, false)]
    [InlineData("Zone_Xx01_tradelane_1", ShapeKind.Box, 500, 500, 10_000, false)]
    [InlineData("Zone_Xx01_Tradelane_1", ShapeKind.Ellipsoid, 500, 500, 10_000, false)]
    public void TheEngineRuleAndTheRuleOfTheEditViewAgree(string nickname, ShapeKind shape, float x, float y, float z,
        bool lane)
    {
        var size = new Vector3(x, y, z);
        var engine = new Zone { Nickname = nickname, Shape = shape, Size = size };
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li01Path, SceneWorkspace.Li01Text + $"""

            [zone]
            nickname = {nickname}
            pos = 0, 0, 0
            shape = {shape.ToString().ToUpperInvariant()}
            size = {string.Join(", ", new[] { x, y, z }.Select(v => v.ToString(CultureInfo.InvariantCulture)))}
            """);
        var session = ws.Session("act");

        Assert.Equal(lane, LaneZones.IsLaneZone(nickname, shape, size));
        Assert.Equal(lane, ZoneRules.IsTradeLaneZone(engine));
        Assert.Equal(lane, LaneZones.IsLaneZone(session, SceneWorkspace.Zone(nickname)));
    }

    /// <summary>The rule compares the exact products of the sizes in both places.</summary>
    [Fact]
    public void BothRulesCompareTheExactProductsOfTheSizes()
    {
        var size = new Vector3(MathF.BitIncrement(1000f), 500, MathF.BitIncrement(10_000f));

        Assert.True(LaneZones.IsLaneZone("Zone_Xx01_Tradelane_1", ShapeKind.Box, size));
        Assert.True(ZoneRules.IsTradeLaneZone(new Zone { Nickname = "Zone_Xx01_Tradelane_1", Shape = ShapeKind.Box, Size = size }));
    }
}
