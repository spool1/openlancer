using System;
using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// The last frame in which a view drew. A state of the view that waits for the user, for example an open rename field or
/// a drag, counts only while the view draws (section 5.5, item 3 of the MCP1 design). So a view that does not draw any
/// more, for example a hidden tab, never keeps an agent call waiting.
/// </summary>
/// <param name="frames">Gives the number of the frame now. The default is the frame count of ImGui. The tests give a fake
/// count.</param>
internal sealed class DrawnFrame(Func<int>? frames = null)
{
    private readonly Func<int> frameNow = frames ?? ImGui.GetFrameCount;
    private int? last;

    /// <summary>Records a draw of the view in this frame.</summary>
    public void Drew() => last = frameNow();

    /// <summary>True when the view drew in this frame or in the frame before it. An agent call runs before the panels
    /// draw, so it reads the draw of the frame before.</summary>
    public bool Recent => last is { } frame && frame >= frameNow() - 1;
}
