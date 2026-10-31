using System;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The gestures of the user in the mission graph that an agent call waits for (section 5.5, item 3 of the MCP1 design).
/// They are a rename of a node, quick add, and a drag of nodes or of a link. A drag of nodes or of a link runs only while
/// the left button stays down after a click on a node or a pin. A gesture of a graph that does not draw any more does not
/// count, for example a rename in a hidden tab.
/// </summary>
/// <param name="frames">Gives the number of the frame now. The default is the frame count of ImGui. The tests give a fake
/// count.</param>
internal sealed class GraphGesture(Func<int>? frames = null)
{
    private readonly DrawnFrame drawn = new(frames);

    // True from a click of the left button on a node or a pin until the release of the button.
    private bool pressed;

    // True when the node editor ran a link drag in the last draw.
    private bool linkDrag;

    /// <summary>Starts a draw of the graph. A link drag counts again only when the node editor runs it in this
    /// draw.</summary>
    public void Drew()
    {
        drawn.Drew();
        linkDrag = false;
    }

    /// <summary>Records a link drag of the node editor in this draw.</summary>
    public void DraggedLink() => linkDrag = true;

    /// <summary>Follows the left button after the canvas drew.</summary>
    /// <param name="clicked">The left button went down in this frame.</param>
    /// <param name="overItem">The mouse is over a node or a pin of the graph.</param>
    /// <param name="down">The left button is down now.</param>
    public void Press(bool clicked, bool overItem, bool down) => pressed = down && (clicked ? overItem : pressed);

    /// <summary>True while a gesture of the user is open in a graph that draws.</summary>
    /// <param name="editingText">The user renames a node, or quick add is open.</param>
    public bool Open(bool editingText) => drawn.Recent && (editingText || pressed || linkDrag);
}
