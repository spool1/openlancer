using System;
using LibreLancer.Data;
using LibreLancer.Data.GameData.World;
using LibreLancer.Infocards;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The texts of items as the edit view gives them, for example the name of an object of the open system. A text comes
/// from the section of the item in W, so it shows an edit before the save: a placeholder through the text of the active
/// mod, and a number through the game data. An item without a section in W shows the text of its id in the game data.
/// A light has no text, as in the game. A nebula and an asteroid field take their texts from their zone, because their
/// sections have no text key (section 5 of the M22 design).
/// </summary>
/// <remarks>
/// A scene item keeps the text id that the system loaded with when W holds a placeholder (see
/// <see cref="Runtime.Scene.SceneApplier"/>), so each reader of a scene name reads it here (section 14 of the design).
/// </remarks>
/// <param name="name">The text of a string id, or an empty string.</param>
/// <param name="info">The plain text of an infocard id, or an empty string.</param>
internal sealed class ItemTexts(EditSession edits, Func<int, string> name, Func<int, string> info)
{
    /// <summary>Gives the section of a scene item in W, or null. The scene sets it while a system is open.</summary>
    public Func<object, SectionRef?>? SceneSections { get; set; }

    /// <summary>The name of an item: its <c>ids_name</c>, or the <c>strid_name</c> of a system or a base. An empty
    /// string when it has no name.</summary>
    public string NameOf(object? item) => TextOf(item, NameKey(item));

    /// <summary>The key of the name of an item: <c>strid_name</c> for a system and for the <c>[system]</c> and
    /// <c>[Base]</c> sections of <c>universe.ini</c>, else <c>ids_name</c>.</summary>
    public static string NameKey(object? item) => item switch
    {
        StarSystem => "strid_name",
        SectionRef section when section.Id.Type.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                                section.Id.Type.Equals("Base", StringComparison.OrdinalIgnoreCase) => "strid_name",
        _ => "ids_name",
    };

    /// <summary>The name of a section: the text of its <c>ids_name</c>, or an empty string.</summary>
    public string NameOf(SectionView view) => TextOf(view, "ids_name", StringKind.String);

    /// <summary>
    /// The text of a text key of an item, for example <c>tradelane_space_name</c> of a trade lane ring: from the section
    /// of the item in W, or from the id of the item when W has no section for it. An empty string when it has no
    /// text.
    /// </summary>
    public string TextOf(object? item, string key)
    {
        if (item is Nebula or AsteroidField)
            item = SceneItems.ZoneOf(item);
        if (SectionOf(item) is { } section && edits.Get(section) is { } view)
            return TextOf(view, key, edits.Schema(section, key)?.TextKind ?? StringKind.String);
        return IdOf(item, key) is var id and > 0 ? name(id) : "";
    }

    /// <summary>
    /// The section of W that an item shows, or null. A light gives no section: the engine reads no text key of a light
    /// (GameItemDb.cs:1466-1516), so a light has no name, also when its section has an <c>ids_name</c> line.
    /// </summary>
    private SectionRef? SectionOf(object? item) => item switch
    {
        null or LightSource => null,
        SectionRef section => section,
        StarSystem system => edits.SystemSection(system.Nickname),
        _ => SceneSections?.Invoke(item),
    };

    /// <summary>The text of the last line of a key: the engine reads only the last line.</summary>
    private string TextOf(SectionView view, string key, StringKind kind)
    {
        var lines = view.Lines(key);
        if (lines.Count == 0 || lines[^1].Values is not [var value, ..])
            return "";
        if (StringPlaceholder.TryParse(value, out var placeholder))
        {
            var text = edits.Text(placeholder) ?? "";
            return placeholder.Kind == StringKind.Infocard && text.Length > 0 ? PlainText(text) : text;
        }
        return value.TryToInt32(out var id) && id > 0 ? kind == StringKind.Infocard ? info(id) : name(id) : "";
    }

    /// <summary>The plain text of an infocard in RDL XML.</summary>
    public static string PlainText(string xml) => RDLParse.Parse(xml).ExtractText().TrimEnd();

    /// <summary>The string resource id of the name of an item, or 0 when the item has no name.</summary>
    public static int NameIds(object? item) => item switch
    {
        SystemObject obj => obj.IdsName,
        Zone zone => zone.IdsName,
        NamedItem named => named.IdsName,
        _ => 0,
    };

    /// <summary>The text id of a key of an item without a section in W, or 0.</summary>
    private static int IdOf(object? item, string key) => key.ToLowerInvariant() switch
    {
        "ids_name" or "strid_name" => NameIds(item),
        "tradelane_space_name" => item is SystemObject obj ? obj.TradelaneSpaceName : 0,
        _ => 0,
    };
}
