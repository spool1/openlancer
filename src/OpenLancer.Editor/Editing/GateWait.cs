using System;

namespace OpenLancer.Editor.Editing;

/// <summary>
/// Runs a save or a Play of the user one frame after a status line, when the save gate must wait for the index of the
/// cutscene uses (see <see cref="Data.Editing.EditSession.CutsceneGateWaits"/>). The gate blocks the UI thread, and a
/// status line that the editor writes just before the blocking call does not show until the call ends. So the editor
/// writes the status line, draws one frame with it, and saves in the frame after that.
/// </summary>
/// <remarks>
/// <para>A save of the user runs through <see cref="Run"/>. A Play request waits in its controller until it starts,
/// so the controller asks <see cref="Hold"/> in each frame before the request saves.</para>
/// <para>Each member runs on the UI thread. <see cref="EditorWindow"/> calls <see cref="Frame"/> at the start of each
/// frame.</para>
/// </remarks>
internal sealed class GateWait
{
    /// <summary>The status line while the editor waits for the gate.</summary>
    public const string Status = "The editor checks the cutscenes that use the changed items.";

    // The number of the frame. Frame adds one at the start of each frame.
    private long frame;

    // The save that waits for its frame, with the frame of its status line.
    private (Action Action, long Since)? waiting;

    // The Play request that waits for its frame, with the frame of its status line.
    private (object Request, long Since)? held;

    /// <summary>True while a save waits for its frame.</summary>
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
        waiting = (action, frame);
    }

    /// <summary>
    /// True while a Play request must wait before it saves. The first call for a request writes the status line when
    /// the gate waits. The calls for the same request in the next frame give true, so that frame shows the status line.
    /// From the second frame, the call gives false, and the request starts. Another request writes the status line
    /// again.
    /// </summary>
    /// <param name="request">The request that starts next.</param>
    /// <param name="waits">True when the save gate waits now.</param>
    /// <param name="status">Writes a status line.</param>
    public bool Hold(object request, bool waits, Action<string> status)
    {
        if (!waits)
        {
            held = null;
            return false;
        }

        if (held is not var (heldRequest, since) || !ReferenceEquals(heldRequest, request))
        {
            status(Status);
            held = (request, frame);
            return true;
        }

        if (frame - since < 2)
        {
            return true;
        }

        held = null;
        return false;
    }

    /// <summary>Call it once at the start of each frame. The second call after <see cref="Run"/> runs the kept action,
    /// so the frame between the two calls shows the status line.</summary>
    public void Frame()
    {
        frame++;
        if (waiting is not var (action, since) || frame - since < 2)
        {
            return;
        }

        waiting = null;
        action();
    }
}
