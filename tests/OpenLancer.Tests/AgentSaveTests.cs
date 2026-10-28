using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The tool <c>save</c> of section 7.6 of the MCP1 design in the host without a window: the save gate, then the save
/// of the session. The tests cover the saved patch files, <c>save_blocked</c> with the messages of the gate,
/// <c>changed_on_disk</c>, <c>read_only</c>, <c>paused</c> and <c>busy</c>. <c>AgentControllerTests</c> covers the
/// save of the editor host with its validation.
/// </summary>
public sealed class AgentSaveTests
{
    private const string Move = """{ "ref": "obj:Li01_01", "fields": { "pos": [10, 20, 30] } }""";

    private static readonly string PatchFile = Path.Combine(Mod.PatchesFolder, SceneWorkspace.Li01Path).Replace('/', '\\');

    [Fact]
    public void ASaveWritesThePatchFilesAndGivesThemRelativeToTheMod()
    {
        var (ws, session, host) = AgentEditToolTests.Scene();
        using var _ = ws;
        Ok(host, "set_fields", Move);

        var saved = Ok(host, "save");
        var again = Ok(host, "save");

        Assert.True((bool)saved["saved"]!);
        Assert.Equal(1, (int)saved["number"]!);
        Assert.Equal([PatchFile], saved["written"]!.AsArray().Select(f => (string?)f));
        Assert.Empty(saved["deleted"]!.AsArray());
        Assert.False((bool)saved["incomplete"]!);
        Assert.Null(saved["validation"]);
        Assert.Contains("pos = 10, 20, 30", ws.ReadModPatch(SceneWorkspace.Li01Path));
        Assert.False(session.IsDirty);
        // A save does not change the revision (section 8.6, item 3 of the MCP1 design).
        Assert.Equal("1", (string?)saved["rev"]);
        Assert.Equal(1, session.Revision);
        // A save without unsaved edits saves and changes no file.
        Assert.True((bool)again["saved"]!);
        Assert.Equal(0, (int)again["number"]!);
        Assert.Empty(again["written"]!.AsArray());
        // A save is no edit of the edit view, so the host gets no edit for its Console.
        Assert.Equal(["set_fields"], host.Edits.Select(e => e.Tool));
    }

    /// <summary>A save that the save gate refuses gives <c>save_blocked</c> with the messages of the gate, and writes
    /// nothing. The edit stays in the edit view.</summary>
    [Fact]
    public void ASaveWithAnErrorGivesSaveBlockedWithTheMessageOfTheGate()
    {
        var (ws, session, host) = AgentEditToolTests.Scene();
        using var _ = ws;
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "loadout": "lo_missing" } }""");
        var gate = session.GateRefusal()!.Problems.Where(p => p.Severity == DiagnosticSeverity.Error).ToList();

        var result = Call(host, "save");

        Assert.True(result.IsError);
        Assert.Equal("save_blocked", (string?)result.Content["code"]);
        Assert.Equal("Gate", (string?)result.Content["detail"]);
        var error = Assert.Single(gate);
        Assert.Equal($"The save stopped. 1 error blocks it. Correct it, then save again. {error.Message}",
            (string?)result.Content["message"]);
        Assert.Equal("obj:Li01_01", (string?)result.Content["target"]);
        Assert.Equal(before, TestWorkspace.Files(ws.ModFolder("act")));
        Assert.True(session.IsDirty);
        Assert.Equal(("save", "save_blocked"), (host.Refusals.Single().Tool, Snake(host.Refusals.Single().Error.Kind)));
    }

    /// <summary>More errors than the message names: the message names the first three and tells the agent to call
    /// <c>check</c>.</summary>
    [Fact]
    public void AMessageOfSaveBlockedNamesTheFirstErrorsAndTellsToCallCheck()
    {
        var (ws, session, host) = AgentEditToolTests.Scene();
        using var _ = ws;
        foreach (var nickname in new[] { "Li01_01", "Li01_dock", "Li01_child", "Li01_grandchild" })
            Ok(host, "set_fields", $$"""{ "ref": "obj:{{nickname}}", "fields": { "loadout": "lo_missing" } }""");
        var errors = session.GateRefusal()!.Problems.Where(p => p.Severity == DiagnosticSeverity.Error).ToList();

        var (code, _, message) = Error(host, "save");

        Assert.Equal("save_blocked", code);
        Assert.Equal(4, errors.Count);
        Assert.Equal($"The save stopped. 4 errors block it. Correct them, then save again. " +
                     $"{string.Join(" ", errors.Take(3).Select(e => e.Message))} Call check to read all errors.", message);
    }

    /// <summary>A file of the mod that changed outside the host stops the save with <c>changed_on_disk</c>. The agent
    /// never replaces the file (section 7.6 of the MCP1 design).</summary>
    [Fact]
    public void AFileThatChangedOutsideTheHostGivesChangedOnDiskAndStays()
    {
        var (ws, session, host) = AgentEditToolTests.Scene();
        using var _ = ws;
        Ok(host, "set_fields", Move);
        Ok(host, "save");
        Ok(host, "set_fields", """{ "ref": "obj:Li01_01", "fields": { "pos": [40, 50, 60] } }""");
        var file = Path.Combine(ws.ModFolder("act"), Mod.PatchesFolder, SceneWorkspace.Li01Path);
        const string Outside = "[Object]\nnickname = Li01_01\npos = 7, 8, 9\n";
        File.WriteAllText(file, Outside);

        var (code, detail, message) = Error(host, "save");

        Assert.Equal(("changed_on_disk", "ChangedOnDisk"), (code, detail));
        Assert.Equal($"Files of the mod changed outside the server: {PatchFile}. The server does not replace them, so " +
                     "the save wrote nothing. Ask the user to open the mod in the editor.", message);
        Assert.Equal(Outside, File.ReadAllText(file));
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void ASaveOfASessionThatTakesNoEditsGivesReadOnlyAndPauseGivesPaused()
    {
        using var ws = SceneWorkspace.Create();
        using var reading = EditSession.OpenReadOnly(ModProfile.Load(ws.ProfilePath), "act",
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator, "Only to read.");
        var paused = new TestAgentHost(ws.Session("act")) { TestPaused = true };

        Assert.Equal(("read_only", "NotEditable", "Only to read."), Error(new TestAgentHost(reading), "save"));
        Assert.Equal("paused", Error(paused, "save").Code);
    }

    /// <summary>A batch or a transaction of the session stops a save. The tool then gives <c>busy</c>.</summary>
    [Fact]
    public void ASaveWhileABatchIsOpenGivesBusy()
    {
        var (ws, session, host) = AgentEditToolTests.Scene();
        using var _ = ws;
        Ok(host, "set_fields", Move);

        using (session.BeginBatch("Open", EditOrigin.User))
            Assert.Equal("busy", Error(host, "save").Code);

        Assert.True((bool)Ok(host, "save")["saved"]!);
    }

    private static string Snake(Data.Editing.Commands.EditErrorKind kind) =>
        System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString());
}
