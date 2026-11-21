using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Scene;

/// <summary>The state of the save card of the Scene (see <see cref="SaveCard"/>).</summary>
internal enum SaveCardState
{
    /// <summary>The loaded game data does not have the system. The card asks for a save.</summary>
    New,

    /// <summary>The game data loads. The card waits for the end of the load.</summary>
    Reloading,

    /// <summary>The loaded game data has the system now, so the Scene opens it.</summary>
    Open,

    /// <summary>The edit view has no <c>[system]</c> of the system now, for example after an undo of its create. The card
    /// closes.</summary>
    Gone,
}

/// <summary>
/// The card that the Scene shows in place of a new system that the mod has not saved. The Scene builds a system from the
/// loaded game data, so a new system opens only after the save and the reload of the game data. Each open of a system
/// by its nickname shows the card for a new system: "Go to partner", the "Go to" of a completeness step, the Database,
/// the page of a <c>[system]</c> and follow mode of the agent. The Save button runs the save of File &gt; Save, with the
/// save gate. During the reload, the card tells that the editor loads the game data. After the reload, the Scene opens
/// the system.
/// </summary>
internal static class SaveCard
{
    /// <summary>The text of the card of a new system.</summary>
    internal static readonly EmptyState NewSystem = new("This system is new.", "Save the mod to open it in the Scene.");

    /// <summary>The text of the card while the game data loads.</summary>
    internal const string Reloading = "The editor loads the game data.";

    /// <summary>The text of the button of the card.</summary>
    internal const string SaveButton = "Save";

    /// <summary>The state of the card of a system.</summary>
    /// <param name="busy">True while the game data loads.</param>
    /// <param name="loaded">True when the loaded game data has the system.</param>
    internal static SaveCardState StateOf(EditSession edits, string system, bool busy, bool loaded) =>
        busy ? SaveCardState.Reloading
        : edits.SystemSection(system) == null ? SaveCardState.Gone
        : loaded ? SaveCardState.Open
        : SaveCardState.New;
}
