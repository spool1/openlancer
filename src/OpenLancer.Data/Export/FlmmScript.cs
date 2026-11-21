using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Export;

/// <summary>
/// The script of an FLMM package (milestone M11): the operations of the Freelancer Mod Manager that change the INI
/// files of the game, and the whole files that the package holds.
/// </summary>
/// <remarks>
/// <para>No official schema of the script exists. The rules come from the FLMM tutorial, community posts and working
/// scripts, so the FLMM check of the M11 manual test must confirm them. Until then, the package is a preview.</para>
/// <list type="bullet">
/// <item><c>append</c> without <c>section</c> adds a new section at the end of a file.</item>
/// <item><c>sectionappend</c> finds the text of <c>section</c> and adds <c>source</c> after it. The anchor is the
/// header line and the key line of a section, and the source starts with a line end.</item>
/// <item><c>sectionreplace</c> replaces the text of <c>dest</c> in the block that <c>section</c> finds.</item>
/// <item><c>GENERATESTRRES("text")</c> and <c>GENERATEXMLRES("text")</c> give the id of a new string or infocard in
/// a text DLL of FLMM.</item>
/// </list>
/// <para>A change that the operations cannot express puts the whole merged file into the package. A whole file
/// cannot hold mod text, so such a change with mod text is a refusal.</para>
/// <para>A new <c>[Base]</c> of <c>universe.ini</c> is a refusal too. It must go before the <c>[system]</c> sections
/// (see <see cref="UniverseOrder"/>), and an <c>append</c> puts it at the end of the file. FLMM has no tested
/// operation that puts a section before the first <c>[system]</c> yet. Each refusal names the drop-in zip, which
/// always carries the new content.</para>
/// </remarks>
internal sealed partial class FlmmScript
{
    /// <summary>The oldest FLMM version that the script needs.</summary>
    public const string ScriptVersion = "1.31";

    private const string ScriptName = "script.xml";
    private const string DataSection = "Data";

    private readonly List<string> operations = [];
    private readonly List<(string Path, byte[] Bytes)> files = [];
    private readonly List<Diagnostic> problems = [];
    private readonly TargetFiles target;
    private readonly ModStrings strings;
    private readonly bool probe;

    private FlmmScript(TargetFiles target, ModStrings strings, bool probe)
    {
        this.target = target;
        this.strings = strings;
        this.probe = probe;
    }

    /// <summary>The text of <c>script.xml</c>: ASCII, with CR LF line ends, without an XML declaration.</summary>
    public string Xml { get; private set; } = "";

    /// <summary>The text of <c>script.xml</c> without its <c>header</c> element. The guard digest of the package uses
    /// it, so that a change of the publish texts does not change the digest (decision D589).</summary>
    public string OperationsXml { get; private set; } = "";

    /// <summary>The whole files of the package tree, by install path with the letter case of the game.</summary>
    public IReadOnlyList<(string Path, byte[] Bytes)> Files => files;

    /// <summary>The refusals (errors) and the fallbacks (warnings).</summary>
    public IReadOnlyList<Diagnostic> Problems => problems;

    /// <summary>Makes the script of one mod.</summary>
    /// <param name="target">The files of a target build that keeps the text placeholders.</param>
    /// <param name="header">The fields of the header element.</param>
    /// <param name="probe">True to write each mod text in its macro as it is, also with a quote. The FLMM check uses
    /// such a package. It is never for players.</param>
    public static FlmmScript Build(TargetFiles target, Mod mod, FlmmHeader header, bool probe)
    {
        var script = new FlmmScript(target, mod.Strings, probe);
        foreach (var file in target.Files)
            script.Add(file);
        script.Xml = script.Write(header);
        script.OperationsXml = script.Write(null);
        return script;
    }

    private string DiskPath(string path) => PathRules.WithDiskCase(target.Base.Root, target.Base.ToDisk(path));

    private void Add(TargetFile file)
    {
        switch (file.Kind)
        {
            case TargetFileKind.TextDll:
                // FLMM makes its own text DLL from the macros.
                return;
            case TargetFileKind.Ini when file.Generated:
                // The game has a file at this path, but the package copies the generated file as a whole file. It
                // holds no mod text.
                files.Add((DiskPath(file.Path), file.Bytes));
                return;
            case TargetFileKind.Ini when VfsPath.Comparer.Equals(file.Path, ModOverlay.FreelancerIniPath):
                AddFreelancerIni(file);
                return;
            case TargetFileKind.Ini when file.HasBase && !file.IsModFile:
                AddIni(file);
                return;
            case TargetFileKind.Ini when HasPlaceholder(file.Merged!.Sections):
                Refuse(file.Path, "the whole file holds mod text. FLMM reads its text macros only in script operations");
                return;
            default:
                files.Add((DiskPath(file.Path), file.Bytes));
                return;
        }
    }

    /// <summary>The <c>[Data]</c> lines of the mod. FLMM adds its own <c>[Resources]</c> line, and a package never holds a
    /// whole <c>freelancer.ini</c>.</summary>
    private void AddFreelancerIni(TargetFile file)
    {
        var diff = file.Diff!;
        var raw = RawIni.Scan(file.BaseBytes!, out _);
        var data = new List<(string Key, string Line)>();
        foreach (var pair in diff.Changed)
        {
            var section = diff.MergedSections[pair.Merged];
            if (section.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase) && !pair.Removed.Any() &&
                !pair.Changed.Any() && pair.Inserted.All(j => IsOwnDll(section[j])))
                continue;
            if (!section.Name.Equals(DataSection, StringComparison.OrdinalIgnoreCase) || pair.Removed.Any() ||
                pair.Changed.Any())
            {
                Refuse(file.Path, $"it changes [{section.Name}] of freelancer.ini. The package can only add [Data] lines");
                return;
            }
            data.AddRange(pair.Inserted.Select(j => (section[j].Name, IniLines.Line(section[j].Name, section[j]))));
        }
        if (diff.Removed.Count > 0 || diff.Added.Count > 0)
        {
            Refuse(file.Path, "it adds or removes a section of freelancer.ini. The package can only add [Data] lines");
            return;
        }
        if (data.Count == 0)
            return;
        if (raw == null || raw.Sections.Count != diff.BaseSections.Count)
        {
            Refuse(file.Path, "OpenLancer cannot read the lines of freelancer.ini");
            return;
        }

        var text = Join(raw);
        var eol = raw.LineEnd;
        foreach (var group in data.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            // The anchor is the raw last line of the key in [Data].
            var last = raw.Sections
                .Where((s, k) => diff.BaseSections[k].Name.Equals(DataSection, StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s.Entries)
                .LastOrDefault(line => raw.RawKey(line).Equals(group.Key, StringComparison.OrdinalIgnoreCase), -1);
            if (last < 0 || !Unique(text, raw.Lines[last]))
            {
                Refuse(file.Path, last < 0
                    ? $"[Data] has no {group.Key} line, so FLMM has no place for the new line"
                    : $"the line '{raw.Lines[last]}' is not unique in freelancer.ini, so FLMM cannot find its place");
                return;
            }
            var source = eol + string.Join(eol, group.Select(d => d.Line));
            Operation(file.Path, "sectionappend", raw.Lines[last], null, source);
        }
    }

    private bool IsOwnDll(Entry entry) =>
        entry.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase) && entry.Count == 1 &&
        entry[0].ToString().Equals(target.Options.StringsDllName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The operations of a merged INI file, or the whole merged file when an operation cannot express a
    /// change.</summary>
    private void AddIni(TargetFile file)
    {
        var diff = file.Diff!;
        if (UniverseOrder.IsUniverse(file.Path) &&
            diff.Added.Where(m => UniverseOrder.IsBase(diff.MergedSections[m])).ToList() is { Count: > 0 } bases)
        {
            // The nickname names the base. A [Base] without a nickname keeps the name of its section.
            var names = bases.Select(m => SectionIdentity.Of(diff.MergedSections, m)).Select(s => s.Key ?? s.ToString());
            Refuse(file.Path, $"the mod adds the new {(bases.Count == 1 ? "base" : "bases")} {string.Join(", ", names)} to " +
                              "universe.ini. The game links a base to its station only when the [Base] comes before the " +
                              "[system] sections. FLMM has no tested operation that puts a section there yet");
            return;
        }
        var binary = file.BaseDocument!.IsBinary;
        var raw = RawIni.Scan(binary ? file.BaseDocument.ToText() : file.BaseBytes!, out _);
        if (binary)
            problems.Add(Diagnostic.Warning(file.Path, "FLMM converts binary INI files to text before it edits them. The " +
                                                       "text that FLMM writes is not known. Check the package with FLMM " +
                                                       "before you publish it."));
        var reason = raw == null ? "OpenLancer cannot read the lines of the file"
            : raw.Sections.Count != diff.BaseSections.Count ? "OpenLancer cannot match the lines of the file with its sections"
            : null;
        var ops = new List<(string Method, string Section, string? Dest, string Source)>();
        if (reason == null)
            reason = Operations(file.Path, raw!, diff, ops);
        if (reason != null)
        {
            Fallback(file, reason);
            return;
        }
        foreach (var op in ops)
            Operation(file.Path, op.Method, op.Section, op.Dest, op.Source);
    }

    /// <summary>Adds the operations of the changes of a file to the list. Returns the reason why an operation cannot
    /// express a change, or null.</summary>
    /// <param name="path">The install path of the file.</param>
    private string? Operations(string path, RawIni raw, FileDiff diff, List<(string, string, string?, string)> ops)
    {
        if (diff.Removed.Count > 0)
            return "the mod removes a section";
        var text = Join(raw);
        var eol = raw.LineEnd;
        var lastMatched = diff.Matched.Count > 0 ? diff.Matched.Max(m => m.Merged) : -1;

        foreach (var pair in diff.Changed)
        {
            var section = diff.MergedSections[pair.Merged];
            var rawSection = raw.Sections[pair.Base];
            if (pair.Removed.Any())
                return $"the mod removes a line of [{section.Name}]";
            var touched = pair.Changed.Select(i => diff.BaseSections[pair.Base][i].Name)
                .Concat(pair.Inserted.Select(j => section[j].Name)).ToList();
            if (SectionKeys.LinkedGroups(section.Name).Any(g => g.Any(n => touched.Contains(n, StringComparer.OrdinalIgnoreCase))))
                return $"the mod changes lines of [{section.Name}] that belong together";
            if (pair.Inserted.Any() && SectionKeys.IsOrderSensitive(section.Name, path))
                return $"the game reads the lines of [{section.Name}] in order, and FLMM adds lines after the key line";
            if (Anchor(raw, pair.Base, diff.BaseSections[pair.Base], text) is not { } anchor)
                return $"OpenLancer finds no unique anchor for [{section.Name}]";

            var block = raw.Join(rawSection.Header, raw.SectionEnd(pair.Base));
            foreach (var i in pair.Changed)
            {
                var line = rawSection.Entries[i];
                if (!Unique(block, raw.Lines[line]))
                    return $"the line '{raw.Lines[line]}' is not unique in its section";
                var merged = section[pair.MergedOf[i]];
                ops.Add(("sectionreplace", anchor.Text, raw.Lines[line],
                    raw.Indent(line) + IniLines.Line(raw.RawKey(line), merged) + raw.Comment(line)));
            }
            if (pair.Inserted.Any())
                ops.Add(("sectionappend", anchor.Text, null,
                    anchor.Source(pair.Inserted.Select(j => IniLines.Line(section[j].Name, section[j])), eol)));
        }

        foreach (var m in diff.Added)
        {
            var section = diff.MergedSections[m];
            if (m < lastMatched)
                return $"the mod adds [{section.Name}] between other sections";
            if (MarketAnchor(raw, diff, section, text) is { } market)
            {
                ops.Add(("sectionappend", market.Text, null, market.Source(
                    section.Where(e => !e.Name.Equals("base", StringComparison.OrdinalIgnoreCase))
                        .Select(e => IniLines.Line(e.Name, e)), eol)));
                continue;
            }
            ops.Add(("append", "", null, eol + string.Join(eol, IniLines.SectionLines(section)) + eol));
        }
        return null;
    }

    /// <summary>The text that a <c>section</c> element of FLMM finds.</summary>
    /// <param name="Text">The raw text of the anchor.</param>
    /// <param name="EndsWithLine">True when the text ends with the line end of its last line.</param>
    private sealed record AnchorText(string Text, bool EndsWithLine)
    {
        /// <summary>The source of a <c>sectionappend</c> after the anchor: the lines, with the line ends that put them
        /// after the last line of the anchor.</summary>
        public string Source(IEnumerable<string> lines, string eol) =>
            EndsWithLine ? string.Concat(lines.Select(l => l + eol)) : eol + string.Join(eol, lines);
    }

    /// <summary>
    /// The anchor of a section: its raw header line and the raw line of its key, when the key line directly follows the
    /// header. A section without a key has its header line alone. Null when the key line does not follow the header, or
    /// when the anchor does not occur once in the file.
    /// </summary>
    /// <remarks>A key can be the start of another key, for example <c>Li01_08</c> and <c>Li01_08_depot</c>. Then the
    /// anchor also holds the line end after the key line, which makes it unique.</remarks>
    private static AnchorText? Anchor(RawIni raw, int index, Section section, string text)
    {
        var rawSection = raw.Sections[index];
        var match = SectionKeys.For(section.Name);
        if (match.Kind != SectionMatchKind.Keyed || !SectionKeys.HasKeyLine(section, match))
            return UniqueAnchor(raw, rawSection.Header, rawSection.Header, text);
        var key = section.ToList().FindIndex(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase));
        return key < 0 || rawSection.Entries[key] != rawSection.Header + 1
            ? null
            : UniqueAnchor(raw, rawSection.Header, rawSection.Header + 1, text);
    }

    /// <summary>The raw lines as an anchor when they occur once in the file. Else the raw lines with the line end of the
    /// last line, when that text occurs once. Else null.</summary>
    private static AnchorText? UniqueAnchor(RawIni raw, int from, int to, string text)
    {
        var anchor = raw.Join(from, to);
        if (Unique(text, anchor))
            return new AnchorText(anchor, false);
        return raw.Ends[to].Length > 0 && Unique(text, anchor + raw.Ends[to]) ? new AnchorText(anchor + raw.Ends[to], true) : null;
    }

    /// <summary>The anchor of the <c>[BaseGood]</c> section of the base of a new <c>[BaseGood]</c>, or null. The engine
    /// adds the goods of each <c>[BaseGood]</c> of a base, so the new lines can go into the section of the game
    /// file.</summary>
    private static AnchorText? MarketAnchor(RawIni raw, FileDiff diff, Section section, string text)
    {
        if (!section.Name.Equals("BaseGood", StringComparison.OrdinalIgnoreCase) ||
            section.FirstOrDefault(e => e.Name.Equals("base", StringComparison.OrdinalIgnoreCase)) is not { Count: 1 } line)
            return null;
        for (var k = 0; k < diff.BaseSections.Count; k++)
        {
            var old = diff.BaseSections[k];
            if (!old.Name.Equals("BaseGood", StringComparison.OrdinalIgnoreCase) || old.Count == 0 ||
                !old[0].Name.Equals("base", StringComparison.OrdinalIgnoreCase) || old[0].Count != 1 ||
                !old[0][0].ToString().Equals(line[0].ToString(), StringComparison.OrdinalIgnoreCase))
                continue;
            var rawSection = raw.Sections[k];
            if (rawSection.Entries.Count == 0 || rawSection.Entries[0] != rawSection.Header + 1)
                return null;
            return UniqueAnchor(raw, rawSection.Header, rawSection.Header + 1, text);
        }
        return null;
    }

    /// <summary>Puts the whole merged file into the package, or refuses the change when the file holds mod
    /// text.</summary>
    private void Fallback(TargetFile file, string reason)
    {
        if (HasPlaceholder(file.Merged!.Sections))
        {
            Refuse(file.Path, $"{reason}, and the whole file would hold mod text");
            return;
        }
        problems.Add(Diagnostic.Warning(file.Path,
            $"The FLMM package holds the whole merged file {file.Path}, because {reason}. The file undoes the changes " +
            "of other mods in it."));
        files.Add((DiskPath(file.Path), file.Bytes));
    }

    private void Refuse(string path, string reason) =>
        problems.Add(Diagnostic.Error(path, $"The FLMM package cannot hold this change in {path}: {reason}. Use the " +
                                            "drop-in zip, or change the mod."));

    /// <summary>Adds one operation. The texts get the text macros of the mod text, and the operation is refused when a
    /// text is not ASCII.</summary>
    private void Operation(string path, string method, string section, string? dest, string source)
    {
        string resolved;
        try
        {
            resolved = Macros(source);
        }
        catch (FlmmTextException e)
        {
            Refuse(path, e.Message);
            return;
        }
        foreach (var text in new[] { section, dest ?? "", resolved })
        {
            if (text.FirstOrDefault(c => c > 127) is var c and not '\0')
            {
                problems.Add(Diagnostic.Error(path, $"The FLMM package cannot hold the character U+{(int)c:X4} in " +
                                                    $"{path}. Use the drop-in zip."));
                return;
            }
        }
        var op = new StringBuilder();
        op.Append($"  <data file=\"{EscapeAttribute(DiskPath(path))}\" method=\"{method}\">\r\n");
        if (section.Length > 0)
            op.Append($"    <section>{Escape(section)}</section>\r\n");
        if (dest != null)
            op.Append($"    <dest>{Escape(dest)}</dest>\r\n");
        op.Append($"    <source>{Escape(resolved)}</source>\r\n");
        op.Append("  </data>\r\n");
        operations.Add(op.ToString());
    }

    /// <summary>The text with a text macro of FLMM in place of each text placeholder.</summary>
    /// <exception cref="FlmmTextException">A text has a quote, and the script is not a probe.</exception>
    private string Macros(string text) => Placeholder().Replace(text, match =>
    {
        StringPlaceholder.TryParse(match.Value, out var placeholder);
        var table = placeholder.Kind == StringKind.String ? strings.Strings : strings.Infocards;
        if (!table.TryGetValue(placeholder.Key, out var content))
            throw new FlmmTextException($"'{placeholder}' is not in strings.json");
        if (content.Contains('"') && !probe)
            throw new FlmmTextException($"the text of '{placeholder}' has a quote, and FLMM has no known escape for it");
        var macro = placeholder.Kind == StringKind.String ? "GENERATESTRRES" : "GENERATEXMLRES";
        return $"{macro}(\"{content}\")";
    });

    /// <summary>A text placeholder in a line. A key has letters, digits, '_', '-' and '.' only (see
    /// <c>strings.json</c>).</summary>
    [GeneratedRegex(@"\$(?:str|ifc):[A-Za-z0-9_.\-]+")]
    private static partial Regex Placeholder();

    /// <summary>The script, with the header element when <paramref name="header"/> is not null (section 9.2 of the
    /// M11c design).</summary>
    private string Write(FlmmHeader? header)
    {
        var xml = new StringBuilder();
        xml.Append("<script>\r\n");
        if (header != null)
            xml.Append(HeaderXml(header));
        foreach (var op in operations)
            xml.Append(op);
        xml.Append("</script>\r\n");
        return xml.ToString();
    }

    /// <summary>The header element of a script, as the script holds it: ASCII, indented, with CR LF line ends. The
    /// Preview tab of the Mods window shows it.</summary>
    public static string HeaderXml(FlmmHeader header)
    {
        var xml = new StringBuilder();
        xml.Append($"  <header name=\"{EscapeAttribute(Ascii(header.Name))}\">\r\n");
        xml.Append($"    <scriptversion>{ScriptVersion}</scriptversion>\r\n");
        xml.Append($"    <author>{Escape(Ascii(header.Author))}</author>\r\n");
        xml.Append($"    <description>{Escape(Ascii(header.Description)).Replace("\n", "\r\n")}</description>\r\n");
        if (header.ModUrl.Length > 0)
            xml.Append($"    <modurl>{Escape(Ascii(header.ModUrl))}</modurl>\r\n");
        xml.Append("  </header>\r\n");
        return xml.ToString();
    }

    /// <summary>The name of the script file in the package.</summary>
    public static string FileName => ScriptName;

    /// <summary>The ASCII form of a header text (decision D580): the common typographic characters get their ASCII
    /// form, and each other character outside ASCII becomes <c>?</c>.</summary>
    private static string Ascii(string text) => PageText.ToAscii(text);

    /// <summary>Escapes the characters that XML text cannot hold: <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c>.</summary>
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Escapes the characters that an XML attribute in quotes cannot hold.</summary>
    private static string EscapeAttribute(string text) => Escape(text).Replace("\"", "&quot;");

    /// <summary>True when the text occurs once in the file, with exact letter case and also with any letter
    /// case.</summary>
    private static bool Unique(string text, string part) =>
        part.Length > 0 && Count(text, part, StringComparison.Ordinal) == 1 &&
        Count(text, part, StringComparison.OrdinalIgnoreCase) == 1;

    private static int Count(string text, string part, StringComparison comparison)
    {
        var count = 0;
        for (var i = text.IndexOf(part, comparison); i >= 0; i = text.IndexOf(part, i + 1, comparison))
            count++;
        return count;
    }

    /// <summary>The whole text of the raw lines, with their line ends.</summary>
    private static string Join(RawIni raw) => string.Concat(raw.Lines.Zip(raw.Ends, (l, e) => l + e));

    private static bool HasPlaceholder(IEnumerable<Section> sections) =>
        sections.SelectMany(s => s).SelectMany(e => e).Any(v => StringPlaceholder.TryParse(v, out _));

    private sealed class FlmmTextException(string message) : Exception(message);
}

/// <summary>The fields of the header of an FLMM script (section 9.2 of the M11c design). The script writes them as
/// ASCII.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Author">The authors, or empty.</param>
/// <param name="Description">The text of the description element, with LF line ends.</param>
/// <param name="ModUrl">The page link, or empty for no <c>modurl</c> element.</param>
internal sealed record FlmmHeader(string Name, string Author, string Description, string ModUrl)
{
    /// <summary>The header of a package: "Version &lt;version&gt; for &lt;target&gt;.", the summary, and the
    /// description as plain text.</summary>
    public static FlmmHeader Of(PublishTexts texts, ExportTarget target)
    {
        var description = $"Version {texts.Manifest.Version} for {target.Name}.";
        if (texts.Settings.Summary.Length > 0)
            description += "\n" + texts.Settings.Summary;
        if (texts.DescriptionText.Count > 0)
            description += "\n\n" + string.Join("\n", texts.DescriptionText);
        return new FlmmHeader(texts.Manifest.Name, texts.Authors, description, texts.PageUrl);
    }
}
