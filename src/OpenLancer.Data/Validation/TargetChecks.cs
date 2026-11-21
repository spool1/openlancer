using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Missions;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;

namespace OpenLancer.Data.Validation;

/// <summary>
/// The sections of the INI files that the target checks read: the files without the mods, and the files with them.
/// </summary>
internal abstract class CheckView
{
    /// <summary>The install paths of the INI files that the mods change or add.</summary>
    public abstract IReadOnlyCollection<string> ChangedPaths { get; }

    /// <summary>The sections of a file without the mods, or with them, or null when the file does not exist.</summary>
    /// <param name="path">An install path in the normal form of <see cref="VfsPath"/>.</param>
    public abstract IReadOnlyList<Section>? Sections(string path, bool withMods);

    /// <summary>The names of the folders in a folder, with the mods.</summary>
    public abstract IReadOnlyList<string> Folders(string path);

    /// <summary>The sections of a file of a file system, or null when it does not exist or does not parse.</summary>
    internal static IReadOnlyList<Section>? Read(FileSystem fs, string path)
    {
        if (!fs.FileExists(path))
            return null;
        try
        {
            return IniDocument.Parse(path, fs.ReadAllBytes(path)).Sections;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The merge and the validation report a file that does not parse.
            return null;
        }
    }
}

/// <summary>
/// The content checks of a retail target (milestone M11, publishing plan E6). They find data that Librelancer loads, but
/// that the retail game does not accept or does not read. Each check compares the data with the mods with the data
/// without them, so it reports only what the mods add. Each error says what the game does with the data.
/// </summary>
/// <remarks>
/// <para>The crash checks follow the crash list of The Starport knowledge base, read again on 2026-10-19. The game
/// crashes with this data:</para>
/// <list type="bullet">
/// <item>more than 10 jump holes and jump gates in one system,</item>
/// <item>a good with the price 0,</item>
/// <item>a system without solar objects,</item>
/// <item>a munition that needs ammo, when lootprops.ini has no entry for the ammo or for its gun,</item>
/// <item>a mission trigger without a condition,</item>
/// <item>an <c>[Asteroids]</c> section that names no zone of its system,</item>
/// <item>an NPC loadout without an engine,</item>
/// <item>an invalid boolean in <c>requires_ammo</c> or <c>force_gun_ori</c>.</item>
/// </list>
/// <para>The retail data itself has two such cases. The cutscene missile <c>missile01_mark01_rtc</c> and its ammo have
/// no lootprops.ini entry. Some capital ships of the story missions have loadouts without an engine. The game uses them
/// only in scripted scenes. The checks report only what the mods add, so these cases give no finding.</para>
/// <para>Retail reads only the <c>[Data]</c> keys that its loaders know, and ignores the others. The keys
/// <c>newplayer</c>, <c>mpnewcharacter</c>, <c>mbases</c>, <c>navmap</c>, <c>mouse</c> and <c>cameras</c>, the
/// <c>[Extended]</c> section and <c>cruise_speed</c> of an <c>[Engine]</c> are Librelancer extensions.</para>
/// <para>The text boolean getter of retail reads <c>true</c>, <c>false</c>, <c>1</c> and <c>0</c> in any letter case.
/// For another value it logs an error and gives false. Some keys do not use the getter. Retail reads
/// <c>allow_simultaneous_creation</c> as the lowercase words <c>yes</c>, <c>true</c>, <c>no</c> and <c>false</c>, and the
/// retail encounter files write <c>yes</c>.</para>
/// </remarks>
public static partial class TargetChecks
{
    /// <summary>The most jump holes and jump gates that one system can have.</summary>
    public const int MaxJumpObjects = 10;

    /// <summary>The line of the README and of <c>oltool validate --target</c> for a mod that changes gameplay
    /// data.</summary>
    public const string GameplayNote = "Single player, or a server with the same mod.";

    /// <summary>The <c>[Data]</c> keys that the retail client and server read, in lowercase. Retail ignores each
    /// other key.</summary>
    private static readonly HashSet<string> RetailDataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "solar", "universe", "constants", "equipment", "ships", "asteroids", "explosions", "loadouts", "bodyparts",
        "costumes", "bases", "goods", "markets", "petaldb", "groups", "fuses", "weaponmoddb", "newchardb", "rtcslider",
        "fonts", "rich_fonts", "sounds", "voices", "effects", "effect_shapes", "debris", "intro", "hud", "igraph",
        "gate_tunnels", "jump_effect", "fonts_dir", "concave", "stars",
    };

    /// <summary>The boolean keys that retail reads as words, not with its boolean getter, and the words that it reads.
    /// The compare uses the letter case. For another word, retail keeps the default value.</summary>
    private static readonly Dictionary<string, string[]> WordBooleans = new(StringComparer.OrdinalIgnoreCase)
    {
        // [EncounterFormation] of the encounter files. Retail files use yes.
        ["allow_simultaneous_creation"] = ["yes", "true", "no", "false"],
    };

    /// <summary>The boolean keys whose invalid values crash the game, by the crash list.</summary>
    private static readonly HashSet<string> CrashBooleans = new(StringComparer.OrdinalIgnoreCase)
    {
        "requires_ammo", "force_gun_ori",
    };

    /// <summary>A number with a trailing <c>d</c>, <c>b</c> or <c>h</c>, which the retail float getter reads in
    /// another base.</summary>
    [GeneratedRegex(@"^[+-]?[0-9][0-9A-Fa-f]*[dbh]$")]
    private static partial Regex SuffixNumber();

    /// <summary>Runs the checks on the files of a target build.</summary>
    public static IReadOnlyList<Diagnostic> Run(TargetFiles files) => Run(new TargetFilesView(files));

    /// <summary>
    /// The warnings of a target build on HD Edition 0.7.1. Each file in which the mods change or remove lines that HD
    /// Edition changed gets one warning. A line of HD Edition can be a fix, which the mod then undoes. The check compares
    /// each such line of the HD Edition file with the line of the retail file.
    /// </summary>
    /// <param name="retail">The files of a Freelancer 1.0 or 1.1 game.</param>
    public static IReadOnlyList<Diagnostic> HdEditionLines(TargetFiles files, GameBase retail) =>
        HdLines(files, retail.OpenFileSystem()).ToList();

    /// <summary>True when the mods change or add an INI file under <c>DATA</c>. Then the client and the server need the
    /// same mod.</summary>
    public static bool ChangesGameplay(TargetFiles files) =>
        files.Files.Any(f => f.Kind == TargetFileKind.Ini && f.Path.StartsWith(@"DATA\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Runs the checks on a view, and gives the findings that the data with the mods adds.</summary>
    internal static IReadOnlyList<Diagnostic> Run(CheckView view)
    {
        var changed = view.ChangedPaths.Select(p => VfsPath.Normalize(p) ?? p).ToHashSet(VfsPath.Comparer);
        if (changed.Count == 0)
            return [];
        var without = new Side(view, false);
        var with = new Side(view, true);
        var scope = new Scope(view, changed, without, with);
        var known = Findings(without, scope).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return Findings(with, scope).Where(f => !known.Contains(f.Key)).Select(f => f.Problem).ToList();
    }

    private static IEnumerable<Finding> Findings(Side side, Scope scope) =>
        Systems(side, scope)
            .Concat(Prices(side, scope))
            .Concat(Ammo(side, scope))
            .Concat(Triggers(side, scope))
            .Concat(NpcEngines(side, scope))
            .Concat(LibrelancerKeys(side, scope))
            .Concat(Booleans(side, scope));

    /// <summary>The jump objects, the solar objects and the asteroid zones of each system that the mods can
    /// change.</summary>
    private static IEnumerable<Finding> Systems(Side side, Scope scope)
    {
        foreach (var nickname in scope.Systems)
        {
            if (side.SystemPath(nickname) is not { } path || side.Sections(path) is not { } sections)
                continue;
            var label = $"System '{nickname}'";
            var key = nickname.ToLowerInvariant();
            var objects = sections.Where(s => Is(s.Name, "Object")).ToList();
            var jumps = objects.Count(o => ItemIndex.First(o, "goto") != null);
            if (jumps > MaxJumpObjects)
                yield return new($"jumps|{key}", Diagnostic.Error(path,
                    $"{label} has {jumps} jump holes and jump gates. The game crashes with more than {MaxJumpObjects} " +
                    $"in one system. Remove {jumps - MaxJumpObjects} of them."));
            if (objects.Count == 0)
                yield return new($"objects|{key}", Diagnostic.Error(path,
                    $"{label} has no solar objects. The game can crash when it loads the system. Add an object, for " +
                    "example a sun or a planet."));

            var zones = sections.Where(s => Is(s.Name, "Zone")).Select(s => ItemIndex.First(s, "nickname"))
                .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ids = SectionIdentity.Identify(sections);
            for (var i = 0; i < sections.Count; i++)
            {
                if (!Is(sections[i].Name, "Asteroids"))
                    continue;
                var zone = ItemIndex.First(sections[i], "zone");
                if (zone != null && zones.Contains(zone))
                    continue;
                var what = zone == null ? "has no zone line" : $"names the zone '{zone}', and the system has no such zone";
                yield return new($"asteroids|{key}|{zone?.ToLowerInvariant()}|{ids[i]}", Problem(true, path, ids[i], "zone",
                    $"{label}: an [Asteroids] section {what}. The game crashes when it loads the system. Pick a zone of " +
                    "the system."));
            }
        }
    }

    /// <summary>The prices of the goods in the goods files that the mods change. A ship package has no price line,
    /// so only a price line counts.</summary>
    private static IEnumerable<Finding> Prices(Side side, Scope scope)
    {
        foreach (var path in scope.Goods)
        {
            foreach (var (section, id) in side.Each(path, "Good"))
            {
                if (section.FirstOrDefault(e => Is(e.Name, "price")) is not { Count: > 0 } line)
                    continue;
                var text = RetailText.Of(line[0]);
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                        ? price > 0 && double.IsFinite(price)
                        : SuffixNumber().IsMatch(text))
                    continue;
                var nickname = ItemIndex.First(section, "nickname") ?? id.ToString();
                yield return new($"price|{nickname.ToLowerInvariant()}", Problem(true, path, id, "price",
                    $"Good '{nickname}': the price is {text}. The game can crash when a player buys or sells the good. " +
                    "Write a price above 0."));
            }
        }
    }

    /// <summary>The lootprops.ini entries of each munition that needs ammo, and of each gun that fires it.</summary>
    private static IEnumerable<Finding> Ammo(Side side, Scope scope)
    {
        if (!scope.Equipment)
            yield break;
        var munitions = new List<(string Path, SectionIdentity Id, string Nickname)>();
        var guns = new List<(string Gun, string Munition)>();
        foreach (var path in side.DataFiles("equipment"))
        {
            foreach (var (section, id) in side.Each(path, "Munition", "Gun"))
            {
                if (ItemIndex.First(section, "nickname") is not { } nickname)
                    continue;
                if (Is(section.Name, "Gun"))
                {
                    if (ItemIndex.First(section, "projectile_archetype") is { } projectile)
                        guns.Add((nickname, projectile));
                }
                else if (section.FirstOrDefault(e => Is(e.Name, "requires_ammo")) is { Count: > 0 } line &&
                         RetailText.Of(line[0]).ToLowerInvariant() is "true" or "1")
                    munitions.Add((path, id, nickname));
            }
        }
        if (munitions.Count == 0)
            yield break;

        var loot = side.LootProps is { } lootPath
            ? side.Each(lootPath, "mLootProps").Select(s => ItemIndex.First(s.Section, "nickname")).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        foreach (var (path, id, munition) in munitions)
        {
            var needs = guns.Where(g => Is(g.Munition, munition)).Select(g => g.Gun).Prepend(munition);
            foreach (var item in needs.Where(n => !loot.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                yield return new($"ammo|{munition.ToLowerInvariant()}|{item.ToLowerInvariant()}", Problem(true, path, id,
                    "requires_ammo",
                    $"Munition '{munition}' has requires_ammo = true, and lootprops.ini has no [mLootProps] for " +
                    $"'{item}'. The game can crash. Add an [mLootProps] section with nickname = {item} to lootprops.ini."));
        }
    }

    /// <summary>The conditions of the mission triggers in the files that the mods change.</summary>
    private static IEnumerable<Finding> Triggers(Side side, Scope scope)
    {
        foreach (var path in scope.Changed)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (section, id) in side.Each(path, "Trigger"))
            {
                // The game skips a trigger without a nickname (TR2) and the second trigger with a name (TR4). A condition
                // line before the nickname does not count (TR2). The mission checks use the same rules (decision D556).
                if (TriggerRules.Nickname(section) is not { } trigger || !seen.Add(trigger) ||
                    TriggerRules.EffectiveCondition(section) >= 0)
                    continue;
                yield return new($"trigger|{path.ToLowerInvariant()}|{id}", Problem(true, path, id, null,
                    TriggerRules.NoConditionMessage(trigger)));
            }
        }
    }

    /// <summary>The engine of the loadout of each NPC ship, when the mods change the equipment, the loadouts or the NPC
    /// ships.</summary>
    private static IEnumerable<Finding> NpcEngines(Side side, Scope scope)
    {
        if (scope.NpcFiles.Count == 0)
            yield break;
        var engines = side.DataFiles("equipment").SelectMany(p => side.Each(p, "Engine"))
            .Select(s => ItemIndex.First(s.Section, "nickname")).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loadouts = new Dictionary<string, Section>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, _) in side.DataFiles("loadouts").SelectMany(p => side.Each(p, "Loadout")))
        {
            if (ItemIndex.First(section, "nickname") is { } nickname)
                loadouts.TryAdd(nickname, section);
        }
        foreach (var path in scope.NpcFiles)
        {
            foreach (var (section, id) in side.Each(path, "NPCShipArch"))
            {
                if (ItemIndex.First(section, "loadout") is not { } name || !loadouts.TryGetValue(name, out var loadout))
                    continue;
                if (loadout.Any(e => Is(e.Name, "equip") && e.Count > 0 && engines.Contains(e[0].ToString()!)))
                    continue;
                var nickname = ItemIndex.First(section, "nickname") ?? id.ToString();
                yield return new($"engine|{path.ToLowerInvariant()}|{nickname.ToLowerInvariant()}", Problem(true, path,
                    id, "loadout",
                    $"NPC ship '{nickname}': its loadout '{name}' has no engine. The game crashes when the ship tries to " +
                    "go into cruise. Add an engine to the loadout."));
            }
        }
    }

    /// <summary>The keys that only Librelancer reads, in the files that the mods change.</summary>
    private static IEnumerable<Finding> LibrelancerKeys(Side side, Scope scope)
    {
        const string onlyOpenLancer = "Only OpenLancer reads it.";
        var freelancerIni = ModOverlay.FreelancerIniPath;
        if (scope.Changed.Contains(freelancerIni) && side.Sections(freelancerIni) is { } sections)
        {
            var ids = SectionIdentity.Identify(sections);
            for (var i = 0; i < sections.Count; i++)
            {
                if (Is(sections[i].Name, "Extended"))
                    yield return new($"extended|{ids[i]}", Problem(true, freelancerIni, ids[i], null,
                        $"[{sections[i].Name}]: the game does not read this section. {onlyOpenLancer}"));
                if (!Is(sections[i].Name, "Data"))
                    continue;
                foreach (var entry in sections[i].Where(e => !RetailDataKeys.Contains(e.Name)))
                {
                    var text = string.Join(", ", entry.Select(RetailText.Of));
                    yield return new($"data|{entry.Name.ToLowerInvariant()}|{text.ToLowerInvariant()}", Problem(true,
                        freelancerIni, ids[i], entry.Name,
                        $"[Data] {entry.Name} = {text}: the game does not read this key. {onlyOpenLancer}"));
                }
            }
        }
        foreach (var path in scope.Equipment ? side.DataFiles("equipment").Where(scope.Changed.Contains) : [])
        {
            foreach (var (section, id) in side.Each(path, "Engine"))
            {
                if (section.Any(e => Is(e.Name, "cruise_speed")))
                    yield return new($"cruise|{path.ToLowerInvariant()}|{id}", Problem(true, path, id, "cruise_speed",
                        $"{id} cruise_speed: the game does not read this key. {onlyOpenLancer}"));
            }
        }
    }

    /// <summary>The values of the boolean keys in the files that the mods change.</summary>
    private static IEnumerable<Finding> Booleans(Side side, Scope scope)
    {
        foreach (var path in scope.Changed)
        {
            if (side.Sections(path) is not { } sections)
                continue;
            var ids = SectionIdentity.Identify(sections);
            for (var i = 0; i < sections.Count; i++)
            {
                foreach (var entry in sections[i].Where(e => e.Count > 0 && IsBooleanKey(sections[i].Name, e.Name)))
                {
                    var text = RetailText.Of(entry[0]);
                    var key = $"bool|{path.ToLowerInvariant()}|{ids[i]}|{entry.Name.ToLowerInvariant()}|{text}";
                    if (WordBooleans.TryGetValue(entry.Name, out var words))
                    {
                        if (!words.Contains(text, StringComparer.Ordinal))
                            yield return new(key, Problem(false, path, ids[i], entry.Name,
                                $"{ids[i]} {entry.Name} = {text}: the game reads only {string.Join(", ", words[..^1])} and {words[^1]} here, " +
                                "in lowercase. It keeps the default value."));
                        continue;
                    }
                    if (TriggerRules.ReadsAsBool(text))
                        continue;
                    var crash = CrashBooleans.Contains(entry.Name);
                    // The mission checks give the same text for rule M14 (decision D556). The prefix names the line,
                    // because the output of oltool shows only the message.
                    yield return new(key, Problem(crash, path, ids[i], entry.Name, crash
                        ? $"{ids[i]} {entry.Name} = {text}: the game reads only true, false, 1 and 0 here. The game can " +
                          "crash with another value."
                        : $"{ids[i]} {entry.Name}: {TriggerRules.BooleanMessage(text)}"));
                }
            }
        }
    }

    /// <summary>The lines that the mods change or remove in an HD Edition file, where the HD Edition file has another
    /// line than the retail file. One warning for each file.</summary>
    private static IEnumerable<Diagnostic> HdLines(TargetFiles files, FileSystem retail)
    {
        foreach (var file in files.Files.Where(f => f is { Kind: TargetFileKind.Ini, IsModFile: false }))
        {
            if (file is not { BaseDocument: { } hd, Diff: { } diff })
                continue;
            var retailSections = CheckView.Read(retail, file.Path);
            var retailIds = retailSections == null ? null : SectionIdentity.Identify(retailSections);
            var hdIds = SectionIdentity.Identify(hd.Sections);
            var lines = new List<string>();
            foreach (var pair in diff.Matched)
            {
                var section = hd.Sections[pair.Base];
                var retailIndex = retailIds == null ? -1 : Array.IndexOf(retailIds, hdIds[pair.Base]);
                var retailSection = retailIndex < 0 ? null : retailSections![retailIndex];
                foreach (var line in pair.Removed.Concat(pair.Changed).Order())
                {
                    if (!SameInRetail(section, line, retailSection))
                        lines.Add($"{hdIds[pair.Base]} {section[line].Name}");
                }
            }
            foreach (var removed in diff.Removed)
            {
                var retailIndex = retailIds == null ? -1 : Array.IndexOf(retailIds, hdIds[removed]);
                if (retailIndex < 0 || !SameSection(hd.Sections[removed], retailSections![retailIndex]))
                    lines.Add(hdIds[removed].ToString());
            }
            if (lines.Count == 0)
                continue;
            const int shown = 10;
            var list = string.Join(", ", lines.Take(shown)) + (lines.Count > shown ? $" and {lines.Count - shown} more" : "");
            var count = lines.Count == 1 ? "1 line" : $"{lines.Count} lines";
            yield return Diagnostic.Warning(file.Path,
                $"The mod changes {count} that HD Edition changed: {list}. Make sure that the mod keeps the fixes of " +
                "HD Edition.");
        }
    }

    /// <summary>True when the retail section has the same line as the n-th line of its name in the HD section.</summary>
    private static bool SameInRetail(Section hd, int line, Section? retail)
    {
        if (retail == null)
            return false;
        var name = hd[line].Name;
        var occurrence = hd.Take(line).Count(e => Is(e.Name, name));
        var match = retail.Where(e => Is(e.Name, name)).Skip(occurrence).FirstOrDefault();
        return match != null && IniSectionDiff.LineKey(match) == IniSectionDiff.LineKey(hd[line]);
    }

    private static bool SameSection(Section a, Section b) =>
        a.Count == b.Count && a.Zip(b).All(p => IniSectionDiff.LineKey(p.First) == IniSectionDiff.LineKey(p.Second));

    /// <summary>True when each engine schema of the section types the key as a boolean.</summary>
    private static bool IsBooleanKey(string section, string key) =>
        BooleanKeys.GetOrAdd((section.ToLowerInvariant(), key.ToLowerInvariant()), k =>
        {
            var types = SchemaCatalog.All
                .Where(s => s.Section.Equals(k.Section, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Schema.Key(k.Key)?.ClrType)
                .Where(t => t != null)
                .ToList();
            return types.Count > 0 && types.All(t => t == typeof(bool));
        });

    /// <summary>The answer of <see cref="IsBooleanKey"/> for each section type and key, in lowercase. The checks read
    /// each line of a changed file, so the cache keeps the answer of each key.</summary>
    private static readonly ConcurrentDictionary<(string Section, string Key), bool> BooleanKeys = new();

    private static Diagnostic Problem(bool error, string path, SectionIdentity id, string? key, string message) =>
        (error ? Diagnostic.Error(path, message) : Diagnostic.Warning(path, message)) with
        {
            Target = new SectionRef(path, id),
            Key = key,
        };

    private static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One finding of a check. The key tells two findings of the same data apart, and is the same without and
    /// with the mods.</summary>
    private readonly record struct Finding(string Key, Diagnostic Problem);

    /// <summary>The data without the mods or with them, and the files that the game reads for it.</summary>
    private sealed class Side(CheckView view, bool withMods)
    {
        private IReadOnlyList<Section>? freelancerIni;
        private Dictionary<string, string?>? systems;

        public IReadOnlyList<Section>? Sections(string path) => view.Sections(path, withMods);

        private IReadOnlyList<Section> FreelancerIni => freelancerIni ??= Sections(ModOverlay.FreelancerIniPath) ?? [];

        /// <summary>The install paths of the files of a <c>[Data]</c> key, in load order.</summary>
        public IReadOnlyList<string> DataFiles(string key) =>
            DataPaths.Entries(FreelancerIni).Where(e => Is(e.Name, key)).Select(e => e.Path)
                .Distinct(VfsPath.Comparer).ToList();

        /// <summary>The install path of a file relative to the data folder, or null.</summary>
        public string? DataPath(string relative) => DataPaths.Resolve(FreelancerIni, relative);

        public string? LootProps => DataPath(@"MISSIONS\lootprops.ini");

        /// <summary>The sections of the types in a file, with their identities.</summary>
        public IEnumerable<(Section Section, SectionIdentity Id)> Each(string path, params string[] types)
        {
            if (Sections(path) is not { } sections)
                yield break;
            var ids = SectionIdentity.Identify(sections);
            for (var i = 0; i < sections.Count; i++)
            {
                if (types.Any(t => Is(sections[i].Name, t)))
                    yield return (sections[i], ids[i]);
            }
        }

        /// <summary>The nickname of each <c>[system]</c> of <c>universe.ini</c>, with the install path of its
        /// file.</summary>
        public IReadOnlyDictionary<string, string?> SystemFiles
        {
            get
            {
                if (systems != null)
                    return systems;
                systems = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (Editing.SystemFiles.ReadUniversePath(FreelancerIni) is not { } universe)
                    return systems;
                var folder = VfsPath.Parent(universe);
                foreach (var (section, _) in Each(universe, "system"))
                {
                    if (ItemIndex.First(section, "nickname") is not { } nickname)
                        continue;
                    // The engine reads the last file line.
                    var file = section.LastOrDefault(e => Is(e.Name, "file") && e.Count > 0)?[0].ToString();
                    systems[nickname] = file == null ? null : VfsPath.Normalize($@"{folder}\{file}");
                }
                return systems;
            }
        }

        public string? SystemPath(string nickname) => SystemFiles.GetValueOrDefault(nickname);
    }

    /// <summary>The files and systems that the checks read: the ones that the mods can change. Both sides read the same
    /// ones, so that their findings compare.</summary>
    private sealed class Scope
    {
        public Scope(CheckView view, HashSet<string> changed, Side without, Side with)
        {
            Changed = changed;
            Systems = without.SystemFiles.Keys.Union(with.SystemFiles.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(n => without.SystemPath(n) is var a && with.SystemPath(n) is var b &&
                            (a != null && changed.Contains(a) || b != null && changed.Contains(b) ||
                             !VfsPath.Comparer.Equals(a, b)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Goods = Both(without, with, s => s.DataFiles("goods")).Where(changed.Contains).ToList();

            var equipment = Both(without, with, s => s.DataFiles("equipment"));
            var loot = Both(without, with, s => s.LootProps is { } p ? [p] : []);
            Equipment = equipment.Concat(loot).Any(changed.Contains);

            var npcFiles = FindNpcFiles(view, changed, without, with);
            var loadouts = Both(without, with, s => s.DataFiles("loadouts"));
            NpcFiles = equipment.Concat(loadouts).Concat(npcFiles).Any(changed.Contains) ? npcFiles : [];
        }

        /// <summary>The INI files that the mods change or add.</summary>
        public HashSet<string> Changed { get; }

        /// <summary>The systems whose file the mods change, or whose file another file of the mods names.</summary>
        public IReadOnlyList<string> Systems { get; }

        /// <summary>The goods files that the mods change.</summary>
        public IReadOnlyList<string> Goods { get; }

        /// <summary>True when the mods change an equipment file or lootprops.ini.</summary>
        public bool Equipment { get; }

        /// <summary>The files of NPC ships, when the mods change them, the loadouts or the equipment. Else
        /// empty.</summary>
        public IReadOnlyList<string> NpcFiles { get; }

        /// <summary>The files of NPC ships:
        /// <list type="bullet">
        /// <item><c>MISSIONS\npcships.ini</c> and <c>MISSIONS\npcships_test.ini</c>,</item>
        /// <item>the <c>npcships.ini</c> of each mission folder,</item>
        /// <item>each file that an <c>npc_ship_file</c> line of a changed file names,</item>
        /// <item>each changed file with an <c>[NPCShipArch]</c>.</item>
        /// </list>
        /// </summary>
        private static List<string> FindNpcFiles(CheckView view, HashSet<string> changed, Side without, Side with) =>
            new[] { without, with }
                .SelectMany(side => NpcShipFiles.Find(side.DataPath, view.Folders,
                    changed.Select(p => (p, side.Sections(p)))))
                .Distinct(VfsPath.Comparer).Order(StringComparer.OrdinalIgnoreCase).ToList();

        private static List<string> Both(Side without, Side with, Func<Side, IEnumerable<string>> paths) =>
            paths(without).Union(paths(with), VfsPath.Comparer).ToList();
    }

    /// <summary>The view of a target build: the merged documents of the changed files, and the files of the game for
    /// each other file.</summary>
    private sealed class TargetFilesView(TargetFiles files) : CheckView
    {
        private readonly FileSystem game = files.Base.OpenFileSystem();
        private readonly Dictionary<string, IReadOnlyList<Section>?> read = new(VfsPath.Comparer);
        private FileSystem? merged;

        public override IReadOnlyCollection<string> ChangedPaths { get; } =
            files.Files.Where(f => f.Kind == TargetFileKind.Ini).Select(f => f.Path).ToList();

        public override IReadOnlyList<Section>? Sections(string path, bool withMods)
        {
            if (files.File(path) is { Kind: TargetFileKind.Ini } file)
                return withMods ? file.Merged?.Sections : file.BaseDocument?.Sections;
            // The mods do not change the file, so both sides read the file of the game.
            if (!read.TryGetValue(path, out var sections))
                read[path] = sections = Read(game, path);
            return sections;
        }

        public override IReadOnlyList<string> Folders(string path) =>
            (merged ??= files.Overlay.CreateFileSystem()).GetDirectories(path);
    }
}
