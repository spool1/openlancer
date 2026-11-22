using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Universe;

/// <summary>
/// The model of the Universe panel for an edit session, and the item that a click on a system selects. The map keeps
/// the model until the edit view, its texts or the loaded game data change. While a gesture of the command layer is
/// open, the map keeps the model of the start of the gesture. Examples of a gesture are a drag of the gizmo and a typed
/// field. The end of the gesture makes the model again. A drag changes the edit view in each frame, and the build of
/// the model reads the whole jump graph.
/// </summary>
internal sealed class UniverseMap
{
    // The model, and what it shows: the session, its version and the loaded game data.
    private (EditSession Edits, int Version, object Data)? shown;
    private UniverseModel? model;
    private Dictionary<string, SystemEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model of an edit session.</summary>
    /// <param name="version">A number that changes each time the edit view or its texts change.</param>
    /// <param name="data">The loaded game data. Other data makes the model again, because it changes the new
    /// systems.</param>
    /// <param name="loaded">The system of the loaded game data with a nickname, or null.</param>
    public UniverseModel Of(EditSession edits, ItemTexts texts, int version, object data, Func<string, StarSystem?> loaded)
    {
        if (model != null && shown is var (e, v, d) && ReferenceEquals(e, edits) && ReferenceEquals(d, data) &&
            (v == version || edits.InTransaction))
        {
            return model;
        }

        entries = SystemEntry.All(edits, loaded).ToDictionary(entry => entry.Nickname, StringComparer.OrdinalIgnoreCase);
        var (systems, connections) = UniverseRows.All(edits, texts);
        model = UniverseModel.Of(systems, connections, n => entries.TryGetValue(n, out var entry) && entry.IsNew);
        shown = (edits, version, data);
        return model;
    }

    /// <summary>The item that a click on a system selects (see <see cref="SystemEntry.Item"/>), or null when the model
    /// has no such system.</summary>
    public object? ItemOf(string nickname) => entries.TryGetValue(nickname, out var entry) ? entry.Item : null;
}
