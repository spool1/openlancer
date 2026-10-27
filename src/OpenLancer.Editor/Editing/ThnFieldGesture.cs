using System;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// The edit of a field of a cutscene value: one transaction, as <see cref="LineGesture"/> has for an INI key. Each
/// change of the text shows at once in W. A text that equals the start text puts back W as it was, so the edit adds no
/// entry. A text that is not valid keeps the last valid value and sets the error.
/// </summary>
internal sealed class ThnFieldGesture : FieldGesture
{
    private readonly EditTransaction transaction;
    private readonly Func<string, ThnFieldParse> parse;
    private readonly Func<ThnValue, EditCommand> command;

    /// <param name="key">The key of the field (see <see cref="Key"/>).</param>
    /// <param name="parse">Gives the value of a text.</param>
    /// <param name="command">The edit of a value, for example <see cref="ThnFieldCommand.Set"/>, and the label of its
    /// entry.</param>
    /// <exception cref="InvalidOperationException">The session takes no edits, or a transaction is open.</exception>
    public ThnFieldGesture(EditSession session, FieldKey key, string startText, Func<string, ThnFieldParse> parse,
        ThnFieldCommand command) : base(key, startText)
    {
        this.parse = parse;
        this.command = command.Make;
        transaction = session.Begin(command.Label);
    }

    public override void Change(string text)
    {
        if (string.Equals(text, StartText, StringComparison.Ordinal))
        {
            transaction.Apply(EditCommand.Group("", []));
            Error = null;
            return;
        }

        var result = parse(text);
        if (result.Value is not { } value)
        {
            Error = result.Error;
            return;
        }

        try
        {
            transaction.Apply(command(value));
            Error = null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // The transaction is back at the state before the edit.
            Error = ErrorText.Of(e);
        }
    }

    public override string? End(bool keep, bool interactive)
    {
        if (!keep || Error != null)
        {
            transaction.Cancel();
            return keep ? Error : null;
        }

        transaction.Commit();
        return null;
    }

    public override void Dispose() => transaction.Cancel();
}
