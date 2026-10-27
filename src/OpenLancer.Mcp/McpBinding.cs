using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenLancer.Data;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Mcp;

/// <summary>
/// The binding of the agent tools to the MCP SDK (section 5.1 of the MCP1 design). Both hosts use it, so the binding
/// exists once: the editor host on Streamable HTTP and <c>oltool mcp serve</c> on stdio. A tool call goes into the
/// <see cref="ToolQueue"/> of the host, so its session part runs on the thread of the session.
/// </summary>
/// <remarks>The binding sets the low-level handlers of the SDK, so the tool table stays free of SDK types. The SDK
/// serves the protocol revision 2026-07-28 and the earlier revisions (decision D737).</remarks>
public static class McpBinding
{
    /// <summary>The name of the server for the client.</summary>
    public const string ServerName = "openlancer";

    /// <summary>The tool list of the whole tool table, in its order, for <c>oltool mcp tools</c>.</summary>
    public static ListToolsResult ToolList() => ToolList(AgentTools.All);

    /// <summary>The tool list of <c>tools/list</c>: the tools of a host, in the order of the tool table.</summary>
    public static ListToolsResult ToolList(IEnumerable<StudioTool> tools) => new() { Tools = tools.Select(Protocol).ToList() };

    /// <summary>The resources of <c>resources/list</c>.</summary>
    public static ListResourcesResult ResourceList() => new()
    {
        Resources = AgentResources.All.Select(r => new Resource
        {
            Uri = r.Uri,
            Name = r.Name,
            Title = r.Title,
            Description = r.Description,
            MimeType = r.MimeType,
        }).ToList(),
    };

    /// <summary>The version of OpenLancer, for the server information.</summary>
    public static string Version =>
        typeof(McpBinding).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    /// <summary>
    /// Sets the server information, the instructions, the capabilities and the handlers of the tools and the resources.
    /// The tool list and the instructions follow the tools of the host. A call of a tool that the host does not have is
    /// a protocol error, as a call of an unknown tool is. Review finding R10 of MCP1-2 asks for this rule.
    /// </summary>
    /// <param name="queue">The queue of the host. Each tool call waits in it for the thread of the session.</param>
    public static void Configure(McpServerOptions options, ToolQueue queue)
    {
        var tools = queue.Host.Tools;
        options.ServerInfo = new Implementation { Name = ServerName, Title = Product.Name, Version = Version };
        options.ServerInstructions = AgentResources.InstructionsFor(tools);
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = false },
            Resources = new ResourcesCapability { ListChanged = false, Subscribe = false },
        };
        options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(ToolList(tools));
        options.Handlers.CallToolHandler = (request, cancel) => Call(queue, tools, request, cancel);
        options.Handlers.ListResourcesHandler = (_, _) => ValueTask.FromResult(ResourceList());
        options.Handlers.ReadResourceHandler = (request, _) => ValueTask.FromResult(Read(request.Params?.Uri));
    }

    /// <summary>The result of a call in the form of the protocol: the JSON as structured content and as one text
    /// block (section 5.7 of the MCP1 design). Each image of a view tool follows the text as an image block (section
    /// 7.7).</summary>
    public static CallToolResult Protocol(ToolResult result) => new()
    {
        Content = [
            new TextContentBlock { Text = result.Text },
            .. result.Images.Select(i => ImageContentBlock.FromBytes(i.Png, ToolImage.MimeType)),
        ],
        StructuredContent = JsonSerializer.SerializeToElement(result.Content),
        IsError = result.IsError,
    };

    private static async ValueTask<CallToolResult> Call(ToolQueue queue, IReadOnlyList<StudioTool> tools,
        RequestContext<CallToolRequestParams> request, CancellationToken cancel)
    {
        var name = request.Params?.Name ?? "";
        if (tools.All(t => t.Name != name))
            throw new McpProtocolException($"The server has no tool '{name}'.", McpErrorCode.InvalidParams);
        JsonObject? arguments = null;
        if (request.Params?.Arguments is { } given)
        {
            arguments = new JsonObject();
            foreach (var (key, value) in given)
                arguments[key] = JsonNode.Parse(value.GetRawText());
        }
        var result = await queue.CallAsync(name, arguments, request.Server.ClientInfo?.Name, cancel).ConfigureAwait(false);
        return Protocol(result);
    }

    private static ReadResourceResult Read(string? uri)
    {
        var resource = uri == null ? null : AgentResources.Find(uri);
        if (resource == null)
            throw new McpProtocolException($"The server has no resource '{uri}'.", McpErrorCode.ResourceNotFound);
        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = resource.Uri, MimeType = resource.MimeType, Text = resource.Read() }],
        };
    }

    private static Tool Protocol(StudioTool tool) => new()
    {
        Name = tool.Name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema),
        OutputSchema = JsonSerializer.SerializeToElement(tool.OutputSchema),
        Annotations = new ToolAnnotations
        {
            Title = tool.Title,
            ReadOnlyHint = tool.Hints.ReadOnly,
            DestructiveHint = tool.Hints.Destructive,
            IdempotentHint = tool.Hints.Idempotent,
            OpenWorldHint = false,
        },
    };
}
