using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing.Lists;

/// <summary>
/// The data lists of a session (milestone M21): the entries that a reference key can name, from the sections that the
/// engine reads, in W of the edit view. Mods extend a list when they add sections. Only engine-fixed sets come from C#
/// enums.
/// </summary>
/// <remarks>
/// <para>The rule data (see <see cref="ListRules"/>) maps a value of a key of a section type to a list. A list is
/// available when its files have at least one file in the edit view, or when it is an enum. An unavailable list gives
/// no result, so a synthetic profile without the files of a role gets no list check.</para>
/// <para>Each cache keeps the revision of W at its last check (see <see cref="EditSession.CacheRevision"/>). A call at
/// the same revision returns at once. At a new revision, a cache compares the W states of its own files only, so an edit of one
/// file builds again only the lists and the reverse index parts of that file.</para>
/// <para>"Missing" is a pure function of W: no state stores it. So an undo of a delete heals each referrer at once
/// (decision D81). "Unusable" is a pure function of W too, so one undo of the edit that made an entry unusable heals
/// each line that names it.</para>
/// <para>A file that the active mod replaces with a whole file has its lower data in W. The lists read such a file as
/// the edit view gives it, so they hold its entries. A whole mission file of the active mod is a document of the kind
/// "whole file" whose W has the data of the mod, so the lists read its W.</para>
/// </remarks>
public sealed class DataLists
{
    private readonly EditSession session;
    private readonly ListRules rules;
    private readonly Dictionary<(string Ids, string? Scope), CachedView> views = [];
    private readonly Dictionary<string, ReferencePart> parts = new(VfsPath.Comparer);
    private readonly Dictionary<string, MergedReferences> merged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SectionRef, IReadOnlyList<ListProblem>> problems = [];
    private readonly Dictionary<SectionRef, ListProblem?> unusable = [];
    private readonly HashSet<SectionRef> unusableInProgress = [];
    private readonly Dictionary<string, (ModOverlay View, FileTable? Table)> wholeFiles = new(VfsPath.Comparer);
    private readonly Dictionary<string, RemovedPart> removedParts = new(VfsPath.Comparer);
    private int checksRevision = int.MinValue;

    /// <summary>The last <see cref="ReferencePart.Id"/>.</summary>
    private long partIds;

    // The files of the edit view of each files list source: its extensions, its folder and its class. The edit view
    // changes only with a save.
    private readonly Dictionary<(string Extensions, string? Folder, string? FileClass), (ModOverlay View, List<string> Paths)>
        editViewFiles = [];

    // The file system of the edit view, for the file test of a rule with the flag exists.
    private (ModOverlay View, FileSystem Files)? editViewSystem;
    private (int Revision, IReadOnlyList<RemovedEntry> Entries, Dictionary<(string, string, string), RemovedEntry> Keys)? removed;

    internal DataLists(EditSession session, ListRules rules)
    {
        this.session = session;
        this.rules = rules;
    }

    internal ListRules RuleData => rules;

    private DataFiles Files => session.DataFiles;

    /// <summary>The rules of a key of a section, by value index. Empty when no rule of the roles of its file has the
    /// key.</summary>
    public IReadOnlyList<ListRule> Rules(SectionRef section, string key)
    {
        var roles = Files.RolesOf(section.Path);
        return roles.Count switch
        {
            0 => [],
            1 => rules.Rules(roles[0], section.Id.Type, key),
            _ => roles.SelectMany(r => rules.Rules(r, section.Id.Type, key)).OrderBy(r => r.Index).ToList(),
        };
    }

    /// <summary>
    /// The list of a rule for a line of a section, or null when the list is not available: its files are not in the
    /// edit view, or a <c>systemOf:N</c> scope names no known system.
    /// </summary>
    /// <param name="context">The section of the line.</param>
    /// <param name="line">The values of the line, for a scope that another value gives, or null.</param>
    public ListView? List(ListRule rule, SectionRef context, IReadOnlyList<ValueBase>? line) =>
        ScopeOf(rule, context.Path, line, out var scope) ? View(rule.Lists, scope) : null;

    /// <summary>A list that has no scope, or null when it is not available or the rule data has no such list.</summary>
    public ListView? List(string id) => View([id], null);

    /// <summary>A scoped list of the sections of one file, for example the objects of a system file.</summary>
    public ListView? List(string id, string scopeFile) => View([id], VfsPath.Normalize(scopeFile));

    /// <summary>
    /// Each line value that names an entry of a list, in all referrer files of the list, also files that the session
    /// did not open yet. A scoped list needs <paramref name="scopeFile"/>, the file of the entry, to tell the entries of
    /// different files apart. Without it, the result has the references of all files.
    /// </summary>
    public IReadOnlyList<ListReference> ReferencesTo(string list, string value, string? scopeFile = null)
    {
        var map = Merged(list);
        var normal = scopeFile == null ? null : VfsPath.Normalize(scopeFile);
        var lower = ValueKey(list, value);
        if (normal != null || !IsScoped(list))
            return map.GetValueOrDefault((normal?.ToLowerInvariant(), lower)) ?? [];
        return map.Where(p => p.Key.Value == lower).SelectMany(p => p.Value).ToList();
    }

    /// <summary>The key of a value of a list in the reverse index: the value without letter case, or for a files list
    /// its path below the data folder without letter case, so the spellings of one path give one key.</summary>
    private string ValueKey(string list, string value)
    {
        var trimmed = value.Trim();
        var files = rules.Lists.TryGetValue(list, out var definition) && definition.Source?.Files != null;
        return (files && ThnReferences.PathOf(trimmed) is { } path ? path : trimmed).ToLowerInvariant();
    }

    /// <summary>The number of line values that name an entry (see <see cref="ReferencesTo"/>).</summary>
    public int CountReferences(string list, string value, string? scopeFile = null) =>
        ReferencesTo(list, value, scopeFile).Count;

    /// <summary>
    /// The entries that the lower layer has and the active mod removes: a delete, an <c>ol.delete</c> patch, a whole
    /// file without the entry, or a rename. Only files that the active mod changes can remove an entry. A name that
    /// another file of the list still gives is not removed.
    /// </summary>
    public IReadOnlyList<RemovedEntry> Removed() => RemovedState().Entries;

    /// <summary>The list problems of a section (layer 2 of roadmap section 5.4), with the severities of section 11.1
    /// of the M21 design. Empty when the edit view has no such section.</summary>
    public IReadOnlyList<ListProblem> Problems(SectionRef target)
    {
        RefreshChecks();
        if (!problems.TryGetValue(target, out var result))
        {
            result = ListChecks.Check(session, this, target);
            problems[target] = result;
        }
        return result;
    }

    /// <summary>
    /// Why the engine cannot use an entry, or null when it can (decision D82, as the user decided it on 2026-10-18).
    /// An entry is unusable when its list says so (<see cref="ListEntry.Usable"/>), or when the list checks of its
    /// section give an error whose effect drops the item or stops the load (<see cref="ListRule.MakesUnusable"/>). An
    /// error with the effect crash stays on its own line. An error of a list check is always a change of the active
    /// mod, so the active mod made such an entry unusable.
    /// </summary>
    internal Unusability? Unusable(ListEntry entry)
    {
        if (!entry.Usable)
            return new Unusability(ListChecks.Lower(entry.Problem ?? "the game cannot use it."), null,
                entry.UnusableByActiveMod);
        return entry.Section is { } section && UnusableProblem(section) is { } problem
            ? new Unusability(problem.Reason, problem.Consequence, true)
            : null;
    }

    /// <summary>
    /// The errors of the lines of any layer, under a closed rule, that name an entry that the active mod removes
    /// (decision D81) or makes unusable (decision D82, as decided on 2026-10-18). The save gate reads them. A line
    /// whose effect makes its own section unusable makes the entries of that section unusable in turn, so the search
    /// follows it: a munition without its explosion makes each gun that fires it unusable, and so each loadout line
    /// that names such a gun is an error.
    /// </summary>
    internal IReadOnlyList<ListProblem> ReferrerErrors()
    {
        var result = new List<ListProblem>();
        var sections = new Queue<SectionRef>();
        var seenSections = new HashSet<SectionRef>();
        var seenEntries = new HashSet<(string List, string? Scope, string Value)>();

        void Follow(IEnumerable<ListReference> references, ListProblemKind kind)
        {
            foreach (var reference in references.Where(r => r.Rule.Closed))
            {
                result.AddRange(Problems(reference.Section).Where(p => p.Kind == kind && p.IsError));
                sections.Enqueue(reference.Section);
            }
        }

        foreach (var entry in Removed())
            Follow(ReferencesTo(entry.List, entry.Value, IsScoped(entry.List) ? entry.LowerSection.Path : null),
                ListProblemKind.Removed);
        // Only a section that the active mod adds or changes, or a section that names an unusable entry, can make an
        // entry unusable.
        foreach (var path in session.ActiveChangedPaths())
        {
            if (TableOf(path) is not { } table)
                continue;
            for (var i = 0; i < table.Working.Count; i++)
            {
                if (ChangedByActiveMod(table, i))
                    sections.Enqueue(table.Ref(i));
            }
        }
        while (sections.TryDequeue(out var section))
        {
            if (!seenSections.Add(section) || TableOf(section.Path) is not { } table ||
                IndexOf(table, section.Id) is not (>= 0 and var index))
                continue;
            foreach (var definition in SourcesOf(table.Path, table.Working[index].Name))
            {
                var source = definition.Source!;
                if (!Matches(table.Working[index], source.Where))
                    continue;
                var scope = source.Scoped ? table.Path : null;
                foreach (var name in Names(table.Working[index], source))
                {
                    if (!seenEntries.Add((definition.Id.ToLowerInvariant(), scope?.ToLowerInvariant(),
                            name.ToLowerInvariant())) ||
                        View([definition.Id], scope)?.Find(name) is not { } entry ||
                        Unusable(entry) is not { ByActiveMod: true })
                        continue;
                    Follow(ReferencesTo(definition.Id, name, scope), ListProblemKind.Unusable);
                }
            }
        }
        return result;
    }

    /// <summary>The first error of a section whose effect makes the section unusable, from the rules with such an
    /// effect only, or null.</summary>
    private ListProblem? UnusableProblem(SectionRef section)
    {
        RefreshChecks();
        if (unusable.TryGetValue(section, out var known))
            return known;
        // A cycle of such rules: the section in progress counts as usable, so the check ends. A section of the cycle can
        // then miss an unusable entry of the cycle. The rule data of the retail game has no such cycle.
        if (!unusableInProgress.Add(section))
            return null;
        try
        {
            known = ListChecks.Check(session, this, section, unusableOnly: true)
                .FirstOrDefault(p => p.IsError && ListRule.MakesUnusable(p.Effect));
        }
        finally
        {
            unusableInProgress.Remove(section);
        }
        unusable[section] = known;
        return known;
    }

    /// <summary>Clears the check results of an older revision of W.</summary>
    private void RefreshChecks()
    {
        if (checksRevision == session.CacheRevision)
            return;
        problems.Clear();
        unusable.Clear();
        checksRevision = session.CacheRevision;
    }

    /// <summary>True when the active mod adds or changes the section at an index of a table: a section of W that is
    /// not the section of the lower layer, or a section of a whole file of the active mod. B of a document of the kind
    /// "whole file" is the file of the active mod, so each of its sections counts.</summary>
    internal bool ChangedByActiveMod(FileTable table, int index) =>
        session.ActiveMod != null && (table.Document is not { } document || document.IsWholeFile ||
                                      document.BaseSection(table.Ids[index]) is not { } lower ||
                                      !ReferenceEquals(lower, table.Working[index]));

    private static int IndexOf(FileTable table, SectionIdentity id) =>
        table.Document?.IndexOf(id) ?? Array.IndexOf(table.Ids, id);

    /// <summary>The entry of the list that the active mod removes, or null (see <see cref="Removed"/>).</summary>
    /// <param name="scope">The file of a scoped list, or null.</param>
    internal RemovedEntry? RemovedOf(string list, string? scope, string value)
    {
        var keys = RemovedState().Keys;
        var name = value.Trim().ToLowerInvariant();
        if (!IsScoped(list))
            return keys.GetValueOrDefault((list.ToLowerInvariant(), "", name));
        // A scope of more than one file removes the entry when one of its files removes it.
        return (scope == null ? [""] : ScopeFiles(scope))
            .Select(file => keys.GetValueOrDefault((list.ToLowerInvariant(), file.ToLowerInvariant(), name)))
            .FirstOrDefault(r => r != null);
    }

    /// <summary>
    /// The scope file of a rule for a line, or false when the scope names no known system. Null for a list without a
    /// scope. The scope <c>owner</c> gives the system files that name the file of the line, joined with
    /// <see cref="ScopeSeparator"/>, or false when no system file names it (milestone M3).
    /// </summary>
    internal bool ScopeOf(ListRule rule, string path, IReadOnlyList<ValueBase>? line, out string? scope)
    {
        scope = null;
        if (rule.OwnerScope)
        {
            var owners = Files.OwnersOf(path);
            if (owners.Count == 0)
                return false;
            scope = string.Join(ScopeSeparator, owners);
            return true;
        }
        if (rule.SystemValue is >= 0 and var index)
        {
            if (line == null || index >= line.Count || session.SystemPath(line[index].ToString()) is not { } system)
                return false;
            scope = system;
            return true;
        }
        if (rule.Scope == "file" || rule.Lists.Any(IsScoped))
            scope = VfsPath.Normalize(path);
        return true;
    }

    /// <summary>True for a list whose rule gives the file.</summary>
    internal bool IsScoped(string list) => rules.Lists.TryGetValue(list, out var d) && d.Source is { Scoped: true };

    /// <summary>The separator of the files of a scope with more than one file. A path cannot hold it.</summary>
    internal const char ScopeSeparator = '|';

    /// <summary>The files of a scope: none for null, else each file of a scope with more than one file.</summary>
    private static string[] ScopeFiles(string? scope) => scope == null ? [] : scope.Split(ScopeSeparator);

    /// <summary>The definitions of the lists whose entries a section of a file can give.</summary>
    internal IEnumerable<ListDefinition> SourcesOf(string path, string sectionType)
    {
        var roles = Files.RolesOf(path);
        var isSystem = session.IsSystemFile(path);
        bool Gives(ListSource s) => GivesEntries(s, roles, isSystem) && s.Values == null &&
                                    s.Sections.Contains(sectionType, StringComparer.OrdinalIgnoreCase);
        return rules.Lists.Values.Where(d => d.Source is { } s && Gives(s));
    }

    /// <summary>
    /// True when the sections of a file can give entries of a list source. A scoped source without a role reads a
    /// system file. Each other source reads the files of its role. A scoped source with a role reads one file of its
    /// role, for example one mission script (section 14.1 of the M6a design).
    /// </summary>
    /// <param name="roles">The roles of the file.</param>
    /// <param name="isSystem">True when the file is a system file.</param>
    private static bool GivesEntries(ListSource source, IReadOnlyList<string> roles, bool isSystem) =>
        source is { Scoped: true, Role: null }
            ? isSystem
            : source.Role != null && roles.Contains(source.Role, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The names of a list that the base folder without mods (B) gives and the edit view does not give (decision
    /// D551). Only a file that a mod changes can lose a name. So the method reads only the
    /// files of the list that a mod changes, in their version of the base folder. Each name has the last mod that
    /// changes its file. A files list, an enum and a scoped list give no names.
    /// </summary>
    /// <param name="baseFiles">The file system of the base folder.</param>
    /// <param name="modOf">Gives the last mod that changes a file, or null for a file that no mod changes.</param>
    internal IReadOnlyDictionary<string, string> NamesOnlyInBase(string id, FileSystem baseFiles, Func<string, string?> modOf)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!rules.Lists.TryGetValue(id, out var definition) ||
            definition.Source is not { Files: null, Scoped: false, Role: { } role } source)
            return result;
        var current = List(id);
        foreach (var path in Files.Paths(role))
        {
            if (modOf(path) is not { } mod || BaseSections(baseFiles, path) is not { } sections)
                continue;
            var table = new FileTable(path, sections);
            var types = source.Values is { } values ? [values.Section] : source.Sections;
            foreach (var index in types.SelectMany(table.OfType))
            {
                var section = table.Working[index];
                if (!Matches(section, source.Where))
                    continue;
                foreach (var name in Names(section, source).Where(n => current?.Find(n) == null))
                    result.TryAdd(name, mod);
            }
        }
        return result;
    }

    /// <summary>The sections of a file of the base folder. Null when the base folder has no such INI file or the file
    /// does not parse.</summary>
    internal static IReadOnlyList<Section>? BaseSections(FileSystem baseFiles, string path)
    {
        try
        {
            return baseFiles.FileExists(path) ? IniDocument.Parse(path, baseFiles.ReadAllBytes(path)).Sections : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The view of one or more lists in order, for a scope, or null when none is available.</summary>
    private ListView? View(IReadOnlyList<string> ids, string? scope)
    {
        var key = (string.Join("+", ids).ToLowerInvariant(), scope?.ToLowerInvariant());
        if (!views.TryGetValue(key, out var cached))
            views[key] = cached = new CachedView();
        if (cached.Revision == session.CacheRevision)
            return cached.View;
        cached.Revision = session.CacheRevision;
        var paths = new List<string>();
        var other = new List<object?>();
        foreach (var id in ids)
        {
            if (!rules.Lists.TryGetValue(id, out var definition) || definition.Source is not { } source)
                continue;
            if (source.Files != null)
            {
                // The files of the edit view change only with a save. The new cutscenes of W change with each edit.
                other.Add(session.EditView);
                other.AddRange(session.NewCutsceneDocuments.Select(d => d.State));
                continue;
            }
            paths.AddRange(source.Scoped ? ScopeFiles(scope) : Files.Paths(source.Role!));
        }
        var states = paths.Select(StateOf).Concat(other).ToArray();
        if (cached.Built && cached.Paths.SequenceEqual(paths, VfsPath.Comparer) && cached.States.Length == states.Length &&
            cached.States.Zip(states).All(p => ReferenceEquals(p.First, p.Second)))
            return cached.View;
        cached.Built = true;
        cached.Paths = paths;
        cached.States = states;
        cached.View = Build(ids, scope);
        return cached.View;
    }

    /// <summary>Builds the view of lists in order. The first list with a name gives the entry.</summary>
    private ListView? Build(IReadOnlyList<string> ids, string? scope)
    {
        var definitions = ids.Select(id => rules.Lists.GetValueOrDefault(id)).OfType<ListDefinition>().ToList();
        if (definitions.Count == 0)
            return null;
        var entries = new List<ListEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var available = false;
        var closed = true;
        var collisions = new List<IReadOnlyList<string>>();
        foreach (var definition in definitions)
        {
            var one = BuildOne(definition, scope);
            if (one == null)
                continue;
            available = true;
            closed &= one.Value.Closed;
            collisions.AddRange(one.Value.Collisions);
            foreach (var entry in one.Value.Entries)
            {
                if (seen.Add(entry.Value))
                    entries.Add(entry);
            }
        }
        if (!available)
            return null;
        var first = definitions[0];
        // The values of a files list compare as paths below the data folder, as the engine finds the files.
        Func<string, string?>? keyOf = definitions.Any(d => d.Source?.Files != null) ? ThnReferences.PathOf : null;
        return new ListView(definitions.Count == 1 ? first.Id : string.Join("+", definitions.Select(d => d.Id)),
            definitions.Count == 1 ? first.Label : string.Join(" or ", definitions.Select(d => d.Label)), first.Kind,
            closed, definitions.All(d => d.Enum != null), entries, collisions, keyOf);
    }

    /// <summary>The entries of one list for a scope, or null when the list is not available.</summary>
    private (IReadOnlyList<ListEntry> Entries, bool Closed, IReadOnlyList<IReadOnlyList<string>> Collisions)? BuildOne(
        ListDefinition definition, string? scope)
    {
        if (definition.Enum != null)
        {
            var type = typeof(FLHash).Assembly.GetType(definition.Enum);
            return type is { IsEnum: true } ? (ListView.Of(type).Entries, true, []) : null;
        }
        if (definition.Source is not { } source)
            return null;
        if (source.Files != null)
            return (FileEntries(source), true, []);
        IReadOnlyList<string> paths = source.Scoped ? ScopeFiles(scope) : Files.Paths(source.Role!);
        var order = new List<string>();
        var byName = new Dictionary<string, (ListEntry Entry, int Count, bool AnyChanged)>(StringComparer.OrdinalIgnoreCase);
        var available = false;
        foreach (var path in paths)
        {
            if (TableOf(path) is not { } table)
                continue;
            available = true;
            var types = source.Values is { } values ? [values.Section] : source.Sections;
            foreach (var type in types)
            {
                foreach (var index in table.OfType(type))
                {
                    var section = table.Working[index];
                    if (!Matches(section, source.Where))
                        continue;
                    foreach (var name in Names(section, source))
                    {
                        var entry = Entry(table, index, name, definition);
                        // Only a repeated name of a unique list needs to know if the active mod changed a section.
                        var changed = definition.Unique && ChangedByActiveMod(table, index);
                        if (byName.TryGetValue(name, out var known))
                        {
                            // The last section with the name gives the entry, as in the engine. A values list keeps the
                            // first section, because each section only shares the value.
                            byName[name] = (source.Values == null ? entry : known.Entry, known.Count + 1,
                                known.AnyChanged || changed);
                        }
                        else
                        {
                            byName[name] = (entry, 1, changed);
                            order.Add(name);
                        }
                    }
                }
            }
        }
        if (!available)
            return null;
        var entries = order.Select(n =>
        {
            var (entry, count, anyChanged) = byName[n];
            return definition.Unique && count > 1
                ? entry with
                {
                    Usable = false,
                    Problem = $"{count} sections have the name '{entry.Value}', so the game finds none of them.",
                    UnusableByActiveMod = anyChanged,
                }
                : entry;
        }).ToList();
        var collisions = definition.Hash ? Collisions(order) : [];
        var closed = source.Scoped || source.Role == null || Files.Engine(source.Role);
        return (entries, closed, collisions);
    }

    /// <summary>
    /// The entries of a files list (section 5.6 of the M8b design). They are the files of the edit view with the
    /// extension, in the folder and of the class of the list (milestone M22). Each new cutscene of W is an entry too. The
    /// value of an entry is its path below the data folder, in the letter case of the file, as an INI line names it. The
    /// name is the file name without the extension. A new cutscene whose New cutscene an undo took back is not an entry,
    /// because the next save deletes its file.
    /// </summary>
    private IReadOnlyList<ListEntry> FileEntries(ListSource source)
    {
        var extensions = source.Files!;
        var view = session.EditView;
        var key = (string.Join("|", extensions).ToLowerInvariant(), source.Folder?.ToLowerInvariant(), source.FileClass);
        if (!editViewFiles.TryGetValue(key, out var known) || !ReferenceEquals(known.View, view))
        {
            known = (view, FindFiles(view.CreateFileSystem(), source).ToList());
            editViewFiles[key] = known;
        }

        var cutscenes = extensions.Contains(".thn", StringComparer.OrdinalIgnoreCase);
        var absent = cutscenes
            ? session.NewCutsceneDocuments.Where(d => !d.State.Exists).Select(d => d.Path).ToHashSet(VfsPath.Comparer)
            : [];
        var paths = known.Paths.Where(p => !absent.Contains(p)).ToList();
        if (cutscenes)
        {
            var inView = known.Paths.ToHashSet(VfsPath.Comparer);
            paths.AddRange(session.NewCutscenes.Where(p => !inView.Contains(p)));
        }

        var entries = new List<ListEntry>(paths.Count);
        foreach (var path in paths)
        {
            if (ThnReferences.IniValueOf(path) is not { } value)
                continue;
            var (origin, mod, unsaved) = session.FileOrigin(path);
            entries.Add(new ListEntry(value, null, origin, mod, path)
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Unsaved = unsaved,
            });
        }
        return entries;
    }

    /// <summary>
    /// The install paths of the files of a files list source in a view. These are the files with an extension of the
    /// source below the folder of the source, or below the install root without a folder. With a file class, only the
    /// files that have a section and whose section types are all sections of the class. So the texture files of
    /// <c>SOLAR\NEBULA</c> are not nebula files.
    /// </summary>
    private IEnumerable<string> FindFiles(FileSystem view, ListSource source)
    {
        var folder = source.Folder == null ? "" : ThnReferences.PathOf(source.Folder) ?? "";
        var type = source.FileClassType;
        foreach (var path in VfsFiles.Find(view, source.Files!, folder))
        {
            if (type == null)
            {
                yield return path;
                continue;
            }
            IReadOnlyList<Section>? sections;
            try
            {
                sections = session.EditViewSections(path);
            }
            catch (Exception)
            {
                // A file that the parser cannot read is no entry. The engine cannot read it either.
                continue;
            }
            if (sections is { Count: > 0 } && sections.All(s => SchemaCatalog.Section(type, s.Name) != null))
                yield return path;
        }
    }

    /// <summary>
    /// True when the file that a value names exists in the edit view. The value is a path below the data folder, as a
    /// <c>file</c> line of a <c>[Nebula]</c> names it. A rule with the flag exists accepts such a value outside its list
    /// (see <see cref="ListRule.Exists"/>).
    /// </summary>
    public bool FileExists(string value)
    {
        if (value.Trim() is not { Length: > 0 } text || ThnReferences.PathOf(text) is not { } path)
            return false;
        var view = session.EditView;
        if (editViewSystem is not { } known || !ReferenceEquals(known.View, view))
        {
            known = (view, view.CreateFileSystem());
            editViewSystem = known;
        }
        return known.Files.FileExists(path);
    }

    /// <summary>The entry of a name of a section.</summary>
    private ListEntry Entry(FileTable table, int index, string name, ListDefinition definition)
    {
        var section = table.Working[index];
        var id = table.Ids[index];
        var keyName = SectionKeys.For(section.Name).KeyEntry ?? SectionKeys.DefaultKey;
        var (origin, mod, inW, unsaved) = session.EntryOrigin(table, index, keyName);
        var missing = definition.Requires.Where(k => !section.Any(e => Is(e.Name, k) && e.Count > 0)).ToList();
        return new ListEntry(name, inW ? new SectionRef(table.Path, id) : null, origin, mod, Detail(section, definition))
        {
            Usable = missing.Count == 0,
            Problem = missing.Count == 0 ? null : $"The [{section.Name}] '{name}' has no '{missing[0]}' line.",
            Unsaved = unsaved,
            UnusableByActiveMod = missing.Count > 0 && ChangedByActiveMod(table, index),
        };
    }

    /// <summary>The detail text of an entry: the first line of each detail key, as "key = values".</summary>
    private static string Detail(Section section, ListDefinition definition)
    {
        var text = new StringBuilder();
        foreach (var key in definition.Detail)
        {
            if (section.FirstOrDefault(e => Is(e.Name, key)) is not { } entry)
                continue;
            if (text.Length > 0)
                text.Append(", ");
            text.Append(entry.Name).Append(" = ").Append(string.Join(", ", entry.Select(v => v.ToString())));
        }
        return text.ToString();
    }

    /// <summary>The groups of distinct names with one FLHash. The engine compares the names without letter
    /// case.</summary>
    private static List<IReadOnlyList<string>> Collisions(IEnumerable<string> names) =>
        names.Where(n => n.Length > 0)
            .GroupBy(FLHash.CreateID)
            .Select(g => (IReadOnlyList<string>)g.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            .Where(g => g.Count > 1)
            .ToList();

    /// <summary>True when the section has each key value of a filter: the first value of the last line of the key,
    /// without letter case.</summary>
    private static bool Matches(Section section, IReadOnlyDictionary<string, string> where)
    {
        foreach (var (key, value) in where)
        {
            if (section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0) is not { } line ||
                !Is(line[0].ToString().Trim(), value))
                return false;
        }
        return true;
    }

    /// <summary>The names that a section gives to a list: the first value of the last line of each name key, or the
    /// value of a values list.</summary>
    internal static IEnumerable<string> Names(Section section, ListSource source)
    {
        if (source.Values is { } values)
        {
            foreach (var line in section.Where(e => Is(e.Name, values.Key)))
            {
                if (line.Count > values.Index && line[values.Index].ToString().Trim() is { Length: > 0 } value)
                    yield return value;
            }
            yield break;
        }
        var keys = source.Keys ?? [SectionKeys.For(section.Name).KeyEntry ?? SectionKeys.DefaultKey];
        foreach (var key in keys)
        {
            if (section.LastOrDefault(e => Is(e.Name, key) && e.Count > 0) is { } line &&
                line[0].ToString().Trim() is { Length: > 0 } name)
                yield return name;
        }
    }

    /// <summary>The sections of a file for the lists: W, or the edit view of a whole file of the active mod that the
    /// editor shows read-only. Null when the edit view has no such INI file.</summary>
    internal FileTable? TableOf(string path)
    {
        if (session.ActiveMod is { } active && active.Files.ContainsKey(path) && !EditDocument.EditsWholeFile(path))
            return WholeFile(path);
        return session.Table.File(path);
    }

    /// <summary>The object that changes when the sections of a file for the lists change.</summary>
    private object? StateOf(string path) => TableOf(path);

    /// <summary>A whole file of the active mod as the edit view gives it. The edit view changes only at a save.</summary>
    private FileTable? WholeFile(string path)
    {
        var view = session.EditView;
        if (wholeFiles.TryGetValue(path, out var known) && ReferenceEquals(known.View, view))
            return known.Table;
        var sections = session.EditViewSections(path);
        var table = sections == null ? null : new FileTable(VfsPath.Normalize(path) ?? path, sections);
        wholeFiles[path] = (view, table);
        return table;
    }

    private (IReadOnlyList<RemovedEntry> Entries, Dictionary<(string, string, string), RemovedEntry> Keys) RemovedState()
    {
        if (removed is { } known && known.Revision == session.CacheRevision)
            return (known.Entries, known.Keys);
        var entries = new List<RemovedEntry>();
        if (session.ActiveMod is { } active)
        {
            foreach (var path in session.ActiveChangedPaths())
            {
                if (session.OpenDocument(path) is not { } document || TableOf(document.Path) is not { } now)
                    continue;
                var modPath = active.Patches.GetValueOrDefault(document.Path) ??
                              active.Files.GetValueOrDefault(document.Path) ?? document.Path;
                foreach (var (definition, name, lower) in RemovedPartOf(document, now).Names)
                {
                    // A name that another file of the list still gives is not removed.
                    if (definition.Source!.Scoped || View([definition.Id], null)?.Find(name) == null)
                        entries.Add(new RemovedEntry(definition.Id, name, lower, modPath));
                }
            }
        }
        var keys = new Dictionary<(string, string, string), RemovedEntry>();
        foreach (var e in entries)
            keys.TryAdd((e.List.ToLowerInvariant(), IsScoped(e.List) ? e.LowerSection.Path.ToLowerInvariant() : "",
                e.Value.ToLowerInvariant()), e);
        removed = (session.CacheRevision, entries, keys);
        return (entries, keys);
    }

    /// <summary>
    /// The names of each list that B of a file gives and the sections of the file for the lists do not give. The part is
    /// built again only when one of these states changes, or when the file becomes or stops being a system file. So a
    /// new revision reads again only the files that changed (review finding R7).
    /// </summary>
    private RemovedPart RemovedPartOf(EditDocument document, FileTable now)
    {
        var isSystem = session.IsSystemFile(document.Path);
        var roles = Files.RolesOf(document.Path);
        if (removedParts.TryGetValue(document.Path, out var part) && ReferenceEquals(part.Now, now) &&
            ReferenceEquals(part.Base, document.Base) && part.IsSystem == isSystem)
            return part;
        var lower = new FileTable(document.Path, document.Base);
        var names = new List<(ListDefinition, string, SectionRef)>();
        foreach (var definition in rules.Lists.Values)
        {
            // A files list has no sections, so an INI file removes none of its entries.
            if (definition.Source is not { Values: null, Files: null } source || !GivesEntries(source, roles, isSystem))
                continue;
            var current = NamesOf(now, source);
            foreach (var (name, index) in NamesWithIndex(lower, source))
            {
                if (!current.Contains(name))
                    names.Add((definition, name, lower.Ref(index)));
            }
        }
        part = new RemovedPart(now, document.Base, isSystem, names);
        removedParts[document.Path] = part;
        return part;
    }

    private static HashSet<string> NamesOf(FileTable table, ListSource source) =>
        NamesWithIndex(table, source).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<(string Name, int Index)> NamesWithIndex(FileTable table, ListSource source)
    {
        foreach (var type in source.Sections)
        {
            foreach (var index in table.OfType(type))
            {
                if (!Matches(table.Working[index], source.Where))
                    continue;
                foreach (var name in Names(table.Working[index], source))
                    yield return (name, index);
            }
        }
    }

    /// <summary>
    /// The merged reverse index of a list, built from the parts of its referrer files. The files can stay the same
    /// while one part changes. Then only the values that the part names, or named, get their references again, in the
    /// order of the files. So a create in a large system does not merge the parts of all files again. A batch of
    /// creates then runs in one frame of the editor (section 13.4 of the MCP1 design).
    /// </summary>
    private Dictionary<(string? Scope, string Value), List<ListReference>> Merged(string list)
    {
        if (!merged.TryGetValue(list, out var cached))
            merged[list] = cached = new MergedReferences();
        if (cached.Revision == session.CacheRevision)
            return cached.Map;
        cached.Revision = session.CacheRevision;
        var roles = rules.All.Where(r => r.Lists.Contains(list, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Role).Distinct(StringComparer.OrdinalIgnoreCase);
        var paths = roles.SelectMany(Files.Paths).Distinct(VfsPath.Comparer).ToList();
        var fileParts = paths.Select(Part).ToArray();
        var lower = list.ToLowerInvariant();
        if (cached.Parts != null && cached.Paths.SequenceEqual(paths, VfsPath.Comparer))
        {
            var changed = Enumerable.Range(0, fileParts.Length)
                .Where(i => !ReferenceEquals(cached.Parts[i], fileParts[i])).ToList();
            if (changed.Count == 0)
                return cached.Map;
            if (changed.Count == 1)
            {
                var at = changed[0];
                var keys = fileParts[at] is { FromId: > 0 } made && made.FromId == cached.Parts[at]?.Id
                    ? made.ChangedKeys.Where(k => k.List == lower).Select(k => (k.Scope, k.Value)).ToList()
                    : ChangedKeys(lower, cached.Parts[at], fileParts[at]);
                cached.Parts = fileParts;
                foreach (var key in keys)
                {
                    var references = fileParts.OfType<ReferencePart>()
                        .SelectMany(p => p.Map.GetValueOrDefault((lower, key.Scope, key.Value)) ?? []).ToList();
                    if (references.Count == 0)
                        cached.Map.Remove(key);
                    else
                        cached.Map[key] = references;
                }
                return cached.Map;
            }
        }
        cached.Parts = fileParts;
        cached.Paths = paths;
        var map = new Dictionary<(string?, string), List<ListReference>>();
        foreach (var part in fileParts.OfType<ReferencePart>())
        {
            foreach (var ((id, scope, value), references) in part.Map)
            {
                if (id != lower)
                    continue;
                if (!map.TryGetValue((scope, value), out var all))
                    map[(scope, value)] = all = [];
                all.AddRange(references);
            }
        }
        cached.Map = map;
        return map;
    }

    /// <summary>The keys of a list whose references differ between the old and the new part of one file.</summary>
    /// <param name="list">The id of the list in lowercase.</param>
    private static List<(string? Scope, string Value)> ChangedKeys(string list, ReferencePart? old, ReferencePart? made)
    {
        var keys = new List<(string?, string)>();
        foreach (var ((id, scope, value), references) in old?.Map ?? [])
        {
            if (id == list && (made?.Map.GetValueOrDefault((id, scope, value)) is not { } now ||
                               !references.SequenceEqual(now)))
                keys.Add((scope, value));
        }
        if (made == null)
            return keys;
        foreach (var (id, scope, value) in made.Map.Keys)
        {
            if (id == list && old?.Map.ContainsKey((id, scope, value)) != true)
                keys.Add((scope, value));
        }
        return keys;
    }

    /// <summary>The reverse index part of one referrer file, built again only when W of the file changes, or when W of
    /// universe.ini changes for a file with <c>systemOf</c> references. A whole file of the active mod gives the
    /// lines of the edit view, as for the lists. A reference of such a file names a section that W does not have, so
    /// it gives no check result: the editor cannot change a whole file (review finding R22).</summary>
    private ReferencePart? Part(string path)
    {
        var table = TableOf(path);
        if (table == null)
        {
            parts.Remove(path);
            return null;
        }
        var universe = session.UniversePath is { } u ? session.Table.File(u) : null;
        var owners = Files.FieldFilesState;
        if (parts.TryGetValue(path, out var part) && ReferenceEquals(part.Table, table) &&
            (!part.UsesSystems || ReferenceEquals(part.Universe, universe)) &&
            (!part.UsesOwners || ReferenceEquals(part.Owners, owners)))
            return part;
        part = BuildPart(table, universe, owners, part);
        parts[path] = part;
        return part;
    }

    /// <param name="owners">The state of the nebula and field files when the part is built (milestone M3). A part with
    /// <c>owner</c> references is built again when it changes.</param>
    /// <param name="old">The part of an older W state of the file, or null. The old state can have a section at the same
    /// place with the same identity. While universe.ini and the owners stay the same, that section names the same
    /// values. So it takes its references from the old part, and a create in a large system reads only the new
    /// section.</param>
    private ReferencePart BuildPart(FileTable table, FileTable? universe, object owners, ReferencePart? old)
    {
        var map = new Dictionary<(string, string?, string), List<ListReference>>();
        var usesSystems = false;
        var usesOwners = false;
        var roles = Files.RolesOf(table.Path);
        var reuse = old != null && ReferenceEquals(old.Universe, universe) && ReferenceEquals(old.Owners, owners);
        // The rules of each section type, by key. A system file has hundreds of sections of a few types.
        var rulesOfType = new Dictionary<string, Dictionary<string, List<ListRule>>>(StringComparer.OrdinalIgnoreCase);
        var sections = new SectionReferences[table.Working.Count];
        var kept = new bool[reuse ? old!.Sections.Length : 0];
        var changed = new HashSet<(string, string?, string)>();
        for (var i = 0; i < table.Working.Count; i++)
        {
            var section = table.Working[i];
            SectionReferences own;
            if (reuse && i < old!.Sections.Length && ReferenceEquals(old.Table.Working[i], section) &&
                old.Table.Ids[i].Equals(table.Ids[i]))
            {
                own = old.Sections[i];
                kept[i] = true;
            }
            else
            {
                if (!rulesOfType.TryGetValue(section.Name, out var byKey))
                {
                    rulesOfType[section.Name] = byKey = roles.SelectMany(r => rules.ByRoleAndSection.GetValueOrDefault((
                            r.ToLowerInvariant(), section.Name.ToLowerInvariant())) ?? [])
                        .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                }
                own = byKey.Count == 0 ? SectionReferences.None : References(table, i, byKey);
                changed.UnionWith(own.References.Select(r => r.Key));
            }
            sections[i] = own;
            usesSystems |= own.UsesSystems;
            usesOwners |= own.UsesOwners;
            foreach (var (key, reference) in own.References)
            {
                if (!map.TryGetValue(key, out var all))
                    map[key] = all = [];
                all.Add(reference);
            }
        }
        for (var i = 0; i < kept.Length; i++)
        {
            if (!kept[i])
                changed.UnionWith(old!.Sections[i].References.Select(r => r.Key));
        }
        return new ReferencePart(table, universe, usesSystems, map, sections)
        {
            Owners = owners,
            UsesOwners = usesOwners,
            Id = ++partIds,
            FromId = reuse ? old!.Id : 0,
            ChangedKeys = changed,
        };
    }

    /// <summary>The line values of one section that name entries of lists, with the keys of the reverse index.</summary>
    /// <param name="byKey">The rules of the type of the section, by key.</param>
    private SectionReferences References(FileTable table, int index, Dictionary<string, List<ListRule>> byKey)
    {
        var result = new List<((string, string?, string) Key, ListReference Reference)>();
        var usesSystems = false;
        var usesOwners = false;
        var target = table.Ref(index);
        var lineOfKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in table.Working[index])
        {
            if (!byKey.TryGetValue(entry.Name, out var keyRules))
                continue;
            var line = lineOfKey.GetValueOrDefault(entry.Name);
            lineOfKey[entry.Name] = line + 1;
            foreach (var rule in keyRules)
            {
                if (rule.IndexIn(entry) is not (>= 0 and var at) ||
                    entry[at].ToString().Trim() is not { Length: > 0 } value)
                    continue;
                usesSystems |= rule.SystemValue >= 0;
                usesOwners |= rule.OwnerScope;
                if (!ScopeOf(rule, table.Path, entry.ToList(), out var scope))
                    continue;
                var reference = new ListReference(target, entry.Name, line, at, rule);
                // A line of a file with more than one owner names a zone of each owner.
                var files = scope == null ? new string?[] { null } : ScopeFiles(scope);
                foreach (var list in rule.Lists)
                {
                    foreach (var file in files)
                        result.Add(((list.ToLowerInvariant(), file?.ToLowerInvariant(), ValueKey(list, value)), reference));
                }
            }
        }
        return new SectionReferences(result, usesSystems, usesOwners);
    }

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A cached view and the states of its files when it was built.</summary>
    private sealed class CachedView
    {
        public int Revision = int.MinValue;
        public bool Built;
        public List<string> Paths = [];
        public object?[] States = [];
        public ListView? View;
    }

    /// <summary>The line values of one section that name entries of lists, with the keys of the reverse index.</summary>
    /// <param name="UsesSystems">True when a line of the section has a <c>systemOf</c> scope.</param>
    /// <param name="UsesOwners">True when a line of the section has an <c>owner</c> scope.</param>
    private sealed record SectionReferences(
        IReadOnlyList<((string List, string? Scope, string Value) Key, ListReference Reference)> References,
        bool UsesSystems, bool UsesOwners)
    {
        /// <summary>A section without references.</summary>
        public static readonly SectionReferences None = new([], false, false);
    }

    private sealed class MergedReferences
    {
        public int Revision = int.MinValue;
        public ReferencePart?[]? Parts;

        /// <summary>The referrer files of the list when the map was merged, in the order of <see cref="Parts"/>.</summary>
        public List<string> Paths = [];

        public Dictionary<(string? Scope, string Value), List<ListReference>> Map = [];
    }

    /// <param name="Now">The sections of the file for the lists when the part was built.</param>
    /// <param name="Base">B of the file when the part was built.</param>
    /// <param name="IsSystem">True when the file was a system file when the part was built.</param>
    /// <param name="Names">The names that B gives and <paramref name="Now"/> does not, with their lists and their
    /// sections in B.</param>
    private sealed record RemovedPart(FileTable Now, IReadOnlyList<Section> Base, bool IsSystem,
        IReadOnlyList<(ListDefinition Definition, string Name, SectionRef Lower)> Names);

    /// <param name="Universe">The table of universe.ini when the part was built.</param>
    /// <param name="UsesSystems">True when a line of the file has a <c>systemOf</c> scope, so the part depends on
    /// universe.ini.</param>
    /// <param name="Sections">The references of each section of the table, by index.</param>
    private sealed record ReferencePart(FileTable Table, FileTable? Universe, bool UsesSystems,
        Dictionary<(string List, string? Scope, string Value), List<ListReference>> Map, SectionReferences[] Sections)
    {
        /// <summary>The state of the nebula and field files when the part was built.</summary>
        public object? Owners { get; init; }

        /// <summary>True when a line of the file has an <c>owner</c> scope, so the part depends on the owners of the
        /// file.</summary>
        public bool UsesOwners { get; init; }

        /// <summary>A number of the part that no other part of the session has.</summary>
        public long Id { get; init; }

        /// <summary>The <see cref="Id"/> of the part of the older W state that this part reused, or 0. The part keeps
        /// the number, not the old part, so the old parts do not stay in memory.</summary>
        public long FromId { get; init; }

        /// <summary>The keys whose references can differ from the part of <see cref="FromId"/>: the keys of each
        /// section that the part did not reuse.</summary>
        public IReadOnlySet<(string List, string? Scope, string Value)> ChangedKeys { get; init; } =
            new HashSet<(string, string?, string)>();
    }
}
