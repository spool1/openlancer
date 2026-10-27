using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>The result of <c>edit_batch</c>: the result of the batch, and the result of each step.</summary>
internal sealed record BatchResult : EditResult
{
    public BatchResult(EditResult result, IReadOnlyList<EditResult> steps) : base(result)
    {
        Steps = steps;
    }

    /// <summary>The result of each step, in order. A step has the status of the batch.</summary>
    public IReadOnlyList<EditResult> Steps { get; init; }
}

/// <summary>
/// The tool <c>edit_batch</c> of section 7.6 of the MCP1 design (decision D710): edit steps as one entry of the edit
/// history, all or nothing. Each step runs its commands on the result of the steps before it. A later step can so name
/// an item that an earlier step made.
/// </summary>
/// <remarks>
/// <para>Only a tool of the kind <see cref="ToolKind.Edit"/> can be a step (review finding R8 of MCP1-2). An asset
/// write cannot be a step, because it writes files at once and no cancel can undo it. A query, a view tool, a
/// play tool and a tool of the session as a whole cannot be a step either.</para>
/// <para>A step cannot have <c>preview</c> or <c>with_patch</c>, because the batch gives the preview. A step that
/// needs a confirmation fails unless it has <c>confirm: true</c>. The <c>if_revision</c> of a step compares with the
/// revision before the batch. A failed step gives <c>step_failed</c> with its index and its error, and the batch
/// changes nothing.</para>
/// </remarks>
internal static class BatchTool
{
    /// <summary>The names of the tools that can be steps, in table order.</summary>
    public static IEnumerable<string> StepTools => AgentTools.All.Where(t => t.Kind == ToolKind.Edit).Select(t => t.Name);

    public static ToolStep Run(ToolCall call)
    {
        var args = call.Args;
        var steps = args.Node("steps")!.AsArray();
        var limit = call.Limits.BatchSteps;
        if (steps.Count > limit)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, "BatchSteps",
                AgentTexts.Error("limit_steps", ("max", limit))) { Field = "steps" });
        }
        var parsed = steps.Select((s, i) => Step(s!.AsObject(), i)).ToList();
        var preview = args.Flag("preview");
        var withPatch = args.Flag("with_patch");
        if (withPatch && !preview)
            throw ToolInput.Bad("with_patch", AgentTexts.Error("with_patch_preview"));
        EditTools.ReadOnlyGuard(call);
        var results = new List<EditResult>();
        // Each step asks for its own confirmation, so the batch itself needs none.
        return EditTools.Commit(call, args.Text("label"), preview, withPatch, confirm: true, scope =>
        {
            for (var i = 0; i < parsed.Count; i++)
            {
                var (tool, arguments) = parsed[i];
                if (AgentTools.Unavailable(call.Host, tool) is { } unavailable)
                    throw Failed(i, unavailable);
                ToolStep step;
                try
                {
                    step = tool.Run(new ToolCall(call.Host, tool.Name, tool.Input.Read(tool.Name, arguments)) { Scope = scope });
                }
                catch (EditException e)
                {
                    throw Failed(i, e.Error);
                }
                if (step.Result is { IsError: true } error)
                    throw Failed(i, JsonSerializer.Deserialize<EditError>(error.Content, EditJson.Options)!);
                results.Add(scope.StepResult());
            }
        }, result => ToolResult.Of(new BatchResult(result,
            results.Select(r => r with { Status = result.Status }).ToList())));
    }

    /// <summary>The tool and the arguments of a step, after the checks that need no session.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for a tool that the server does not
    /// have or for a step with <c>preview</c> or <c>with_patch</c>. <see cref="EditErrorKind.Refused"/> for a tool that
    /// cannot be a step.</exception>
    private static (StudioTool Tool, JsonObject? Arguments) Step(JsonObject step, int index)
    {
        var name = (string)step["tool"]!;
        var field = $"steps[{index}]";
        var tool = AgentTools.Find(name) ?? throw new EditException(new EditError(EditErrorKind.InvalidArgument,
            ToolInput.BadArgument, AgentTexts.Error("step_unknown", ("index", index), ("tool", name)))
        {
            Index = index,
            Field = field + ".tool",
        });
        if (tool.Kind != ToolKind.Edit)
        {
            var text = tool.Kind == ToolKind.AssetWrite ? "step_asset_write" : "step_kind";
            throw new EditException(new EditError(EditErrorKind.Refused, "StepTool",
                AgentTexts.Error(text, ("index", index), ("tool", name))) { Index = index, Field = field + ".tool" });
        }
        var arguments = step["arguments"] as JsonObject;
        if (arguments != null && new[] { "preview", "with_patch" }.FirstOrDefault(arguments.ContainsKey) is { } own)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, ToolInput.BadArgument,
                AgentTexts.Error("step_preview", ("name", own))) { Index = index, Field = $"{field}.arguments.{own}" });
        }
        return (tool, (JsonObject?)arguments?.DeepClone());
    }

    /// <summary>The error of a step that failed: <c>step_failed</c> with the index of the step. It keeps the kind, the
    /// detail and the message of the error of the step.</summary>
    private static EditException Failed(int index, EditError error) => new(new EditError(EditErrorKind.StepFailed,
        error.Detail, AgentTexts.Error("step_failed", ("index", index), ("message", error.Message)))
    {
        Index = index,
        Target = error.Target,
        Field = error.Field == null ? $"steps[{index}]" : $"steps[{index}].arguments.{error.Field}",
        StepCode = error.Kind,
    });
}
