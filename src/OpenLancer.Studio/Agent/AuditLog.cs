using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenLancer.Studio.Agent;

/// <summary>One line of an audit log, or of a file of calls for <c>oltool mcp replay</c>.</summary>
/// <param name="Line">The line number in the file, from 1.</param>
/// <param name="Arguments">The arguments, or null when the line has none.</param>
/// <param name="Expected">The recorded result, or null when the line has none.</param>
/// <param name="ArgumentsOmitted">True when the log left out the arguments of the call, because the call did not run
/// (see <see cref="AuditLog.Write"/>). A replay cannot run such a call.</param>
public sealed record AuditEntry(int Line, string Tool, JsonObject? Arguments, ToolOutcome? Expected,
    bool ArgumentsOmitted = false);

/// <summary>
/// The audit log of the agent calls (decision D728 of the MCP1 design): one JSON line for each call, with the tool, the
/// arguments and the short result. The editor writes one file for each run into the log folder of OpenLancer. The file
/// holds no token, because the token is a header of the request and never an argument. <c>oltool mcp replay</c> reads
/// the same format, so a user can replay an agent run on a copy of the profile.
/// </summary>
/// <remarks>
/// <para>The format of a line: <c>{"time": "...", "tool": "list_items", "arguments": {...}, "result": {"status":
/// "ok"}}</c>. A tool error has <c>"status": "error"</c> with <c>code</c> and <c>detail</c>.</para>
/// <para>The log keeps the arguments of each call that runs without an error, because a replay needs them. A
/// <c>mission_edit</c> call with many commands has large arguments. The size limit of a request of the server bounds
/// the arguments of one call. A client can also send many calls, for example calls with large arguments that the input
/// check refuses. So the log leaves out the arguments of a call that does not run, and the arguments above
/// <see cref="MaxErrorArgumentBytes"/> of a call that gives an error, and writes <c>"arguments_omitted": true</c>
/// instead (final finding 8 of MCP1-3). A call that gives an error changes nothing, so a replay does not need it. The
/// file stops at a size limit. Then the log tells its owner once and writes no more lines.</para>
/// </remarks>
public sealed class AuditLog : IDisposable
{
    /// <summary>The default size limit of one file in bytes.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>The largest arguments of a line of a call that gives an error, as JSON text in bytes of UTF-8. The
    /// arguments of a valid call that is not an edit are much smaller.</summary>
    public const int MaxErrorArgumentBytes = 16 * 1024;

    private readonly object gate = new();
    private readonly StreamWriter writer;
    private readonly long maxBytes;
    private readonly Action? full;
    private long size;
    private bool stopped;

    /// <summary>Opens a log file for append.</summary>
    /// <param name="maxBytes">The size limit of the file in bytes.</param>
    /// <param name="full">Runs once, on the thread of the call, when the file reaches its size limit, or null.</param>
    /// <exception cref="IOException">The file does not open.</exception>
    /// <exception cref="UnauthorizedAccessException">The file does not open.</exception>
    public AuditLog(string path, long maxBytes = DefaultMaxBytes, Action? full = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        FilePath = path;
        this.maxBytes = maxBytes;
        this.full = full;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        size = stream.Length;
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>The path of the file.</summary>
    public string FilePath { get; }

    /// <summary>The file name of the log of a run that starts at a time: <c>agent-20261020-142233.jsonl</c>.</summary>
    public static string FileName(DateTime startUtc) =>
        "agent-" + startUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>Adds the line of one call. It can run on any thread.</summary>
    /// <param name="arguments">The arguments of the call, or null for a call without arguments.</param>
    /// <param name="ran">False for a call that did not run, for example a call that the rate limit refused. Its line
    /// leaves out the arguments.</param>
    public void Write(DateTimeOffset time, string tool, JsonObject? arguments, ToolOutcome outcome, bool ran = true)
    {
        var line = new JsonObject
        {
            ["time"] = time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["tool"] = tool,
        };
        if (ran && (outcome.Status != ToolOutcome.Error || arguments == null ||
                    Encoding.UTF8.GetByteCount(arguments.ToJsonString()) <= MaxErrorArgumentBytes))
            line["arguments"] = arguments?.DeepClone() ?? new JsonObject();
        else
            line["arguments_omitted"] = true;
        line["result"] = Outcome(outcome);
        var json = line.ToJsonString();
        var bytes = Encoding.UTF8.GetByteCount(json) + 1;
        var tell = false;
        lock (gate)
        {
            if (stopped)
                return;
            if (size + bytes > maxBytes)
            {
                stopped = true;
                tell = true;
            }
            else
            {
                try
                {
                    writer.WriteLine(json);
                    size += bytes;
                }
                catch (IOException)
                {
                    // A full disk stops the log, not the call.
                }
            }
        }
        if (tell)
            full?.Invoke();
    }

    public void Dispose()
    {
        lock (gate)
            writer.Dispose();
    }

    /// <summary>The short result in its JSON form.</summary>
    public static JsonObject Outcome(ToolOutcome outcome)
    {
        var node = new JsonObject { ["status"] = outcome.Status };
        if (outcome.Code != null)
            node["code"] = outcome.Code;
        if (outcome.Detail != null)
            node["detail"] = outcome.Detail;
        return node;
    }

    /// <summary>
    /// Reads the lines of a log or of a file of calls. An empty line is skipped. A line has <c>tool</c>, optional
    /// <c>arguments</c> and an optional recorded <c>result</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">A line is not such a JSON object. The message names the line.</exception>
    public static IEnumerable<AuditEntry> Read(TextReader reader)
    {
        var number = 0;
        while (reader.ReadLine() is { } text)
        {
            number++;
            if (string.IsNullOrWhiteSpace(text))
                continue;
            JsonObject line;
            try
            {
                line = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException(
                    $"Line {number} is not a JSON object.");
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"Line {number} is not valid JSON: {e.Message}");
            }
            if (line["tool"] is not JsonValue tool || tool.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException($"Line {number} has no tool name.");
            if (line["arguments"] is { } arguments && arguments is not JsonObject)
                throw new InvalidDataException($"The arguments of line {number} are not a JSON object.");
            var omitted = line["arguments_omitted"]?.GetValueKind() == JsonValueKind.True;
            ToolOutcome? expected = null;
            if (line["result"] is JsonObject result)
            {
                expected = (string?)result["status"] is { } status
                    ? new ToolOutcome(status, (string?)result["code"], (string?)result["detail"])
                    : throw new InvalidDataException($"The result of line {number} has no status.");
            }
            yield return new AuditEntry(number, tool.GetValue<string>(), (JsonObject?)line["arguments"]?.DeepClone(),
                expected, omitted);
        }
    }
}
