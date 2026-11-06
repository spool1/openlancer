using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The new entities of <see cref="ThnTemplates.NewEntity"/> (milestone MCP1-4): the records of each type, the engine
/// reads each one, and the checks report only a DEFORMABLE without a costume and a COMPOUND with a template and no
/// category. The script of a new cutscene keeps its three entities.
/// </summary>
[Collection(nameof(CompilerCollection))]
public sealed class ThnTemplatesTests
{
    private const string Path = "new_entities.thn";

    /// <summary>The keys of the record of an entity, in order.</summary>
    private static string[] Keys(ThnTable entity) => [.. entity.Record.Select(f => f.Key)];

    /// <summary>
    /// A script with one new entity of each type gives a script that the engine reads. The checks without game data
    /// find no error, except where the entity needs more data: a DEFORMABLE without a template and without an Actor
    /// loads no costume, and a COMPOUND with a template needs a category.
    /// </summary>
    [Theory]
    [InlineData("CAMERA", null, null)]
    [InlineData("MARKER", null, null)]
    [InlineData("DEFORMABLE", "my_costume", null)]
    [InlineData("DEFORMABLE", null, "template_name")]
    [InlineData("COMPOUND", null, null)]
    [InlineData("COMPOUND", "my_prop", "userprops.category")]
    [InlineData("LIGHT", null, null)]
    [InlineData("SOUND", "my_sound", null)]
    [InlineData("PSYS", "my_effect", null)]
    [InlineData("MOTION_PATH", null, null)]
    [InlineData("MONITOR", null, null)]
    [InlineData("SCENE", null, null)]
    public void EachNewEntityGivesAScriptThatTheEngineReads(string type, string? template, string? error)
    {
        var placed = ThnTemplates.HasPlace(type);
        var entity = ThnTemplates.NewEntity(type, "my_entity", template, placed ? new Vector3(30, 10, 0) : null,
            placed ? Matrix4x4.CreateRotationY(MathF.PI / 2) : null);
        var globals = ThnTable.FromRecord(new ThnField(ThnScriptModel.DurationGlobal, new ThnNumber(10)),
            new ThnField(ThnScriptModel.EntitiesGlobal, ThnTable.FromList(entity)),
            new ThnField(ThnScriptModel.EventsGlobal, ThnTable.Empty));
        var text = ThnWriter.Write(globals);

        var script = new ThnScript(Encoding.ASCII.GetBytes(text), include => throw new FileNotFoundException(include), Path);
        var errors = ThnChecks.Errors(Path, Evaluate(text), Evaluator);

        var read = Assert.Single(script.Entities.Values);
        Assert.Equal("my_entity", read.Name);
        Assert.Equal(template ?? "", read.Template);
        if (placed)
            Assert.Equal(new Vector3(30, 10, 0), read.Position);
        if (error == null)
            Assert.Empty(errors);
        else
            Assert.Contains($"entity \"my_entity\", {error}: The key is missing.", Assert.Single(errors).Message);
    }

    /// <summary>Each type with a place gets the position and the rotation in <c>spatialprops</c>, and the origin
    /// without a turn by default. A CAMERA gets the lens of the camera of a new cutscene, a LIGHT a white directional
    /// light, and the SCENE a zero ambient. A MONITOR has only its name and its type.</summary>
    [Fact]
    public void EachTypeGetsTheRecordsThatTheEngineNeeds()
    {
        var rotation = Matrix4x4.CreateRotationX(0.25f);
        var camera = ThnTemplates.NewEntity("CAMERA", "cam", "", new Vector3(1, 2, 3), rotation);
        var marker = ThnTemplates.NewEntity("MARKER", "mark", null, null, null);
        var light = ThnTemplates.NewEntity("LIGHT", "lamp", null, null, null);
        var compound = ThnTemplates.NewEntity("COMPOUND", "box", "my_prop", null, null);

        Assert.Equal(["entity_name", "type", "spatialprops", "cameraprops"], Keys(camera));
        Assert.Equal(ThnPose.Vector(new Vector3(1, 2, 3)), ThnPose.PositionPath.ValueIn(ThnElementKind.Entity, camera));
        Assert.Equal(ThnPose.Orient(rotation), ThnPose.OrientPath.ValueIn(ThnElementKind.Entity, camera));
        var newCamera = ThnScriptModel.From(ThnTemplates.NewScript(), ThnElementId.Counter()).EntityNamed(ThnTemplates.NewCamera)!;
        Assert.Equal(newCamera.Table["cameraprops"], camera["cameraprops"]);
        Assert.Equal(ThnPose.Vector(Vector3.Zero), ThnPose.PositionPath.ValueIn(ThnElementKind.Entity, marker));
        Assert.Equal(ThnPose.Orient(Matrix4x4.Identity), ThnPose.OrientPath.ValueIn(ThnElementKind.Entity, marker));
        Assert.Equal(["entity_name", "type", "template_name", "spatialprops"], Keys(compound));
        Assert.Null(ThnElementPaths.Get(ThnElementKind.Entity, compound, new ThnPath("userprops.category")));
        Assert.Equal(["entity_name", "type", "spatialprops", "lightprops"], Keys(light));
        Assert.Equal(ThnWriter.Value(ThnTable.FromRecord(new ThnField("type", new ThnSymbol("L_DIRECT")),
                new ThnField("on", new ThnBool(true)), new ThnField("diffuse", ThnPose.Vector(Vector3.One)),
                new ThnField("direction", ThnPose.Vector(Vector3.UnitZ)), new ThnField("range", new ThnNumber(2000)),
                new ThnField("atten", ThnPose.Vector(new Vector3(1, 0, 0.000004f))))),
            ThnWriter.Value(light["lightprops"]!));
        Assert.Equal(["entity_name", "type"], Keys(ThnTemplates.NewEntity("MONITOR", "mon", null, null, null)));
        Assert.Equal(["entity_name", "type", "ambient"], Keys(ThnTemplates.NewEntity("SCENE", "Scene", null, null, null)));
    }

    /// <summary>The script of a new cutscene has the scene, the monitor and the camera of <see cref="ThnTemplates.NewEntity"/>,
    /// with the keys in the order of the template of the M8b design.</summary>
    [Fact]
    public void TheNewScriptHasTheSceneTheMonitorAndTheCamera()
    {
        var model = ThnScriptModel.From(ThnTemplates.NewScript(), ThnElementId.Counter());

        Assert.Equal([["entity_name", "type", "ambient"], ["entity_name", "type"],
            ["entity_name", "type", "spatialprops", "cameraprops"]], model.Entities.Select(e => Keys(e.Table)));
        Assert.Equal(ThnTemplates.NewEntity("CAMERA", ThnTemplates.NewCamera, null, null, null),
            model.EntityNamed(ThnTemplates.NewCamera)!.Table);
    }

    /// <summary>An unknown type, an empty name and a place for a type without a place throw.</summary>
    [Fact]
    public void NewEntityRefusesATypeANameAndAPlaceThatDoNotFit()
    {
        Assert.Contains("ROBOT is not an entity type", Assert.Throws<ArgumentException>(
            () => ThnTemplates.NewEntity("ROBOT", "r", null, null, null)).Message);
        Assert.Contains("cannot be empty", Assert.Throws<ArgumentException>(
            () => ThnTemplates.NewEntity("MARKER", "", null, null, null)).Message);
        Assert.Contains(ThnTemplates.NoPlace("MONITOR"), Assert.Throws<ArgumentException>(
            () => ThnTemplates.NewEntity("MONITOR", "m", null, Vector3.Zero, null)).Message);
        Assert.False(ThnTemplates.HasPlace("SCENE"));
        Assert.True(ThnTemplates.HasPlace("MOTION_PATH"));
    }
}
