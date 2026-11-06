using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OpenLancer.Studio.Agent;

/// <summary>An argument of a prompt. Each argument of a prompt is required.</summary>
public sealed record AgentPromptArgument(string Name, string Description);

/// <summary>
/// A prompt of the agent server (section 9.4 of the MCP1 design): the steps of a workflow of section 9.5, as one
/// message to the agent. In Claude Code, a prompt shows as a slash command.
/// </summary>
/// <param name="Tools">The tools that the steps need. A host lists the prompt only when it has each of them.</param>
/// <param name="OptionalTools">The tools that the steps name, but do not need. The steps tell what to do without such a
/// tool. An example is <c>save</c>, which a host with <c>--no-save</c> does not have.</param>
public sealed record AgentPrompt(string Name, string Title, string Description,
    IReadOnlyList<AgentPromptArgument> Arguments, IReadOnlyList<string> Tools, IReadOnlyList<string> OptionalTools,
    IReadOnlyList<string> Lines)
{
    /// <summary>The message of the prompt: its lines, with the value of each argument in place of its
    /// <c>{name}</c>.</summary>
    /// <param name="values">The value of each argument, by name. The caller checks that each argument has a
    /// value.</param>
    public string Text(IReadOnlyDictionary<string, string> values)
    {
        var text = string.Join("\n", Lines);
        foreach (var argument in Arguments)
            text = text.Replace("{" + argument.Name + "}", values[argument.Name]);
        return text;
    }
}

/// <summary>The prompts of this build. The texts of each prompt are in <c>agent-tools.json</c>.</summary>
public static class AgentPrompts
{
    /// <summary>The prompts in the order of <c>prompts/list</c>.</summary>
    public static IReadOnlyList<AgentPrompt> All { get; } = AgentTexts.Prompts.Select(p => new AgentPrompt(p.Key,
        p.Value.Title, p.Value.Description, [.. p.Value.Arguments.Select(a => new AgentPromptArgument(a.Name, a.Help))],
        [.. ToolsOf(p.Value.Text).Except(p.Value.OptionalTools)], p.Value.OptionalTools, p.Value.Text)).ToList();

    /// <summary>The prompts of a host with a tool set: each prompt whose steps need only tools of the host. So a host
    /// that only reads lists no prompt of an edit workflow.</summary>
    public static IReadOnlyList<AgentPrompt> For(IEnumerable<StudioTool> tools)
    {
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return All.Where(p => p.Tools.All(names.Contains)).ToList();
    }

    /// <summary>The names of the tools of the table that the lines name, as whole words.</summary>
    private static List<string> ToolsOf(IReadOnlyList<string> lines)
    {
        var text = string.Join("\n", lines);
        return AgentTools.All.Select(t => t.Name)
            .Where(name => Regex.IsMatch(text, $@"(?<![\w]){Regex.Escape(name)}(?![\w])"))
            .ToList();
    }
}
