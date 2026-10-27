using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using LibreLancer;
using OpenLancer.Data.Editing;
using OpenLancer.Runtime.Scene;
using OpenLancer.Tests;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the gizmo (section 16.2 of the design). Each drag runs through <see cref="GizmoDrag"/> with the
/// matrices that ImGuizmo gives, and the save writes a patch with only the nickname and the changed key, in the number
/// format of the base file. The base file does not change.
/// </summary>
public sealed class SceneMoveRetailTests(ITestOutputHelper output)
{
    /// <summary>Each tool has another step, so a mix-up of the steps changes a result.</summary>
    private static readonly GizmoSnap Off = new(false, 100, 15, 50);

    static SceneMoveRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>
    /// A snapped move of +1,000 on X of <c>Li01_14_depot_water</c> writes exactly its nickname and its new
    /// <c>pos</c>, with integers. An undo and a save delete the patch, and a redo and a save write the same bytes.
    /// </summary>
    [Fact]
    public void ASnappedMoveOfTheDepotSavesOnlyItsPos()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();
        var snap = Off with { On = true, Move = 1000 };
        // Less than half a step at the first frame, then one step.
        float[] steps = [130.25f, 481.45f, 450.8f, 227.5f];

        var patch = DragAndSave(workspace, session, "Li01", "Object", "Li01_14_depot_water", GizmoTool.Move,
            GizmoSpace.World, snap, click => Frames(click, steps, (m, x) => ImGuizmoResults.Move(m, new Vector3(x, 0, 0))),
            "pos = -37675, 0, -27705");

        var bytes = File.ReadAllBytes(patch);
        session.Undo();
        var undone = session.Save();
        Assert.True(undone.Saved);
        Assert.Equal([patch], undone.Deleted);
        Assert.False(File.Exists(patch));
        session.Redo();
        Assert.True(session.Save().Saved);
        Assert.Equal(bytes, File.ReadAllBytes(patch));
        RetailChecks.AssertBaseIsUnchanged(baseFolder);
    }

    /// <summary>
    /// A world yaw of 15 degrees, in three frames of 5, turns <c>Li01_to_Li05</c> (<c>rotate = 0, 105, 0</c>) to
    /// <c>rotate = 0, 120, 0</c> and writes no other key. The engine's own conversion would give (180, 59.999985, 180)
    /// (rule 5 of section 9.3).
    /// </summary>
    [Fact]
    public void AWorldYawOfTheGateSavesOnlyItsRotate()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();

        DragAndSave(workspace, session, "Li01", "Object", "Li01_to_Li05", GizmoTool.Rotate, GizmoSpace.World, Off,
            click => Frames(click, [5f, 5f, 5f], (m, degrees) => ImGuizmoResults.WorldTurn(m, Vector3.UnitY, degrees)),
            "rotate = 0, 120, 0");
    }

    /// <summary>A move of +500 on X of <c>Light_Li01_Manhattan</c> writes only its new <c>pos</c>.</summary>
    [Fact]
    public void AMoveOfTheManhattanLightSavesOnlyItsPos()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();

        DragAndSave(workspace, session, "Li01", "LightSource", "Light_Li01_Manhattan", GizmoTool.Move, GizmoSpace.World,
            Off, click => [ImGuizmoResults.Move(click, new Vector3(500, 0, 0))], "pos = -33020, 0, -30946");
    }

    /// <summary>
    /// The X handle of the ring <c>Zone_Br03_ross_ring</c> (<c>size = 3000, 700, 200</c>, <c>rotate = -29, 16, 3</c>)
    /// doubled, in three frames, writes the outer radius 6000. The inner radius and the height keep their values, also
    /// with the float noise of the rotated axes.
    /// </summary>
    [Fact]
    public void TheXHandleOfTheRingDoubledSavesItsOuterRadius()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();

        DragAndSave(workspace, session, "Br03", "zone", "Zone_Br03_ross_ring", GizmoTool.Scale, GizmoSpace.Local, Off,
            click => Frames(click, [1.4f, 1.8f, 2f], (m, x) => ImGuizmoResults.Scale(click, m, new Vector3(x, 1, 1))),
            "size = 6000, 700, 200");
    }

    /// <summary>
    /// The height handle of the cylinder <c>Zone_Li01_path_outcasts1_1</c> (<c>size = 750, 13088</c>,
    /// <c>rotate = -90, -46, 0</c>) times 1.25 writes the height 16360. ImGuizmo gives the length of the height axis
    /// with float noise, and without the rounding of the ratio the height would be 16360.001.
    /// </summary>
    [Fact]
    public void TheHeightHandleOfTheOutcastsCylinderSavesItsHeight()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();

        DragAndSave(workspace, session, "Li01", "zone", "Zone_Li01_path_outcasts1_1", GizmoTool.Scale, GizmoSpace.Local,
            Off, click => [ImGuizmoResults.Scale(click, click, new Vector3(1, 1.25f, 1))], "size = 750, 16360");
    }

    /// <summary>A scale of the sphere <c>Zone_Li01_Manhattan_death</c> (<c>size = 4100</c>) by 1.5 writes its one size
    /// value.</summary>
    [Fact]
    public void AScaleOfTheSphereSavesItsOneSizeValue()
    {
        var baseFolder = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(baseFolder, "m2proof");
        var session = workspace.Open();

        DragAndSave(workspace, session, "Li01", "zone", "Zone_Li01_Manhattan_death", GizmoTool.Scale, GizmoSpace.Local,
            Off, click => [ImGuizmoResults.Scale(click, click, new Vector3(1.5f, 1, 1))], "size = 6150");
    }

    /// <summary>
    /// A frame of a drag of <c>Li01_14_depot_water</c> uses only a small part of one frame of the editor at 60 frames
    /// each second. A step of the gesture does not make the report of its plan. The report reads the names of all
    /// systems again after each change. That took about 12 ms for each frame (review finding of MCP1-2 step 1). The
    /// gate takes the mean of 300 frames as one try.
    /// </summary>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void AFrameOfADragFitsTheGate()
    {
        const int frames = 300;
        var limit = TimeSpan.FromMilliseconds(5);
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "dragframe");
        var session = workspace.Open();
        var section = new SectionRef(session.SystemPath("Li01")!, new SectionIdentity("Object", "Li01_14_depot_water"));
        var target = GizmoTarget.Of(session, section, null, out var reason);
        Assert.True(target != null, reason);
        var click = target.Matrix(GizmoTool.Move);

        TimeSpan Frame()
        {
            using var drag = TestGestures.Drag(session, target, GizmoTool.Move, GizmoSpace.World);
            var time = Timing.Measure(() =>
            {
                for (var i = 1; i <= frames; i++)
                    drag.Update(click * Matrix4x4.CreateTranslation(i, 0, 0), Off);
            });
            drag.Cancel();
            return time / frames;
        }

        Frame();
        var frame = new TimingGates().Gate(limit, Frame, tries: 3);
        output.WriteLine($"{frame.TotalMilliseconds:0.000} ms for each frame of a drag");
        Assert.True(frame < limit, $"{frame.TotalMilliseconds:0.000} ms for each frame of a drag");
        Assert.False(session.CanUndo);
        Assert.False(session.IsDirty);
    }

    /// <summary>
    /// Drags a section of a retail system through <see cref="GizmoDrag"/> as one entry, then saves. The patch file has
    /// the letter case of the base file (rule P8). It holds the section header as the base file spells it, the nickname
    /// and the new line, with CRLF. The base file does not change.
    /// </summary>
    /// <param name="frames">The raw matrices of the frames, from the matrix that the gizmo holds at the click: the
    /// matrix of the target for the tool. The drag must start from the same matrix.</param>
    /// <returns>The patch file.</returns>
    private string DragAndSave(RetailWorkspace workspace, EditSession session, string system, string type,
        string nickname, GizmoTool tool, GizmoSpace space, GizmoSnap snap,
        Func<Matrix4x4, IEnumerable<Matrix4x4>> frames, string line)
    {
        var path = session.SystemPath(system)!;
        var section = new SectionRef(path, new SectionIdentity(type, nickname));
        var baseFile = Path.Combine(GameDataPaths.RequireBase(), path);
        var baseHash = Sha256(baseFile);
        var target = GizmoTarget.Of(session, section, null, out var reason);
        Assert.True(target != null, reason);
        var click = target.Matrix(tool);

        using (var drag = TestGestures.Drag(session, target, tool, space, null))
        {
            Assert.Equal(click, drag.Start);
            foreach (var frame in frames(click))
                drag.Update(frame, snap);
            drag.End();
        }
        Assert.Equal($"{tool} {nickname}", session.UndoLabel);
        var saved = session.Save();

        Assert.True(saved.Saved);
        Assert.Empty(saved.Problems);
        var patch = Assert.Single(saved.Written);
        Assert.Equal($@"patches\DATA\Universe\Systems\{system}\{system}.ini", Path.GetRelativePath(workspace.ModFolder, patch));
        var bytes = File.ReadAllBytes(patch);
        output.WriteLine(Encoding.UTF8.GetString(bytes));
        Assert.Equal(Encoding.ASCII.GetBytes($"[{type}]\r\nnickname = {nickname}\r\n{line}\r\n"), bytes);
        Assert.Equal(baseHash, Sha256(baseFile));
        return patch;
    }

    /// <summary>The raw matrices of a drag: each frame changes the raw matrix of the frame before, as the editor gives
    /// it to the gizmo.</summary>
    private static IEnumerable<Matrix4x4> Frames(Matrix4x4 start, IEnumerable<float> steps, Func<Matrix4x4, float, Matrix4x4> frame)
    {
        var current = start;
        foreach (var step in steps)
            yield return current = frame(current, step);
    }

    private static string Sha256(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
}
