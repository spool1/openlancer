using System;
using System.IO;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The host without a window with edits (section 6.2 and section 13.2, items 6 and 7 of the MCP1 design). The tests
/// cover the lock of the mod and the recovery file 2 seconds after the last edit and at the close. They also cover a
/// waiting recovery file, a mod in the OpenLancer repo, a profile in a game folder, <c>--no-save</c> and the replay
/// with <c>--preview</c>.
/// </summary>
public sealed class HeadlessHostTests
{
    private const string Move = """{ "ref": "obj:Li01_01", "fields": { "pos": [10, 20, 30] } }""";
    private const string MoveAgain = """{ "ref": "obj:Li01_01", "fields": { "pos": [40, 50, 60] } }""";

    private static string Recovery(TestWorkspace ws) => RecoveryFile.PathOf(ws.ModFolder("act"));

    private static string? Line(EditSession session, string key) =>
        AgentEditToolTests.Line(session, SceneWorkspace.Object("Li01_01"), key);

    /// <summary>The host takes the lock of the mod and edits. It writes the recovery file 2 seconds after the last edit,
    /// and again when it closes. A new session then finds the edits.</summary>
    [Fact]
    public void AnEditGoesIntoTheRecoveryFileTwoSecondsLaterAndAtTheClose()
    {
        using var ws = SceneWorkspace.Create();
        var clock = new FakeClock();
        var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { Clock = clock });

        var before = host.Idle();
        Ok(host, "set_fields", Move);
        var wait = host.Idle();
        clock.Advance(TimeSpan.FromSeconds(1.5));
        var shorter = host.Idle();
        var early = File.Exists(Recovery(ws));
        clock.Advance(TimeSpan.FromSeconds(0.5));
        var written = host.Idle();
        var first = File.Exists(Recovery(ws));
        Ok(host, "set_fields", MoveAgain);
        var holds = host.Session.HoldsLock;
        host.Dispose();
        var session = ws.Session("act");
        var restored = session.RestoreRecovery();

        Assert.True(holds);
        Assert.Null(before);
        Assert.Equal(TimeSpan.FromSeconds(2), wait);
        Assert.Equal(TimeSpan.FromSeconds(0.5), shorter);
        Assert.False(early);
        Assert.Null(written);
        Assert.True(first);
        Assert.Empty(restored);
        Assert.Equal("40, 50, 60", Line(session, "pos"));
    }

    /// <summary>A replay keeps no unsaved edits, so its host writes no recovery file.</summary>
    [Fact]
    public void AHostWithoutTheRecoveryWritesNoRecoveryFile()
    {
        using var ws = SceneWorkspace.Create();
        var clock = new FakeClock();

        using (var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { Clock = clock, WritesRecovery = false }))
        {
            Ok(host, "set_fields", Move);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Null(host.Idle());
        }

        Assert.False(File.Exists(Recovery(ws)));
    }

    /// <summary>A recovery file of an earlier session stops the edits until the user decides (section 6.2 of the MCP1
    /// design). <c>--discard-recovery</c> deletes it, and <c>--restore-recovery</c> restores its edits as one Undo
    /// step.</summary>
    [Fact]
    public void AWaitingRecoveryFileStopsTheEditsUntilTheUserDecides()
    {
        using var ws = SceneWorkspace.Create();
        void Earlier()
        {
            using var earlier = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions());
            Ok(earlier, "set_fields", Move);
        }

        Earlier();
        var messages = new StringWriter();
        string? kept;
        (string? Code, string? Detail, string? Message) refused;
        using (var keep = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { Messages = messages }))
        {
            kept = keep.NoEditReason;
            refused = Error(keep, "set_fields", MoveAgain);
        }
        using (var discard = HeadlessHost.Open(ws.ProfilePath, "act",
                   new HeadlessOptions { Recovery = RecoveryChoice.Discard, Messages = messages }))
        {
            Assert.Null(discard.NoEditReason);
            Assert.False(File.Exists(Recovery(ws)));
            Assert.Null(Line(discard.Session, "pos"));
        }
        Earlier();
        using (var restore = HeadlessHost.Open(ws.ProfilePath, "act",
                   new HeadlessOptions { Recovery = RecoveryChoice.Restore, Messages = messages }))
        {
            Assert.Null(restore.NoEditReason);
            Assert.Equal("10, 20, 30", Line(restore.Session, "pos"));
            Assert.Equal("Restore unsaved edits", restore.Session.UndoLabel);
            // The restored edits are unsaved edits of this session, so the host keeps them in a new recovery file.
            Assert.InRange(restore.Idle()!.Value, TimeSpan.FromTicks(1), HeadlessHost.RecoveryDelay);
        }

        Assert.Equal("The mod act has unsaved edits of an earlier session in its recovery file. Restore them in the " +
                     "editor, or start the server with --restore-recovery or --discard-recovery.", kept);
        Assert.Equal(("read_only", "NotEditable", kept), refused);
        Assert.Contains("The server deleted the unsaved edits of the earlier session.", messages.ToString());
        Assert.Contains("The server restored the unsaved edits of the earlier session. Save them to keep them.",
            messages.ToString());
    }

    /// <summary>A mod in the OpenLancer repo opens read-only and without the lock (section 13.2, item 7 and decision
    /// D738 of the MCP1 design). The editor can still edit it.</summary>
    [Fact]
    public void ARepoModOpensWithoutTheLockAndGivesReadOnly()
    {
        using var ws = SceneWorkspace.Create();
        File.WriteAllText(Path.Combine(ws.Root, RepoCheck.MarkerFile), "");

        using var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions());
        var editor = ws.Session("act");

        const string Text = "The mod act is part of the OpenLancer repo. The server without a window cannot change it. " +
                            "Use the agent server of the editor.";
        Assert.Equal(("read_only", "NotEditable", Text), Error(host, "set_fields", Move));
        Assert.False(host.Session.HoldsLock);
        Assert.True(editor.HoldsLock);
        Assert.Equal(AgentTools.All, host.Tools);
    }

    /// <summary>A profile in a game folder opens read-only, as the session does in the editor (section 8.2, item 7 of
    /// the MCP1 design). The host writes nothing into the game folder.</summary>
    [Fact]
    public void AProfileInAGameFolderGivesReadOnly()
    {
        using var ws = SceneWorkspace.Create();
        var inside = Path.Combine(ws.Base, "inside.json");
        File.WriteAllText(inside, """{ "base": ".", "mods": [ "../mods/act" ] }""");

        using var host = HeadlessHost.Open(inside, "act", new HeadlessOptions());

        Assert.Equal(("read_only", "NotEditable", GameTexts.ProfileInGame), Error(host, "set_fields", Move));
        Assert.Equal(("read_only", "NotEditable", GameTexts.ProfileInGame), Error(host, "save"));
        Assert.False(host.Session.HoldsLock);
    }

    /// <summary>When an editor has the mod open, the host takes no edits (section 5.4 of the MCP1 design). The reason
    /// names the agent server of that editor.</summary>
    [Fact]
    public void AModThatAnEditorHasOpenGivesReadOnlyWithTheServerOfTheEditor()
    {
        using var ws = SceneWorkspace.Create();
        var editor = ws.Session("act");

        using var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions());

        Assert.True(editor.HoldsLock);
        Assert.StartsWith("Another editor has mod 'act' open", host.NoEditReason);
        Assert.EndsWith("Or use the agent server of the editor that has the mod open.", host.NoEditReason);
        Assert.Equal("read_only", Error(host, "set_fields", Move).Code);
    }

    /// <summary><c>--no-save</c> leaves out <c>save</c> and <c>export_mod</c> (section 6.2 of the MCP1 design). The edit
    /// tools stay.</summary>
    [Fact]
    public void NoSaveLeavesOutSaveAndExport()
    {
        using var ws = SceneWorkspace.Create();

        using var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { NoSave = true });

        Assert.Equal(AgentTools.All.Where(t => t.Name is not ("save" or "export_mod")), host.Tools);
        Assert.Contains(host.Tools, t => t.Name == "set_fields");
        Assert.Contains(host.Tools, t => t.Name == "publish_checklist");
    }

    /// <summary>A replay runs the edits on its profile and saves at a recorded save (section 6.2 of the MCP1 design).
    /// With <c>--preview</c>, each edit runs as a preview, and the replay skips the save. It then changes no
    /// file.</summary>
    [Fact]
    public void AReplaySavesAtARecordedSaveAndAPreviewReplayChangesNothing()
    {
        using var ws = SceneWorkspace.Create();
        var before = TestWorkspace.Files(ws.ModFolder("act"));
        var log = $$$"""
            {"tool": "set_fields", "arguments": {{{Move}}}, "result": {"status": "ok"}}
            {"tool": "save", "arguments": {}, "result": {"status": "ok"}}
            {"tool": "undo", "arguments": {}, "result": {"status": "ok"}}
            """;

        var previewOutput = new StringWriter();
        string? previewDifference;
        using (var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { WritesRecovery = false }))
        {
            previewDifference = AgentReplay.Run(host, new StringReader(log), previewOutput, preview: true);
            Assert.False(host.Session.IsDirty);
        }
        var afterPreview = TestWorkspace.Files(ws.ModFolder("act"));
        var output = new StringWriter();
        string? difference;
        using (var host = HeadlessHost.Open(ws.ProfilePath, "act", new HeadlessOptions { WritesRecovery = false }))
            difference = AgentReplay.Run(host, new StringReader(log), output);

        Assert.Null(previewDifference);
        Assert.Equal(before, afterPreview);
        var lines = previewOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => System.Text.Json.Nodes.JsonNode.Parse(l)!).ToList();
        Assert.Equal("previewed", (string?)lines[0]["content"]!["status"]);
        Assert.Equal(["skipped", "skipped"], lines.Skip(1).Select(l => (string?)l["result"]!["status"]));
        Assert.Null(difference);
        Assert.Contains("pos = 10, 20, 30", ws.ReadModPatch(SceneWorkspace.Li01Path));
        Assert.False(File.Exists(Recovery(ws)));
    }
}
