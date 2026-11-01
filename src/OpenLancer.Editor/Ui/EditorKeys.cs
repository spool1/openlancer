using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>A key and its modifier keys, for example Ctrl+Shift+Z.</summary>
internal readonly record struct KeyChord(ImGuiKey Key, bool Ctrl = false, bool Shift = false)
{
    /// <summary>The chord for <c>ImGui.Shortcut</c>: the key with the flags of its modifier keys.</summary>
    public int Shortcut => (int)Key | (Ctrl ? (int)ImGuiKey.ImGuiMod_Ctrl : 0) | (Shift ? (int)ImGuiKey.ImGuiMod_Shift : 0);

    /// <summary>The text of the chord for the user, for example "Ctrl+Shift+Z" or "Left arrow".</summary>
    public string Text => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + KeyName(Key);

    /// <summary>The name of a key for the user. A letter or a function key keeps its name, for example "W" or
    /// "F5".</summary>
    public static string KeyName(ImGuiKey key) => key switch
    {
        ImGuiKey.Escape => "Esc",
        ImGuiKey.LeftArrow => "Left arrow",
        ImGuiKey.RightArrow => "Right arrow",
        ImGuiKey.UpArrow => "Up arrow",
        ImGuiKey.DownArrow => "Down arrow",
        _ => key.ToString(),
    };
}

/// <summary>A key of the editor and what it does, for the handler of the key and for its row in Help &gt;
/// Keys.</summary>
internal sealed record EditorKey(KeyChord Chord, string Action);

/// <summary>
/// The global keys and the Scene keys of the editor. The key handlers read their chords here, and Help &gt; Keys shows
/// the same list. So the Keys window cannot show a key that the editor does not use. The keys of the gizmo tools are in
/// <see cref="OpenLancer.Editor.Scene.GizmoInput"/>, and the keys of the cutscene preview are in
/// <see cref="OpenLancer.Editor.Cutscene.PreviewKeys"/>.
/// </summary>
internal static class EditorKeys
{
    public static readonly EditorKey Save = new(new KeyChord(ImGuiKey.S, Ctrl: true),
        "Save the edits of the active mod, as File > Save does. It also works while a text field is active.");

    public static readonly EditorKey Undo = new(new KeyChord(ImGuiKey.Z, Ctrl: true), "Undo the last edit.");

    public static readonly EditorKey Redo = new(new KeyChord(ImGuiKey.Y, Ctrl: true), "Redo the last edit that you undid.");

    public static readonly EditorKey RedoShift = new(new KeyChord(ImGuiKey.Z, Ctrl: true, Shift: true),
        "Redo the last edit that you undid.");

    public static readonly EditorKey PlayInGame = new(new KeyChord(ImGuiKey.F5, Ctrl: true),
        "Play the mods in the linked game of the last play, or in the first linked game.");

    public static readonly EditorKey Play = new(new KeyChord(ImGuiKey.F5),
        "Play from the Scene camera in OpenLancer. In the Mission tab, play the open mission from its play start.");

    public static readonly EditorKey Stop = new(new KeyChord(ImGuiKey.F5, Shift: true), "Stop the game.");

    public static readonly EditorKey Rename = new(new KeyChord(ImGuiKey.F2), "Rename the selected item.");

    public static readonly EditorKey Duplicate = new(new KeyChord(ImGuiKey.D, Ctrl: true), "Duplicate the selected item.");

    public static readonly EditorKey Delete = new(new KeyChord(ImGuiKey.Delete), "Delete the selected item.");

    public static readonly EditorKey Frame = new(new KeyChord(ImGuiKey.F),
        "Frame the selected item: the Scene camera moves to show it. In the Scene, the mouse must be over the view.");

    public static readonly EditorKey CancelDrag = new(new KeyChord(ImGuiKey.Escape),
        "Cancel the drag of the gizmo. The item goes back to where it was before the drag. The right mouse button also " +
        "cancels the drag.");

    /// <summary>The keys that work in each panel. Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z and the F5 keys wait while a text field
    /// is active, a field edit is open or a dialog shows.</summary>
    public static readonly EditorKey[] Global = [Save, Undo, Redo, RedoShift, Play, PlayInGame, Stop];

    /// <summary>The keys of the selected item of the open system. They work while the Scene or the Hierarchy has the
    /// focus and no text field is active.</summary>
    public static readonly EditorKey[] Scene = [Rename, Duplicate, Delete, Frame, CancelDrag];
}
