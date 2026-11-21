using System;
using System.Linq;
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

    /// <summary>The loaded game data has the system, but a field edit or a drag runs. The open waits for its end, as the
    /// open after a reload does.</summary>
    Waiting,

    /// <summary>The loaded game data has the system now, so the Scene opens it.</summary>
    Open,

    /// <summary>The edit view has no <c>[system]</c> of the system now, for example after an undo of its create or after
    /// its delete. The card closes.</summary>
    Gone,
}

/// <summary>The new system of the save card of the Scene, and the section to select when the system opens.</summary>
/// <param name="System">The nickname of the system.</param>
/// <param name="Select">The section of an item of the system to select when the system opens after the save, or
/// null.</param>
internal sealed record SaveCardTarget(string System, SectionRef? Select)
{
    /// <summary>
    /// The target after a change of the edit view. A rename of the <c>[system]</c> of the system gives the new nickname,
    /// also when an agent makes it and when an undo takes it back. So the card stays, as Unity keeps a renamed item in
    /// view. A rename of the section that waits for the open moves it too.
    /// </summary>
    public SaveCardTarget After(EditChange change, EditSession edits)
    {
        var target = this;
        foreach (var (from, to) in change.Renames)
        {
            if (edits.IsSystemSection(from) && to.Id.Key is { } nickname &&
                string.Equals(from.Id.Key, target.System, StringComparison.OrdinalIgnoreCase))
            {
                target = target with { System = nickname };
            }

            if (from.Equals(target.Select))
            {
                target = target with { Select = to };
            }
        }

        return target;
    }
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

    /// <summary>The text of the card while the open waits for the end of a field edit or a drag.</summary>
    internal const string Waiting = "The Scene opens the system after the field edit or the drag.";

    /// <summary>The text of the card in the frame before the Scene opens the system.</summary>
    internal const string Opening = "The Scene opens the system.";

    /// <summary>The text of the button of the card.</summary>
    internal const string SaveButton = "Save";

    /// <summary>The state of the card of a system.</summary>
    /// <param name="busy">True while the game data loads.</param>
    /// <param name="loaded">True when the loaded game data has the system.</param>
    /// <param name="waiting">True while a field edit or a drag runs.</param>
    internal static SaveCardState StateOf(EditSession edits, string system, bool busy, bool loaded, bool waiting) =>
        busy ? SaveCardState.Reloading
        : edits.SystemSection(system) == null ? SaveCardState.Gone
        : !loaded ? SaveCardState.New
        : waiting ? SaveCardState.Waiting
        : SaveCardState.Open;

    /// <summary>
    /// The <c>[system]</c> whose card an open of a system by its nickname shows, or the Console text that tells why the
    /// open shows no card. Call it for a nickname that the loaded game data does not have.
    /// </summary>
    /// <param name="dataLoaded">False while the first load of the game data runs.</param>
    internal static (SectionRef? Entry, string? Why) EntryOf(EditSession edits, string nickname, bool dataLoaded)
    {
        if (!dataLoaded)
        {
            return (null, $"The editor loads the game data. Open the system {nickname} after the load.");
        }

        if (edits.SystemSection(nickname) is { } entry)
        {
            return (entry, null);
        }

        return (null, edits.SystemSections().Any(s => string.Equals(s.Id.Key, nickname, StringComparison.OrdinalIgnoreCase))
            ? $"More than one [system] has the nickname {nickname}. Give each system a different nickname to open it " +
              "in the Scene."
            : $"The game data and the mod have no system {nickname}.");
    }
}
