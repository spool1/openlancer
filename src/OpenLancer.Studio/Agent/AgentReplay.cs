using System.IO;
using System.Text.Json.Nodes;
using System.Threading;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// <c>oltool mcp replay</c> (section 6.2 of the MCP1 design): runs each line of an audit log, or of a file of calls,
/// through the tool table, on the thread of the caller. It writes one result line for each call. When a line has a
/// recorded result, it compares the status and the error code with it. It is the form of the agent calls for CI, and a
/// user replays an agent run on a copy of the profile with it. The edit calls change the profile that the replay gets,
/// and a recorded <c>save</c> saves them.
/// </summary>
/// <remarks>
/// <para>Some results come from the host of the log, not from the data (decision D728): a call that waited too long, a
/// call that the rate limit refused, a tool that the user hid, a call without a profile, and a tool that only the
/// editor host has. The replay skips such a call, because it did not run or cannot run here. It also skips a line
/// without its arguments. The error <c>limit</c> tells the limits of a host, not the data, and the limits are settings.
/// So the replay runs a call with a recorded or a replayed <c>limit</c> and does not compare its result. The replay
/// applies the limits of its own host, also the size limit of a result.</para>
/// <para>A skipped line gives the status <c>skipped</c> with the reason. A line that the replay runs and does not
/// compare has <c>"compared": false</c>.</para>
/// </remarks>
public static class AgentReplay
{
    /// <summary>The status of a line that the replay does not run.</summary>
    public const string Skipped = "skipped";

    private const string Limit = "limit";

    /// <summary>Runs the calls until the first difference.</summary>
    /// <param name="host">The host, which owns its session on the thread of the caller.</param>
    /// <param name="calls">The JSON lines of the calls.</param>
    /// <param name="output">Gets one JSON line for each call: the line number, the tool, the short result and the
    /// structured content.</param>
    /// <param name="preview">True to change no file (<c>--preview</c>). The edits then change only the data in memory, so
    /// a later call can still use the items of an earlier call. The replay skips each call that writes files:
    /// <c>save</c>, <c>export_mod</c> and the asset writes of the prop groups. The host must write no recovery
    /// file.</param>
    /// <returns>Null when each result is as recorded, else why the replay stopped.</returns>
    /// <exception cref="InvalidDataException">A line is not a call. The message names the line.</exception>
    public static string? Run(AgentHost host, TextReader calls, TextWriter output, bool preview = false)
    {
        foreach (var call in AuditLog.Read(calls))
        {
            if (AgentTools.Find(call.Tool) is not { } tool)
                return $"Line {call.Line}: the tool table has no tool '{call.Tool}'.";
            if ((SkipReason(host, tool, call) ?? (preview ? PreviewSkip(tool) : null)) is { } reason)
            {
                output.WriteLine(new JsonObject
                {
                    ["line"] = call.Line,
                    ["tool"] = call.Tool,
                    ["result"] = new JsonObject { ["status"] = Skipped },
                    ["reason"] = reason,
                }.ToJsonString());
                continue;
            }
            var step = AgentTools.Run(host, call.Tool, call.Arguments);
            var result = (step.Result ?? step.Tail!(CancellationToken.None, ToolProgress.None).GetAwaiter().GetResult()).Limited(host.Limits);
            var outcome = result.Outcome;
            var compared = call.Expected == null || (call.Expected.Code != Limit && outcome.Code != Limit);
            var line = new JsonObject
            {
                ["line"] = call.Line,
                ["tool"] = call.Tool,
                ["result"] = AuditLog.Outcome(outcome),
                ["content"] = result.Content.DeepClone(),
            };
            if (!compared)
                line["compared"] = false;
            output.WriteLine(line.ToJsonString());
            if (!compared)
                continue;
            if (call.Expected is { } expected && (expected.Status != outcome.Status || expected.Code != outcome.Code))
                return $"Line {call.Line}: {call.Tool} gave {Describe(outcome)}, and the log has {Describe(expected)}.";
            if (call.Expected == null && result.IsError)
                return $"Line {call.Line}: {call.Tool} gave {Describe(outcome)}: {(string?)result.Content["message"]}";
        }
        return null;
    }

    /// <summary>Why a replay with <c>--preview</c> does not run a call, or null: the call writes files.</summary>
    private static string? PreviewSkip(StudioTool tool) =>
        tool.Kind == ToolKind.AssetWrite || HeadlessHost.SaveTools.Contains(tool.Name)
            ? $"The replay with --preview does not run {tool.Name}, because the tool writes files."
            : null;

    /// <summary>Why the replay does not run a line, or null when it runs it.</summary>
    private static string? SkipReason(AgentHost host, StudioTool tool, AuditEntry call)
    {
        if (call.ArgumentsOmitted)
            return "The log has no arguments of this call.";
        var served = host.Kind == AgentHostKind.Editor ? ToolHosts.Editor : ToolHosts.Headless;
        if ((tool.Hosts & served) == 0)
            return $"Only the editor host has the tool {tool.Name}.";
        return call.Expected switch
        {
            { Code: "busy" or "paused" or "not_available" } or { Code: Limit, Detail: "CallRate" } =>
                $"The call did not run in the host of the log ({call.Expected.Code}).",
            _ => null,
        };
    }

    private static string Describe(ToolOutcome outcome) =>
        outcome.Code == null ? outcome.Status : $"{outcome.Status} {outcome.Code}";
}
