using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ModelContextProtocol;
using OpenLancer.Data.Mods;
using OpenLancer.Mcp;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Tools;

/// <summary>
/// The commands <c>oltool mcp</c> of the agent server (section 6.2 of the MCP1 design): <c>serve</c>, <c>replay</c>,
/// <c>token</c> and <c>tools</c>. The server without a window takes edits and saves, unless <c>--read-only</c> or
/// <c>--no-save</c> stops it. <c>replay</c> runs the edits of a log on the profile that it gets.
/// </summary>
internal static class McpCommands
{
    public const string Usage = """
          mcp serve <profile.json> --mod <id> [--read-only] [--no-save]
                    [--restore-recovery | --discard-recovery]
                                            Serve the agent tools on stdin and stdout (the MCP stdio transport). The
                                            server opens the profile with the mod as the active mod and takes its lock.
                                            It writes the unsaved edits to the recovery file of the mod 2 seconds after
                                            the last edit, and at the end of stdin. --read-only: the server only reads
                                            and writes no file. --no-save: the server has no save and no export.
                                            --restore-recovery or --discard-recovery: restore or delete the unsaved
                                            edits of an earlier session. Without them, a recovery file stops the edits.
                                            The server writes its own messages to stderr.
          mcp replay <profile.json> --mod <id> <calls.jsonl> [--preview]
                                            Run each call of an audit log or of a file of calls through the agent
                                            tools, and write one result line for each call. Exit 1 at the first result
                                            that differs from the recorded result, or at an error of a call without a
                                            recorded result. The edits change the profile: give a copy. The replay saves
                                            only at a recorded save and writes no recovery file. --preview: change no
                                            file. The edits change only the data in memory, and the replay skips the
                                            saves, the exports and the prop group writes.
          mcp token                         Print the header with the token of the agent server of the editor, as JSON,
                                            for the headersHelper of Claude Code. Exit 2 and print nothing when no
                                            editor server runs, or when CLAUDE_CODE_MCP_SERVER_URL names no running
                                            server on 127.0.0.1. The helper gives no token for localhost.
          mcp tools [--json]                Print the agent tools: names, titles and descriptions. --json: print the
                                            tool list of the MCP protocol with the schemas and the hints.
        """;

    /// <param name="output">The standard output of oltool. Only <c>tools</c> and <c>replay</c> write to it.</param>
    /// <param name="error">The standard error of oltool.</param>
    /// <returns>The exit code, or null for arguments that are not an <c>mcp</c> command.</returns>
    public static int? Run(string[] args, TextWriter output, TextWriter error) => args switch
    {
        ["serve", var profile, .. var options] => Serve(profile, options, error),
        ["replay", var profile, .. var options] => Replay(profile, options, output, error),
        ["token"] => Token(output, error),
        ["tools"] => Tools(output, json: false),
        ["tools", "--json"] => Tools(output, json: true),
        _ => null,
    };

    private static int Serve(string profile, string[] options, TextWriter error)
    {
        string? mod = null;
        bool readOnly = false, noSave = false, restore = false, discard = false;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--mod" when mod == null && i + 1 < options.Length:
                    mod = options[++i];
                    break;
                case "--read-only" when !readOnly:
                    readOnly = true;
                    break;
                case "--no-save" when !noSave:
                    noSave = true;
                    break;
                case "--restore-recovery" when !restore:
                    restore = true;
                    break;
                case "--discard-recovery" when !discard:
                    discard = true;
                    break;
                default:
                    return Fail(error, $"Unknown argument {options[i]}. Usage:\n{Usage}");
            }
        }
        if (mod == null)
            return Fail(error, $"The server needs the active mod. Add --mod <id>. Usage:\n{Usage}");
        if (restore && discard)
            return Fail(error, "Give --restore-recovery or --discard-recovery, not both.");
        if (readOnly && (restore || discard))
            return Fail(error, "A server with --read-only does not read the recovery file. Remove --read-only to restore " +
                               "or discard the unsaved edits.");

        var recovery = restore ? RecoveryChoice.Restore : discard ? RecoveryChoice.Discard : RecoveryChoice.Keep;
        SessionThread thread;
        try
        {
            thread = SessionThread.Start(() => readOnly
                ? HeadlessHost.OpenReadOnly(profile, mod)
                : HeadlessHost.Open(profile, mod,
                    new HeadlessOptions { NoSave = noSave, Recovery = recovery, Messages = error }));
        }
        catch (Exception e) when (e is ModLoadException or ArgumentException or IOException or UnauthorizedAccessException
                                      or InvalidDataException)
        {
            return Fail(error, e.Message);
        }
        using (thread)
        {
            var host = thread.Host;
            var state = readOnly ? "read-only"
                : host.NoEditReason is { } reason ? $"no edits: {reason}"
                : noSave ? "edits, no save"
                : "edits";
            error.WriteLine($"oltool mcp serve: profile {host.Session!.Profile.Name}, mod {mod}, {state}. The server reads " +
                            "the MCP messages on stdin.");
            McpServe.ServeStdioAsync(thread, CancellationToken.None).GetAwaiter().GetResult();
        }
        return 0;
    }

    private static int Replay(string profile, string[] options, TextWriter output, TextWriter error)
    {
        var preview = options.Contains("--preview");
        if (options.Where(o => o != "--preview").ToArray() is not ["--mod", var mod, var calls] ||
            options.Count(o => o == "--preview") > 1)
            return Fail(error, $"Usage:\n{Usage}");
        if (!File.Exists(calls))
            return Fail(error, $"The file {calls} does not exist.");
        try
        {
            // The replay keeps no unsaved edits: it saves only at a recorded save.
            using var host = HeadlessHost.Open(profile, mod, new HeadlessOptions { WritesRecovery = false, Messages = error });
            using var reader = new StreamReader(calls);
            if (AgentReplay.Run(host, reader, output, preview) is { } difference)
            {
                error.WriteLine(difference);
                return 1;
            }
            return 0;
        }
        catch (Exception e) when (e is ModLoadException or ArgumentException or InvalidDataException or IOException
                                      or UnauthorizedAccessException)
        {
            return Fail(error, e.Message);
        }
    }

    /// <summary>The token helper (section 6.1, item 7 of the MCP1 design). Claude Code gives the address of the server
    /// in CLAUDE_CODE_MCP_SERVER_URL. The helper reads no other variable.</summary>
    private static int Token(TextWriter output, TextWriter error)
    {
        var (headers, problem) = AgentDiscovery.Headers(AgentDiscovery.DefaultFolder,
            Environment.GetEnvironmentVariable("CLAUDE_CODE_MCP_SERVER_URL"));
        if (headers == null)
            return Fail(error, problem!);
        output.WriteLine(headers);
        return 0;
    }

    private static int Tools(TextWriter output, bool json)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(McpBinding.ToolList(),
                new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { WriteIndented = true }));
            return 0;
        }
        foreach (var tool in AgentTools.All)
        {
            var host = tool.Hosts == ToolHosts.Editor ? " (editor only)" : "";
            output.WriteLine($"{tool.Name}: {tool.Title}{host}");
            output.WriteLine($"    {tool.Description}");
        }
        return 0;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }
}
