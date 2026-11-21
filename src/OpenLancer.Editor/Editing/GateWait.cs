using System;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// Runs a save or a Play of the user one frame after a status line, when the save gate must wait for the index of the
/// cutscene uses (see <see cref="Data.Editing.EditSession.CutsceneGateWaits"/>). The gate blocks the UI thread, and a
/// status line that the editor writes just before the blocking call does not show until the call ends. So the editor
/// writes the status line, draws one frame with it, and runs the action in the frame after that.
/// </summary>
/// <remarks>Each member runs on the UI thread. <see cref="EditorWindow"/> calls <see cref="Frame"/> at the start of
/// each frame.</remarks>
internal sealed class GateWait
{
    /// <summary>The status line while the editor waits for the gate.</summary>
    public const string Status = "The editor checks the cutscenes that use the changed items.";

    private Action? waiting;
    private int frames;

    /// <summary>True while an action waits for its frame.</summary>
    public bool Waiting => waiting != null;

    /// <summary>Runs the action now when the gate does not wait. Else writes the status line, and keeps the action for
    /// <see cref="Frame"/>. A second action before that replaces the first one.</summary>
    /// <param name="waits">True when the save gate waits now.</param>
    /// <param name="status">Writes a status line.</param>
    public void Run(bool waits, Action<string> status, Action action)
    {
        if (!waits)
        {
            action();
            return;
        }

        status(Status);
        waiting = action;
        frames = 0;
    }

    /// <summary>Call it once at the start of each frame. The second call after <see cref="Run"/> runs the kept action,
    /// so the frame between the two calls shows the status line.</summary>
    public void Frame()
    {
        if (waiting is not { } action || ++frames < 2)
        {
            return;
        }

        waiting = null;
        action();
    }
}
