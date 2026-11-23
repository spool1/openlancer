using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Strings;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of the name-key table (<c>SchemaCatalog.TextKind</c>): each retail text id of a key in the table is
/// a string or an infocard of the retail resource DLLs, as the table says. The editor writes <c>$str</c> or
/// <c>$ifc</c> for a key from the table.
/// </summary>
public sealed class NameKeyKindTests(ITestOutputHelper output)
{
    /// <summary>
    /// The retail lines whose <c>ids_info</c> names a string, by section type. Freelancer shows such a string as an
    /// infocard with plain text, for example "A small space station." for many solars. Librelancer reads an infocard
    /// only, so the table gives an infocard, which both engines show.
    /// </summary>
    private static readonly Dictionary<string, int> RetailStringInfos = new(StringComparer.Ordinal)
    {
        ["[commodity] ids_info"] = 65,
        ["[missioncreatedsolar] ids_info"] = 1,
        ["[object] ids_info"] = 94,
        ["[solar] ids_info"] = 155,
    };

    /// <summary>
    /// The name-key table as docs/MOD_FORMAT.md gives it: a section type for a section-type exception, or null for
    /// all section types, the key and the kind of text. The table of the editor must be the same, and a retail text
    /// must confirm each row. So the test fails when a row is missing, added or not confirmed.
    /// </summary>
    private static readonly (string? Section, string Name, StringKind Kind)[] Table =
    [
        (null, "ids_name", StringKind.String),
        (null, "ids_short_name", StringKind.String),
        (null, "strid_name", StringKind.String),
        (null, "tradelane_space_name", StringKind.String),
        (null, "individual_name", StringKind.String),
        (null, "ids_info", StringKind.Infocard),
        (null, "ids_info1", StringKind.Infocard),
        (null, "ids_info2", StringKind.Infocard),
        (null, "ids_info3", StringKind.Infocard),
        ("keycmd", "ids_info", StringKind.String),
    ];

    /// <summary>Keys that look like text keys, but whose retail values have no text. So the retail data cannot
    /// confirm their kind, and they stay out of the table.</summary>
    private static readonly string[] Unconfirmed = ["[package] strid_desc"];

    static NameKeyKindTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void EachRetailTextIdIsTheKindThatTheTableGives()
    {
        var baseFolder = GameDataPaths.RequireBase();
        var texts = new InfocardManager(new FreelancerIni(FileSystem.FromPath(baseFolder)).Resources);
        Assert.Equal(Table.Order(),
            SchemaCatalog.TextKeyRows.Select(r => (r.Section?.ToLowerInvariant(), r.Name.ToLowerInvariant(), r.Kind)).Order());
        var counts = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
        var confirmed = new HashSet<(string?, string)>();
        var notInTable = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(baseFolder, "*.ini", SearchOption.AllDirectories))
        {
            foreach (var section in IniDocument.Parse(file, File.ReadAllBytes(file)).Sections)
            {
                foreach (var entry in section)
                {
                    var name = entry.Name.ToLowerInvariant();
                    var label = $"[{section.Name.ToLowerInvariant()}] {name}";
                    var kind = SchemaCatalog.TextKind(section.Name, name);
                    if (kind == null)
                    {
                        // A key that looks like a text key and holds a number must be in the table.
                        if ((name.StartsWith("ids_", StringComparison.Ordinal) || name.StartsWith("strid_", StringComparison.Ordinal)) &&
                            entry.Count > 0 && entry[0] is Int32Value && !Unconfirmed.Contains(label))
                            notInTable.Add(label);
                        continue;
                    }
                    var count = counts.TryGetValue(label, out var c) ? c : counts[label] = new Counts();
                    var id = entry.Count > 0 && entry[0].TryToInt32(out var number) ? number : 0;
                    var isString = texts.HasStringResource(id);
                    var isInfocard = texts.HasXmlResource(id);
                    // Retail uses id 1, a string of one space, for no text.
                    if (id <= 0 || isString && !isInfocard && string.IsNullOrWhiteSpace(texts.GetStringResource(id)))
                        count.NoText++;
                    else if (kind == StringKind.String ? isString : isInfocard)
                    {
                        count.Right++;
                        var type = section.Name.ToLowerInvariant();
                        confirmed.Add(Table.Any(r => r.Section == type && r.Name == name) ? (type, name) : (null, name));
                    }
                    else if (kind == StringKind.String ? isInfocard : isString)
                        count.Wrong++;
                    else
                        count.Missing++;
                }
            }
        }

        foreach (var (label, count) in counts)
            output.WriteLine($"{label}: {count.Right} right, {count.Wrong} wrong, {count.Missing} missing, {count.NoText} without text");
        foreach (var label in notInTable)
            output.WriteLine("not in the table: " + label);
        Assert.Empty(notInTable);
        Assert.Equal(RetailStringInfos, counts.Where(c => c.Value.Wrong > 0).ToDictionary(c => c.Key, c => c.Value.Wrong));
        // Retail confirms the kind of the key in each section type that has a text for it.
        Assert.All(counts.Where(c => c.Value.Right + c.Value.Wrong > 0), c => Assert.True(c.Value.Right > 0, c.Key));
        // The ship dealer shows these keys. The plan kept them read-only until this test confirms their kind.
        Assert.All(Table, row => Assert.Contains((row.Section, row.Name), confirmed));
        Assert.All(new[] { "[ship] ids_info1", "[ship] ids_info2", "[ship] ids_info3" },
            label => Assert.Equal(33, counts[label].Right));
    }

    private sealed class Counts
    {
        public int Right;
        public int Wrong;
        public int Missing;
        public int NoText;
    }
}
