using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Editor.Ui;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// One edit of a field: a text, a label drag or a color picker, from the activation of its widget to its deactivation
/// (see <see cref="FieldEditRouter"/>). The edit becomes at most one entry of the edit history.
/// </summary>
internal abstract class FieldGesture : IDisposable
{
    /// <param name="startText">The text of the field at the start of the edit.</param>
    protected FieldGesture(FieldKey key, string startText)
    {
        Key = key;
        StartText = startText;
    }

    /// <summary>The key that the field edits.</summary>
    public FieldKey Key { get; }

    /// <summary>The text of the field at the start of the edit.</summary>
    public string StartText { get; }

    /// <summary>Why the last text cannot be the value, or null.</summary>
    public string? Error { get; protected set; }

    /// <summary>Takes the new text of the field.</summary>
    public abstract void Change(string text);

    /// <summary>
    /// Ends the edit. Returns the error when the edit keeps nothing because its last text is not valid, else null.
    /// </summary>
    /// <param name="keep">False to discard the edit, for example after Escape.</param>
    /// <param name="interactive">False when the editor cannot ask a question, for example when the window closes.
    /// Then the edit takes the choice that changes the least.</param>
    public abstract string? End(bool keep, bool interactive);

    /// <summary>Discards the edit when it did not end.</summary>
    public abstract void Dispose();
}

/// <summary>
/// The edit of the lines of a bound key as one gesture of the command layer. The edit keeps the key and the section as
/// they were at its start. Each change runs a field command in a step of the gesture, so it shows at once in the edit
/// view. While the edit is open, the Inspector lays out the rows of the section from its start (see
/// <see cref="Inspector.PropertyGrid.LayoutView"/>).
/// </summary>
/// <remarks>The edit gets its gesture from <see cref="CommandRunner.Gesture{T}"/>, and the edit owns it: the end of the
/// edit commits or cancels the gesture.</remarks>
internal abstract class LineEditGesture : FieldGesture
{
    private readonly CommandTransaction gesture;

    private protected LineEditGesture(CommandTransaction gesture, FieldBinding binding, string startText)
        : base(binding.FieldKey, startText)
    {
        this.gesture = gesture;
        Binding = binding;
        StartSection = gesture.Session.Get(binding.Section);
    }

    /// <summary>The key as it was at the start of the edit.</summary>
    public FieldBinding Binding { get; }

    /// <summary>The section of the key as it was at the start of the edit, or null when the edit view did not have
    /// it.</summary>
    public SectionView? StartSection { get; }

    /// <summary>The label of the history entry of an edit of a key, for example <c>Set mass</c>.</summary>
    public static string Label(FieldBinding binding) => $"Set {binding.Name}";

    /// <summary>
    /// Ends the edit. When the last change is valid, the edit becomes one entry of the history. Otherwise the edit view
    /// goes back to its state before the edit, and the result is the error.
    /// </summary>
    public override string? End(bool keep, bool interactive)
    {
        if (!keep || Error != null)
        {
            gesture.Cancel();
            return keep ? Error : null;
        }
        gesture.Commit();
        return null;
    }

    public override void Dispose() => gesture.Cancel();

    /// <summary>Shows a step in the edit view, in place of the step of the last change. A plan of the step that refuses
    /// sets <see cref="FieldGesture.Error"/>, and the edit view goes back to its state before the edit.</summary>
    /// <param name="step">Makes the plan of a field command from the session.</param>
    /// <returns>True when the step applied.</returns>
    private protected bool Apply(Func<EditSession, EditPlan> step)
    {
        var result = gesture.Apply(step);
        if (result.Status == EditStatus.Refused)
        {
            Error = result.Errors.FirstOrDefault()?.Message ?? $"'{Binding.Name}' did not change.";
            return false;
        }
        Error = null;
        return true;
    }

    /// <summary>Puts back the edit view as it was at the start of the edit. The edit stays open.</summary>
    private protected void Reset()
    {
        gesture.Reset();
        Error = null;
    }
}

/// <summary>
/// The edit of the lines of a bound key from the text of a field.
/// </summary>
/// <remarks>
/// A text that equals the value at the start puts back the edit view as it was, so the edit removes no pin and adds
/// no entry. A text that is not valid keeps the last valid state and sets <see cref="FieldGesture.Error"/>.
/// </remarks>
internal sealed class LineGesture : LineEditGesture
{
    /// <param name="gesture">The gesture of the edit. The edit owns it.</param>
    public LineGesture(CommandTransaction gesture, FieldBinding binding) : base(gesture, binding, binding.Text)
    {
    }

    /// <summary>Opens the gesture of an edit of the key and starts the edit in it.</summary>
    /// <exception cref="InvalidOperationException">The editor takes no edits, or a transaction or a batch is
    /// open.</exception>
    public static LineGesture Open(CommandRunner commands, FieldBinding binding) =>
        commands.Gesture(Label(binding), gesture => new LineGesture(gesture, binding));

    /// <summary>Shows the text of the field in the edit view, or sets <see cref="FieldGesture.Error"/>.</summary>
    public override void Change(string text)
    {
        var parse = FieldEdit.Parse(Binding.Key, text);
        if (parse.Lines is not { } lines)
        {
            Error = parse.Error;
            return;
        }

        if (Binding.Holds(lines))
            Reset();
        else
            Apply(session => FieldCommands.SetLines(session, Binding.Section, Binding.Name, lines));
    }
}

/// <summary>
/// The edit of the first values of the one line of a bound key by a label drag or a color picker. The text of a change
/// is the numbers of these values (see <see cref="FieldDrag.Text"/>). The values go into the line by the number rules
/// of a drag (<see cref="TransformValues.Line(IReadOnlyList{ValueBase}, ReadOnlySpan{float})"/>), so a label drag and
/// a gizmo drag write the same text.
/// </summary>
/// <remarks>
/// <para>The edit reads its start line from the edit view when it starts. It is the only source of the start of the
/// drag, because the end of an edit before it can change the edit view.</para>
/// <para>A change to the numbers that the edit view holds does nothing. A change back to the start numbers puts back
/// the edit view as it was, so the edit removes no pin and adds no entry. Each start value after the written values
/// stays, for example the alpha of a color.</para>
/// </remarks>
internal sealed class NumberLineGesture : LineEditGesture
{
    private readonly IReadOnlyList<ValueBase> start;
    private readonly float[] startNumbers;
    private float[] applied;

    /// <summary>Starts the edit of the first <paramref name="count"/> values of the line of the key in a
    /// gesture.</summary>
    /// <param name="gesture">The gesture of the edit. The edit owns it when it starts.</param>
    /// <exception cref="InvalidOperationException">The key does not have exactly one line, or one of the first
    /// <paramref name="count"/> values is not a finite number (see <see cref="TransformValues.TryNumber"/>). The edit
    /// does not start, and the caller cancels the gesture (see <see cref="CommandRunner.Gesture{T}"/>).</exception>
    public NumberLineGesture(CommandTransaction gesture, FieldBinding binding, int count)
        : this(gesture, StartOf(gesture.Session, binding, count))
    {
    }

    private NumberLineGesture(CommandTransaction gesture, Start at)
        : base(gesture, at.Binding, FieldDrag.Text(at.Numbers))
    {
        start = at.Line;
        startNumbers = at.Numbers;
        applied = at.Numbers;
    }

    /// <summary>Opens the gesture of a drag or a pick of the key and starts the edit in it. When the edit cannot start,
    /// no gesture stays open.</summary>
    /// <exception cref="InvalidOperationException">The edit cannot start (the message tells why), the editor takes no
    /// edits, or a transaction or a batch is open.</exception>
    public static NumberLineGesture Open(CommandRunner commands, FieldBinding binding, int count) =>
        commands.Gesture(Label(binding), gesture => new NumberLineGesture(gesture, binding, count));

    /// <summary>The number of values that the edit writes.</summary>
    public int Count => startNumbers.Length;

    /// <summary>Shows the numbers of the text in the line, or sets <see cref="FieldGesture.Error"/>.</summary>
    public override void Change(string text)
    {
        if (!FieldDrag.TryParse(text, Count, out var numbers))
        {
            // A guard: the label drag and the picker make the text.
            Error = $"'{text}' is not {Count} finite numbers separated by commas.";
            return;
        }

        if (Same(numbers, applied) && Error == null)
            return;

        IReadOnlyList<ValueBase> line;
        try
        {
            line = TransformValues.Line(start, numbers);
        }
        catch (ArgumentException e)
        {
            Error = ErrorText.Of(e);
            return;
        }

        if (line.Count == start.Count && line.Select((v, i) => ReferenceEquals(v, start[i])).All(s => s))
        {
            Reset();
            applied = numbers;
            return;
        }

        applied = Apply(session => FieldCommands.SetLine(session, Binding.Section, Binding.Name, line))
            ? numbers
            : startNumbers;
    }

    /// <summary>The binding, the line and the numbers of the key in the edit view now.</summary>
    private static Start StartOf(EditSession session, FieldBinding binding, int count)
    {
        var view = session.Get(binding.Section) ??
                   throw new InvalidOperationException($"The edit view has no section {binding.Section.Id}.");
        var lines = view.Lines(binding.Name);
        if (lines.Count != 1)
        {
            throw new InvalidOperationException(lines.Count == 0
                ? $"'{binding.Name}' has no line. Type a value in the field first."
                : $"'{binding.Name}' has {lines.Count} lines. Correct the key in the field first.");
        }

        var values = lines[0].Values;
        if (FieldDrag.NumbersOf(values, count, out var numbers) is { } reason)
            throw new InvalidOperationException($"The editor cannot change '{binding.Name}' with a drag or a picker. {reason}");
        return new Start(FieldBinding.Create(session, binding.Section, view, binding.Name), values, numbers);
    }

    /// <summary>True when two lists of numbers have the same bits.</summary>
    private static bool Same(float[] a, float[] b) =>
        a.Length == b.Length && a.Select((v, i) => BitConverter.SingleToInt32Bits(v) == BitConverter.SingleToInt32Bits(b[i])).All(s => s);

    private sealed record Start(FieldBinding Binding, IReadOnlyList<ValueBase> Line, float[] Numbers);
}

/// <summary>
/// The edit of a field whose command reads the edit view when it is made, for example the text of a name or a market
/// line. The edit changes nothing while the field is active. When the field ends with a text that differs from its
/// start text, the edit runs its commit action one time.
/// </summary>
/// <remarks>
/// A text edit refuses to run on a newer state than the state that it was made from (see
/// <see cref="ItemEdits.SetText"/>). So the command is made only at the end.
/// </remarks>
/// <param name="commit">Runs the edit with the last text. Its second argument is the interactive flag of
/// <see cref="FieldGesture.End"/>. It returns why the text cannot be the value, or null.</param>
internal sealed class CommitGesture(FieldKey key, string startText, Func<string, bool, string?> commit)
    : FieldGesture(key, startText)
{
    private string text = startText;
    private bool ended;

    public override void Change(string text) => this.text = text;

    public override string? End(bool keep, bool interactive)
    {
        if (ended)
        {
            return null;
        }

        ended = true;
        return keep && !string.Equals(text, StartText, StringComparison.Ordinal) ? commit(text, interactive) : null;
    }

    public override void Dispose() => ended = true;
}
