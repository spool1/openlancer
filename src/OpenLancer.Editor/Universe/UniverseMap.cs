using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Agent;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Universe;

/// <summary>What a selection highlights in the Universe panel (AGENTS.md UI rule 6).</summary>
/// <param name="System">The nickname of the system with the ring, or null.</param>
/// <param name="Line">The line of a selected jump object, or null.</param>
/// <param name="SystemLines">True when the system itself is selected. Then each line of the system is wider.</param>
internal readonly record struct UniverseHighlight(string? System, UniverseMapLine? Line, bool SystemLines)
{
    /// <summary>True when a system has the ring.</summary>
    public bool HasRing(UniverseMapSystem system) => Same(system.Nickname, System);

    /// <summary>True when a line is wider: the line of the selected jump object, or a line of the selected
    /// system.</summary>
    public bool IsWide(UniverseMapLine line) =>
        ReferenceEquals(line, Line) || SystemLines && (Same(line.From, System) || Same(line.To, System));

    private static bool Same(string a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

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

    // The highlight of the last selection, for the model that gave it.
    private (object? Selection, UniverseModel Model, UniverseHighlight Highlight)? highlighted;

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

    /// <summary>
    /// What a selection highlights in the last model. A selected system gets the ring, and its lines are wider. A selected
    /// section of a system file gives the ring to its system. When the section is a jump object, its line is also wider.
    /// Each other selection highlights nothing.
    /// </summary>
    /// <param name="selection">The selected item. For an item of the Scene, the caller gives its section.</param>
    public UniverseHighlight Highlight(EditSession edits, object? selection)
    {
        if (model == null)
        {
            return default;
        }

        if (highlighted is var (kept, keptModel, highlight) && Equals(kept, selection) && ReferenceEquals(keptModel, model))
        {
            return highlight;
        }

        highlight = selection switch
        {
            StarSystem system => new UniverseHighlight(system.Nickname, null, true),
            SectionRef section when edits.IsSystemSection(section) => new UniverseHighlight(section.Id.Key, null, true),
            SectionRef section when edits.SystemNicknameOf(section.Path) is { } nickname =>
                new UniverseHighlight(nickname, model.LineOf(ItemRefs.Canonical(edits, section)), false),
            _ => default,
        };
        highlighted = (selection, model, highlight);
        return highlight;
    }
}
