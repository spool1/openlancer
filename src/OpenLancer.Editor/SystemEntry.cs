using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor;

/// <summary>
/// A system of the edit view: a <c>[system]</c> of <c>universe.ini</c>, with the system of the loaded game data. The
/// Systems table of the Database and the Universe panel show it. Both mark a new system and select its item with the
/// rules of this record.
/// </summary>
/// <param name="Section">The <c>[system]</c> section in the edit view.</param>
/// <param name="Loaded">The system of the loaded game data, or null for a new system that the mod has not
/// saved.</param>
internal sealed record SystemEntry(string Nickname, SectionRef Section, StarSystem? Loaded)
{
    /// <summary>The tooltip of the mark of a new system that the mod has not saved.</summary>
    internal const string NewMark = "New. Save to open it in the Scene.";

    /// <summary>True for a new system that the loaded game data does not have.</summary>
    public bool IsNew => Loaded == null;

    /// <summary>The item that a click on the system selects: the system of the loaded game data, so that its full page
    /// shows, or the <c>[system]</c> of a new system.</summary>
    public object Item => (object?)Loaded ?? Section;

    /// <summary>
    /// One entry for each nickname of the <c>[system]</c> sections of the edit view, in file order. A nickname that two
    /// sections share gets one entry, because the engine uses only one of them.
    /// </summary>
    /// <param name="loaded">The system of the loaded game data with a nickname, or null.</param>
    public static IReadOnlyList<SystemEntry> All(EditSession edits, Func<string, StarSystem?> loaded) =>
        edits.SystemSections()
            .DistinctBy(s => s.Id.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SystemEntry(s.Id.Key!, s, loaded(s.Id.Key!)))
            .ToList();
}
