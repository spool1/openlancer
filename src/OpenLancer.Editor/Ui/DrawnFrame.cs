namespace OpenLancer.Editor.Ui;

/// <summary>
/// The last frame in which a view drew. A state of the view that waits for the user, for example an open rename field or
/// a drag, counts only while the view draws (section 5.5, item 3 of the MCP1 design). So a view that does not draw any
/// more, for example a hidden tab, never keeps an agent call waiting.
/// </summary>
internal sealed class DrawnFrame
{
    private int? last;

    /// <summary>Records a draw of the view in a frame.</summary>
    public void Drew(int frame) => last = frame;

    /// <summary>True when the view drew in this frame or in the frame before it. An agent call runs before the panels
    /// draw, so it reads the draw of the frame before.</summary>
    public bool Recent(int now) => last is { } frame && frame >= now - 1;
}
