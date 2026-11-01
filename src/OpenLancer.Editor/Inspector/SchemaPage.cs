using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a section of an item file, for example a good, a hull good, a package or a new item that the
/// game data does not have yet. It shows the links to the related sections, the keys that the section sets in the
/// declaration order of the schema, a closed list of the keys that the section does not set, the bases that sell a
/// good, then the keys that the schema does not know. The panel adds the raw INI block of the section (see
/// <see cref="SectionExtras"/>).
/// </summary>
internal sealed class SchemaPage(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The empty state of a selected section that the edit view does not have, for example after an undo of
    /// its create (#56).</summary>
    internal static readonly EmptyState NoSection = new("The edit view does not have this section.",
        "Select another item, or click Undo or Redo to restore the section.");

    private readonly MarketSection market = new(window, grid);
    private readonly SectionExtras extras = new(window, grid);
    private readonly MissionUsesRows missionRows = new(window, grid);

    public void Draw(SectionRef section)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        if (session.Edits.Get(section) is not { } view)
        {
            PanelStates.Message(NoSection);
            return;
        }

        var key = $"Section.{view.Type}";
        Related(session, section, view, key);
        var keys = Layout(session.Edits, section, grid.OpenEdit);
        using (grid.Bind(section))
        {
            Keys(keys, key);
            UnsetKeys(keys, key);
        }

        if (view.Type.Equals("Good", StringComparison.OrdinalIgnoreCase) && section.Id.Key is { } nickname)
        {
            market.Draw(nickname, key + ".SoldAt");
        }

        // A base lists the mission lines that name it (section 10.18 of the M6a design).
        if (view.Type.Equals("Base", StringComparison.OrdinalIgnoreCase))
        {
            missionRows.Section(section.Id.Key, key);
        }

        // The page shows each schema key in Keys or in Unset keys.
        extras.Draw([section], key, moreKeys: false);
    }

    /// <summary>The sections that the section links to, or that link to it, with a label for each link.</summary>
    internal static List<(string Label, SectionRef Target)> RelatedLinks(EditSession edits, SectionRef section, SectionView view)
    {
        var links = new List<(string, SectionRef)>();
        void Add(string label, IEnumerable<SectionRef?> targets) =>
            links.AddRange(targets.OfType<SectionRef>().Where(t => !t.Equals(section)).Select(t => (label, t)));

        Add("Good", edits.Related(section, ItemRelation.Good));
        Add("Munition", edits.Related(section, ItemRelation.Munition));
        Add("Hull good", edits.Related(section, ItemRelation.Hull));
        Add("Package", edits.Related(section, ItemRelation.Package));
        if (view.Type.Equals("Good", StringComparison.OrdinalIgnoreCase))
        {
            Add("Equipment", [section.Id.Key == null ? null : edits.Find(ItemFileKind.Equipment, section.Id.Key)]);
            Add("Ship", [First(view, "ship") is { } ship ? edits.Find(ItemFileKind.Ships, ship) : null]);
            Add("Hull good", [First(view, "hull") is { } hull ? edits.Find(ItemFileKind.Goods, hull) : null]);
        }

        return links;
    }

    private void Related(EditorSession session, SectionRef section, SectionView view, string key)
    {
        var links = RelatedLinks(session.Edits, section, view);
        if (links.Count == 0 || !grid.BeginSection(key + ".Related", EditorIcons.Tags, "Related"))
        {
            return;
        }

        foreach (var (label, target) in links)
        {
            grid.Reference(label, ObjectKinds.OfSection(target.Id.Type), target.Id.Key ?? target.Id.ToString(),
                session.SectionName(target), target);
        }

        grid.EndSection();
    }

    /// <summary>The keys of a section in the lists of the page.</summary>
    /// <param name="Set">The schema keys that the section sets, in the declaration order of the schema.</param>
    /// <param name="Unknown">The keys that the schema does not know, in file order.</param>
    /// <param name="Unset">The schema keys that the section does not set, without the key entry.</param>
    internal sealed record KeyLists(IReadOnlyList<string> Set, IReadOnlyList<string> Unknown,
        IReadOnlyList<string> Unset);

    /// <summary>
    /// The keys of a section in the lists of the page. While a field edit changes the lines of the section, the lists
    /// come from the section at the start of the edit. So the field of the edit keeps its place when the edit sets an
    /// unset key or clears a set key, and the next characters that the user types go to the same key. After the edit,
    /// the key moves to its new list.
    /// </summary>
    /// <param name="open">The open field edit, or null.</param>
    internal static KeyLists Layout(EditSession edits, SectionRef section, FieldGesture? open)
    {
        var view = PropertyGrid.LayoutView(edits, section, open);
        if (view == null)
        {
            return new KeyLists([], [], []);
        }

        var schema = edits.Schema(section);
        return new KeyLists(
            schema.Select(k => k.Name).Where(n => view.Lines(n).Count > 0).ToList(),
            view.Entries.Select(e => e.Name)
                .Where(n => !schema.Any(k => k.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            schema.Where(k => !k.IsKeyEntry && view.Lines(k.Name).Count == 0).Select(k => k.Name).ToList());
    }

    /// <summary>The schema keys that the section sets, in declaration order.</summary>
    private void Keys(KeyLists keys, string key)
    {
        if (!grid.BeginSection(key + ".Keys", EditorIcons.Th, "Keys"))
        {
            return;
        }

        foreach (var name in keys.Set)
        {
            grid.Text(name, null, name);
        }

        grid.EndSection();
    }

    /// <summary>The schema keys that the section does not set. A value in a row adds the key.</summary>
    private void UnsetKeys(KeyLists keys, string key)
    {
        if (keys.Unset.Count == 0 ||
            !grid.BeginSection(key + ".Unset", EditorIcons.Plus, "Unset keys", defaultOpen: false))
        {
            return;
        }

        foreach (var name in keys.Unset)
        {
            grid.Text(name, null, name);
        }

        grid.EndSection();
    }

    private static string? First(SectionView view, string name) =>
        view.Lines(name) is [{ Values: [var value, ..] }, ..] ? value.ToString() : null;
}
