using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;
using static OpenLancer.Tests.AgentEditToolTests;

namespace OpenLancer.Tests;

/// <summary>
/// The tool <c>edit_batch</c> (section 7.6 of the MCP1 design, decision D710): edit steps as one entry of the edit
/// history, all or nothing. The tests also cover the limit of the steps and the refusal of each tool that cannot be a
/// step.
/// </summary>
public sealed class EditBatchToolTests
{
    /// <summary>A step of set_fields that moves an object.</summary>
    private static string Move(string item, int x, string extra = "") =>
        $$"""{ "tool": "set_fields", "arguments": { "ref": {{Json(item)}}, "fields": { "pos": [{{x}}, 0, 0] }{{extra}} } }""";

    private static string Batch(params string[] steps) => $$"""{ "steps": [{{string.Join(", ", steps)}}] }""";

    [Fact]
    public void HundredStepsAreOneEntryAndTheNextStepGivesLimit()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var hundred = Enumerable.Range(0, 100).Select(i => Move("obj:Li01_01", i)).ToArray();

        var result = Ok(host, "edit_batch", Batch(hundred));
        var limit = Error(host, "edit_batch", Batch([.. hundred, Move("obj:Li01_01", 100)]));

        Assert.Equal("applied", (string?)result["status"]);
        Assert.Equal(100, result["steps"]!.AsArray().Count);
        Assert.All(result["steps"]!.AsArray(), s => Assert.Equal("applied", (string?)s!["status"]));
        Assert.Equal("Agent: Set pos", session.UndoLabel);
        Assert.Equal("99, 0, 0", Line(session, Station, "pos"));
        Assert.Equal(1, session.Revision);
        Assert.Equal(("limit", "BatchSteps", "One batch can have at most 100 steps. Split the batch."), limit);
        Assert.Equal([("edit_batch", "Agent: Set pos")], host.Edits.Select(e => (e.Tool, e.Result.UndoLabel)));
    }

    /// <summary>A failed step gives step_failed with its index and its error, and the batch changes nothing.</summary>
    [Fact]
    public void AFailedStepGivesItsIndexAndTheBatchChangesNothing()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        var before = session.WorkingSection(Station);

        var result = Call(host, "edit_batch", Batch(Move("obj:Li01_01", 5), Move("obj:none", 6)));

        Assert.True(result.IsError);
        var error = result.Content;
        Assert.Equal(("step_failed", ItemRefs.NotFound, 1, "not_found", "steps[1].arguments.ref"),
            ((string?)error["code"], (string?)error["detail"], (int?)error["index"], (string?)error["step_code"],
                (string?)error["field"]));
        Assert.StartsWith("Step 1 failed: ", (string?)error["message"]);
        Assert.Contains("The edit view has no object 'none'.", (string?)error["message"]);
        Assert.Equal("obj:none", (string?)error["target"]);
        Assert.EndsWith("The batch changed nothing. The first step has the index 0.", (string?)error["message"]);
        Assert.Same(before, session.WorkingSection(Station));
        Assert.False(session.CanUndo);
        Assert.Null(session.OpenBatch);
    }

    /// <summary>A later step names the item that an earlier step renamed, so it sees the result of the step before
    /// it. The label of the batch is the label of the entry.</summary>
    [Fact]
    public void ALaterStepNamesAnItemOfAnEarlierStep()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var result = Ok(host, "edit_batch", $$"""
            {
              "label": "Move the new dock",
              "steps": [
                { "tool": "rename_item", "arguments": { "ref": "obj:Li01_dock", "new_name": "Li01_dock_new" } },
                {{Move("obj:Li01_dock_new", 7)}}
              ]
            }
            """);

        Assert.Equal("Agent: Move the new dock", (string?)result["undo_label"]);
        Assert.Equal("obj:Li01_dock_new", (string?)result["steps"]![0]!["renames"]![0]!["to"]);
        Assert.Equal(["obj:Li01_dock_new"], Texts(result["steps"]![1]!["changes"]));
        Assert.Equal("7, 0, 0", Line(session, SceneWorkspace.Object("Li01_dock_new"), "pos"));
        session.Undo();
        Assert.NotNull(session.Get(SceneWorkspace.Object("Li01_dock")));
        Assert.Null(session.Get(SceneWorkspace.Object("Li01_dock_new")));
    }

    /// <summary>
    /// A step renames an existing zone, and a later step creates a zone that takes its old free nickname. The result
    /// keeps the rename and gives the new zone as an add. A rename changes an add only when the add came before it
    /// (review finding of MCP1-2, step 4).
    /// </summary>
    [Fact]
    public void ARenameBeforeAnAddWithTheOldNicknameStaysARename()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        Ok(host, "scene_create_zone", """{ "system": "Li01", "position": [0, 0, 0] }""");

        var result = Ok(host, "edit_batch", """
            {
              "steps": [
                { "tool": "rename_item", "arguments": { "ref": "zone:li01_zone_1", "new_name": "patrol_a" } },
                { "tool": "scene_create_zone", "arguments": { "system": "Li01", "position": [0, 0, 100] } }
              ]
            }
            """);
        var later = Ok(host, "edit_batch", """
            {
              "steps": [
                { "tool": "scene_create_zone", "arguments": { "system": "Li01", "position": [0, 0, 200] } },
                { "tool": "rename_item", "arguments": { "ref": "zone:li01_zone_2", "new_name": "patrol_b" } }
              ]
            }
            """);

        Assert.Equal(["zone:li01_zone_1"], Texts(result["adds"]));
        var rename = Assert.Single(result["renames"]!.AsArray())!;
        Assert.Equal(("zone:li01_zone_1", "zone:patrol_a"), ((string?)rename["from"], (string?)rename["to"]));
        Assert.DoesNotContain("zone:li01_zone_1", Texts(result["changes"]));
        Assert.Equal(["zone:li01_zone_1"], Texts(result["steps"]![1]!["adds"]));
        Assert.NotNull(session.Get(SceneWorkspace.Zone("patrol_a")));
        Assert.Equal("0, 0, 100", Line(session, SceneWorkspace.Zone("li01_zone_1"), "pos"));
        // An add before the rename is one new item with its last nickname.
        Assert.Equal(["zone:patrol_b"], Texts(later["adds"]));
        Assert.Empty(later["renames"]!.AsArray());
    }

    /// <summary>A step that needs a confirmation fails unless its arguments have confirm: true.</summary>
    [Fact]
    public void AStepThatNeedsAConfirmationFailsWithoutConfirm()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var asks = Call(host, "edit_batch", """{ "steps": [{ "tool": "delete_items", "arguments": { "refs": ["obj:Li01_01"] } }] }""");
        var done = Ok(host, "edit_batch",
            """{ "steps": [{ "tool": "delete_items", "arguments": { "refs": ["obj:Li01_01"], "confirm": true } }] }""");

        Assert.Equal(("step_failed", EditTools.ConfirmChanges, "needs_confirmation"),
            ((string?)asks.Content["code"], (string?)asks.Content["detail"], (string?)asks.Content["step_code"]));
        Assert.Equal("applied", (string?)done["status"]);
        Assert.Null(session.Get(SceneWorkspace.Object("Li01_child")));
    }

    /// <summary>A query, a view tool, a tool of the session and an unknown tool cannot be a step. A step cannot have
    /// preview or with_patch.</summary>
    [Fact]
    public void AToolThatIsNotAnEditAndAStepWithPreviewRefuse()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var query = Error(host, "edit_batch", Batch(Move("obj:Li01_01", 1), """{ "tool": "get_item", "arguments": { "ref": "obj:Li01_01" } }"""));
        var undo = Error(host, "edit_batch", Batch("""{ "tool": "undo" }"""));
        var batch = Error(host, "edit_batch", Batch("""{ "tool": "edit_batch" }"""));
        var unknown = Error(host, "edit_batch", Batch("""{ "tool": "launch_rocket" }"""));
        var preview = Error(host, "edit_batch", Batch(Move("obj:Li01_01", 1, """, "preview": true""")));

        Assert.Equal(("refused", "StepTool"), (query.Code, query.Detail));
        Assert.Equal("Step 1: get_item cannot be a step of edit_batch. A step must be an edit tool, for example set_fields.",
            query.Message);
        Assert.Equal(("refused", "StepTool"), (undo.Code, undo.Detail));
        Assert.Equal(("refused", "StepTool"), (batch.Code, batch.Detail));
        Assert.Equal(("invalid_argument", "BadArgument", "Step 0: the server has no tool launch_rocket."), unknown);
        Assert.Equal(("invalid_argument", "BadArgument"), (preview.Code, preview.Detail));
        Assert.False(session.CanUndo);
    }

    /// <summary>The if_revision of a step compares with the revision before the batch. A step changes it only at the
    /// commit of the batch.</summary>
    [Fact]
    public void TheRevisionGuardOfAStepComparesWithTheRevisionBeforeTheBatch()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        Ok(host, "edit_batch", Batch(Move("obj:Li01_01", 1), Move("obj:Li01_dock", 2, """, "if_revision": "0" """)));
        var stale = Call(host, "edit_batch", Batch(Move("obj:Li01_dock", 3, """, "if_revision": "0" """)));

        Assert.Equal(("step_failed", "stale_revision"), ((string?)stale.Content["code"], (string?)stale.Content["step_code"]));
        Assert.Equal("2, 0, 0", Line(session, SceneWorkspace.Object("Li01_dock"), "pos"));
        Assert.Equal(1, session.Revision);
    }

    /// <summary>A preview of a batch changes nothing and gives the result of each step. A batch above the section
    /// limit refuses with limit.</summary>
    [Fact]
    public void APreviewOfABatchChangesNothingAndTheSectionLimitRefuses()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        var preview = Ok(host, "edit_batch", $$"""
            { "steps": [{{Move("obj:Li01_01", 1)}}, {{Move("obj:Li01_dock", 2)}}], "preview": true, "with_patch": true }
            """);
        host.TestLimits = new AgentLimits { EditSections = 1 };
        var limit = Error(host, "edit_batch", Batch(Move("obj:Li01_01", 1), Move("obj:Li01_dock", 2)));

        Assert.Equal("previewed", (string?)preview["status"]);
        Assert.Equal(["previewed", "previewed"], preview["steps"]!.AsArray().Select(s => (string?)s!["status"]));
        Assert.Contains("pos = 2, 0, 0", (string?)preview["patch"]![SceneWorkspace.Li01Path]);
        Assert.Equal(("limit", "EditSections"), (limit.Code, limit.Detail));
        Assert.False(session.CanUndo);
        Assert.Equal(0, session.Revision);
    }

    /// <summary>
    /// A batch whose result is larger than the size limit of a result gives limit and changes nothing. Its preview gives
    /// the same error. Through the queue, a batch whose result fits applies, and the queue keeps its result (review
    /// finding of MCP1-2, step 3).
    /// </summary>
    [Fact]
    public async Task ABatchWhoseResultIsAboveTheSizeLimitChangesNothing()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;
        host.TestLimits = new AgentLimits { ResultKilobytes = 16 };
        var hundred = Enumerable.Range(0, 100).Select(i => Move("obj:Li01_01", i)).ToArray();
        using var queue = new ToolQueue(host);

        async Task<ToolResult> Queued(string arguments)
        {
            var call = queue.CallAsync("edit_batch", JsonNode.Parse(arguments)!.AsObject(), null,
                TestContext.Current.CancellationToken);
            Assert.True(queue.RunNext());
            return await call;
        }

        var large = await Queued(Batch(hundred));
        var preview = await Queued($$"""{ "steps": [{{string.Join(", ", hundred)}}], "preview": true }""");
        Assert.False(session.CanUndo);
        Assert.Equal(0, session.Revision);
        Assert.Empty(host.Edits);
        var small = await Queued(Batch(hundred[..10]));

        foreach (var refused in new[] { large, preview })
        {
            Assert.True(refused.IsError);
            Assert.Equal(("limit", ToolResult.ResultSize),
                ((string?)refused.Content["code"], (string?)refused.Content["detail"]));
            Assert.Contains("The edit changed nothing. Split the edit into smaller calls.", (string?)refused.Content["message"]);
        }
        Assert.False(small.IsError, small.Text);
        Assert.Equal(("applied", "1"), ((string?)small.Content["status"], (string?)small.Content["rev"]));
        Assert.Equal("9, 0, 0", Line(session, Station, "pos"));
    }

    [Fact]
    public void PauseAndAHostThatTakesNoEditsRefuseTheBatch()
    {
        var (ws, session, host) = Scene();
        using var _ = ws;

        host.TestPaused = true;
        Assert.Equal("paused", Error(host, "edit_batch", Batch(Move("obj:Li01_01", 1))).Code);
        host.TestPaused = false;
        host.Reason = "The user did not allow the edits.";
        Assert.Equal(("read_only", "NotEditable", "The user did not allow the edits."),
            Error(host, "edit_batch", Batch(Move("obj:Li01_01", 1))));
        Assert.False(session.CanUndo);
    }
}
