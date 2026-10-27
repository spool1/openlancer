using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor.Editing;

/// <summary>What the command runner needs of the editor. The editor window gives it, and a test gives a fake.</summary>
internal interface ICommandHost
{
    /// <summary>The edit session of the open profile, or null when no profile is open.</summary>
    EditSession? Session { get; }

    /// <summary>Why the editor takes no edits now, or null (see <see cref="EditorSession.NoEditReason"/>).</summary>
    string? NoEditReason { get; }

    /// <summary>The Console.</summary>
    ConsoleLog Log { get; }
}

/// <summary>
/// The one class of the editor that runs the commands, the batches and the gestures of the command layer (section 5.2,
/// item 5 of the MCP1 design, decision D766). A gesture of the user makes a plan through a command of the layer, and the
/// runner runs it as one entry of the edit history with the edit origin User (section 8.1 of the MCP1 design). The
/// agent tools run the same commands with the edit origin Agent.
/// </summary>
/// <remarks>
/// <para>The runner writes each refusal and each problem of an edit to the Console with the source "Edit". A plan that
/// refuses gives an error for each refusal. When the editor takes no edits, the runner gives a warning with the reason
/// and runs nothing. After an edit, the warnings of its plan follow.</para>
/// <para>Only the cutscene callers still run session commands through <see cref="Execute(EditCommand)"/>, until
/// milestone MCP1-4 moves them to the cutscene commands. Outside this class, a test of the editor assembly fails when
/// another type makes a session command, gets a delegate that makes one, runs one, or calls <c>Execute</c>
/// (<c>EditorLayerTests</c>).</para>
/// </remarks>
internal sealed class CommandRunner(ICommandHost host)
{
    /// <summary>The Console source of the edits.</summary>
    public const string Source = EditController.Source;

    private ConsoleLog Log => host.Log;

    /// <summary>
    /// Runs the session command of a plan in a batch with the edit origin User, as one entry of the edit history with
    /// the label of the plan. When the plan refuses, the editor takes no edits or the command does not apply, the
    /// Console tells why and nothing changes. After the edit, the warnings of the plan go to the Console.
    /// </summary>
    /// <remarks>A plan reads the edit view when it is made, so make it and run it at once. A plan that waited for a
    /// dialog runs only when the edit view did not change in between.</remarks>
    /// <returns>The result of the batch, or a result with the status <see cref="EditStatus.Refused"/>.</returns>
    public EditResult Run(EditPlan plan)
    {
        if (Refusal(plan.Label) is { } refused)
            return refused;
        var session = host.Session!;
        if (!plan.CanRun)
        {
            foreach (var refusal in plan.Refusals)
                Log.Add(ConsoleSeverity.Error, Source, refusal.Message, refusal.Target);
            return plan.Report(session) with { Revision = session.Revision };
        }

        EditResult result;
        try
        {
            using var batch = session.BeginBatch(plan.Label, EditOrigin.User);
            batch.Run(plan.Command!);
            batch.Report(plan.Report(session));
            result = batch.Commit();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or EditException)
        {
            var error = e is EditException { Error: var edit }
                ? edit
                : new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, ErrorText.Of(e));
            Log.Add(ConsoleSeverity.Error, Source, error.Message);
            return new EditResult(EditStatus.Refused) { Label = plan.Label, Errors = [error], Revision = session.Revision };
        }

        foreach (var error in result.Errors)
            Log.Add(ConsoleSeverity.Error, Source, error.Message);
        if (result.Status == EditStatus.Refused)
            return result;
        foreach (var warning in plan.Warnings)
            Log.Add(ConsoleSeverity.Warning, Source, warning.Message, warning.Target);
        return result;
    }

    /// <summary>
    /// Makes a plan from the edit view now and runs it, as <see cref="Run(EditPlan)"/> does. When the plan cannot be
    /// made, the Console tells why and nothing changes.
    /// </summary>
    public EditResult Run(Func<EditSession, EditPlan> make)
    {
        if (Refusal("") is { } refused)
            return refused;
        EditPlan plan;
        try
        {
            plan = make(host.Session!);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Add(ConsoleSeverity.Error, Source, ErrorText.Of(e));
            return new EditResult(EditStatus.Refused)
            {
                Errors = [new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, ErrorText.Of(e))],
                Revision = host.Session!.Revision,
            };
        }

        return Run(plan);
    }

    /// <summary>
    /// Opens a gesture with many steps, for example a drag of the gizmo or of a number field: one entry of the edit
    /// history with the label (see <see cref="CommandTransaction"/>). The caller owns it: it commits or cancels it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No profile is open, the editor takes no edits (the message is the
    /// reason), or a transaction or a batch is open.</exception>
    public CommandTransaction Gesture(string label)
    {
        if (host.Session is not { } session)
            throw new InvalidOperationException(NoProfile);
        if (host.NoEditReason is { } reason)
            throw new InvalidOperationException(reason);
        return CommandTransaction.Begin(session, label);
    }

    /// <summary>
    /// Opens a gesture (see <see cref="Gesture(string)"/>) and starts a drag or a field edit in it. When
    /// <paramref name="start"/> throws, the gesture cancels and the exception goes to the caller, so no transaction stays
    /// open.
    /// </summary>
    /// <param name="start">Starts the drag or the field edit in the gesture. The drag or the edit then owns the
    /// gesture.</param>
    /// <exception cref="InvalidOperationException">The gesture cannot open (see <see cref="Gesture(string)"/>).</exception>
    public T Gesture<T>(string label, Func<CommandTransaction, T> start)
    {
        var gesture = Gesture(label);
        try
        {
            return start(gesture);
        }
        catch
        {
            gesture.Cancel();
            throw;
        }
    }

    /// <summary>
    /// Runs a batch of mission commands through the mission editor of the session (decision D660 of the M6b design). A
    /// refusal changes nothing, and the Console tells why. A batch that needs a confirmation tells nothing here, because
    /// the caller asks in a dialog. Returns the result, or null when no profile is open or the editor takes no edits.
    /// </summary>
    public MissionEditResult? Apply(MissionBatch batch)
    {
        if (host.Session is not { } session)
            return null;
        if (host.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return null;
        }

        var result = session.Missions.Apply(batch);
        foreach (var error in result.NeedsConfirmation && !batch.Confirm ? [] : result.Errors)
            Log.Add(ConsoleSeverity.Error, MissionChecks.Source, error.Message);
        foreach (var note in result.Notes)
            Log.Add(ConsoleSeverity.Info, MissionChecks.Source, note);
        return result;
    }

    /// <summary>
    /// Runs a session command as one entry of the edit history. When the editor takes no edits, or the command does not
    /// apply, the Console tells why and the result is false.
    /// </summary>
    public bool Execute(EditCommand command)
    {
        if (host.Session is not { } session)
            return false;
        if (host.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return false;
        }

        try
        {
            session.Execute(command);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Add(ConsoleSeverity.Error, Source, ErrorText.Of(e));
            return false;
        }
    }

    /// <summary>
    /// Makes a session command and runs it, for a command that reads the edit view when it is made. When the command
    /// cannot be made, the Console tells why and the result is false.
    /// </summary>
    public bool Execute(Func<EditCommand> make)
    {
        if (host.NoEditReason is { } reason)
        {
            Log.Add(ConsoleSeverity.Warning, Source, reason);
            return false;
        }

        EditCommand command;
        try
        {
            command = make();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Log.Add(ConsoleSeverity.Error, Source, ErrorText.Of(e));
            return false;
        }

        return Execute(command);
    }

    /// <summary>The text of a request while no profile is open.</summary>
    private const string NoProfile = "No profile is open.";

    /// <summary>The result of a request that cannot run now, or null: no profile is open, or the editor takes no edits.
    /// The Console gives the reason why the editor takes no edits.</summary>
    private EditResult? Refusal(string label)
    {
        if (host.Session is not { } session)
        {
            return new EditResult(EditStatus.Refused)
            {
                Label = label,
                Errors = [new EditError(EditErrorKind.ReadOnly, MissionErrors.NotEditable, NoProfile)],
            };
        }

        if (host.NoEditReason is not { } reason)
            return null;
        Log.Add(ConsoleSeverity.Warning, Source, reason);
        return new EditResult(EditStatus.Refused)
        {
            Label = label,
            Errors = [new EditError(EditErrorKind.ReadOnly, MissionErrors.NotEditable, reason)],
            Revision = session.Revision,
        };
    }
}
