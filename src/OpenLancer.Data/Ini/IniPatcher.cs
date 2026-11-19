using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Ini;

/// <summary>
/// Applies the sections of a mod patch file to a parsed INI file.
/// </summary>
/// <remarks>
/// Each patch section names its target with its key entry (see <see cref="SectionKeys"/>). Then:
/// <list type="bullet">
/// <item>The patch's entries replace all target entries with the same names. They are written as one block, in
/// patch order, at the position of the first replaced entry, or at the end when no entry is replaced. So lines
/// that belong together, such as <c>encounter</c> and its <c>faction</c> lines, stay in order.</item>
/// <item><c>ol.remove = name</c> removes all target entries with that name.</item>
/// <item><c>ol.delete = true</c> removes the target section, and its child sections.</item>
/// <item><c>ol.new = true</c> adds the section at the end of the file. No section with that key may exist. A new
/// <c>[Base]</c> of <c>universe.ini</c> goes directly before the first <c>[system]</c> (see
/// <see cref="UniverseOrder"/>).</item>
/// <item>A new child section (see <see cref="SectionKeys.IsChild"/>) needs <c>ol.parent = &lt;parent key&gt;</c>.
/// Child sections that directly follow a new parent section in the patch belong to that parent.</item>
/// <item><c>ol.rename = &lt;new key&gt;</c> gives the target a new key. The section keeps its place, and the other
/// lines of the patch section change it.</item>
/// <item><c>ol.append = true</c> is only for a <c>[Data]</c> section of a patch of <c>EXE\freelancer.ini</c> (see
/// <see cref="ApplyDataAppend"/>).</item>
/// <item><c>ol.replace = true</c> replaces all lines of a section of a mission type whose line order counts, in a
/// mission file (section 6.2 of the M6b design). <c>ol.base = &lt;hash&gt;</c> holds the hash of the section that the
/// patch replaces or deletes (see <see cref="SectionHash"/>). A section whose hash differs gives a warning.</item>
/// <item><c>ol.before = &lt;type&gt;, &lt;key&gt;</c> puts a new section of a mission file directly before the
/// section with that type and key (section 6.4 of the M6b design).</item>
/// </list>
/// Directives use the <c>ol.</c> prefix because the upstream text parser drops lines that start with <c>@</c>.
/// String values <c>$str:key</c> and <c>$ifc:key</c> become the resource ids of the mod's text.
/// <para>The merge never changes a section or an entry of the target in place. It makes new sections and puts them
/// into the section list. So a copy of the section list keeps the original sections unchanged.</para>
/// </remarks>
internal static class IniPatcher
{
    /// <summary>Entry names with this prefix are directives. Content entries cannot use it.</summary>
    internal const string DirectivePrefix = "ol.";
    internal const string DirectiveNew = "ol.new";
    internal const string DirectiveDelete = "ol.delete";
    internal const string DirectiveRemove = "ol.remove";
    internal const string DirectiveParent = "ol.parent";
    internal const string DirectiveRename = "ol.rename";
    internal const string DirectiveAppend = "ol.append";
    internal const string DirectiveReplace = "ol.replace";
    internal const string DirectiveBase = "ol.base";
    internal const string DirectiveBefore = "ol.before";

    /// <summary>The section of <c>freelancer.ini</c> that lists the data files.</summary>
    private const string DataSection = "Data";

    public static void Apply(IniDocument target, IReadOnlyList<Section> patch, PatchContext context)
    {
        Section? newParent = null;
        foreach (var section in patch)
        {
            var parsed = Parse(section, context);
            if (parsed == null)
            {
                newParent = null;
                continue;
            }
            if (newParent != null && JoinsNewParent(section, parsed))
            {
                InsertChild(target, target.Sections.IndexOf(newParent), section, parsed.Content, context);
                continue;
            }
            newParent = ApplySection(target, section, parsed, context);
        }
    }

    /// <summary>
    /// What the merge does with each section of a patch: it adds a section, deletes its target, or changes its
    /// target. For a patch that has an error, the result can differ from the merge.
    /// </summary>
    internal static PatchAction[] Actions(IReadOnlyList<Section> patch)
    {
        var result = new PatchAction[patch.Count];
        var afterNewParent = false;
        for (var i = 0; i < patch.Count; i++)
        {
            var parsed = Parse(patch[i], null);
            if (parsed == null)
            {
                afterNewParent = false;
                continue;
            }
            if (afterNewParent && JoinsNewParent(patch[i], parsed))
            {
                result[i] = PatchAction.Add;
                continue;
            }
            var append = SectionKeys.For(patch[i].Name).Kind == SectionMatchKind.Append;
            result[i] = parsed.IsNew || append ? PatchAction.Add : parsed.IsDelete ? PatchAction.Delete : PatchAction.Change;
            // The same rule as ApplySection: a new section that is not a child and not appended becomes a parent.
            afterNewParent = parsed.IsNew && !append && !SectionKeys.IsChild(patch[i].Name) && parsed.ParentKey == null;
        }
        return result;
    }

    /// <summary>True when a child section after a new parent section belongs to that parent.</summary>
    private static bool JoinsNewParent(Section section, PatchDirectives p) =>
        SectionKeys.IsChild(section.Name) && !p.IsDelete && p.RemoveNames.Count == 0 && p.ParentKey == null;

    /// <summary>Resolves the text placeholders in every value of a whole mod file.</summary>
    public static void ResolvePlaceholders(IniDocument document, PatchContext context)
    {
        for (var i = 0; i < document.Sections.Count; i++)
        {
            var s = document.Sections[i];
            document.Sections[i] = Build(s.Name, s.File, s.Line, s.ToList(), context, document.IsBinary);
        }
    }

    /// <summary>True when any value of the document is a text placeholder.</summary>
    public static bool HasPlaceholders(IniDocument document) =>
        document.Sections.SelectMany(s => s).SelectMany(e => e)
            .Any(v => StringPlaceholder.TryParse(v.ToString(), out _));

    /// <summary>The new key of an <c>ol.rename</c> line of a patch section, or null when the section has no such line
    /// with one value.</summary>
    internal static string? RenameOf(Section patch) =>
        patch.Where(e => Is(e, DirectiveRename)).ToList() is [{ Count: 1 } line] ? line[0].ToString() : null;

    /// <summary>
    /// True when each section of a patch is a <c>[Data]</c> section with <c>ol.append = true</c> and no other directive.
    /// Only such a patch can change <c>EXE\freelancer.ini</c>.
    /// </summary>
    public static bool IsDataAppend(IReadOnlyList<Section> patch) =>
        patch.Count > 0 && patch.All(s =>
            s.Name.Equals(DataSection, StringComparison.OrdinalIgnoreCase) &&
            s.Any(e => Is(e, DirectiveAppend) && IsTrue(e)) &&
            s.All(e => Is(e, DirectiveAppend) || !e.Name.StartsWith(DirectivePrefix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Adds the lines of the <c>[Data]</c> sections of a patch of <c>EXE\freelancer.ini</c> (see
    /// <see cref="IsDataAppend"/>). The game reads the <c>[Data]</c> lines in order, and the order is the load order.
    /// So each line goes after the last line of the same key. When the file has no line of the key, the line goes at the
    /// end of the last <c>[Data]</c> section.
    /// </summary>
    /// <param name="dataFileExists">True when the merged view has a file, by its path relative to the data
    /// folder.</param>
    public static void ApplyDataAppend(IniDocument target, IReadOnlyList<Section> patch, PatchContext context,
        Func<string, bool> dataFileExists)
    {
        foreach (var line in patch.SelectMany(s => s).Where(e => !Is(e, DirectiveAppend)))
        {
            var label = $"[{DataSection}] {line.Name}";
            if (line.Count != 1 || line[0].ToString().Trim() is not { Length: > 0 } value)
            {
                context.Error($"{label}: a line of [{DataSection}] needs one value, the path of a data file.");
                continue;
            }
            var sections = target.Sections.Where(s => s.Name.Equals(DataSection, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sections.Count == 0)
            {
                context.Error($"{label} = {value}: {target.Path} has no [{DataSection}] section.");
                continue;
            }
            if (sections.SelectMany(s => s).Any(e => Is(e, line.Name) && e.Count > 0 && SamePath(e[0].ToString(), value)))
            {
                context.Error($"{label} = {value}: [{DataSection}] already has this line. Remove it from the patch.");
                continue;
            }
            if (!dataFileExists(value))
            {
                context.Error($"{label} = {value}: the data folder of the game and the mods has no such file. Add the " +
                              "file to the files folder of a mod.");
                continue;
            }

            // After the last line of the key, else at the end of the last [Data] section.
            var section = sections.LastOrDefault(s => s.Any(e => Is(e, line.Name))) ?? sections[^1];
            var after = section.ToList().FindLastIndex(e => Is(e, line.Name));
            if (after < 0)
                after = section.Count - 1;
            var copy = new Section(section.Name, section.Count + 1) { File = section.File, Line = section.Line };
            if (after < 0)
                copy.Add(Copy(copy, line, null, false, null));
            for (var i = 0; i < section.Count; i++)
            {
                copy.Add(Copy(copy, section[i], null, false, null));
                if (i == after)
                    copy.Add(Copy(copy, line, null, false, null));
            }
            target.Sections[target.Sections.IndexOf(section)] = copy;
        }
    }

    /// <summary>True when two data paths name the same file: the same text without letter case, with either
    /// separator.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().Replace('/', '\\'), b.Trim().Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies one section. Returns the section that it added when it is a new parent section.</summary>
    private static Section? ApplySection(IniDocument target, Section patch, PatchDirectives p, PatchContext context)
    {
        var match = SectionKeys.For(patch.Name, context.TargetPath);
        var isChild = SectionKeys.IsChild(patch.Name);
        if (p.IsAppend)
        {
            context.Error($"[{patch.Name}]: {DirectiveAppend} is only for a [{DataSection}] section of a patch of " +
                          @"EXE\freelancer.ini.");
            return null;
        }
        if (MissionDirectiveProblem(patch, p, context) is { } problem)
        {
            context.Error($"[{patch.Name}]: {problem}");
            return null;
        }
        if (p.RenameLines > 0)
        {
            Rename(target, patch, p, context);
            return null;
        }
        if (p.IsReplace)
        {
            Replace(target, patch, p, context);
            return null;
        }
        if (p.ParentKey != null && !(p.IsNew && isChild))
        {
            context.Error($"[{patch.Name}]: {DirectiveParent} is only for a new child section.");
            return null;
        }
        if (p.IsNew && isChild)
        {
            AddChild(target, patch, p, context);
            return null;
        }

        if (match.Kind == SectionMatchKind.Append)
        {
            if (p.IsDelete || p.RemoveNames.Count > 0)
                context.Error($"[{patch.Name}] sections can only be added. {DirectiveDelete} and {DirectiveRemove} are not allowed.");
            else
            {
                target.Sections.Add(Build(patch.Name, patch.File, patch.Line, p.Content, context, target.IsBinary));
                context.RecordAdd(SectionIdentity.Of(target.Sections, target.Sections.Count - 1));
            }
            return null;
        }

        string? keyValue = null;
        // A patch section without a line of an optional key finds the section without that line.
        var keyless = false;
        if (match.Kind == SectionMatchKind.Keyed)
        {
            var keyEntries = p.Content.Where(e => Is(e, match.KeyEntry!)).ToList();
            if (keyEntries.Count == 0 && match.KeyOptional)
                keyless = true;
            else if (keyEntries.Count != 1 || keyEntries[0].Count != 1)
            {
                context.Error($"[{patch.Name}] needs exactly one '{match.KeyEntry}' entry with one value to find its target" +
                              (match.KeyOptional ? $", or no '{match.KeyEntry}' entry for the section without one." : "."));
                return null;
            }
            else
                keyValue = keyEntries[0][0].ToString();
        }

        var identity = new SectionIdentity(patch.Name, keyValue);
        var label = identity.ToString();
        var candidates = target.Sections
            .Select((section, index) => (section, index))
            .Where(c => c.section.Name.Equals(patch.Name, StringComparison.OrdinalIgnoreCase) &&
                        (keyless ? !SectionKeys.HasKeyLine(c.section, match)
                            : keyValue == null || KeyEquals(c.section, match.KeyEntry!, keyValue)))
            .ToList();

        if (p.IsNew)
        {
            if (candidates.Count > 0)
            {
                context.Error($"{label}: {DirectiveNew} is set, but the section already exists.");
                return null;
            }
            var place = UniverseOrder.PlaceOf(context.TargetPath, target.Sections, patch.Name);
            if (p.Before is var (beforeType, beforeKey))
            {
                var anchors = FindKeyed(target, beforeType, beforeKey, context.TargetPath);
                if (anchors.Count != 1)
                {
                    context.Error($"{label}: {DirectiveBefore} = {beforeType}, {beforeKey}: " + (anchors.Count == 0
                        ? $"{target.Path} has no such section."
                        : $"{target.Path} has {anchors.Count} such sections."));
                    return null;
                }
                place = anchors[0];
            }
            var added = Build(patch.Name, patch.File, patch.Line, p.Content, context, target.IsBinary);
            target.Sections.Insert(place, added);
            context.RecordAdd(identity);
            return added;
        }
        if (candidates.Count == 0)
        {
            context.NoTarget(identity, $"{label}: no section to patch in {target.Path}. Set {DirectiveNew} = true to add it.");
            return null;
        }
        if (candidates.Count > 1)
        {
            context.Error($"{label}: matches {candidates.Count} sections in {target.Path}. Use a whole-file override.");
            return null;
        }

        var (original, position) = candidates[0];
        // The identity of a child section also names its parent (see SectionIdentity).
        if (isChild)
            identity = SectionIdentity.Of(target.Sections, position);
        if (p.IsDelete)
        {
            if (p.RemoveNames.Count > 0 || p.Content.Any(e => keyValue == null || !Is(e, match.KeyEntry!)))
            {
                context.Error($"{label}: {DirectiveDelete} cannot be used with other entries.");
                return null;
            }
            CheckBase(original, identity, p, context);
            var children = isChild ? 0 : SectionKeys.ChildCount(target.Sections, position);
            target.Sections.RemoveRange(position, 1 + children);
            context.Record(identity, null);
            return null;
        }

        var changes = p.Content.Where(e => keyValue == null || !Is(e, match.KeyEntry!)).ToList();
        target.Sections[position] = Modify(original, changes, p.RemoveNames, identity, context, target.IsBinary);
        return null;
    }

    /// <summary>
    /// Applies a patch section with <c>ol.rename</c>: it finds the target by the old key, changes it with the other
    /// lines, and gives its key line the new key. The section keeps its place, and its child sections stay after it.
    /// The ledger records a delete of the old identity, an add of the new identity and the rename. So a later patch of
    /// the old key finds no section, and a later patch of the new key finds the section.
    /// </summary>
    private static void Rename(IniDocument target, Section patch, PatchDirectives p, PatchContext context)
    {
        var match = SectionKeys.For(patch.Name, context.TargetPath);
        var name = $"[{patch.Name}]";
        if (p.IsNew || p.IsDelete || p.ParentKey != null)
        {
            context.Error($"{name}: {DirectiveRename} cannot be used with {DirectiveNew}, {DirectiveDelete} or {DirectiveParent}.");
            return;
        }
        if (match.Kind != SectionMatchKind.Keyed)
        {
            context.Error($"{name}: {DirectiveRename} needs a section type with a key line, and {name} has none.");
            return;
        }
        if (SectionKeys.IsChild(patch.Name))
        {
            context.Error($"{name}: {DirectiveRename} cannot rename a child section.");
            return;
        }
        if (p.RenameLines != 1 || p.RenameKey == null)
        {
            context.Error($"{name} needs exactly one '{DirectiveRename}' line with one value.");
            return;
        }
        var keyEntries = p.Content.Where(e => Is(e, match.KeyEntry!)).ToList();
        // With ol.replace, the first key line finds the section, as the game reads the first one.
        if (p.IsReplace ? keyEntries.Count == 0 || keyEntries[0].Count != 1
                : keyEntries.Count != 1 || keyEntries[0].Count != 1)
        {
            context.Error($"{name} needs exactly one '{match.KeyEntry}' entry with one value to find the section that " +
                          $"{DirectiveRename} renames.");
            return;
        }
        var oldKey = keyEntries[0][0].ToString();
        var newKey = p.RenameKey;
        var identity = new SectionIdentity(patch.Name, oldKey);
        var label = identity.ToString();
        if (newKey.Equals(oldKey, StringComparison.OrdinalIgnoreCase))
        {
            context.Error($"{label}: the new key '{newKey}' of {DirectiveRename} must differ from the old key in more " +
                          "than letter case.");
            return;
        }
        var candidates = target.Sections
            .Select((section, index) => (section, index))
            .Where(c => c.section.Name.Equals(patch.Name, StringComparison.OrdinalIgnoreCase) &&
                        (p.IsReplace ? FirstKeyEquals(c.section, match.KeyEntry!, oldKey)
                            : KeyEquals(c.section, match.KeyEntry!, oldKey)))
            .ToList();
        if (candidates.Count == 0)
        {
            context.NoTarget(identity, $"{label}: no section to rename in {target.Path}.");
            return;
        }
        if (candidates.Count > 1)
        {
            context.Error($"{label}: matches {candidates.Count} sections in {target.Path}. {DirectiveRename} needs one.");
            return;
        }
        if (target.Sections.Any(s => s.Name.Equals(patch.Name, StringComparison.OrdinalIgnoreCase) &&
                                     KeyEquals(s, match.KeyEntry!, newKey)))
        {
            context.Error($"{label}: another {name} section in {target.Path} has {match.KeyEntry} = {newKey}.");
            return;
        }

        var (original, position) = candidates[0];
        var renamed = new SectionIdentity(patch.Name, newKey);
        context.Record(identity, null);
        context.RecordAdd(renamed);
        context.RecordRename(identity, renamed);
        Section changed;
        if (p.IsReplace)
        {
            // The body first, then the new key (section 6.2 of the M6b design).
            CheckBase(original, identity, p, context);
            changed = ReplaceBody(original, p.Content, renamed, context, target.IsBinary);
        }
        else
        {
            var changes = p.Content.Where(e => !Is(e, match.KeyEntry!)).ToList();
            changed = changes.Count > 0 || p.RemoveNames.Count > 0
                ? Modify(original, changes, p.RemoveNames, renamed, context, target.IsBinary)
                : original;
        }
        target.Sections[position] = WithKey(changed, match.KeyEntry!, newKey);
    }

    /// <summary>
    /// The problem of the mission directives of a patch section, or null (sections 6.2 to 6.4 of the M6b design).
    /// <c>ol.replace</c> is only for a mission type whose line order counts, in a mission file, and not with
    /// <c>ol.new</c>, <c>ol.delete</c>, <c>ol.remove</c> or <c>ol.parent</c>. <c>ol.base</c> goes with
    /// <c>ol.replace</c> or <c>ol.delete</c> in a mission file. <c>ol.before</c> goes with <c>ol.new</c> in a mission
    /// file.
    /// </summary>
    private static string? MissionDirectiveProblem(Section patch, PatchDirectives p, PatchContext context)
    {
        var missionFile = SectionKeys.IsMissionFile(context.TargetPath);
        if (p.IsReplace && !SectionKeys.IsReplaced(patch.Name, context.TargetPath))
            return $"{DirectiveReplace} is only for sections whose line order counts.";
        if (p.IsReplace && (p.IsNew || p.IsDelete || p.RemoveNames.Count > 0 || p.ParentKey != null))
            return $"{DirectiveReplace} cannot be used with {DirectiveNew}, {DirectiveDelete}, {DirectiveRemove} or " +
                   $"{DirectiveParent}.";
        if (p.BaseLines > 0 && (!missionFile || !(p.IsReplace || p.IsDelete)))
            return $"{DirectiveBase} is only for a section of a mission file with {DirectiveReplace} or {DirectiveDelete}.";
        if (p.BaseLines > 1 || p.BaseLines == 1 && string.IsNullOrEmpty(p.BaseHash))
            return $"{DirectiveBase} needs one line with one value: the hash that the editor writes.";
        if (p.BeforeLines > 0 && (!missionFile || !p.IsNew))
            return $"{DirectiveBefore} is only for a new section of a mission file. Use it with {DirectiveNew}.";
        if (p.BeforeLines > 1 || p.BeforeLines == 1 && p.Before == null)
            return $"{DirectiveBefore} needs one line with two values: the type and the key of a section.";
        return null;
    }

    /// <summary>
    /// Applies a patch section with <c>ol.replace</c>: it finds the target by the first key line, wherever that line is
    /// in the patch section. All entries of the target take the entries of the patch section, in patch order. The
    /// section keeps its place in the file.
    /// </summary>
    private static void Replace(IniDocument target, Section patch, PatchDirectives p, PatchContext context)
    {
        var match = SectionKeys.For(patch.Name, context.TargetPath);
        var key = p.Content.FirstOrDefault(e => Is(e, match.KeyEntry!));
        if (key is not { Count: 1 })
        {
            context.Error($"[{patch.Name}] needs a '{match.KeyEntry}' entry with one value to find the section that " +
                          $"{DirectiveReplace} replaces.");
            return;
        }
        var identity = new SectionIdentity(patch.Name, key[0].ToString());
        var positions = target.Sections
            .Select((section, index) => (section, index))
            .Where(c => c.section.Name.Equals(patch.Name, StringComparison.OrdinalIgnoreCase) &&
                        FirstKeyEquals(c.section, match.KeyEntry!, identity.Key!))
            .Select(c => c.index)
            .ToList();
        if (positions.Count == 0)
        {
            context.NoTarget(identity, $"{identity}: no section to patch in {target.Path}. Set {DirectiveNew} = true to add it.");
            return;
        }
        if (positions.Count > 1)
        {
            context.Error($"{identity}: matches {positions.Count} sections in {target.Path}. Use a whole-file override.");
            return;
        }
        var original = target.Sections[positions[0]];
        CheckBase(original, identity, p, context);
        target.Sections[positions[0]] = ReplaceBody(original, p.Content, identity, context, target.IsBinary);
    }

    /// <summary>
    /// A section with the entries of a patch section in patch order, in place of all entries of the original. The n-th
    /// line of a name gets the value types of the n-th original line of the name, as a change does. The ledger records
    /// each name of the original and of the new entries.
    /// </summary>
    private static Section ReplaceBody(Section original, List<Entry> content, SectionIdentity identity,
        PatchContext context, bool binary)
    {
        var originalLines = original.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new Section(original.Name, content.Count) { File = original.File, Line = original.Line };
        foreach (var entry in content)
        {
            var n = occurrence.GetValueOrDefault(entry.Name);
            occurrence[entry.Name] = n + 1;
            var replaced = originalLines.TryGetValue(entry.Name, out var lines) && n < lines.Count ? lines[n] : null;
            result.Add(Copy(result, entry, context, binary, replaced));
        }
        foreach (var name in original.Select(e => e.Name).Concat(content.Select(e => e.Name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            context.Record(identity, name);
        return result;
    }

    /// <summary>
    /// Warns when the hash of <c>ol.base</c> differs from the hash of the section that the patch replaces or deletes
    /// (section 6.3 of the M6b design). The changes of the lower layers in the section then go away.
    /// </summary>
    private static void CheckBase(Section original, SectionIdentity identity, PatchDirectives p, PatchContext context)
    {
        if (p.BaseHash is not { } expected || SectionHash.Matches(original, expected))
            return;
        context.ChangedBases.Add(identity);
        context.Warning($"Mod '{context.ModId}' replaces {identity} of {context.TargetPath}. The base of this section " +
                        "changed after the edit, and its changes go away. Compare the two, then keep your lines or use " +
                        "the new base.");
    }

    /// <summary>The indexes of the sections of a type whose first key line has the key. <c>ol.before</c> finds its
    /// section as <c>ol.replace</c> does, because the game reads the first <c>nickname</c> line of a mission
    /// section.</summary>
    private static List<int> FindKeyed(IniDocument target, string type, string key, string path)
    {
        var match = SectionKeys.For(type, path);
        return target.Sections
            .Select((section, index) => (section, index))
            .Where(c => c.section.Name.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                        match.Kind == SectionMatchKind.Keyed && FirstKeyEquals(c.section, match.KeyEntry!, key))
            .Select(c => c.index)
            .ToList();
    }

    /// <summary>A copy of a section whose first key line has the new key, as a string.</summary>
    private static Section WithKey(Section section, string keyEntry, string key)
    {
        var result = new Section(section.Name, section.Count) { File = section.File, Line = section.Line };
        var done = false;
        foreach (var entry in section)
        {
            if (!done && Is(entry, keyEntry))
            {
                done = true;
                var line = new Entry(result, entry.Name, 1) { Line = entry.Line };
                line.Add(new StringValue(key) { Entry = line, Line = entry.Line });
                result.Add(line);
                continue;
            }
            result.Add(Copy(result, entry, null, false, null));
        }
        return result;
    }

    private static void AddChild(IniDocument target, Section patch, PatchDirectives p, PatchContext context)
    {
        if (p.ParentKey == null)
        {
            context.Error($"A new [{patch.Name}] needs '{DirectiveParent} = <parent key>', or must follow a new parent section in the patch.");
            return;
        }
        var parents = target.Sections
            .Select((section, index) => (section, index))
            .Where(c => !SectionKeys.IsChild(c.section.Name) &&
                        string.Equals(KeyOf(c.section), p.ParentKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (parents.Count != 1)
        {
            context.Error($"[{patch.Name}] {DirectiveParent} = {p.ParentKey}: {parents.Count} sections in {target.Path} have that key.");
            return;
        }
        InsertChild(target, parents[0].index, patch, p.Content, context);
    }

    /// <summary>
    /// Adds a new child section after the child sections of the parent at <paramref name="parentIndex"/>.
    /// A child with a key must not repeat the key of another child of the same type under that parent.
    /// </summary>
    private static void InsertChild(IniDocument target, int parentIndex, Section patch, List<Entry> content,
        PatchContext context)
    {
        var child = Build(patch.Name, patch.File, patch.Line, content, context, target.IsBinary);
        var key = KeyOf(child);
        var siblings = target.Sections.Skip(parentIndex + 1).Take(SectionKeys.ChildCount(target.Sections, parentIndex));
        if (key != null && siblings.Any(s => s.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase) &&
                                             string.Equals(KeyOf(s), key, StringComparison.OrdinalIgnoreCase)))
        {
            context.Error($"[{child.Name}] {SectionKeys.For(child.Name).KeyEntry} = {key}: {DirectiveNew} is set, " +
                          $"but its parent {KeyOf(target.Sections[parentIndex])} already has this section.");
            return;
        }
        var index = parentIndex + 1 + SectionKeys.ChildCount(target.Sections, parentIndex);
        target.Sections.Insert(index, child);
        context.RecordAdd(SectionIdentity.Of(target.Sections, index));
    }

    private static Section Modify(Section original, List<Entry> changes, List<string> removeNames,
        SectionIdentity identity, PatchContext context, bool binary)
    {
        var label = identity.ToString();
        var replaced = new HashSet<string>(changes.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(removeNames, StringComparer.OrdinalIgnoreCase);

        foreach (var group in SectionKeys.LinkedGroups(original.Name))
        {
            var touched = group.Where(n => replaced.Contains(n) || removed.Contains(n)).ToList();
            var left = group.Where(n => !touched.Contains(n, StringComparer.OrdinalIgnoreCase) &&
                                        original.Any(e => Is(e, n))).ToList();
            if (touched.Count > 0 && left.Count > 0)
            {
                context.Error($"{label}: the {string.Join(", ", group)} lines belong together. The patch changes " +
                              $"{string.Join(", ", touched)}, so it must also list {string.Join(", ", left)}.");
                return original;
            }
        }

        // The original lines of each name, to keep the value types of a binary file. The n-th patch line of a name
        // replaces the n-th original line of that name.
        var originalLines = original.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var occurrence = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Entry CopyChange(Section into, Entry e)
        {
            var n = occurrence.GetValueOrDefault(e.Name);
            occurrence[e.Name] = n + 1;
            var replacedLine = originalLines.TryGetValue(e.Name, out var lines) && n < lines.Count ? lines[n] : null;
            return Copy(into, e, context, binary, replacedLine);
        }

        foreach (var name in removed)
        {
            if (!original.Any(e => Is(e, name)))
                context.Warning($"{label}: {DirectiveRemove} '{name}' has no entry to remove.");
            if (replaced.Contains(name))
                context.Warning($"{label}: '{name}' is in {DirectiveRemove} and is also set. It is set.");
            context.Record(identity, name);
        }

        var result = new Section(original.Name) { File = original.File, Line = original.Line };
        var blockWritten = false;
        foreach (var entry in original)
        {
            if (replaced.Contains(entry.Name))
            {
                if (!blockWritten)
                {
                    foreach (var e in changes)
                        result.Add(CopyChange(result, e));
                    blockWritten = true;
                }
                continue;
            }
            if (!removed.Contains(entry.Name))
                result.Add(Copy(result, entry, null, binary, null));
        }
        if (!blockWritten)
        {
            foreach (var e in changes)
                result.Add(CopyChange(result, e));
        }
        foreach (var name in replaced)
            context.Record(identity, name);
        return result;
    }

    /// <summary>The directives and the content entries of a patch section, or null after an error.</summary>
    /// <param name="context">Gets the error, or null.</param>
    private static PatchDirectives? Parse(Section patch, PatchContext? context)
    {
        var p = new PatchDirectives();
        foreach (var entry in patch)
        {
            if (Is(entry, DirectiveNew))
                p.IsNew = IsTrue(entry);
            else if (Is(entry, DirectiveDelete))
                p.IsDelete = IsTrue(entry);
            else if (Is(entry, DirectiveRemove))
                p.RemoveNames.AddRange(entry.Select(v => v.ToString()));
            else if (Is(entry, DirectiveParent))
                p.ParentKey = entry.Count == 1 ? entry[0].ToString() : null;
            else if (Is(entry, DirectiveRename))
            {
                p.RenameLines++;
                p.RenameKey = entry.Count == 1 && entry[0].ToString().Trim() is { Length: > 0 } key ? key : null;
            }
            else if (Is(entry, DirectiveAppend))
                p.IsAppend = true;
            else if (Is(entry, DirectiveReplace))
                p.IsReplace = IsTrue(entry);
            else if (Is(entry, DirectiveBase))
            {
                p.BaseLines++;
                p.BaseHash = entry.Count == 1 ? SectionHash.TextOf(entry[0]) : null;
            }
            else if (Is(entry, DirectiveBefore))
            {
                p.BeforeLines++;
                p.Before = entry.Count == 2 && entry[0].ToString().Trim() is { Length: > 0 } type &&
                           entry[1].ToString().Trim() is { Length: > 0 } key
                    ? (type, key)
                    : null;
            }
            else if (entry.Name.StartsWith(DirectivePrefix, StringComparison.OrdinalIgnoreCase))
            {
                context?.Error($"[{patch.Name}]: unknown directive '{entry.Name}'.");
                return null;
            }
            else
                p.Content.Add(entry);
        }
        return p;
    }

    /// <summary>
    /// The key that <c>ol.parent</c> compares: the first value of the first key entry with a value, or null.
    /// </summary>
    internal static string? KeyOf(Section section)
    {
        var match = SectionKeys.For(section.Name);
        if (match.Kind != SectionMatchKind.Keyed)
            return null;
        var entry = section.FirstOrDefault(e => Is(e, match.KeyEntry!));
        return entry is { Count: > 0 } ? entry[0].ToString() : null;
    }

    private static Section Build(string name, string? file, int line, List<Entry> content, PatchContext context,
        bool binary)
    {
        var section = new Section(name) { File = file, Line = line };
        foreach (var entry in content)
            section.Add(Copy(section, entry, context, binary, null));
        return section;
    }

    /// <summary>
    /// Copies an entry into <paramref name="section"/>. With a context, string placeholders are resolved, and for a
    /// binary file each value gets a type that the file can store exactly (see <see cref="BinaryValueTypes.Store"/>).
    /// </summary>
    /// <param name="replaced">The target line that this entry replaces, to keep its value types, or null.</param>
    internal static Entry Copy(Section section, Entry source, PatchContext? context, bool binary, Entry? replaced)
    {
        var count = source.Count;
        if (context != null && binary && count > BinaryValueTypes.MaxValuesPerLine)
        {
            context.Error($"'{source.Name}': a line of a binary INI file holds at most {BinaryValueTypes.MaxValuesPerLine} " +
                          $"values, not {count}. The merge keeps the first {BinaryValueTypes.MaxValuesPerLine}.");
            count = BinaryValueTypes.MaxValuesPerLine;
        }
        var copy = new Entry(section, source.Name, count) { Line = source.Line };
        for (var i = 0; i < count; i++)
        {
            var value = source[i];
            if (context != null && StringPlaceholder.TryParse(value.ToString(), out var placeholder) &&
                context.ResolveString(placeholder) is { } id)
                copy.Add(new Int32Value(id) { Entry = copy, Line = value.Line });
            else if (context != null && binary)
                copy.Add(StoreBinary(source, i, replaced, copy, context));
            else
                copy.Add(value);
        }
        return copy;
    }

    /// <summary>
    /// Applies the binary type rules to one value (see <see cref="BinaryValueTypes.Store"/>). Reports a large integer
    /// that the file cannot store, and a large integer that the file stores as a float with other digits.
    /// </summary>
    private static ValueBase StoreBinary(Entry source, int index, Entry? replaced, Entry copy, PatchContext context)
    {
        var value = source[index];
        var stored = BinaryValueTypes.Store(source, index, replaced, copy);
        if (stored == null && value is BooleanValue)
        {
            context.Error(BinaryValueTypes.BooleanInNumber(source.Name));
            return value;
        }
        if (stored == null)
        {
            var number = value.ToInt64();
            context.Error($"'{source.Name} = {number}': a binary INI file cannot store this value exactly. " +
                          $"Write {number}.0 for a number{HashHint(number)}.");
            return value;
        }
        if (stored is SingleValue single && BinaryValueTypes.IsLargeInteger(value, out var large) &&
            !BinaryValueTypes.HoldsExactly(single, large))
        {
            var kept = ((double)(float)single).ToString("F0", CultureInfo.InvariantCulture);
            context.Warning($"'{source.Name} = {large}': the value replaces a float, and a float cannot hold this " +
                            $"number. The file stores {kept}. Write {kept}.0 for that number{HashHint(large)}.");
        }
        return stored;
    }

    /// <summary>
    /// The hint that names the signed form of a hash. A binary file stores the signed form as the same 32 bits. The
    /// hint is empty for a number outside the hash range.
    /// </summary>
    private static string HashHint(long number) =>
        BinaryValueTypes.IsUnsignedHash(number) ? $", or {unchecked((int)number)} for a hash" : "";

    private static bool KeyEquals(Section section, string keyEntry, string keyValue) =>
        section.Any(e => Is(e, keyEntry) && e.Count > 0 &&
                         e[0].ToString().Equals(keyValue, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the first key line of a section has the key. The game reads the first <c>nickname</c> line of
    /// a mission section.</summary>
    private static bool FirstKeyEquals(Section section, string keyEntry, string keyValue) =>
        section.FirstOrDefault(e => Is(e, keyEntry)) is { Count: > 0 } first &&
        first[0].ToString().Equals(keyValue, StringComparison.OrdinalIgnoreCase);

    private static bool Is(Entry entry, string name) => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(Entry entry) => entry.Count == 1 && entry[0].TryToBoolean(out var b) && b;

    private sealed class PatchDirectives
    {
        public bool IsNew;
        public bool IsDelete;

        /// <summary>True when the section has an <c>ol.append</c> line, which only a patch of <c>freelancer.ini</c> can
        /// use.</summary>
        public bool IsAppend;
        public string? ParentKey;

        /// <summary>The number of <c>ol.rename</c> lines, and the new key of the last one when it has one value.</summary>
        public int RenameLines;
        public string? RenameKey;

        /// <summary>True when the section has <c>ol.replace = true</c>.</summary>
        public bool IsReplace;

        /// <summary>The number of <c>ol.base</c> lines, and the hash of the last one when it has one value.</summary>
        public int BaseLines;
        public string? BaseHash;

        /// <summary>The number of <c>ol.before</c> lines, and the type and the key of the last one when it has two
        /// values.</summary>
        public int BeforeLines;
        public (string Type, string Key)? Before;
        public readonly List<string> RemoveNames = [];
        public readonly List<Entry> Content = [];
    }
}

/// <summary>What the merge does with one section of a patch (see <see cref="IniPatcher.Actions"/>).</summary>
internal enum PatchAction
{
    /// <summary>The section changes its target.</summary>
    Change,

    /// <summary>The section adds a new section.</summary>
    Add,

    /// <summary>The section deletes its target.</summary>
    Delete,
}

/// <summary>
/// The state that one patch file needs: its mod, the string ids, the conflict ledger and the diagnostics.
/// </summary>
/// <param name="stringIds">The text ids, or null where text placeholders are not allowed.</param>
/// <param name="keepPlaceholders">True to keep the text placeholders as strings, with no error. The editor builds
/// its working copy of a file this way, because the text ids of a mod change when its strings change.</param>
internal sealed class PatchContext(
    string modId,
    string sourceFile,
    string targetPath,
    StringIds? stringIds,
    PatchLedger ledger,
    List<Diagnostic> diagnostics,
    bool keepPlaceholders = false)
{
    /// <summary>The id of the mod of the patch.</summary>
    public string ModId => modId;

    /// <summary>The install path of the file that the patch changes.</summary>
    public string TargetPath => targetPath;

    public void Error(string message) => diagnostics.Add(Diagnostic.Error(sourceFile, message));

    /// <summary>The patch sections that found no section to change, with their errors. The target check of the save
    /// gate gives each one its own text (rule T01, section 6.12 of the M6b design).</summary>
    public List<(SectionIdentity Section, Diagnostic Error)> NoTargets { get; } = [];

    /// <summary>The sections whose <c>ol.base</c> differs from the hash of the section that the patch replaces or
    /// deletes (rule T02, section 6.12 of the M6b design).</summary>
    public List<SectionIdentity> ChangedBases { get; } = [];

    /// <summary>Adds the error of a patch section that finds no section to change.</summary>
    public void NoTarget(SectionIdentity section, string message)
    {
        var error = Diagnostic.Error(sourceFile, message);
        diagnostics.Add(error);
        NoTargets.Add((section, error));
    }

    public void Warning(string message) => diagnostics.Add(Diagnostic.Warning(sourceFile, message));

    /// <summary>The text id of a placeholder, or null when the placeholder stays a string.</summary>
    public int? ResolveString(StringPlaceholder placeholder)
    {
        if (keepPlaceholders)
            return null;
        if (stringIds == null)
        {
            Error($"'{placeholder}': text placeholders cannot be used in {targetPath}.");
            return null;
        }
        if (stringIds.TryGet(modId, placeholder, out var id))
            return id;
        Error($"'{placeholder}' is not in {modId}'s strings.json.");
        return null;
    }

    /// <summary>Records that this mod changes an entry, or deletes the section when the name is null.</summary>
    public void Record(SectionIdentity section, string? entryName)
    {
        var previous = ledger.Record(targetPath, section, entryName, modId);
        if (previous != null && previous != modId)
            Warning($"{section}: '{entryName ?? "(section)"}' was also changed by mod '{previous}'. This mod loads later and wins.");
    }

    /// <summary>Records that this mod adds a section.</summary>
    public void RecordAdd(SectionIdentity section) => ledger.RecordAdd(targetPath, section, modId);

    /// <summary>Records that this mod gives a section a new key (<c>ol.rename</c>).</summary>
    public void RecordRename(SectionIdentity from, SectionIdentity to) => ledger.RecordRename(targetPath, from, to, modId);
}
