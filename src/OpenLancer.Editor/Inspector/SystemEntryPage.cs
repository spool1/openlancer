using OpenLancer.Data.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a <c>[system]</c> of <c>universe.ini</c>. The Database row and follow mode select it for a new
/// system that the mod has not saved. The page shows the rows of <see cref="SystemEntryRows"/> from W, then the keys
/// that no row shows. The panel adds the raw INI block. After the save and the reload of the game data, the Database
/// row selects the system, and <see cref="StarSystemPage"/> adds the environment and the contents.
/// </summary>
internal sealed class SystemEntryPage(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The first part of the settings keys of the sections of the page.</summary>
    private const string Key = "Section.system";

    /// <summary>The note above the rows of a new system.</summary>
    internal const string NewNote =
        "This system is new. Save the mod to open it in the Scene. Then the page of the system also shows its " +
        "environment and its contents.";

    private readonly SystemEntryRows rows = new(window, grid);
    private readonly SectionExtras extras = new(window, grid);

    public void Draw(SectionRef entry)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        if (session.Edits.Get(entry) == null)
        {
            PanelStates.Message(SchemaPage.NoSection);
            return;
        }

        var nickname = entry.Id.Key ?? "";
        var loaded = session.GameData?.Items.Systems.Get(nickname);
        if (grid.BeginSection(Key + ".Identity", EditorIcons.Tag, "Identity"))
        {
            if (loaded == null)
            {
                grid.NoteRow(NewNote);
            }

            rows.Identity(nickname, entry, loaded);
            grid.EndSection();
        }

        if (grid.BeginSection(Key + ".Universe", EditorIcons.Globe, "Universe"))
        {
            rows.NavMap(nickname, entry, loaded);
            grid.EndSection();
        }

        if (grid.BeginSection(Key + ".Advanced", EditorIcons.Cog, "Advanced", false))
        {
            rows.Advanced(nickname, entry, loaded);
            grid.EndSection();
        }

        extras.Draw([entry], Key, moreKeys: true);
    }
}
