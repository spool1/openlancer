using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// Edit sessions on a profile with a cutscene script, for the tests of the cutscene document, the THN edits and the
/// save. The base folder has the script and the INI files of <see cref="EditSessionTests"/>.
/// </summary>
internal static class ThnSessions
{
    internal const string ScriptPath = ModOverlayThnTests.ScriptPath;
    internal const string PatchFile = ModOverlayThnTests.PatchFile;
    internal const string WholeFile = @"files\" + ScriptPath;

    /// <summary>A patch of the script with an error: its entity entry matches no entity. It makes the cutscene
    /// read-only, but the session still takes edits.</summary>
    internal const string BrokenPatch = """
        ol_thn_patch = 1
        entities = { { ol_match = "Nobody", ol_set = { ["cameraprops.fovh"] = 20 } } }
        """;

    /// <summary>
    /// A character with an Actor value that is its own name, two sounds whose speaker names it in other letter cases,
    /// events that name it in other letter cases, and two events with one key.
    /// </summary>
    internal const string Script = """
        duration = 10
        entities = {
            { entity_name = "Cam_A", type = CAMERA, cameraprops = { fovh = 10, hvaspect = 1.5, nearplane = 1, farplane = 100 }, spatialprops = { pos = { 0, 0, 0 }, orient = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } } } },
            { entity_name = "Monitor", type = MONITOR },
            { entity_name = "Char_trent", type = DEFORMABLE, userprops = { Actor = "Char_trent", category = "Char" } },
            { entity_name = "Voice_trent", type = SOUND, userprops = { speaker = "char_TRENT" } },
            { entity_name = "Voice_other", type = SOUND, userprops = { Speaker = "Char_trent", note = "Char_trent" } },
            { entity_name = "Fx", type = PSYS, psysprops = {} }
        }
        events = {
            { 0, SET_CAMERA, { "Monitor", "Cam_A" } },
            { 5, START_PSYS, { "Fx" }, { duration = 2 } },
            { 5, START_PSYS, { "Fx" }, { duration = 3 } },
            { 6, START_MOTION, { "Char_Trent" }, { animation = "Sc_X", duration = 2 } },
            { 6, START_SOUND, { "Voice_trent" }, { duration = 1.5 } },
            { 6, ATTACH_ENTITY, { "Cam_A", "char_trent" } }
        }
        """;

    /// <summary>A workspace with the base files, the lower mod "low" when it has files, the active mod "act" with
    /// these files, and the profile.</summary>
    public static TestWorkspace Workspace(Dictionary<string, string>? activeFiles = null, string script = Script,
        Dictionary<string, string>? lowFiles = null)
    {
        var ws = new TestWorkspace();
        ws.WriteBase(EditSessionTests.GoodsPath, EditSessionTests.Goods);
        ws.WriteBase(ScriptPath, script);
        if (lowFiles != null)
            ws.Mod("low", lowFiles);
        ws.Mod("act", activeFiles ?? new());
        if (lowFiles != null)
            ws.WriteProfile("low", "act");
        else
            ws.WriteProfile("act");
        return ws;
    }

    /// <summary>W of the script.</summary>
    public static ThnScriptModel W(EditSession session) => session.Cutscene(ScriptPath)!;

    /// <summary>The entity of W with a name.</summary>
    public static ThnRef Entity(EditSession session, string name) =>
        new(ScriptPath, ThnElementKind.Entity, W(session).Entities.Single(e => e.Table["entity_name"] == new ThnText(name)).Id);

    /// <summary>The event at an index of the events of W.</summary>
    public static ThnRef Event(EditSession session, int index) => new(ScriptPath, ThnElementKind.Event, W(session).Events[index].Id);

    /// <summary>The value of an element of W at a path.</summary>
    public static ThnValue? Value(EditSession session, ThnRef target, string path) =>
        ThnElementPaths.Get(target.Kind, Table(session, target), new ThnPath(path));

    public static ThnTable Table(EditSession session, ThnRef target) =>
        W(session).Elements(target.Kind).Single(e => e.Id == target.Id).Table;

    /// <summary>The patch file of the active mod, or null when it does not exist.</summary>
    public static string? PatchText(TestWorkspace ws)
    {
        var file = Path.Combine(ws.ModFolder("act"), PatchFile);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>The patch file of the active mod as a patch.</summary>
    public static ThnPatch Patch(TestWorkspace ws) =>
        ThnPatch.Read(File.ReadAllBytes(Path.Combine(ws.ModFolder("act"), PatchFile)), PatchFile, ThnScripts.Evaluator);

    public static void AssertSaved(SaveResult result)
    {
        Assert.True(result.Saved, string.Join("\n", result.Problems));
        Assert.Empty(result.Problems);
    }
}
