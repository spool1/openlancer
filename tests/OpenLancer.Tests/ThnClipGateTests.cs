using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Inspector;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ThnSessions;

namespace OpenLancer.Tests;

/// <summary>
/// The save gate checks the clip of a START_MOTION event on a character with the list <c>animation</c> of W (#19,
/// section 12.1 of the M8b design, decisions D96 and D120). A clip that the game does not have blocks Save and Play
/// with the reason. An empty clip names no clip, and a clip without its key crashes the game. A clip on a COMPOUND
/// names a script of its model, so it gets no check. An unsaved <c>[Animations]</c> line counts, and an undo heals. The
/// clip field shows a clip that the gate blocks as an error.
/// </summary>
public sealed class ThnClipGateTests
{
    private const string Typo = "event { 1, START_MOTION, { \"Man\" } }, props.animation: The game has no animation " +
                                "\"Sc_Typo\". The character does not move.";

    private static string[] Errors(EditSession session) => [.. session.CutsceneErrors(ScriptPath).Select(e => e.Message)];

    private static void SetClip(EditSession session, int index, string clip) =>
        session.Execute(ThnEdits.Set(Event(session, index), new ThnPath(ThnTemplateRules.AnimationPath), new ThnText(clip)));

    /// <summary>True when the clip field of the event shows the problem of the gate as an error.</summary>
    private static bool FieldBlocked(EditSession session, ThnRef ev) =>
        ThnAnimationPicker.Blocked(session.CutsceneErrors(ScriptPath), session.Cutscene(ScriptPath)!, ev);

    /// <summary>A clip that the game does not have on a character blocks the save, with the reason and the
    /// consequence, at the place of the clip. A clip in other letters is known. An undo heals the problem.</summary>
    [Fact]
    public void AnUnknownClipOnACharacterBlocksTheSaveWithTheReason()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        Assert.Empty(Errors(session));

        SetClip(session, 0, "Sc_Typo");
        var refused = session.Save();

        Assert.Equal([Typo], Errors(session));
        Assert.False(refused.Saved);
        var error = Assert.Single(refused.Problems);
        Assert.Equal((Typo, ScriptPath, DiagnosticSeverity.Error), (error.Message, error.Source, error.Severity));
        Assert.Equal((ThnElementKind.Event, 0, ThnTemplateRules.AnimationPath),
            (error.Cutscene!.Kind, error.Cutscene.Index, error.Cutscene.Value));

        SetClip(session, 0, "sc_sit");
        Assert.Empty(Errors(session));
        session.Undo();
        session.Undo();
        Assert.Empty(Errors(session));
        AssertSaved(session.Save());
    }

    /// <summary>
    /// A new START_MOTION event on a character has an empty clip. No clip has an empty name, so the character does not
    /// move, and the event blocks the save until a clip is picked. The clip field of the new event shows the problem as
    /// an error, and the field of the other event does not.
    /// </summary>
    [Fact]
    public void ANewMotionOfACharacterBlocksTheSaveUntilAClipIsPicked()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        var plan = CutsceneCommands.AddEvent(session, ScriptPath, "START_MOTION", 3, ["Man"]);
        Assert.Equal(EditStatus.Applied, FakeCommandHost.Runner(session).Run(plan).Status);
        var added = Assert.Single(plan.Added(session));
        const string noClip = "event { 3, START_MOTION, { \"Man\" } }, props.animation: The event has no clip. The " +
                              "character does not move.";

        Assert.Equal([noClip], Errors(session));
        Assert.Equal([noClip], session.Save().Problems.Where(p => p.Severity == DiagnosticSeverity.Error)
            .Select(p => p.Message));
        Assert.True(FieldBlocked(session, added));
        Assert.False(FieldBlocked(session, Event(session, 0)));

        session.Execute(ThnEdits.Set(added, new ThnPath(ThnTemplateRules.AnimationPath), new ThnText("Sc_Sit")));

        Assert.Empty(Errors(session));
        Assert.False(FieldBlocked(session, added));
        AssertSaved(session.Save());
    }

    /// <summary>The engine finds a clip by the name as it is. So a known name with a space before or after it names no
    /// clip, and it blocks the save.</summary>
    [Theory]
    [InlineData(" Sc_Sit")]
    [InlineData("Sc_Sit ")]
    public void AKnownClipNameWithASpaceAroundItBlocksTheSave(string clip)
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");

        SetClip(session, 0, clip);

        Assert.Equal([Typo.Replace("Sc_Typo", clip)], Errors(session));
        Assert.False(session.Save().Saved);
    }

    /// <summary>
    /// A START_MOTION event on a character without the clip key makes the engine throw when it looks for the clip. An
    /// agent can remove the key with <c>set_fields</c>, and <c>save</c> then refuses with the crash text of the
    /// gate.
    /// </summary>
    [Fact]
    public void AMotionWithoutTheClipKeyBlocksTheSaveAsACrash()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var ev = AgentCalls.Json($"thn:{ScriptPath}#event/0");
        var crash = $"{EditSession.CutsceneCrash} event {{ 1, START_MOTION, {{ \"Man\" }} }}, props.animation: " +
                    "The key is missing. The engine needs it for a START_MOTION on a DEFORMABLE.";

        AgentCalls.Ok(host, "set_fields", $$"""{ "ref": {{ev}}, "fields": { "props.animation": null } }""");
        var (code, _, message) = AgentCalls.Error(host, "save");

        Assert.Equal([crash], Errors(session));
        Assert.Equal("save_blocked", code);
        Assert.Contains(crash, message, System.StringComparison.Ordinal);
    }

    /// <summary>A COMPOUND plays the scripts of its own model, so a clip on a COMPOUND does not block the save.</summary>
    [Fact]
    public void AClipOnACompoundDoesNotBlock()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");

        SetClip(session, 1, "Sc_open_door");

        Assert.Empty(Errors(session));
        AssertSaved(session.Save());
    }

    /// <summary>An unsaved <c>[Animations]</c> line that names a file makes the clips of the file known, with no save
    /// between. An undo of the line brings the problem back.</summary>
    [Fact]
    public void AnUnsavedAnimationsLineMakesItsClipKnown()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        SetClip(session, 0, "Sc_Extra");
        Assert.Equal([Typo.Replace("Sc_Typo", "Sc_Extra")], Errors(session));

        session.Execute(AnimationWorkspace.AddLine(@"characters\animations\extra.anm"));
        Assert.Empty(Errors(session));

        session.Undo();
        Assert.Equal([Typo.Replace("Sc_Typo", "Sc_Extra")], Errors(session));
    }

    /// <summary>
    /// An animation file of the active mod that removes a clip blocks the save of a cutscene that the active mod
    /// changes, also when the clip does not change. The lower layer has the clip, so B does not have the problem. A
    /// clip that the lower layer does not have either stays out of the gate.
    /// </summary>
    [Fact]
    public void AClipThatTheActiveModRemovesBlocksTheSave()
    {
        using var ws = AnimationWorkspace.Create((AnimationWorkspace.More, ["Sc_ModSit"]));
        ws.WriteBase(ScriptPath, """
            duration = 10
            entities = {
                { entity_name = "Man", type = DEFORMABLE, template_name = "trent" },
                { entity_name = "Cam", type = CAMERA, cameraprops = { fovh = 10 } }
            }
            events = {
                { 1, START_MOTION, { "Man" }, { animation = "Sc_Sit" } },
                { 2, START_MOTION, { "Man" }, { animation = "Sc_Lost" } }
            }
            """);
        var session = ws.Session("act");
        var removed = Typo.Replace("Sc_Typo", "Sc_Sit");

        session.Execute(ThnEdits.Set(Entity(session, "Cam"), new ThnPath("cameraprops.fovh"), new ThnNumber(20)));

        Assert.Equal([removed], Errors(session));
        Assert.Contains(session.SaveProblems(), p => p.Message == removed);
        Assert.DoesNotContain(session.SaveProblems(), p => p.Message.Contains("Sc_Lost", System.StringComparison.Ordinal));
        // The field of a clip that the gate blocks is an error. A clip that the lower layer misses too keeps the
        // warning color.
        Assert.True(FieldBlocked(session, Event(session, 0)));
        Assert.False(FieldBlocked(session, Event(session, 1)));
    }

    /// <summary>
    /// The agent tools read the same check: <c>list_values</c> gives the clips of the list <c>animation</c> with their
    /// files, <c>set_fields</c> applies a clip that the game does not have and gives the problem in
    /// <c>new_problems</c>, and <c>save</c> refuses with the text of the save gate of the editor.
    /// </summary>
    [Fact]
    public void TheAgentToolsGiveTheClipsAndSaveRefusesWithTheGateText()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var ev = AgentCalls.Json($"thn:{ScriptPath}#event/0");

        var values = AgentCalls.Ok(host, "list_values", """{ "list": "animation" }""");
        var result = AgentCalls.Ok(host, "set_fields", $$"""{ "ref": {{ev}}, "fields": { "props.animation": "Sc_Typo" } }""");
        var gate = session.GateRefusal()!.Problems.Single(p => p.Severity == DiagnosticSeverity.Error);
        var (code, _, message) = AgentCalls.Error(host, "save");

        Assert.Equal([("Sc_Run", "body.anm"), ("Sc_Sit", "more.anm"), ("SC_WALK", "more.anm")],
            values["rows"]!.AsArray().Select(r => ((string?)r!["value"], (string?)r["detail"])));
        Assert.Equal("applied", (string?)result["status"]);
        var problem = Assert.Single(result["new_problems"]!.AsArray())!;
        Assert.Equal((Typo, $"thn:{ScriptPath}#event/0"), ((string?)problem["message"], (string?)problem["ref"]));
        Assert.Equal(Typo, gate.Message);
        Assert.Equal("save_blocked", code);
        Assert.Equal($"The save stopped. 1 error blocks it. Correct it, then save again. {Typo} The error is in " +
                     $"thn:{ScriptPath}#event/0.", message);
    }
}
