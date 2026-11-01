using System;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// The edit of a field of a cutscene value as one gesture of the command layer, as <see cref="LineGesture"/> is for an
/// INI key. Each change of the text runs the command of the field (see <see cref="ThnFieldCommand"/>) in a step, so it
/// shows at once in W. A text that equals the start text puts back W as it was, so the edit adds no entry. A text that
/// is not valid keeps the last valid value and sets the error.
/// </summary>
internal sealed class ThnFieldGesture : CommandFieldGesture
{
    private readonly Func<string, ThnFieldParse> parse;
    private readonly ThnFieldCommand command;

    private ThnFieldGesture(CommandTransaction gesture, FieldKey key, string startText,
        Func<string, ThnFieldParse> parse, ThnFieldCommand command) : base(gesture, key, startText)
    {
        this.parse = parse;
        this.command = command;
    }

    /// <summary>Opens the gesture of an edit of a field, with the label of the command, and starts the edit in
    /// it.</summary>
    /// <param name="key">The key of the field (see <see cref="FieldGesture.Key"/>).</param>
    /// <param name="startText">The text of the field at the start of the edit.</param>
    /// <param name="parse">Gives the value of a text.</param>
    /// <param name="command">The edit of a value, for example <see cref="ThnFieldCommand.Set"/>, and the label of its
    /// entry.</param>
    /// <exception cref="InvalidOperationException">The editor takes no edits, or a transaction or a batch is
    /// open.</exception>
    public static ThnFieldGesture Open(CommandRunner commands, FieldKey key, string startText,
        Func<string, ThnFieldParse> parse, ThnFieldCommand command) =>
        commands.Gesture(command.Label, gesture => new ThnFieldGesture(gesture, key, startText, parse, command));

    public override void Change(string text)
    {
        if (string.Equals(text, StartText, StringComparison.Ordinal))
        {
            Reset();
            return;
        }

        var result = parse(text);
        if (result.Value is not { } value)
        {
            Error = result.Error;
            return;
        }

        Apply(session => command.Make(session, value));
    }
}
