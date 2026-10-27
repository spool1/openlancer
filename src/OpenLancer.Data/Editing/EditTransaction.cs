using System;

namespace OpenLancer.Data.Editing;

/// <summary>
/// One entry of the edit history that is made from many steps, for example one drag or one text field. Each
/// <see cref="Apply"/> shows its command at once and takes the place of the command of the previous
/// <see cref="Apply"/>. <see cref="Commit"/> adds one entry to the history.
/// </summary>
/// <remarks>
/// While a transaction is open, the session refuses other edits, undo, redo and save. <see cref="Dispose"/> without
/// <see cref="Commit"/> cancels the transaction.
/// </remarks>
public sealed class EditTransaction : IDisposable
{
    private readonly EditSession session;
    private readonly ChangeSet change;
    private bool ended;

    internal EditTransaction(EditSession session, ChangeSet change)
    {
        this.session = session;
        this.change = change;
    }

    /// <summary>The label of the entry. A gesture of the command layer can change it until the commit (see
    /// <see cref="Commands.CommandTransaction.Label"/>).</summary>
    internal string Label
    {
        get => change.Label;
        set => change.Label = value;
    }

    /// <summary>
    /// Puts back the edit view as it was before the transaction, then runs <paramref name="command"/>. When the command
    /// throws, the edit view stays as it was before the transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transaction has ended.</exception>
    public void Apply(EditCommand command)
    {
        ThrowIfEnded();
        session.ApplyInTransaction(change, command);
    }

    /// <summary>Ends the transaction and adds its edit to the history, when it changed anything.</summary>
    /// <exception cref="InvalidOperationException">The transaction has ended.</exception>
    public void Commit()
    {
        ThrowIfEnded();
        ended = true;
        session.EndTransaction(change, commit: true);
    }

    /// <summary>Ends the transaction and puts back the edit view as it was before it. Does nothing after the end.</summary>
    public void Cancel()
    {
        if (ended)
            return;
        ended = true;
        session.EndTransaction(change, commit: false);
    }

    public void Dispose() => Cancel();

    private void ThrowIfEnded()
    {
        if (ended)
            throw new InvalidOperationException("The transaction has ended.");
    }
}
