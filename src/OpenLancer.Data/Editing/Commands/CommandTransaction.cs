using System;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>
/// One gesture of the user with many steps, for example a drag of the gizmo, a drag of a number field or a typed text
/// (section 5.2, item 5 of the MCP1 design, decision D766). Each <see cref="Apply"/> puts back the state before the
/// gesture, then makes the plan of a command of the command layer and runs it. So the gesture keeps only its last step,
/// and a plan never runs on a newer state. <see cref="Commit"/> adds one entry to the history.
/// </summary>
/// <remarks>
/// <para>The gesture is built on <see cref="EditTransaction"/>. While it is open, the session refuses other edits,
/// undo, redo and save. Each step raises <see cref="EditSession.Changed"/>, so the views show the step at once.
/// <see cref="Cancel"/> puts back the state before the gesture and raises <see cref="EditSession.Changed"/> once when a
/// step changed the state. <see cref="Dispose"/> without <see cref="Commit"/> cancels.</para>
/// <para><see cref="Missions.Editing.MissionTransaction"/> is the form of a gesture for the mission commands.</para>
/// </remarks>
public sealed class CommandTransaction : IDisposable
{
    private readonly EditTransaction transaction;

    /// <summary>The plan of the last step that ran, or null when no step runs now.</summary>
    private EditPlan? plan;

    private bool ended;

    private CommandTransaction(EditSession session, EditTransaction transaction, string label)
    {
        Session = session;
        this.transaction = transaction;
        Label = label;
    }

    /// <summary>The session of the gesture.</summary>
    public EditSession Session { get; }

    /// <summary>The label of the history entry, for example <c>Move Li01_14</c>. It can change until the
    /// commit.</summary>
    public string Label { get; set; }

    /// <summary>True until the commit or the cancel.</summary>
    public bool IsOpen => !ended;

    /// <summary>Opens a gesture: one history entry for many steps.</summary>
    /// <exception cref="InvalidOperationException">A transaction or a batch is open, or the session takes no
    /// edits.</exception>
    public static CommandTransaction Begin(EditSession session, string label) =>
        new(session, session.Begin(label), label);

    /// <summary>
    /// Puts back the state before the gesture, then makes the plan of a step and runs its session command. The step
    /// takes the place of the step before. A plan that refuses, and a plan or a command that throws an
    /// <see cref="ArgumentException"/>, an <see cref="InvalidOperationException"/> or an <see cref="EditException"/>,
    /// leave the state before the gesture. The gesture then stays open, and the result has the status
    /// <see cref="EditStatus.Refused"/> with the reason.
    /// </summary>
    /// <param name="step">Makes the plan of the step from the session, after the restore of the state before the
    /// gesture.</param>
    /// <returns>The report of the plan of the step (see <see cref="EditPlan.Report"/>).</returns>
    /// <exception cref="InvalidOperationException">The gesture has ended.</exception>
    public EditResult Apply(Func<EditSession, EditPlan> step)
    {
        ThrowIfEnded();
        plan = null;
        var command = new StepCommand(Label, step);
        try
        {
            transaction.Apply(command);
        }
        catch (EditException e)
        {
            return Refused(e.Error);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return Refused(new EditError(EditErrorKind.Refused, EditPlan.PlanRefused, ErrorText.Of(e)));
        }
        var made = command.Plan!;
        if (made.CanRun)
            plan = made;
        return made.Report(Session) with { Revision = Session.Revision };
    }

    /// <summary>Puts back the state before the gesture, for example when a drag goes back to its start. The gesture
    /// stays open.</summary>
    /// <exception cref="InvalidOperationException">The gesture has ended.</exception>
    public void Reset()
    {
        ThrowIfEnded();
        plan = null;
        transaction.Apply(EditCommand.Group(Label, []));
    }

    /// <summary>
    /// Ends the gesture with the state of its last step: one history entry with <see cref="Label"/>, or none when the
    /// state is the state before the gesture.
    /// </summary>
    /// <returns>The status <see cref="EditStatus.Applied"/> with the report of the last step, or
    /// <see cref="EditStatus.NoChange"/>.</returns>
    /// <exception cref="InvalidOperationException">The gesture has ended.</exception>
    public EditResult Commit()
    {
        ThrowIfEnded();
        ended = true;
        var revision = Session.Revision;
        var report = plan?.Report(Session);
        transaction.Label = Label;
        transaction.Commit();
        var applied = Session.Revision != revision;
        return (applied && report != null ? report : new EditResult(EditStatus.NoChange)) with
        {
            Status = applied ? EditStatus.Applied : EditStatus.NoChange,
            Label = Label,
            UndoLabel = applied ? Label : null,
            Revision = Session.Revision,
        };
    }

    /// <summary>Puts back the state before the gesture and ends it. Does nothing after the end.</summary>
    public void Cancel()
    {
        if (ended)
            return;
        ended = true;
        plan = null;
        transaction.Cancel();
    }

    public void Dispose() => Cancel();

    private EditResult Refused(EditError error) =>
        new(EditStatus.Refused) { Label = Label, Errors = [error], Revision = Session.Revision };

    private void ThrowIfEnded()
    {
        if (ended)
            throw new InvalidOperationException("The gesture has ended.");
    }

    /// <summary>The session command of a step: it makes the plan in the scope of the session, after the restore, and
    /// runs the session command of the plan. A plan that refuses runs nothing.</summary>
    private sealed class StepCommand(string label, Func<EditSession, EditPlan> make) : EditCommand(label)
    {
        /// <summary>The plan of the last run, or null before it.</summary>
        public EditPlan? Plan { get; private set; }

        internal override void Run(EditSession.CommandScope scope)
        {
            Plan = make(scope.Session);
            Plan.Command?.Run(scope);
        }
    }
}
