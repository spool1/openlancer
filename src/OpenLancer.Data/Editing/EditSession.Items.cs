using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    private ItemIndex? items;
    private IReadOnlyList<Section>? freelancerIni;

    /// <summary>The item files of the edit view: the ship, equipment, goods and market files.</summary>
    internal ItemIndex Items => items ??= new ItemIndex(ItemIndex.ReadPaths(DataFiles), Table, () => CacheRevision);

    /// <summary>
    /// The section of the files of a kind with a nickname, or null when no section has it, or when more than one
    /// section has it (the engine then uses only one of them). Market sections have no nickname.
    /// </summary>
    public SectionRef? Find(ItemFileKind kind, string nickname) => Items.Find(kind, nickname);

    /// <summary>The sections of the files of a kind that are not child sections, in load order.</summary>
    public IReadOnlyList<SectionRef> List(ItemFileKind kind) => Items.List(kind);

    /// <summary>The sections that a relation links an item to, in load order. The list is empty when the item has no
    /// such link.</summary>
    public IReadOnlyList<SectionRef> Related(SectionRef item, ItemRelation relation) => Items.Related(item, relation);

    /// <summary>The <c>marketgood</c> lines of all market files that sell a good, in load order. A line of a
    /// <c>[BaseGood]</c> section that the active mod does not add is read-only (see <see cref="Get"/>).</summary>
    public IReadOnlyList<MarketLine> MarketLines(string good) => Items.MarketLines(good);

    /// <summary>
    /// The keys that the engine reads in a section, in declaration order. The list is empty when the editor does not
    /// know the schema of the section. The editor knows the schema of the item files, of <c>universe.ini</c> and of
    /// the star system files.
    /// </summary>
    public IReadOnlyList<KeySchema> Schema(SectionRef target) => SchemaOf(target)?.Keys ?? [];

    /// <summary>What the engine reads from the lines of a name in a section, or null when the schema has no such
    /// key, or when the editor does not know the schema of the section.</summary>
    public KeySchema? Schema(SectionRef target, string name) => SchemaOf(target)?.Key(name);

    /// <summary>
    /// The fast checks of a section: an unknown key, a missing required key, a key with one value given two times,
    /// a line with too few values, a value that the engine cannot read as the type of its key, a string that the
    /// engine reads as another integer, a text placeholder of the wrong kind or without text, and, in the item files,
    /// a nickname that another section of the same kind of file also has. The list is empty when the edit view has no
    /// such section, or when the editor does not know its schema. Each problem has the section as its
    /// <see cref="Diagnostic.Target"/>. A problem of the lines of a key also has the key as its
    /// <see cref="Diagnostic.Key"/>.
    /// </summary>
    public IReadOnlyList<Diagnostic> Check(SectionRef target) =>
        SchemaOf(target) is { } schema && TryFind(target, out var document, out var index)
            ? Check(target, schema, document, index)
            : [];

    /// <summary>The checks of <see cref="Check(SectionRef)"/> for a section whose schema and document the caller
    /// has.</summary>
    private List<Diagnostic> Check(SectionRef target, SectionSchema schema, EditDocument document, int index)
    {
        var section = document.Working[index];
        var problems = SectionChecks.Check(schema, section, target, document.IsBinary);
        if (ActiveMod != null && HasPlaceholder(section))
        {
            // The save refuses a placeholder without text (rule S8).
            foreach (var lines in section.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                problems.AddRange(lines.SelectMany(MissingPlaceholders).Distinct().Select(p =>
                    Diagnostic.Error(target.Path, $"{target.Id}: {MissingTextMessage(p)}") with
                    {
                        Target = target,
                        Key = lines.Key,
                    }));
            }
        }
        var key = target.Id.Key;
        if (Items.KindOf(target.Path) is { } kind && key != null && !SectionKeys.IsChild(target.Id.Type) &&
            Items.Named(kind, key) is { Count: > 1 } same)
        {
            problems.Add(Diagnostic.Warning(target.Path,
                $"{target.Id}: {same.Count} sections of the {kind.ToString().ToLowerInvariant()} files have this " +
                $"nickname, so the engine uses only one of them: {string.Join(", ", same.Select(s => s.Path))}.") with
            {
                Target = target,
            });
        }
        return problems;
    }

    private SectionSchema? SchemaOf(SectionRef target) =>
        Items.KindOf(target.Path) is { } kind
            ? SchemaCatalog.Section(ItemIndex.SchemaOf(kind), target.Id.Type)
            : Systems.SchemaOf(target.Path, target.Id.Type) ??
              (Rooms.Contains(target.Path) ? SchemaCatalog.Section(typeof(Room), target.Id.Type) : null);

    /// <summary>True when a value of a line of the section is a text placeholder. Most sections have no placeholder,
    /// so the check of rule S8 can skip them. This is important for the catch-up of a system in the scene, which
    /// checks each section of the system.</summary>
    private static bool HasPlaceholder(Section section)
    {
        foreach (var entry in section)
        {
            for (var i = 0; i < entry.Count; i++)
            {
                if (StringPlaceholder.TryParse(entry[i], out _))
                    return true;
            }
        }
        return false;
    }

    /// <summary>The sections of the <c>freelancer.ini</c> of the edit view, read at the first use. A patch cannot
    /// change the file, so it does not change in a session.</summary>
    private IReadOnlyList<Section> FreelancerIni => freelancerIni ??= ReadFreelancerIni();

    /// <summary>The sections of the <c>freelancer.ini</c> of the edit view, or none when it has no such file.</summary>
    private IReadOnlyList<Section> ReadFreelancerIni()
    {
        var fs = EditView.CreateFileSystem();
        if (!fs.FileExists(ModOverlay.FreelancerIniPath))
            return [];
        var bytes = fs.ReadAllBytes(ModOverlay.FreelancerIniPath);
        return IniDocument.Parse(ModOverlay.FreelancerIniPath, bytes).Sections;
    }

    /// <summary>The document of an install path, or null when the edit view has no such INI file. It opens the file at
    /// its first use.</summary>
    internal EditDocument? OpenDocument(string path) => TryDocument(path, out var document) ? document : null;

    /// <summary>The install path of an open INI file in the letter case that the session stores (section 4.0.7 of the
    /// M6b design), or the path itself when the file is not open. It opens no file. For example, a path that
    /// <c>freelancer.ini</c> spells in lowercase gives the letter case of the files.</summary>
    internal string StoredPath(string path) =>
        InstallPaths.TryParse(path) is { } normal && documents.TryGetValue(normal, out var document) ? document.Path : path;
}
