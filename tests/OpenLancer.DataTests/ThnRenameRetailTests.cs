using System;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data.IO;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The rename list on retail data (test 13.2 item 8 of the M8 design): <c>Camera_0</c> of the set script of the
/// Manhattan cityscape is the <c>[Camera] name</c> of its room file, so a rename lists that line first.
/// </summary>
public sealed class ThnRenameRetailTests(ITestOutputHelper output)
{
    private const string SetScript = @"DATA\Scripts\Bases\Li_01_cityscape_hardpoint_01.thn";

    static ThnRenameRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRenameListOfCamera0OfTheCityscapeSetHoldsTheCameraLineOfTheRoom()
    {
        var view = FileSystem.FromPath(GameDataPaths.RequireBase());
        var references = ThnReferences.Scan(view);
        var evaluator = ThornEvaluator.Instance;

        var lines = references.EntityNameUses(SetScript, "Camera_0");
        var list = CutsceneRename.Uses(references, SetScript, "Camera_0",
            path => ThnScriptModel.From(evaluator.Evaluate(view.ReadAllBytes(path), path), ThnElementId.Counter()),
            evaluator.Symbols);

        foreach (var use in list)
            output.WriteLine($"{use.File}: {use.Text}");
        var camera = Assert.Single(lines, l => l.Section.Equals("Camera", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Li01_01_cityscape.ini", Path.GetFileName(camera.File), ignoreCase: true);
        Assert.Equal(("name", "Camera_0"), (camera.Key, camera.Value));
        Assert.Equal("Li01_01_cityscape.ini [Camera] name = Camera_0", list[0].Text, ignoreCase: true);
        // The set script has the entity, and a rename of another file never changes this file.
        var set = ThnScriptModel.From(evaluator.Evaluate(view.ReadAllBytes(SetScript), SetScript), ThnElementId.Counter());
        Assert.NotNull(set.EntityNamed("CAMERA_0"));
        // s002xe plays in the room as a cutscene, not as its set or scene script, so its names have no such use.
        Assert.Empty(references.EntityNameUses(ThnPatchRetailTests.S002xe, "Camera_0"));
    }
}
