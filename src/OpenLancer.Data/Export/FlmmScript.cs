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
/// <para>No official schema of the script exists. The rules come from the FLMM tutorial, community posts, working
/// scripts and a test of FLMM 1.31 with a hand-made package. The owner check of an exported package must confirm them.
/// Until then, the package is a preview.</para>
/// <para>FLMM 1.31 reads one tag or one tag end on each line. So each tag and each tag end of the script is on its own
/// line, and the content of an element is on the lines between them. The script adds no indent to a line. Each content
/// line of an operation is an INI line as the game file has it. Each attribute value is in quotes.</para>
/// <list type="bullet">
/// <item><c>append</c> without <c>section</c> adds a new section at the end of a file. Its source starts with an empty
/// line, as the game files have between sections.</item>
/// <item><c>sectionappend</c> finds the lines of <c>section</c> and adds the lines of <c>source</c> after them. The
/// anchor is the header line and the key line of a section.</item>
/// <item><c>sectionreplace</c> finds the line of <c>dest</c> below the header of the section that <c>section</c>
/// finds. It replaces that line with the line of <c>source</c>. FLMM changes only the lines below the header.</item>
/// <item><c>key = 0 ;GENERATESTRRES("text")</c> and <c>key = 0 ;GENERATEXMLRES("text")</c> give the id of a new
/// string or infocard in a text DLL of FLMM. The macro comes after <c>;</c>, so it must be the last value of its
/// line.</item>
/// </list>
/// <para>FLMM takes the first match of an anchor. The test did not show if FLMM matches whole lines or text. So an
/// anchor must occur once as a run of whole lines, and its first match as text must be the anchor itself. The same
/// rule holds for the line of <c>dest</c> below the header of its section.</para>
/// <para>A change that the operations cannot express puts the whole merged file into the package. A whole file
/// cannot hold mod text, so such a change with mod text is a refusal.</para>
/// <para>A new <c>[Base]</c> of <c>universe.ini</c> is a refusal too. It must go before the <c>[system]</c> sections
/// (see <see cref="UniverseOrder"/>), and an <c>append</c> puts it at the end of the file. This version of the package
/// cannot put a section before the first <c>[system]</c> yet. Each refusal names the drop-in zip, which always carries
/// the new content.</para>
/// </remarks>
internal sealed partial class FlmmScript
{
    /// <summary>The oldest FLMM version that the script needs.</summary>
    public const string ScriptVersion = "1.31";

    private const string ScriptName = "script.xml";
    private const string DataSection = "Data";
    private const string LineEnd = "\r\n";

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

    /// <summary>The text of <c>script.xml</c>: ASCII, one tag on each line, with CR LF line ends, without an XML
    /// declaration.</summary>
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

        var text = new RawText(raw);
        foreach (var group in data.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
        {
            // The anchor is the raw last line of the key in [Data].
            var last = raw.Sections
                .Where((s, k) => diff.BaseSections[k].Name.Equals(DataSection, StringComparison.OrdinalIgnoreCase))
                .SelectMany(s => s.Entries)
                .LastOrDefault(line => raw.RawKey(line).Equals(group.Key, StringComparison.OrdinalIgnoreCase), -1);
            if (last < 0 || !text.Finds(0, raw.Lines.Count - 1, last, last))
            {
                Refuse(file.Path, last < 0
                    ? $"[Data] has no {group.Key} line, so FLMM has no place for the new line"
                    : $"FLMM can find '{raw.Lines[last]}' at more than one place in freelancer.ini");
                return;
            }
            Operation(file.Path, new Op("sectionappend", [raw.Lines[last]], null, [.. group.Select(d => d.Line)]));
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
                              "[system] sections. This version of the FLMM package cannot put a section before the " +
                              "systems yet");
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
        var ops = new List<Op>();
        if (reason == null)
            reason = Operations(file.Path, raw!, diff, ops);
        if (reason != null)
        {
            Fallback(file, reason);
            return;
        }
        foreach (var op in ops)
            Operation(file.Path, op);
    }

    /// <summary>Adds the operations of the changes of a file to the list. Returns the reason why an operation cannot
    /// express a change, or null.</summary>
    /// <param name="path">The install path of the file.</param>
    private static string? Operations(string path, RawIni raw, FileDiff diff, List<Op> ops)
    {
        if (diff.Removed.Count > 0)
            return "the mod removes a section";
        var text = new RawText(raw);
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
            if (AnchorLines(raw, pair.Base, diff.BaseSections[pair.Base]) is not { } lines)
                return $"the key line of [{section.Name}] does not directly follow its header";
            if (text.AnchorOf(lines.From, lines.To) is not { } anchor)
                return $"FLMM can find the anchor of [{section.Name}] at more than one place";

            // FLMM finds the line of dest only below the header of the section.
            var end = raw.SectionEnd(pair.Base);
            foreach (var i in pair.Changed)
            {
                var line = rawSection.Entries[i];
                if (!text.Finds(rawSection.Header + 1, end, line, line))
                    return $"FLMM can find '{raw.Lines[line]}' at more than one place in its section";
                var merged = section[pair.MergedOf[i]];
                ops.Add(new Op("sectionreplace", anchor, raw.Lines[line],
                    [raw.Indent(line) + IniLines.Line(raw.RawKey(line), merged) + raw.Comment(line)]));
            }
            if (pair.Inserted.Any())
                ops.Add(new Op("sectionappend", anchor, null, [.. pair.Inserted.Select(j => IniLines.Line(section[j].Name, section[j]))]));
        }

        foreach (var m in diff.Added)
        {
            var section = diff.MergedSections[m];
            if (m < lastMatched)
                return $"the mod adds [{section.Name}] between other sections";
            if (MarketAnchor(text, diff, section) is { } market)
            {
                ops.Add(new Op("sectionappend", market, null,
                [
                    .. section.Where(e => !e.Name.Equals("base", StringComparison.OrdinalIgnoreCase))
                        .Select(e => IniLines.Line(e.Name, e)),
                ]));
                continue;
            }
            ops.Add(new Op("append", [], null, ["", .. IniLines.SectionLines(section)]));
        }
        return null;
    }

    /// <summary>One operation of the script, with the text placeholders of the mod still in its source.</summary>
    /// <param name="Method">The method of the <c>data</c> element.</param>
    /// <param name="Section">The lines of the <c>section</c> element, or no lines for no element.</param>
    /// <param name="Dest">The line of the <c>dest</c> element, or null for no element.</param>
    /// <param name="Source">The lines of the <c>source</c> element.</param>
    private sealed record Op(string Method, IReadOnlyList<string> Section, string? Dest, IReadOnlyList<string> Source);

    /// <summary>
    /// The raw lines of the anchor of a section: its header line and the line of its key, when the key line directly
    /// follows the header. A section without a key has its header line alone. Null when the key line does not directly
    /// follow the header.
    /// </summary>
    /// <remarks>FLMM must also find the anchor at one place only (<see cref="RawText.AnchorOf"/>). A key can be the start
    /// of another key, for example <c>Li01_08</c> and <c>Li01_08_depot</c>. The anchor of the shorter key is then valid
    /// only when its section comes first in the file.</remarks>
    private static (int From, int To)? AnchorLines(RawIni raw, int index, Section section)
    {
        var rawSection = raw.Sections[index];
        var match = SectionKeys.For(section.Name);
        if (match.Kind != SectionMatchKind.Keyed || !SectionKeys.HasKeyLine(section, match))
            return (rawSection.Header, rawSection.Header);
        var key = section.ToList().FindIndex(e => e.Name.Equals(match.KeyEntry, StringComparison.OrdinalIgnoreCase));
        return key < 0 || rawSection.Entries[key] != rawSection.Header + 1 ? null : (rawSection.Header, rawSection.Header + 1);
    }

    /// <summary>The anchor of the <c>[BaseGood]</c> section of the base of a new <c>[BaseGood]</c>, or null. The engine
    /// adds the goods of each <c>[BaseGood]</c> of a base, so the new lines can go into the section of the game
    /// file.</summary>
    private static IReadOnlyList<string>? MarketAnchor(RawText text, FileDiff diff, Section section)
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
            var rawSection = text.Raw.Sections[k];
            if (rawSection.Entries.Count == 0 || rawSection.Entries[0] != rawSection.Header + 1)
                return null;
            return text.AnchorOf(rawSection.Header, rawSection.Header + 1);
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

    /// <summary>Adds one operation. The source lines get the text macros of the mod text, and the operation is refused
    /// when a macro cannot hold a text or when a line is not ASCII.</summary>
    private void Operation(string path, Op op)
    {
        List<string> source;
        try
        {
            source = [.. op.Source.Select(Macro)];
        }
        catch (FlmmTextException e)
        {
            Refuse(path, e.Message);
            return;
        }
        foreach (var line in op.Section.Concat(op.Dest == null ? Array.Empty<string>() : [op.Dest]).Concat(source))
        {
            if (line.FirstOrDefault(c => c > 127) is var c and not '\0')
            {
                problems.Add(Diagnostic.Error(path, $"The FLMM package cannot hold the character U+{(int)c:X4} in " +
                                                    $"{path}. Use the drop-in zip."));
                return;
            }
        }
        var xml = new StringBuilder();
        Tag(xml, $"<data file=\"{EscapeAttribute(DiskPath(path))}\" method=\"{op.Method}\">");
        if (op.Section.Count > 0)
            Element(xml, "section", op.Section);
        if (op.Dest != null)
            Element(xml, "dest", [op.Dest]);
        Element(xml, "source", source);
        Tag(xml, "</data>");
        operations.Add(xml.ToString());
    }

    /// <summary>The line with a text macro of FLMM in place of its text placeholder: <c>key = $str:x</c> becomes
    /// <c>key = 0 ;GENERATESTRRES("text")</c>. A line without a placeholder stays as it is.</summary>
    /// <remarks>The macro comes after <c>;</c>, so the game reads the rest of the line as a comment. So the placeholder
    /// must be the last value of the line. A comment after the placeholder goes away.</remarks>
    /// <exception cref="FlmmTextException">The line has more than one placeholder, the placeholder is not its last
    /// value, or the macro cannot hold the text.</exception>
    private string Macro(string line)
    {
        var matches = Placeholder().Matches(line);
        if (matches.Count == 0)
            return line;
        if (matches.Count > 1)
            throw new FlmmTextException($"the line '{line}' has more than one text. FLMM writes a text after a " +
                                        "semicolon, so a line can hold only one text, as its last value");
        var match = matches[0];
        var rest = line[(match.Index + match.Length)..].TrimStart(' ', '\t');
        if (rest.Length > 0 && rest[0] != ';')
            throw new FlmmTextException($"the text '{match.Value}' is not the last value of the line '{line}'. FLMM " +
                                        "writes a text after a semicolon, and the game reads the rest of the line as a " +
                                        "comment");
        StringPlaceholder.TryParse(match.Value, out var placeholder);
        var table = placeholder.Kind == StringKind.String ? strings.Strings : strings.Infocards;
        if (!table.TryGetValue(placeholder.Key, out var content))
            throw new FlmmTextException($"'{placeholder}' is not in strings.json");
        if (content.Contains('"') && !probe)
            throw new FlmmTextException($"the text of '{placeholder}' has a quote, and FLMM has no known escape for it");
        if (content.Contains('\n') || content.Contains('\r'))
            throw new FlmmTextException($"the text of '{placeholder}' has a line end. FLMM reads a text macro only on " +
                                        "one line");
        var macro = placeholder.Kind == StringKind.String ? "GENERATESTRRES" : "GENERATEXMLRES";
        return $"{line[..match.Index]}0 ;{macro}(\"{content}\")";
    }

    /// <summary>A text placeholder in a line. A key has letters, digits, '_', '-' and '.' only (see
    /// <c>strings.json</c>).</summary>
    [GeneratedRegex(@"\$(?:str|ifc):[A-Za-z0-9_.\-]+")]
    private static partial Regex Placeholder();

    /// <summary>The script, with the header element when <paramref name="header"/> is not null (section 9.2 of the
    /// M11c design).</summary>
    private string Write(FlmmHeader? header)
    {
        var xml = new StringBuilder();
        Tag(xml, "<script>");
        if (header != null)
            xml.Append(HeaderXml(header));
        foreach (var op in operations)
            xml.Append(op);
        Tag(xml, "</script>");
        return xml.ToString();
    }

    /// <summary>The header element of a script, as the script holds it: ASCII, one tag on each line, with CR LF line
    /// ends. The Preview tab of the Mods window shows it.</summary>
    public static string HeaderXml(FlmmHeader header)
    {
        var xml = new StringBuilder();
        Tag(xml, $"<header name=\"{EscapeAttribute(Ascii(header.Name))}\">");
        Element(xml, "scriptversion", [ScriptVersion]);
        Element(xml, "author", Lines(Ascii(header.Author)));
        Element(xml, "description", Lines(Ascii(header.Description)));
        if (header.ModUrl.Length > 0)
            Element(xml, "modurl", [Ascii(header.ModUrl)]);
        Tag(xml, "</header>");
        return xml.ToString();
    }

    /// <summary>The lines of a header text: no lines for an empty text.</summary>
    private static IEnumerable<string> Lines(string text) => text.Length == 0 ? [] : text.Split('\n');

    /// <summary>Writes a tag or a tag end on its own line.</summary>
    private static void Tag(StringBuilder xml, string tag) => xml.Append(tag).Append(LineEnd);

    /// <summary>Writes an element: its tag, each content line with the XML escapes, and its tag end, each on its own
    /// line.</summary>
    private static void Element(StringBuilder xml, string name, IEnumerable<string> lines)
    {
        Tag(xml, $"<{name}>");
        foreach (var line in lines)
            xml.Append(Escape(line)).Append(LineEnd);
        Tag(xml, $"</{name}>");
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

    private static bool HasPlaceholder(IEnumerable<Section> sections) =>
        sections.SelectMany(s => s).SelectMany(e => e).Any(v => StringPlaceholder.TryParse(v, out _));

    /// <summary>The raw text of a file, with the start of each line, for the anchor rule of FLMM.</summary>
    private sealed class RawText
    {
        private static readonly StringComparison[] Comparisons = [StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase];
        private readonly string text;
        private readonly int[] starts;

        public RawText(RawIni raw)
        {
            Raw = raw;
            starts = new int[raw.Lines.Count];
            var all = new StringBuilder();
            for (var i = 0; i < raw.Lines.Count; i++)
            {
                starts[i] = all.Length;
                all.Append(raw.Lines[i]).Append(raw.Ends[i]);
            }
            text = all.ToString();
        }

        public RawIni Raw { get; }

        /// <summary>The raw lines <paramref name="from"/> to <paramref name="to"/> when FLMM finds them in the whole
        /// file, else null.</summary>
        public IReadOnlyList<string>? AnchorOf(int from, int to) =>
            Finds(0, Raw.Lines.Count - 1, from, to) ? Raw.Lines.GetRange(from, to - from + 1) : null;

        /// <summary>
        /// True when FLMM finds the raw lines <paramref name="from"/> to <paramref name="to"/> in the lines
        /// <paramref name="first"/> to <paramref name="last"/>, if it matches whole lines and also if it matches text.
        /// The lines must occur once there as a run of whole lines. The first text match of their text must be the
        /// lines themselves, because FLMM takes the first match. Both rules hold with exact letter case and with any
        /// letter case.
        /// </summary>
        public bool Finds(int first, int last, int from, int to)
        {
            var count = to - from + 1;
            var anchor = Raw.Join(from, to);
            if (anchor.Length == 0 || first > from || to > last)
                return false;
            var region = text[starts[first]..(starts[last] + Raw.Lines[last].Length)];
            foreach (var comparison in Comparisons)
            {
                var runs = 0;
                for (var p = first; p + count - 1 <= last; p++)
                {
                    if (Enumerable.Range(0, count).All(i => Raw.Lines[p + i].Equals(Raw.Lines[from + i], comparison)))
                        runs++;
                }
                if (runs != 1 || region.IndexOf(anchor, comparison) != starts[from] - starts[first])
                    return false;
            }
            return true;
        }
    }

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
