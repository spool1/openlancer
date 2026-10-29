using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;

namespace OpenLancer.Editor.Dialogs;

/// <summary>One button of a <see cref="ChoiceDialog"/>.</summary>
/// <param name="Action">What the button does. The dialog closes first.</param>
internal sealed record Choice(string Label, Action Action)
{
    /// <summary>False for a button that the user cannot click now. <see cref="Tooltip"/> then tells why.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The tooltip of the button, or null.</summary>
    public string? Tooltip { get; init; }

    /// <summary>True for the default button, which Enter clicks. Without a default button, the first button is the
    /// default button.</summary>
    public bool Default { get; init; }
}

/// <summary>
/// A message with buttons, for example Save, Discard and Cancel. The first button is the default button, unless a button
/// says that it is the default button. Escape and the close button of the title bar run the cancel action, except in a
/// dialog that the user cannot cancel (see <see cref="ShowWithoutCancel"/>).
/// </summary>
internal sealed class ChoiceDialog : ModalDialog
{
    private string title = "##choice";
    private string message = "";
    private IReadOnlyList<Choice> choices = [];
    private Action? cancel;
    private bool cancellable = true;

    // True when the question closes as Cancel at its next draw (see Withdraw).
    private bool withdrawn;

    protected override string Title => title;

    protected override bool CanCancel => cancellable;

    /// <summary>Shows a question. When another question shows, the new question takes its place, and the cancel action of
    /// the earlier question runs, because the user did not answer it. So a caller that waits for the answer always gets
    /// one.</summary>
    /// <param name="title">The title. Two dialogs that can show at the same time need different titles.</param>
    /// <param name="cancel">What Escape and the close button of the title bar do, or null for nothing.</param>
    public void Show(string title, string message, IReadOnlyList<Choice> choices, Action? cancel = null)
    {
        var replaced = IsOpen ? this.cancel : null;
        this.title = title;
        this.message = message;
        this.choices = choices;
        this.cancel = cancel;
        cancellable = true;
        withdrawn = false;
        Open();
        // After the new question is in place, so a cancel action that shows a question replaces it in turn.
        replaced?.Invoke();
    }

    /// <summary>Shows a dialog that the user cannot cancel. It blocks the window until a button closes it or the window
    /// closes. Escape does nothing, and the title bar has no close button.</summary>
    /// <param name="title">The title. Two dialogs that can show at the same time need different titles.</param>
    public void ShowWithoutCancel(string title, string message, IReadOnlyList<Choice> choices)
    {
        Show(title, message, choices);
        cancellable = false;
    }

    /// <summary>
    /// Closes the question with these buttons as Cancel does, when it still shows, for example the question of an agent
    /// request whose call ended. ImGui closes a popup only while it draws, so the close and the cancel action run at the
    /// next draw, before the buttons. Call it on the UI thread.
    /// </summary>
    /// <param name="question">The buttons of the question, as <see cref="Show"/> got them.</param>
    public void Withdraw(IReadOnlyList<Choice> question)
    {
        if (IsOpen && ReferenceEquals(choices, question))
        {
            withdrawn = true;
        }
    }

    protected override void Body()
    {
        if (withdrawn)
        {
            withdrawn = false;
            Close();
            cancel?.Invoke();
            return;
        }

        ImGui.TextWrapped(message);
        ImGui.Spacing();
        var primary = Math.Max(0, choices.ToList().FindIndex(c => c.Default));
        for (var i = 0; i < choices.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            var choice = choices[i];
            if (Button(choice.Label, primary: i == primary, enabled: choice.Enabled))
            {
                Close();
                choice.Action();
                return;
            }

            if (choice.Tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(choice.Tooltip);
            }
        }
    }

    protected override void Cancelled() => cancel?.Invoke();
}
