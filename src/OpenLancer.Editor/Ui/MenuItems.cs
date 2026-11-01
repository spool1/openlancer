using ImGuiNET;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// Draws the menu items that can be off (#56). An item with a reason is off, and its tooltip shows the reason, also
/// while the item is off. So each item that is off tells why. Each menu of the editor draws such an item with this
/// class. A test reads the IL of the editor and finds each other call that can turn a menu item off.
/// </summary>
internal static class MenuItems
{
    /// <summary>The reason of an item that needs an open profile.</summary>
    public const string NoProfile = "No profile is open.";

    /// <summary>The reason of an item that waits for the end of a field edit or a drag.</summary>
    public const string EditOpen = "A field edit or a drag is open. Finish it first.";

    /// <summary>The reason of Copy nickname for an item without a nickname.</summary>
    public const string NoNickname = "The item has no nickname.";

    /// <summary>The reason of Copy name for an item without a name.</summary>
    public const string NoName = "The item has no name.";

    /// <summary>The reason of the Revert of a field that has nothing to put back.</summary>
    public const string NoRevert = "The active mod does not change this value, or no lower layer has it.";

    /// <summary>The reason of an item that needs the game data of the open profile, or null when the game data is
    /// there.</summary>
    public static string? NoGameData(EditorSession? session) =>
        session == null ? NoProfile
        : session.GameData != null ? null
        : session.IsLoading ? EditorWindow.ProfileLoading
        : "The game data of the profile did not load.";

    /// <summary>Draws a menu item with a glyph before its label. Returns true when the user clicks it.</summary>
    /// <param name="reason">Why the item is off, or null for an item that is on.</param>
    /// <param name="tooltip">The tooltip of the item while it is on, or null for no tooltip.</param>
    /// <param name="shortcut">The text of the key of the item, for example "Ctrl+S", or an empty text.</param>
    /// <param name="selected">True draws a check mark after the label.</param>
    public static bool Item(char icon, string label, string? reason, string? tooltip = null, string shortcut = "",
        bool selected = false) =>
        Item($"{icon}   {label}", reason, tooltip, shortcut, selected);

    /// <summary>Draws a menu item. Returns true when the user clicks it.</summary>
    /// <param name="reason">Why the item is off, or null for an item that is on.</param>
    /// <param name="tooltip">The tooltip of the item while it is on, or null for no tooltip.</param>
    /// <param name="shortcut">The text of the key of the item, for example "Ctrl+S", or an empty text.</param>
    /// <param name="selected">True draws a check mark after the label.</param>
    public static bool Item(string label, string? reason, string? tooltip = null, string shortcut = "",
        bool selected = false)
    {
        var clicked = ImGui.MenuItem(label, shortcut, selected, reason == null);
        Tooltip(reason ?? tooltip);
        return clicked;
    }

    /// <summary>Draws a menu item with a check mark that a click turns on or off. Returns true when the user clicks
    /// it.</summary>
    /// <param name="reason">Why the item is off, or null for an item that is on.</param>
    /// <param name="tooltip">The tooltip of the item while it is on, or null for no tooltip.</param>
    public static bool Toggle(string label, ref bool value, string? reason, string? tooltip = null)
    {
        var clicked = ImGui.MenuItem(label, "", ref value, reason == null);
        Tooltip(reason ?? tooltip);
        return clicked;
    }

    /// <summary>Starts a submenu. Returns true while it is open. Then call <see cref="ImGui.EndMenu"/>.</summary>
    /// <param name="reason">Why the submenu is off, or null for a submenu that is on.</param>
    /// <param name="tooltip">The tooltip of the submenu while it is on and closed, or null for no tooltip.</param>
    public static bool BeginMenu(string label, string? reason, string? tooltip = null)
    {
        var open = ImGui.BeginMenu(label, reason == null);
        if (!open)
        {
            Tooltip(reason ?? tooltip);
        }

        return open;
    }

    /// <summary>Draws a line that is always off, for example "No recent profiles", which tells why a menu has no items, or
    /// a line of facts.</summary>
    /// <param name="tooltip">More words about the line, or null for no tooltip.</param>
    public static void Note(string text, string? tooltip = null)
    {
        ImGui.MenuItem(text, false);
        Tooltip(tooltip);
    }

    private static void Tooltip(string? text)
    {
        if (text != null)
        {
            ItemTooltip.ShowWrapped(text);
        }
    }
}
