using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>What a call gives, in short, for the audit log and for <c>oltool mcp replay</c> (decision D728 of the
/// MCP1 design).</summary>
/// <param name="Status"><c>ok</c> for a result, <c>error</c> for a tool error.</param>
/// <param name="Code">The error kind of a tool error, for example <c>not_found</c>, else null.</param>
/// <param name="Detail">The detail code of a tool error, else null.</param>
public sealed record ToolOutcome(string Status, string? Code = null, string? Detail = null)
{
    public const string Ok = "ok";
    public const string Error = "error";
}

/// <summary>A PNG image of a result, for example the image of <c>render_scene</c> (section 7.7 of the MCP1
/// design).</summary>
/// <param name="Png">The data of the PNG file.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public sealed record ToolImage(byte[] Png, int Width, int Height)
{
    /// <summary>The media type of the image.</summary>
    public const string MimeType = "image/png";
}

/// <summary>
/// The result of a call (section 5.7 of the MCP1 design): the JSON object in the snake case of the command layer, which
/// the SDK binding gives as <c>structuredContent</c> and as one text block. A tool error has <see cref="IsError"/>,
/// and its object has the error kind as <c>code</c>, the detail code as <c>detail</c> and the message. A view tool
/// also gives an image (section 7.7), which the binding gives as an image block after the text.
/// </summary>
public sealed class ToolResult
{
    private static readonly JsonSerializerOptions Compact = new(EditJson.Options) { WriteIndented = false };

    private ToolResult(JsonObject content, bool isError, IReadOnlyList<ToolImage>? images = null)
    {
        Content = content;
        IsError = isError;
        Images = images ?? [];
        Text = content.ToJsonString(Compact);
    }

    /// <summary>The structured result.</summary>
    public JsonObject Content { get; }

    /// <summary>True for a tool error. The model reads it and can correct its call.</summary>
    public bool IsError { get; }

    /// <summary>The images of the result, for example the image of <c>render_scene</c>. A tool error has none.</summary>
    public IReadOnlyList<ToolImage> Images { get; }

    /// <summary>The JSON text of <see cref="Content"/>, without white space.</summary>
    public string Text { get; }

    /// <summary>The size of <see cref="Text"/> in bytes of UTF-8. The images do not count, because the limit of a result
    /// is for its text (section 8.4 of the MCP1 design).</summary>
    public int Bytes => Encoding.UTF8.GetByteCount(Text);

    /// <summary>The short form of the result for the audit log.</summary>
    public ToolOutcome Outcome => IsError
        ? new ToolOutcome(ToolOutcome.Error, (string?)Content["code"], (string?)Content["detail"])
        : new ToolOutcome(ToolOutcome.Ok);

    /// <summary>The result of a query: the value in the JSON form of the command layer.</summary>
    public static ToolResult Of<T>(T value) where T : notnull =>
        new((JsonObject)JsonSerializer.SerializeToNode(value, EditJson.Options)!, false);

    /// <summary>The result of a view tool: the value in the JSON form of the command layer, and its image.</summary>
    public static ToolResult Of<T>(T value, ToolImage image) where T : notnull =>
        new((JsonObject)JsonSerializer.SerializeToNode(value, EditJson.Options)!, false, [image]);

    /// <summary>A tool error: the error kind as <c>code</c>, the detail code as <c>detail</c>, the message, and the
    /// target and the argument when the error names them.</summary>
    public static ToolResult Failure(EditError error) =>
        new((JsonObject)JsonSerializer.SerializeToNode(error, EditJson.Options)!, true);

    /// <summary>A tool error of the host, with a text of <c>agent-tools.json</c>.</summary>
    internal static ToolResult Failure(EditErrorKind kind, string detail, string message) =>
        Failure(new EditError(kind, detail, message));

    /// <summary>
    /// The result with the revision of the session part as <c>rev</c> (section 8.6, item 3 of the MCP1 design). An agent
    /// reads, then edits with <c>if_revision</c>, so each result tells the revision of the data that it read. A tool
    /// error and a result that has <c>rev</c> already stay as they are.
    /// </summary>
    internal ToolResult WithRevision(long? revision)
    {
        if (IsError || revision is not { } value || Content.ContainsKey(StudioTool.RevisionName))
            return this;
        var content = (JsonObject)Content.DeepClone();
        content[StudioTool.RevisionName] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ToolResult(content, false, Images);
    }

    /// <summary>The detail code of the error <c>limit</c> for a result that is too large.</summary>
    internal const string ResultSize = "ResultSize";

    /// <summary>The size of the text in whole KB, or null. The size is null when the text fits the limit of a result
    /// (section 8.4 of the MCP1 design).</summary>
    internal int? SizeAboveLimit(AgentLimits limits)
    {
        var bytes = Bytes;
        return bytes <= limits.ResultKilobytes * 1024 ? null : (bytes + 1023) / 1024;
    }

    /// <summary>The result, or the error <c>limit</c> when its text is larger than the limit of a result (section 8.4
    /// of the MCP1 design). Both hosts and <c>oltool mcp replay</c> apply it. An edit checks the size before its
    /// commit, so this error never hides an edit that applied.</summary>
    internal ToolResult Limited(AgentLimits limits) =>
        SizeAboveLimit(limits) is { } size
            ? Failure(EditErrorKind.Limit, ResultSize,
                AgentTexts.Error("limit_result", ("size", size), ("max", limits.ResultKilobytes)))
            : this;
}

/// <summary>A progress notification of a tail (section 5.3 of the MCP1 design). A client that asked for progress gets
/// it as <c>notifications/progress</c>.</summary>
/// <param name="Progress">The work until now, for example 1 of 3 steps.</param>
/// <param name="Total">The work of the whole call, or null when it is not known.</param>
/// <param name="Message">What the call does now, in strict STE, or null.</param>
public sealed record ToolProgress(double Progress, double? Total, string? Message)
{
    /// <summary>The progress of a call whose client did not ask for progress: it drops each report.</summary>
    public static readonly IProgress<ToolProgress> None = new Dropped();

    private sealed class Dropped : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }
}

/// <summary>
/// What the session part of a call gives (decision D765 of the MCP1 design): the result, or a tail. A tail runs on a
/// worker, does not touch the session, and gives the result later. The next call can start while a tail runs.
/// </summary>
public sealed class ToolStep
{
    private ToolStep(ToolResult? result, Func<CancellationToken, IProgress<ToolProgress>, Task<ToolResult>>? tail)
    {
        Result = result;
        Tail = tail;
    }

    /// <summary>The result, or null when the call has a tail.</summary>
    public ToolResult? Result { get; }

    /// <summary>The tail, or null. It gets a token that the time limit of the tail and the client cancel, and the
    /// progress of the call. A tail that the client asked no progress of gets <see cref="ToolProgress.None"/>.</summary>
    public Func<CancellationToken, IProgress<ToolProgress>, Task<ToolResult>>? Tail { get; }

    /// <summary>A tail without progress notifications.</summary>
    public static ToolStep Then(Func<CancellationToken, Task<ToolResult>> tail) => new(null, (cancel, _) => tail(cancel));

    /// <summary>A tail that reports its progress, for example an export.</summary>
    public static ToolStep Then(Func<CancellationToken, IProgress<ToolProgress>, Task<ToolResult>> tail) => new(null, tail);

    public static implicit operator ToolStep(ToolResult result) => new(result, null);
}
