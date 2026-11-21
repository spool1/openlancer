using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data.GameData.World;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Universe;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The rows of the <c>[system]</c> of a star system in <c>universe.ini</c>: the nickname, the name, the infocard and the
/// file with a link that opens the system, then the place and the zoom on the nav map and the visit choice, then the
/// advanced values. The rows bind only to the <c>[system]</c> of W, so they work for a new system before the save,
/// without loaded game data. The page of a <c>[system]</c> (<see cref="SystemEntryPage"/>) and the page of a star system
/// (<see cref="StarSystemPage"/>) use them.
/// </summary>
internal sealed class SystemEntryRows(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The widget of a row, and the value of the game data that it shows without a bound section.</summary>
    internal enum RowKind
    {
        Nickname,
        Name,
        Infocard,

        /// <summary>The read-only file, then the row of the link that opens the system.</summary>
        File,

        /// <summary>Two whole-number fields from 0 to <see cref="SystemPlans.MapMax"/>.</summary>
        MapPosition,
        MapScale,

        /// <summary>The choice of <see cref="VisitChoices"/>.</summary>
        Visit,
        VisitFlags,
        VisitValue,
        MessagePrefix,
    }

    /// <summary>A row of the <c>[system]</c>: its plain label, its key and its tooltip.</summary>
    internal sealed record Row(string Label, string Key, RowKind Kind, string? Help = null);

    /// <summary>The rows of the identity of the system.</summary>
    internal static readonly Row[] IdentityRows =
    [
        new("Nickname", "nickname", RowKind.Nickname),
        new("Name", "strid_name", RowKind.Name, "The name of the system on the nav map and in the game."),
        new("Infocard", "ids_info", RowKind.Infocard, "The text that the nav map shows for the system."),
        new("File", "file", RowKind.File, "The system file. It holds the objects, zones and lights of the system."),
    ];

    /// <summary>The rows of the system on the nav map.</summary>
    internal static readonly Row[] NavMapRows =
    [
        new("Nav map position", "pos", RowKind.MapPosition,
            $"The place of the system on the nav map grid, from 0 to {SystemPlans.MapMax} on each axis."),
        new("Nav map scale", "navmapscale", RowKind.MapScale,
            "The zoom of the map of the system in the game. A larger value shows a smaller part of the system."),
        new("Visit", "visit", RowKind.Visit,
            "When the nav map of the game shows the system. \"On the map at start\" shows it in a new game. " +
            "\"After the first visit\" shows it after the player goes there."),
    ];

    /// <summary>The advanced rows: all the visit flags and the prefix of the voice lines.</summary>
    internal static readonly Row[] AdvancedRows =
    [
        new("Visit flags", "visit", RowKind.VisitFlags,
            "All the flags of the visit value. The Visit row changes only the flag that shows the system on the map at start."),
        new("Visit value", "visit", RowKind.VisitValue, "The visit value as a whole number. It holds all the flags."),
        new("Message id prefix", "msg_id_prefix", RowKind.MessagePrefix,
            "The prefix of the voice lines that say the name of the system. Without it, NPC speech about the system is silent."),
    ];

    /// <summary>The texts of the Visit row: without the flag, and with the flag.</summary>
    internal static readonly (string Clear, string Set) VisitChoices = ("After the first visit", "On the map at start");

    /// <summary>The flag of the visit value that shows the system on the nav map of a new game.</summary>
    internal const ulong OnMapAtStart = (ulong)VisitFlags.Visited;

    /// <summary>The labels of the two values of the place on the nav map.</summary>
    internal static readonly PropertyGrid.ComponentLabel[] PositionLabels = [PropertyGrid.Axes[0], PropertyGrid.Axes[1]];

    /// <summary>The label of the row of the link that opens the system.</summary>
    internal const string SceneLabel = "Scene";

    /// <summary>The text of the link that opens the system in the Scene.</summary>
    internal const string OpenText = "Open system";

    internal const string OpenTip = "Open the system in the Scene.";

    internal const string OpenNewTip =
        "This system is new. The Scene shows a card with a Save button, and it opens the system after the save.";

    internal const string ShownTip = "The Scene shows this system.";

    /// <summary>Draws the identity rows.</summary>
    /// <param name="entry">The <c>[system]</c> of W, or null when the edit view has none. Then the rows show the
    /// values of <paramref name="loaded"/>.</param>
    /// <param name="loaded">The system of the loaded game data, or null for a new system.</param>
    public void Identity(string nickname, SectionRef? entry, StarSystem? loaded) =>
        Rows(IdentityRows, nickname, entry, loaded);

    /// <summary>Draws the rows of the nav map (see <see cref="Identity"/>).</summary>
    public void NavMap(string nickname, SectionRef? entry, StarSystem? loaded) => Rows(NavMapRows, nickname, entry, loaded);

    /// <summary>Draws the advanced rows (see <see cref="Identity"/>).</summary>
    public void Advanced(string nickname, SectionRef? entry, StarSystem? loaded) =>
        Rows(AdvancedRows, nickname, entry, loaded);

    private void Rows(IEnumerable<Row> rows, string nickname, SectionRef? entry, StarSystem? loaded)
    {
        using (grid.Bind(entry))
        {
            foreach (var row in rows)
            {
                Draw(row, nickname, loaded);
            }
        }
    }

    private void Draw(Row row, string nickname, StarSystem? loaded)
    {
        if (row.Help != null)
        {
            grid.Help(row.Help);
        }

        switch (row.Kind)
        {
            case RowKind.Nickname:
                grid.Text(row.Label, nickname, row.Key);
                break;
            case RowKind.Name:
                grid.Ids(row.Label, loaded?.IdsName ?? 0, row.Key);
                break;
            case RowKind.Infocard:
                grid.Info(row.Label, loaded?.IdsInfo ?? 0, row.Key);
                break;
            case RowKind.File:
                grid.Text(row.Label, loaded?.SourceFile, row.Key);
                OpenRow(nickname);
                break;
            case RowKind.MapPosition:
                var place = loaded?.UniversePosition ?? Vector2.Zero;
                grid.WholeNumbers(row.Label, [place.X, place.Y], PositionLabels, 0, SystemPlans.MapMax, row.Key);
                break;
            case RowKind.MapScale:
                grid.Number(row.Label, loaded?.NavMapScale ?? 1, iniKey: row.Key);
                break;
            case RowKind.Visit:
                grid.BitChoice(row.Label, (ulong)(loaded?.Visit ?? VisitFlags.None), row.Key, OnMapAtStart, VisitChoices);
                break;
            case RowKind.VisitFlags:
                grid.Flags(row.Label, loaded?.Visit ?? VisitFlags.None, row.Key);
                break;
            case RowKind.VisitValue:
                grid.Number(row.Label, (int)(loaded?.Visit ?? VisitFlags.None), iniKey: row.Key);
                break;
            case RowKind.MessagePrefix:
                grid.Text(row.Label, loaded?.MsgIdPrefix, row.Key);
                break;
        }
    }

    /// <summary>
    /// The row of the link that opens the system in the Scene, as a double-click on its row of the Database does. A new
    /// system that the loaded game data does not have shows the save card of the Scene. The link is off while the
    /// Scene shows the system.
    /// </summary>
    private void OpenRow(string nickname)
    {
        var shown = window.IsSceneSystem(nickname);
        var isNew = window.Session?.GameData?.Items.Systems.Get(nickname) == null;
        grid.Row(SceneLabel, () =>
        {
            ImGui.AlignTextToFramePadding();
            ImGui.BeginDisabled(shown);
            if (ImGui.TextLink(OpenText))
            {
                window.OpenSystem(nickname, null);
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip | ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(shown ? ShownTip : isNew ? OpenNewTip : OpenTip);
            }
        });
    }
}
