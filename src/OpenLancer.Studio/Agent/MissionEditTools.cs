using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The mission edit tools of section 7.4 of the MCP1 design. Each tool runs mission commands of the M6b layer through
/// <see cref="EditScope.RunMission"/>, in the batch of the call. So a call is one Undo step with the edit origin Agent,
/// it can be a preview or a step of <c>edit_batch</c>, and it runs in strict mode (decision D681 of M6b).
/// </summary>
internal static class MissionEditTools
{
    /// <summary>The detail code of a request that turns strict mode off.</summary>
    public const string StrictMode = "StrictMode";

    /// <summary>The detail code of a <c>mission_edit</c> call with more commands than the limit.</summary>
    public const string MissionCommands = "MissionCommands";

    /// <summary>The JSON Schema of a mission batch (section 4.13 of the M6b design), made once at the first use. It has
    /// about 340 definitions, so the tool table does not make it when it loads.</summary>
    private static readonly Lazy<JsonObject> BatchSchema = new(MissionJson.Schema);

    /// <summary>
    /// The arguments of <c>mission_edit</c>: the fields of a mission batch, flat, with the schemas of
    /// <c>MissionJson.Schema()</c>, and the common arguments of an edit tool. The definitions of the batch schema go to
    /// the root of the input schema, because each <c>$ref</c> of the commands names <c>#/$defs/...</c>.
    /// </summary>
    /// <remarks>The label, <c>if_revision</c> and <c>confirm</c> are the common arguments of the edit tools, so they have
    /// the same form as in the other tools. A call without a label gets the label of its one command, or "Mission
    /// edit".</remarks>
    public static ToolInput EditInput { get; } = new([
        .. EditTools.Input(true,
            new TextParam("label", false, null, null),
            new SchemaParam("commands", true, JsonValueKind.Array, () => Property("commands")),
            new SchemaParam("options", false, JsonValueKind.Object, Options),
            new SchemaParam("expect_versions", false, JsonValueKind.Object, () => Property("expect_versions"))).Parameters,
    ])
    {
        Definitions = () => (JsonObject)BatchSchema.Value["$defs"]!,
    };

    /// <summary>
    /// The tool <c>mission_edit</c>: runs a mission batch of any commands of the M6b layer as one Undo step. The checks
    /// that need no session come first: strict mode, the command limit and the read of the commands.
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/> for
    /// <c>reject_new_errors: false</c>, an unknown field, an unknown kind or a reference that is not valid.
    /// <see cref="EditErrorKind.Limit"/> for more commands than the limit. Each error of the edit call.</exception>
    public static ToolStep Edit(ToolCall call)
    {
        var args = call.Args;
        // After the read of a batch, false and a missing field are the same. So the check reads the JSON of the call.
        if (args.Node("options")?["reject_new_errors"] is JsonValue strict && strict.GetValueKind() == JsonValueKind.False)
        {
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, StrictMode,
                AgentTexts.Error("mission_strict")) { Field = "options.reject_new_errors" });
        }
        var commands = args.Node("commands")!.AsArray();
        // A command group counts as one command. The section limit and the size limit of a request bound its work.
        if (commands.Count > call.Limits.BatchSteps)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, MissionCommands,
                AgentTexts.Error("limit_commands", ("max", call.Limits.BatchSteps))) { Field = "commands" });
        }
        var batch = Read(args, commands);
        return EditTools.Apply(call, scope => scope.RunMission(batch, "commands"));
    }

    /// <summary>The batch of the arguments, read by the JSON reader of the mission layer. An error of a command names
    /// the command by its index, for example <c>commands[2]</c>.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: see <see cref="Edit"/>.</exception>
    private static MissionBatch Read(ToolArgs args, JsonArray commands)
    {
        var read = new List<MissionCommand>(commands.Count);
        for (var i = 0; i < commands.Count; i++)
        {
            try
            {
                read.Add(MissionJson.ReadCommand(commands[i]?.ToJsonString() ?? "null"));
            }
            catch (EditException e)
            {
                throw new EditException(e.Error with { Index = i, Field = $"commands[{i}]" });
            }
        }
        var shell = new JsonObject { ["label"] = args.Text("label") ?? "", ["commands"] = new JsonArray() };
        foreach (var name in new[] { "options", "expect_versions" })
        {
            if (args.Node(name) is { } value)
                shell[name] = value.DeepClone();
        }
        try
        {
            return MissionJson.ReadBatch(shell.ToJsonString()) with { Commands = read };
        }
        catch (EditException e) when (e.Error.Detail == MissionErrors.UnknownField)
        {
            // The input check refuses an unknown argument, so an unknown field is a field of the options.
            throw new EditException(e.Error with { Field = "options." + e.Error.Field });
        }
    }

    /// <summary>The schema of a field of the batch, without its help: <c>agent-tools.json</c> gives the help of each
    /// argument.</summary>
    private static JsonObject Property(string name)
    {
        var property = (JsonObject)BatchSchema.Value["properties"]![name]!.DeepClone();
        property.Remove("description");
        return property;
    }

    /// <summary>The schema of the options. The tool always runs in strict mode, so the default of
    /// <c>reject_new_errors</c> is true here.</summary>
    private static JsonObject Options()
    {
        var options = Property("options");
        options["properties"]!["reject_new_errors"]!["default"] = true;
        return options;
    }
}
