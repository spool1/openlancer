using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Data.Missions.Run;

/// <summary>A request of the dry run query (section 7.3 of the M6c design).</summary>
/// <param name="Script">The install path of the script.</param>
public sealed record DryRunRequest(string Script, IReadOnlyList<DryRunInput> Inputs)
{
    /// <summary>The bound of the timer ranges.</summary>
    public TimerBound Timers { get; init; } = TimerBound.Shortest;

    /// <summary>The result gives the steps after this step, and always the full state after the last step.</summary>
    public int SinceStep { get; init; }
}

/// <summary>The limits of one dry run request (section 7.4 of the M6c design). The request runs on the thread of the
/// session, so the limits keep the editor responsive.</summary>
public sealed record DryRunLimits
{
    public static readonly DryRunLimits Default = new();

    /// <summary>The most inputs of one request.</summary>
    public int Inputs { get; init; } = 1_000;

    /// <summary>The most heartbeats of all inputs of one request: one hour of game time. "Next timer" counts with its
    /// most heartbeats.</summary>
    public int Heartbeats { get; init; } = 108_000;
}

/// <summary>A change of a step of the result.</summary>
/// <param name="Trigger">The <c>trg:</c> reference of the trigger of the change, or null for a note.</param>
/// <param name="Text">The row of the Run log.</param>
public sealed record DryRunChangeView(TraceChangeKind Kind, string? Trigger, string? Text)
{
    /// <summary>The trigger name of an Ignored or a Checkpoint change, else null.</summary>
    public string? Name { get; init; }

    /// <summary>The <c>line:</c> reference of the action line of the change, or null.</summary>
    public string? Line { get; init; }

    /// <summary>How many times the change happened in the step.</summary>
    public int Count { get; init; } = 1;
}

/// <summary>A step of the result: its number, its cause, its changes and the heartbeats at its end.</summary>
public sealed record DryRunStepView(int Step, string Cause, IReadOnlyList<DryRunChangeView> Changes, int Heartbeats);

/// <summary>A waiting condition line of the result: one event or fact that active triggers wait for.</summary>
/// <param name="Line">The line: the condition and each named value that it has. An <c>event</c> or a <c>fact</c>
/// input can give it back.</param>
/// <param name="Sentence">The condition sentence of the schema, with the count of a counting condition.</param>
/// <param name="Triggers">The <c>trg:</c> references of the triggers that wait, in the order of the active list.</param>
public sealed record WaitingEventView(LineSpec Line, string Sentence, WorldLineKind Kind, IReadOnlyList<string> Triggers)
{
    /// <summary>The matches of a counting condition so far, or null.</summary>
    public int? Count { get; init; }

    /// <summary>The matches that a counting condition needs, or null.</summary>
    public int? Needed { get; init; }

    /// <summary>"Sent at step {n}" or "Spawned at step {n}" when the run makes the line likely, else null.</summary>
    public string? Note { get; init; }
}

/// <summary>The facts about the player.</summary>
/// <param name="System">The <c>system:</c> reference of the system of the player, or null.</param>
public sealed record PlayerView(string? System, bool InSpace);

/// <summary>The end of the mission: the kind, the <c>trg:</c> reference of the trigger, the fail text id, and the row
/// of the Run log.</summary>
public sealed record MissionEndView(MissionEndKind Kind, string Trigger, string? TextId, string Text);

/// <summary>The stop of the run before the end of the mission: the kind, the <c>trg:</c> reference of the trigger, and
/// the message.</summary>
public sealed record MissionStopView(MachineHaltKind Kind, string Trigger, string Message);

/// <summary>The full state after the last step of a dry run.</summary>
/// <param name="Active">The <c>trg:</c> references of the active list, in its order.</param>
/// <param name="Pending">The <c>trg:</c> references of the pending starts, in the order of the next drain.</param>
/// <param name="Facts">The facts that are true.</param>
public sealed record DryRunStateView(int Heartbeats, IReadOnlyList<string> Active, IReadOnlyList<string> Pending,
    IReadOnlyList<WaitingEventView> WaitingEvents, IReadOnlyList<LineSpec> Facts, PlayerView Player)
{
    public MissionEndView? End { get; init; }

    public MissionStopView? Stop { get; init; }
}

/// <summary>The result of the dry run query: the steps from <see cref="DryRunRequest.SinceStep"/>, and the full state
/// after the last step.</summary>
/// <param name="Script">The <c>msn:</c> reference of the script.</param>
/// <param name="Version">The version of the script that the run read (section 4.2 of the M6b design).</param>
/// <param name="TotalSteps">The number of all steps of the run.</param>
public sealed record DryRunView(string Script, long Version, int TotalSteps, IReadOnlyList<DryRunStepView> Steps,
    DryRunStateView State);

/// <summary>
/// The JSON forms of the dry run (section 7.3 of the M6c design): the inputs as objects with a <c>kind</c>, the
/// requests and the results. All names use snake case, and items use the reference strings of section 4.0.7 of the M6b
/// design. A line of an <c>event</c> or a <c>fact</c> input is <c>{key, values}</c>, as a mission line of section 4.3 of
/// the M6b design.
/// </summary>
public static partial class RunJson
{
    /// <summary>The detail code of an input field that the kind does not have.</summary>
    public const string UnknownField = MissionErrors.UnknownField;

    private static readonly Dictionary<string, (DryRunInputKind Kind, string[] Fields)> Kinds = new(StringComparer.Ordinal)
    {
        ["start_mission"] = (DryRunInputKind.StartMission, []),
        ["load_checkpoint"] = (DryRunInputKind.LoadCheckpoint, ["trigger"]),
        ["start_at"] = (DryRunInputKind.StartAt, ["trigger"]),
        ["run_trigger"] = (DryRunInputKind.RunTrigger, ["trigger"]),
        ["event"] = (DryRunInputKind.Event, ["line"]),
        ["fact"] = (DryRunInputKind.Fact, ["line", "on"]),
        ["heartbeats"] = (DryRunInputKind.Heartbeats, ["count", "until_timer"]),
        ["player_system"] = (DryRunInputKind.PlayerSystem, ["system"]),
        ["player_in_space"] = (DryRunInputKind.PlayerInSpace, ["value"]),
    };

    /// <summary>The <c>kind</c> names of the inputs.</summary>
    public static IReadOnlyCollection<string> KindNames => Kinds.Keys;

    /// <summary>Reads the inputs of a request.</summary>
    /// <param name="script">The install path of the script. Each trigger reference must name a trigger of it.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: an input does not fit the
    /// schema.</exception>
    public static IReadOnlyList<DryRunInput> ReadInputs(JsonArray inputs, string script) =>
        inputs.Select((node, i) => ReadInput(node, script, i)).ToList();

    /// <summary>Reads one input.</summary>
    /// <param name="index">The place of the input in its list, for the error.</param>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the input does not fit the
    /// schema.</exception>
    public static DryRunInput ReadInput(JsonNode? node, string script, int index = 0)
    {
        if (node is not JsonObject input)
            throw Bad(index, null, "Each input is an object with a kind, for example {\"kind\": \"start_mission\"}.");
        if (input["kind"] is not JsonValue kindValue || !kindValue.TryGetValue(out string? kindName) ||
            !Kinds.TryGetValue(kindName, out var spec))
            throw Bad(index, "kind", $"The kind of an input is one of: {string.Join(", ", Kinds.Keys)}.");
        foreach (var (name, _) in input)
        {
            if (name != "kind" && !spec.Fields.Contains(name, StringComparer.Ordinal))
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, UnknownField,
                    $"Input {Place(index)} of the kind {kindName} has no field '{name}'. Its fields are: " +
                    $"{(spec.Fields.Length == 0 ? "none" : string.Join(", ", spec.Fields))}.") { Field = $"inputs[{Place(index)}].{name}" });
        }
        return spec.Kind switch
        {
            DryRunInputKind.StartMission => DryRunInput.StartMission(),
            DryRunInputKind.LoadCheckpoint => DryRunInput.LoadCheckpoint(Trigger(input, script, index)),
            DryRunInputKind.StartAt => DryRunInput.StartAt(Trigger(input, script, index)),
            DryRunInputKind.RunTrigger => DryRunInput.RunTrigger(Trigger(input, script, index)),
            DryRunInputKind.Event => DryRunInput.Event(Line(input, index, fact: false)),
            DryRunInputKind.Fact => DryRunInput.Fact(Line(input, index, fact: true), Bool(input, "on", index)),
            DryRunInputKind.Heartbeats => Heartbeats(input, index),
            DryRunInputKind.PlayerSystem => DryRunInput.PlayerSystem(System(input, index)),
            _ => DryRunInput.PlayerInSpace(Bool(input, "value", index)),
        };
    }

    /// <summary>The JSON form of an input.</summary>
    /// <param name="script">The install path of the script of the trigger references.</param>
    public static JsonObject Write(DryRunInput input, string script)
    {
        var kind = Kinds.First(k => k.Value.Kind == input.Kind).Key;
        var result = new JsonObject { ["kind"] = kind };
        switch (input.Kind)
        {
            case DryRunInputKind.LoadCheckpoint or DryRunInputKind.StartAt or DryRunInputKind.RunTrigger:
                result["trigger"] = new TriggerRef(script, input.Trigger!).ToString();
                break;
            case DryRunInputKind.Event:
                result["line"] = JsonSerializer.SerializeToNode(input.Line, EditJson.Options);
                break;
            case DryRunInputKind.Fact:
                result["line"] = JsonSerializer.SerializeToNode(input.Line, EditJson.Options);
                result["on"] = input.Value;
                break;
            case DryRunInputKind.Heartbeats when input.UntilTimer:
                result["until_timer"] = true;
                break;
            case DryRunInputKind.Heartbeats:
                result["count"] = input.Count;
                break;
            case DryRunInputKind.PlayerSystem:
                result["system"] = input.System == null
                    ? null
                    : ItemRefs.Format(new ItemReference(ItemRefKind.System, input.System));
                break;
            case DryRunInputKind.PlayerInSpace:
                result["value"] = input.Value;
                break;
        }
        return result;
    }

    /// <summary>
    /// Reads a request of the dry run query: <c>script</c>, <c>inputs</c>, <c>timer_bound</c> (<c>shortest</c> or
    /// <c>longest</c>) and <c>since_step</c> (default 0).
    /// </summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.InvalidArgument"/>: the request does not fit the
    /// schema.</exception>
    public static DryRunRequest ReadRequest(JsonObject request)
    {
        if (request["script"] is not JsonValue scriptValue || !scriptValue.TryGetValue(out string? scriptText))
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.BadJson,
                "The request has no script. Give the msn: reference of a script.") { Field = "script" });
        var script = ScriptRef.Parse(scriptText).Path;
        var inputs = request["inputs"] is JsonArray array ? ReadInputs(array, script)
            : request["inputs"] is null ? []
            : throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.BadJson,
                "The inputs are a list of input objects.") { Field = "inputs" });
        var bound = request["timer_bound"];
        var timers = bound == null ? TimerBound.Shortest
            : bound is JsonValue boundValue && boundValue.TryGetValue(out string? word) && word is "shortest" or "longest"
                ? word == "longest" ? TimerBound.Longest : TimerBound.Shortest
                : throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.BadJson,
                    "The timer bound is shortest or longest.") { Field = "timer_bound" });
        var sinceNode = request["since_step"];
        var since = sinceNode == null ? 0 : sinceNode is JsonValue sinceValue && sinceValue.TryGetValue(out int step) ? step : -1;
        if (since < 0)
            throw new EditException(new EditError(EditErrorKind.InvalidArgument, MissionErrors.BadJson,
                "The step since_step is 0 or more.") { Field = "since_step" });
        return new DryRunRequest(script, inputs) { Timers = timers, SinceStep = since };
    }

    /// <summary>Checks the limits of a request (section 7.4 of the M6c design).</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/>: the request has too many inputs or
    /// heartbeats.</exception>
    public static void CheckLimits(IReadOnlyList<DryRunInput> inputs, DryRunLimits limits)
    {
        if (inputs.Count > limits.Inputs)
            throw new EditException(new EditError(EditErrorKind.Limit, "TooManyInputs",
                $"The request has {Number(inputs.Count)} inputs. One request takes at most {Number(limits.Inputs)} " +
                "inputs. Send fewer inputs.") { Field = "inputs" });
        var heartbeats = inputs.Sum(i => (long)i.MostHeartbeats);
        if (heartbeats > limits.Heartbeats)
            throw new EditException(new EditError(EditErrorKind.Limit, "TooManyHeartbeats",
                $"The inputs run up to {Number(heartbeats)} heartbeats. One request runs at most " +
                $"{Number(limits.Heartbeats)} heartbeats, one hour of game time. Send fewer heartbeats.") { Field = "inputs" });
    }

    /// <summary>The result of a fold: the steps after a step, and the full state after the last step.</summary>
    /// <param name="script">The install path of the script.</param>
    public static DryRunView View(string script, long version, DryRunState state, int sinceStep)
    {
        var index = state.Machine.Index;
        string Ref(string trigger) => new TriggerRef(script, trigger).ToString();
        string Of(MissionTrigger trigger) => Ref(trigger.Nickname);
        var steps = state.Trace.Steps.Skip(sinceStep).Select(step => new DryRunStepView(step.Number, step.Cause,
            step.Changes.Select(change => new DryRunChangeView(change.Kind,
                change.Trigger.Length > 0 ? Ref(change.Trigger) : null, change.LogText ?? Kind(change.Kind))
            {
                Name = change.Name,
                Count = change.Count,
                Line = change.Row >= 0 && index.Trigger(change.Trigger) is { } trigger
                    ? new RowRef(new ItemRef(script, trigger.Section.Ref.Id), change.Row).Reference
                    : null,
            }).ToList(), step.Heartbeats)).ToList();
        var machine = state.Machine;
        var world = state.World;
        var facts = WorldEvents.FactLines(index).Where(world.IsFact).Select(WorldEvents.PatternOf).ToList();
        var view = new DryRunStateView(machine.Heartbeats, machine.Active.Select(Of).ToList(),
            machine.Pending.Select(Of).ToList(),
            state.Waiting.Select(w => new WaitingEventView(WorldEvents.PatternOf(w.Line), w.Sentence, w.Kind,
                w.Triggers.Select(Of).ToList())
            {
                Count = w.Count?.Count,
                Needed = w.Count?.Needed,
                Note = w.Cause?.Text,
            }).ToList(),
            facts,
            new PlayerView(world.PlayerSystem == null ? null : ItemRefs.Format(new ItemReference(ItemRefKind.System,
                world.PlayerSystem)), world.InSpace))
        {
            End = machine.End is { } end ? new MissionEndView(end.Kind, Of(end.Trigger), end.TextId, DryRun.EndText(end)) : null,
            Stop = machine.Halt is { } halt ? new MissionStopView(halt.Kind, Of(halt.Trigger), halt.Message) : null,
        };
        return new DryRunView(new ScriptRef(script).ToString(), version, state.Trace.Steps.Count, steps, view);
    }

    /// <summary>The snake case name of a change kind, the text of a change without a row.</summary>
    private static string Kind(TraceChangeKind kind) => JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString());

    private static string Trigger(JsonObject input, string script, int index)
    {
        if (input["trigger"] is not JsonValue value || !value.TryGetValue(out string? text) || text.Trim().Length == 0)
            throw Bad(index, "trigger", "The input needs a trigger: the trg: reference or the nickname of a trigger of the script.");
        // A nickname names a trigger of the script, as the field "to" of the trigger tools does (final finding 22 of
        // MCP1-3).
        if (!text.Contains(':', StringComparison.Ordinal))
            return text.Trim();
        TriggerRef trigger;
        try
        {
            trigger = TriggerRef.Parse(text);
        }
        catch (EditException e)
        {
            throw Bad(index, "trigger", e.Error.Message);
        }
        if (!VfsPath.Comparer.Equals(trigger.Script, script))
            throw Bad(index, "trigger", $"The trigger {text} is not in the script of the request. Use a trigger of {script}.");
        return trigger.Name;
    }

    private static LineSpec Line(JsonObject input, int index, bool fact)
    {
        LineSpec? line;
        try
        {
            line = input["line"] is JsonObject node ? node.Deserialize<LineSpec>(EditJson.Options) : null;
        }
        catch (Exception e) when (e is JsonException or MissionJsonException or InvalidOperationException)
        {
            line = null;
        }
        if (line == null || line.Texts != null || line.Key.Trim().Length == 0)
            throw Bad(index, "line", "The input needs a line {key, values}, for example {\"key\": \"Cnd_Destroyed\", " +
                                     "\"values\": {\"target\": \"my_ship\"}}.");
        var name = MissionSchema.Retail.Name(line.Key);
        var kind = WorldEvents.KindOf(name);
        if (kind == null || fact && kind == WorldLineKind.Event || !fact && kind == WorldLineKind.Fact)
            throw Bad(index, "line", fact
                ? $"{line.Key} is not a condition that the game tests at each heartbeat for the world. A fact names such a " +
                  "condition, for example Cnd_InSpace."
                : $"{line.Key} is not a condition that waits for an event of the world. An event names such a condition, " +
                  "for example Cnd_Destroyed.");
        foreach (var valueName in line.Values.Keys)
        {
            if (name!.Value(valueName) == null && !name.Values.Any(v => v.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase)))
                throw Bad(index, "line", $"{name.Name} has no value '{valueName}'. Its values are: " +
                                         $"{string.Join(", ", name.Values.Select(v => v.Name))}.");
        }
        return line with { Key = name!.Name };
    }

    private static DryRunInput Heartbeats(JsonObject input, int index)
    {
        var until = input["until_timer"] is JsonValue untilValue && untilValue.TryGetValue(out bool flag) && flag;
        if (until)
        {
            if (input.ContainsKey("count"))
                throw Bad(index, "count", "An input with until_timer has no count.");
            return DryRunInput.NextTimer();
        }
        if (input["count"] is not JsonValue countValue || !countValue.TryGetValue(out int count) || count < 1 ||
            count > DryRun.MaxHeartbeats)
            throw Bad(index, "count", $"The count of heartbeats is a whole number from 1 to {Number(DryRun.MaxHeartbeats)}. " +
                                      "Or give until_timer: true.");
        return DryRunInput.Heartbeats(count);
    }

    private static string? System(JsonObject input, int index)
    {
        if (!input.ContainsKey("system"))
            throw Bad(index, "system", "The input needs a system: a system: reference, or null for no system.");
        if (input["system"] is null)
            return null;
        if (input["system"] is not JsonValue value || !value.TryGetValue(out string? text) ||
            ItemRefs.Parse(text) is not { Kind: ItemRefKind.System } reference)
            throw Bad(index, "system", "The system is a system: reference, for example system:ol_system.");
        return reference.Name;
    }

    private static bool Bool(JsonObject input, string field, int index) =>
        input[field] is JsonValue value && value.TryGetValue(out bool result)
            ? result
            : throw Bad(index, field, $"The input needs {field}: true or false.");

    private static EditException Bad(int index, string? field, string message) =>
        new(new EditError(EditErrorKind.InvalidArgument, MissionErrors.BadJson, $"Input {Place(index)}: {message}")
        {
            Field = field == null ? $"inputs[{Place(index)}]" : $"inputs[{Place(index)}].{field}",
        });

    private static string Place(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
