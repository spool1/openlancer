using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Data.Editing;

/// <summary>Who made an entry of the edit history (section 4.0.1 of the M6b design). It is not the origin of a problem
/// or of a Console message.</summary>
public enum EditOrigin
{
    /// <summary>A gesture of the user in the editor.</summary>
    User,

    /// <summary>A tool call of an agent. The label of its entry starts with "Agent: " (decision D709 of the MCP1
    /// design).</summary>
    Agent,

    /// <summary>The restore of the unsaved edits of an earlier session.</summary>
    Recovery,
}

/// <summary>
/// A check that a member of a batch adds (see <see cref="EditBatch.AddCommitCheck"/>). The batch runs each check once,
/// after its last step and before its commit. For example, the mission part checks the scripts that the batch touched,
/// and in strict mode it refuses a batch that adds an error.
/// </summary>
public interface IBatchCheck
{
    /// <summary>Checks the state of the batch now.</summary>
    /// <returns>The part of the result: for example the new and the fixed problems. A part with errors refuses the
    /// batch, so the batch cancels.</returns>
    EditResult Check(EditBatch batch);
}

/// <summary>
/// Commands that give one entry of the edit history and one validation (section 4.0.4 of the M6b design). One gesture
/// of the user, or one tool call of an agent, is one batch.
/// </summary>
/// <remarks>
/// <para>Each <see cref="Run"/> applies on top of the steps before it, so a later step sees the result of an earlier
/// step. A step that throws puts back the states before the batch and ends the batch: nothing changes, and no entry
/// goes into the history.</para>
/// <para><see cref="Commit"/> runs the commit checks once, adds one entry and raises
/// <see cref="EditSession.Changed"/> once. <see cref="Cancel"/> puts back the states before the batch and raises
/// nothing. <see cref="Preview"/> runs the checks, gives the result and cancels. The steps of a batch raise no
/// <see cref="EditSession.Changed"/>, so a batch never stays open across frames of the editor.</para>
/// <para>While a batch is open, the session refuses another batch, a transaction, <see cref="EditSession.Execute"/>,
/// an undo, a redo and a save. A command of the command layer joins <see cref="EditSession.OpenBatch"/>, or opens
/// its own batch.</para>
/// </remarks>
public sealed class EditBatch : IDisposable
{
    private readonly EditSession session;
    private readonly ChangeSet change;
    private readonly List<IBatchCheck> checks = [];
    private readonly List<EditResult> parts = [];

    /// <summary>The parts of the checks of the last <see cref="Finish"/>, or null when a step or a new check came after
    /// it.</summary>
    private List<EditResult>? checkParts;

    internal EditBatch(EditSession session, ChangeSet change, string? label, EditOrigin origin)
    {
        this.session = session;
        this.change = change;
        Label = label;
        Origin = origin;
    }

    /// <summary>The label of the request. It can change until the commit. Without a label, the batch takes the label of
    /// its first step.</summary>
    public string? Label { get; set; }

    /// <summary>Who makes the batch.</summary>
    public EditOrigin Origin { get; }

    /// <summary>True until the commit or the cancel.</summary>
    public bool IsOpen { get; private set; } = true;

    /// <summary>The label of the history entry: the label, with "Agent: " before it for an agent.</summary>
    public string UndoLabel => Origin == EditOrigin.Agent ? AgentPrefix + (Label ?? "") : Label ?? "";

    /// <summary>The start of the label of each history entry of an agent.</summary>
    public const string AgentPrefix = "Agent: ";

    /// <summary>
    /// Runs a session command on top of the steps before it. When it throws, the states before the batch come back and
    /// the batch ends.
    /// </summary>
    /// <exception cref="ArgumentException">The command refers to a section or a file that the edit view does not
    /// have.</exception>
    /// <exception cref="InvalidOperationException">The batch has ended, or the command does not apply.</exception>
    public void Run(EditCommand command)
    {
        ThrowIfEnded();
        Label ??= command.Label;
        checkParts = null;
        try
        {
            session.Run(change, command);
        }
        catch
        {
            End();
            throw;
        }
    }

    /// <summary>Adds a check that runs once before the commit, also in a preview. A member adds a check once.</summary>
    /// <exception cref="InvalidOperationException">The batch has ended.</exception>
    public void AddCommitCheck(IBatchCheck check)
    {
        ThrowIfEnded();
        if (!checks.Contains(check))
            checks.Add(check);
        checkParts = null;
    }

    /// <summary>Adds the part of the result that a member gives, for example the adds and the deletes of its plan. A
    /// part with errors refuses the batch (section 4.0.4 of the M6b design, item 8).</summary>
    /// <exception cref="InvalidOperationException">The batch has ended.</exception>
    public void Report(EditResult part)
    {
        ThrowIfEnded();
        parts.Add(part);
    }

    /// <summary>
    /// Runs the commit checks and gives the result without a commit. The status is <see cref="EditStatus.Applied"/>,
    /// or <see cref="EditStatus.NoChange"/> when the batch changes no state, or <see cref="EditStatus.Refused"/> when a
    /// reported part or a check gives an error. The result has the errors of each part. The checks run again only after
    /// a new step or a new check.
    /// </summary>
    /// <remarks>A check that throws puts back the states before the batch and ends the batch, as a step that throws
    /// does.</remarks>
    /// <exception cref="InvalidOperationException">The batch has ended.</exception>
    public EditResult Finish()
    {
        ThrowIfEnded();
        if (checkParts == null)
        {
            try
            {
                checkParts = checks.Select(c => c.Check(this)).ToList();
            }
            catch
            {
                Cancel();
                throw;
            }
        }
        var result = EditResult.Merge(EditStatus.Applied, [.. parts, .. checkParts]);
        var status = result.Errors.Count > 0 ? EditStatus.Refused
            : session.ChangesState(change) ? EditStatus.Applied
            : EditStatus.NoChange;
        return result with
        {
            Status = status,
            Label = Label,
            Revision = session.Revision,
            Files = Paths.Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>
    /// Runs the commit checks, then adds one entry to the history and raises <see cref="EditSession.Changed"/> once. A
    /// batch that changes no state adds no entry and gives <see cref="EditStatus.NoChange"/>. When a reported part or
    /// a check gives an error, the batch cancels, and the result has the errors.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch has ended.</exception>
    public EditResult Commit()
    {
        var result = Finish();
        if (result.Status == EditStatus.Refused)
        {
            Cancel();
            return result with { Revision = session.Revision };
        }
        End();
        change.Label = UndoLabel;
        session.Push(change);
        var committed = result with
        {
            UndoLabel = result.Status == EditStatus.Applied ? UndoLabel : null,
            Revision = session.Revision,
        };
        Committed?.Invoke(committed);
        return committed;
    }

    /// <summary>Raised after a commit, with the result of the commit. A member that joined the batch of a caller learns
    /// the end of the batch from it, for example the mission editor for its <c>Applied</c> event (section 4.5 of the M6b
    /// design).</summary>
    public event Action<EditResult>? Committed;

    /// <summary>
    /// Runs the commit checks, gives the result and cancels the batch. The status is
    /// <see cref="EditStatus.Previewed"/>, or <see cref="EditStatus.NoChange"/> or <see cref="EditStatus.Refused"/>
    /// as <see cref="Finish"/> gives it. The revision, the history and the edited paths stay as they were.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch has ended.</exception>
    public EditResult Preview()
    {
        var result = Finish();
        Cancel();
        return result.Status == EditStatus.Applied ? result with { Status = EditStatus.Previewed } : result;
    }

    /// <summary>Puts back the states before the batch and ends it. Raises no <see cref="EditSession.Changed"/>. Does
    /// nothing after the end.</summary>
    public void Cancel()
    {
        if (!IsOpen)
            return;
        End();
        session.CancelBatch(change);
    }

    public void Dispose() => Cancel();

    /// <summary>The install paths of the INI files and the cutscenes that the steps changed until now, in normal
    /// form.</summary>
    public IReadOnlyCollection<string> Paths => change.Paths.Where(p => !ModFileDocument.IsKey(p)).ToList();

    /// <summary>The number of different sections that the steps changed, added or removed until now. The agent tools
    /// compare it with the limit of one call (section 8.4 of the MCP1 design).</summary>
    internal int SectionCount => change.SectionCount;

    /// <summary>Each section that a step gave a new key until now, with its old and its new reference, in
    /// order.</summary>
    internal IReadOnlyList<(SectionRef From, SectionRef To)> Renames => change.Renames;

    /// <summary>
    /// The text that a save writes now for each INI file that the steps changed, by install path. The text is the patch
    /// of the file, or the text of a whole file of the mod (<c>with_patch</c>, section 7.0 of the MCP1 design). It holds
    /// the unsaved edits before the batch too. A file that a save does not write, or cannot write, has no text.
    /// </summary>
    /// <exception cref="InvalidOperationException">The batch has ended.</exception>
    internal IReadOnlyDictionary<string, string> PatchTexts()
    {
        ThrowIfEnded();
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (session.SaveText(path) is { } text)
                texts[path] = text;
        }
        return texts;
    }

    private void End()
    {
        IsOpen = false;
        session.EndBatch(this);
    }

    private void ThrowIfEnded()
    {
        if (!IsOpen)
            throw new InvalidOperationException("The batch has ended.");
    }
}
