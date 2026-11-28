using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Publishing;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Export;

/// <summary>The kind of a package, for its README.</summary>
public enum PackageForm
{
    /// <summary>A zip with the folders DATA and EXE at its top. The player extracts it over the game folder.</summary>
    DropIn,

    /// <summary>An FLMM package: a <c>.zip.flmod</c> file with a script for the Freelancer Mod Manager.</summary>
    Flmm,

    /// <summary>A zip of the mod folder for OpenLancer.</summary>
    ModFolder,
}

/// <summary>A character of the FLMM header that FLMM cannot show (decision D580).</summary>
/// <param name="Field">The field of the publish page that holds the character.</param>
/// <param name="FieldName">The field for a text, for example "the summary".</param>
public sealed record FlmmLostCharacter(string Character, PublishField Field, string FieldName)
{
    /// <summary>The warning: "The FLMM package cannot show the character ... in ...".</summary>
    public string Message => PublishMessages.FlmmCharacter(Character, FieldName);
}

/// <summary>
/// The texts that the publish settings give to an export (section 9 of the M11c design): the manifest with the display
/// name and the version, and the settings of <c>publish.json</c>.
/// </summary>
public sealed record PublishTexts(ModManifest Manifest, PublishSettings Settings)
{
    /// <summary>The texts of a mod without publish settings.</summary>
    public static PublishTexts Of(ModManifest manifest) => new(manifest, PublishSettings.Empty);

    /// <summary>The authors for a text, for example "Ann, Bob", or empty.</summary>
    public string Authors => string.Join(", ", Settings.Authors.Where(a => a.Trim().Length > 0));

    /// <summary>The changelog entries of the exported version and the lower versions, newest first. An entry of a
    /// higher version is not in the texts of an export (finding R24).</summary>
    public IReadOnlyList<ChangelogEntry> Changes => Settings.Changelog
        .Select(e => e with { Changes = [.. e.Changes.Where(c => c.Trim().Length > 0)] })
        .Where(e => e.Changes.Count > 0 && NotHigher(e.Version))
        .OrderByDescending(e => e.Version, Comparer<string>.Create(ModVersion.Compare))
        .ToList();

    /// <summary>The plain text of the description, one line for each item.</summary>
    public IReadOnlyList<string> DescriptionText => PageText.ToPlainText(PageText.Parse(Settings.Description));

    /// <summary>The page link for the texts, or empty. A hand-written <c>publish.json</c> can hold any text, so the
    /// texts use only a link that starts with <c>https://</c>.</summary>
    public string PageUrl => PublishSettings.IsHttps(Settings.PageUrl) ? Settings.PageUrl : "";

    /// <summary>The characters of the FLMM header that FLMM cannot show (decision D580), with the field of each.</summary>
    public IReadOnlyList<FlmmLostCharacter> FlmmLost()
    {
        var lost = new List<FlmmLostCharacter>();
        foreach (var (field, name, text) in new[]
                 {
                     (PublishField.DisplayName, "the display name", Manifest.Name),
                     (PublishField.Authors, "the authors", Authors),
                     (PublishField.Summary, "the summary", Settings.Summary),
                     (PublishField.Description, "the description", string.Join("\n", DescriptionText)),
                 })
            lost.AddRange(PageText.LostInAscii(text).Select(c => new FlmmLostCharacter(c, field, name)));
        return lost;
    }

    /// <summary>True when a changelog version is the exported version or a lower version.</summary>
    private bool NotHigher(string version)
    {
        if (ModVersion.TextComparer.Equals(version, Manifest.Version))
            return true;
        return ModVersion.Parse(version) is { } entry && ModVersion.Parse(Manifest.Version) is { } current &&
               entry.CompareTo(current) <= 0;
    }
}

/// <summary>
/// The texts of an export (milestones M11 and M11c): the README of each package, the text of the Nexus page and the
/// checklist for testers. No text has a date, an AI tag or a request for Donation Points.
/// </summary>
public static class PackageTexts
{
    /// <summary>The notice that the Game Content Usage Rules of Microsoft ask for, with the link to the rules.</summary>
    public const string RulesLink = "https://www.xbox.com/en-US/developers/rules";

    /// <summary>The file name of the README of a mod in a package.</summary>
    public static string ReadmeName(ModManifest mod) => $"{mod.Id} README.txt";

    /// <summary>The notice of the Game Content Usage Rules, with the display name. The rules give its words.</summary>
    public static string Notice(ModManifest mod) =>
        $"Freelancer © Microsoft Corporation. {mod.Name} was created under Microsoft's \"Game Content Usage Rules\" using " +
        $"assets from Freelancer, and it is not endorsed by or affiliated with Microsoft. The rules: {RulesLink}";

    /// <summary>The README of a package (section 9.1 of the M11c design). UTF-8 text with CR LF line ends.</summary>
    /// <param name="files">The files of the target build, or null for the target OpenLancer.</param>
    /// <param name="entries">The files of the package, by their path in the game folder.</param>
    /// <param name="probe">True for the FLMM probe of the FLMM check.</param>
    /// <param name="flmmPackage">True when the export also makes the FLMM package of the target. Only then does the
    /// README of the drop-in zip point to it.</param>
    public static string Readme(PublishTexts texts, ExportTarget target, PackageForm form, TargetFiles? files,
        IReadOnlyList<string> entries, bool probe = false, bool flmmPackage = false)
    {
        var mod = texts.Manifest;
        var settings = texts.Settings;
        var lines = new List<string>
        {
            mod.Name,
            $"Version {mod.Version} for {target.Name}. Mod id: {mod.Id}.",
        };
        if (texts.Authors.Length > 0)
            lines.Add($"By {texts.Authors}.");
        if (probe)
            lines.Add("Probe for the FLMM check. Do not publish.");
        lines.Add("");
        if (settings.Summary.Length > 0)
        {
            lines.Add(settings.Summary);
            lines.Add("");
        }
        if (texts.DescriptionText.Count > 0)
        {
            lines.Add("About:");
            lines.AddRange(texts.DescriptionText);
            lines.Add("");
        }
        lines.Add("Requirements:");
        lines.AddRange(Requirements(target, files).Concat(settings.Requirements.Select(Other)).Select(r => "- " + r));
        lines.Add("");
        switch (form)
        {
            case PackageForm.DropIn:
                lines.Add("Files:");
                lines.AddRange(entries.Select(e => "- " + e));
                lines.Add("");
                lines.Add("Install:");
                lines.Add("1. Copy the files of the list from your game folder to a backup folder.");
                lines.Add("2. Extract the zip into your game folder. Let it replace the files.");
                lines.Add("");
                lines.Add("Remove:");
                lines.Add("1. Copy the files of the backup folder back into your game folder.");
                lines.Add("2. Delete the files of the list that the backup folder does not have.");
                lines.Add("");
                lines.Add("This zip replaces whole files. It does not combine with other mods that change the same files.");
                if (files != null && entries.Any(e => VfsPath.Comparer.Equals(e, ModOverlay.FreelancerIniPath)))
                    lines.AddRange(FreelancerIniLines(target, files, flmmPackage));
                break;
            case PackageForm.Flmm:
                lines.Add("Install:");
                lines.Add($"1. In the Freelancer Mod Manager, import {FlmmPackage.InnerFileName(mod)}.");
                lines.Add("2. Activate the mod.");
                lines.Add("");
                lines.Add("Remove:");
                lines.Add("1. In the Freelancer Mod Manager, deactivate the mod. FLMM restores the files of the game.");
                break;
            case PackageForm.ModFolder:
                lines.Add("Install:");
                lines.Add("1. Extract the zip into the mods folder of your OpenLancer workspace.");
                lines.Add($"2. Add the mod {mod.Id} to a profile.");
                lines.Add("");
                lines.Add("Remove:");
                lines.Add($"1. Remove the mod from the profile, and delete its folder {mod.Id}.");
                break;
        }
        if (texts.Changes.Count > 0)
        {
            lines.Add("");
            lines.Add("Changes:");
            lines.AddRange(ChangeLines(texts.Changes));
        }
        if (settings.Credits.Count > 0)
        {
            lines.Add("");
            lines.Add("Credits:");
            lines.AddRange(settings.Credits.Select(Credit));
        }
        if (settings.Permissions.Length > 0)
        {
            lines.Add("");
            lines.Add("Permissions:");
            lines.AddRange(settings.Permissions.Split('\n'));
        }
        if (texts.PageUrl.Length > 0)
        {
            lines.Add("");
            lines.Add($"Page: {texts.PageUrl}");
        }
        lines.Add("");
        lines.Add(Notice(mod));
        return Text(lines);
    }

    /// <summary>The requirement of the game of a target for players.</summary>
    public static string TargetRequirement(ExportTarget target) =>
        target.Id == ModTargets.HdEdition ? "Freelancer: HD Edition 0.7.1 (Nexus mod 29)."
        : target.Id == ModTargets.Vanilla ? "Freelancer 1.1, with or without JFLP."
        : "OpenLancer.";

    /// <summary>The requirements of a package for players.</summary>
    private static IEnumerable<string> Requirements(ExportTarget target, TargetFiles? files)
    {
        yield return TargetRequirement(target);
        if (files == null)
            yield break;
        if (TargetChecks.ChangesGameplay(files))
            yield return TargetChecks.GameplayNote;
        if (ChangesStart(files))
            yield return "A new game.";
        yield return "Not compatible with total conversions.";
    }

    /// <summary>The text of <c>nexus-page.txt</c> (section 9.3 of the M11c design): one block for each field of a
    /// Nexus page, with the name of the field.</summary>
    /// <param name="packages">The valid packages of the version in the folder of the export, also of earlier exports
    /// (finding R7).</param>
    /// <param name="images">The file names in <c>screenshots</c> and their captions, in list order.</param>
    public static string NexusPage(PublishTexts texts, IReadOnlyList<ExportedPackage> packages,
        IReadOnlyList<(string File, string Caption)> images)
    {
        var mod = texts.Manifest;
        var settings = texts.Settings;
        var lines = new List<string> { $"Nexus page of {mod.Name} {mod.Version}. Copy each part into the field of the same name." };
        var targets = ExportTarget.All.Where(t => packages.Any(p => p.Target == t)).ToList();

        Part("Mod name", [mod.Name]);
        Part("Version", [mod.Version]);
        Part("Author", [texts.Authors]);
        Part("Category", [settings.Category]);
        Part("Summary", [settings.Summary]);
        var description = PageText.ToBBCode(PageText.Parse(settings.Description)).ToList();
        if (description.Count > 0)
            description.Add("");
        description.Add("[size=4][b]Install[/b][/size]");
        foreach (var form in new[] { PackageForm.DropIn, PackageForm.Flmm, PackageForm.ModFolder })
        {
            if (packages.Any(p => p.Form == form))
                description.AddRange(InstallSteps(mod, form));
        }
        description.Add("");
        description.Add(Notice(mod));
        Part("Description (BBCode)", description);
        Part("Requirements", targets.Select(t => $"- {t.Name}: {TargetRequirement(t)}")
            .Concat(settings.Requirements.Select(r => "- " + Other(r))));
        Part("Files", packages.Select(p => $"{p.FileName}: {FileDescription(p)}"));
        Part("Changelog", ChangeLines(texts.Changes));
        var permissions = settings.Permissions.Length > 0 ? settings.Permissions.Split('\n').ToList() : [];
        if (settings.Credits.Count > 0)
        {
            if (permissions.Count > 0)
                permissions.Add("");
            permissions.Add("Credits:");
            permissions.AddRange(settings.Credits.Select(Credit));
        }
        Part("Permissions and credits", permissions);
        Part("Tags", settings.Tags);
        Part("Images", images.Select(i => i.Caption.Length > 0
            ? $@"{ModExport.ScreenshotsFolder}\{i.File}: {i.Caption}"
            : $@"{ModExport.ScreenshotsFolder}\{i.File}"));
        return Text(lines);

        void Part(string name, IEnumerable<string> body)
        {
            lines.Add("");
            lines.Add($"== {name} ==");
            lines.AddRange(body);
        }
    }

    /// <summary>The file description of a package for the Nexus page. It stays below 255 characters (question
    /// Q1).</summary>
    public static string FileDescription(ExportedPackage package) => package.Form switch
    {
        PackageForm.DropIn => $"Drop-in zip for {package.Target.Name}. The README gives the backup and install steps.",
        PackageForm.Flmm => $"FLMM package for {package.Target.Name}{(FlmmPackage.Checked ? "" : " (preview)")}. Import " +
                            "it in the Freelancer Mod Manager.",
        _ => "Mod folder for OpenLancer. Extract it into the mods folder of your OpenLancer workspace.",
    };

    /// <summary>
    /// The text of <c>CHECKLIST.txt</c> (section 9.4 of the M11c design): the title, the changes of the version and
    /// the change list of each target.
    /// </summary>
    public static string ChecklistFile(PublishTexts texts, IEnumerable<(ExportTarget Target, IReadOnlyList<string> Lines)> blocks)
    {
        var mod = texts.Manifest;
        var lines = new List<string> { $"{mod.Name} {mod.Version} ({mod.Id}): checklist for testers" };
        var changes = texts.Settings.ChangesOf(mod.Version).Where(c => c.Trim().Length > 0).ToList();
        if (changes.Count > 0)
        {
            lines.Add("");
            lines.Add("Changes of this version:");
            lines.AddRange(changes.SelectMany(c => c.Split('\n').Select((part, i) => (i == 0 ? "- " : "  ") + part)));
        }
        foreach (var (target, block) in blocks)
        {
            lines.Add("");
            lines.Add(ChecklistTarget(target));
            lines.AddRange(block);
        }
        return Text(lines);
    }

    /// <summary>The first line of the block of a target in the checklist.</summary>
    public static string ChecklistTarget(ExportTarget target) => $"Target: {target.Name}";

    /// <summary>The name of a package form and the steps of its install, for the description of the Nexus page. The
    /// Preview tab of the Mods window draws them too.</summary>
    public static (string Name, IReadOnlyList<string> Steps) Install(ModManifest mod, PackageForm form) => form switch
    {
        PackageForm.DropIn => ("Drop-in zip",
        [
            "Copy the files of the list in the README from your game folder to a backup folder.",
            "Extract the zip into your game folder. Let it replace the files.",
        ]),
        PackageForm.Flmm => ("FLMM package",
        [
            $"In the Freelancer Mod Manager, import {FlmmPackage.InnerFileName(mod)}.",
            "Activate the mod.",
        ]),
        _ => ("OpenLancer",
        [
            "Extract the zip into the mods folder of your OpenLancer workspace.",
            $"Add the mod {mod.Id} to a profile.",
        ]),
    };

    /// <summary>The steps of the install of a form, as BBCode for the description of the Nexus page.</summary>
    private static IEnumerable<string> InstallSteps(ModManifest mod, PackageForm form)
    {
        var (name, steps) = Install(mod, form);
        yield return $"[b]{name}[/b]";
        yield return "[list=1]";
        foreach (var step in steps)
            yield return "[*]" + step;
        yield return "[/list]";
    }

    /// <summary>A requirement of the publish settings: "Name: note (link)". The text leaves out a link that does not
    /// start with <c>https://</c>.</summary>
    private static string Other(PublishRequirement requirement) =>
        requirement.Name + (requirement.Note.Length > 0 ? ": " + requirement.Note : "") +
        (PublishSettings.IsHttps(requirement.Url) ? $" ({requirement.Url})" : "");

    private static string Credit(PublishCredit credit) =>
        credit.For.Length > 0 ? $"- {credit.Name}: {credit.For}" : $"- {credit.Name}";

    /// <summary>The lines of the changelog: the version, then a line for each change. The next lines of a change with
    /// several lines start with two spaces.</summary>
    private static IEnumerable<string> ChangeLines(IEnumerable<ChangelogEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry.Version;
            foreach (var change in entry.Changes)
            {
                var parts = change.Split('\n');
                yield return "- " + parts[0];
                foreach (var part in parts.Skip(1))
                    yield return "  " + part;
            }
        }
    }

    /// <summary>The lines with CR LF line ends and a final line end.</summary>
    private static string Text(IEnumerable<string> lines) => string.Join("\r\n", lines) + "\r\n";

    /// <summary>True when the mod changes the files of a new character or the loadouts of the start ship.</summary>
    private static bool ChangesStart(TargetFiles files)
    {
        string[] startFiles = [@"EXE\newplayer.fl", @"EXE\mpnewcharacter.fl", @"DATA\Characters\NewCharacter.ini"];
        if (files.Files.Any(f => startFiles.Contains(f.Path, VfsPath.Comparer)))
            return true;
        var fs = files.Base.OpenFileSystem();
        if (!fs.FileExists(startFiles[2]))
            return false;
        var start = IniDocument.Parse(startFiles[2], fs.ReadAllBytes(startFiles[2])).Sections
            .SelectMany(s => s).Where(e => e.Name.Equals("loadout", StringComparison.OrdinalIgnoreCase) && e.Count > 0)
            .Select(e => e[0].ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return files.Files.Where(f => f.Diff != null).Any(f =>
            f.Diff!.Changed.Select(p => f.Diff.MergedSections[p.Merged])
                .Concat(f.Diff.Removed.Select(k => f.Diff.BaseSections[k]))
                .Any(s => s.Name.Equals("Loadout", StringComparison.OrdinalIgnoreCase) &&
                          SectionIdentity.KeyOf(s) is { } key && start.Contains(key)));
    }

    /// <summary>The lines of the README about <c>EXE\freelancer.ini</c>: the lines that the mod adds, with their
    /// place. A mod with text also gets the note about the text index. The note points to the FLMM package only when
    /// the export makes it, because the FLMM package refuses some changes.</summary>
    private static IEnumerable<string> FreelancerIniLines(ExportTarget target, TargetFiles files, bool flmmPackage)
    {
        var file = files.File(ModOverlay.FreelancerIniPath);
        if (file?.Diff == null)
            yield break;
        yield return "";
        yield return target.Id == ModTargets.HdEdition
            ? @"EXE\freelancer.ini of this zip keeps the options that the author picked in the HD Edition installer, for " +
              "example the intro movies. To keep your own options, do not copy this file. Add these lines to your own file:"
            : @"EXE\freelancer.ini of this zip is the file of the game of the author. To keep your own file, do not copy " +
              "this file. Add these lines to your own file:";
        foreach (var pair in file.Diff.Changed)
        {
            var section = file.Diff.MergedSections[pair.Merged];
            foreach (var j in pair.Inserted)
            {
                var line = IniLines.Line(section[j].Name, section[j]);
                if (section.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase))
                {
                    var number = section.Take(j + 1).Count(e => e.Name.Equals("DLL", StringComparison.OrdinalIgnoreCase));
                    yield return $"- In [Resources], add \"{line}\" as the {Ordinal(number)} DLL line.";
                }
                else
                    yield return $"- In [{section.Name}], add \"{line}\" after the last {section[j].Name} line.";
            }
        }
        if (files.StringIds.Strings.Count + files.StringIds.Infocards.Count > 0)
            yield return "Two OpenLancer mods with text do not work together as drop-in zips, because both use the same " +
                         "text index." + (flmmPackage ? " Use the FLMM package for that." : "");
    }

    /// <summary>
    /// The checklist for testers: one line for each entry that the mods add, change or remove, in plain words, by file.
    /// </summary>
    public static IReadOnlyList<string> Checklist(TargetFiles files)
    {
        var lines = new List<string>();
        foreach (var file in files.Files)
        {
            var items = Items(file, files).ToList();
            if (items.Count == 0)
                continue;
            lines.Add(file.Path);
            lines.AddRange(items.Select(i => "- " + i));
        }
        return lines;
    }

    private static IEnumerable<string> Items(TargetFile file, TargetFiles files)
    {
        switch (file.Kind)
        {
            case TargetFileKind.TextDll:
                yield return $"Text: {Plural.Count(files.StringIds.Strings.Count, "string")} and " +
                             $"{Plural.Count(files.StringIds.Infocards.Count, "infocard")} in {file.Path}.";
                yield break;
            case TargetFileKind.Cutscene:
                yield return Cutscene(file);
                yield break;
            case TargetFileKind.ModFile:
                yield return file.HasBase ? $"The mod replaces {file.Path}." : $"New file {file.Path}.";
                yield break;
        }
        if (file.Routes is { } routes)
        {
            yield return RouteLine(routes.Diff);
            if (routes.Unreached.Count > 0)
                yield return $"No route of this table reaches the new {(routes.Unreached.Count == 1 ? "system" : "systems")} " +
                             $"{string.Join(", ", routes.Unreached)}.";
            yield break;
        }
        if (file.Diff is not { } diff)
            yield break;
        if (!file.HasBase)
        {
            yield return $"New file {file.Path} with {diff.MergedSections.Count} sections.";
            yield break;
        }
        foreach (var k in diff.Removed)
            yield return $"Removed {Label(diff.BaseSections, k)}.";
        foreach (var pair in diff.Changed)
        {
            var label = Label(diff.MergedSections, pair.Merged);
            var section = diff.MergedSections[pair.Merged];
            var old = diff.BaseSections[pair.Base];
            foreach (var i in pair.Changed)
                yield return $"{label}: {old[i].Name} {Values(old[i])} -> {Values(section[pair.MergedOf[i]])}.";
            foreach (var i in pair.Removed)
                yield return $"{label}: removed {IniLines.Line(old[i].Name, old[i])}.";
            foreach (var j in pair.Inserted)
                yield return $"{label}: new {IniLines.Line(section[j].Name, section[j])}.";
        }
        foreach (var m in diff.Added)
        {
            var section = diff.MergedSections[m];
            var market = section.Name.Equals("BaseGood", StringComparison.OrdinalIgnoreCase)
                ? section.FirstOrDefault(e => e.Name.Equals("base", StringComparison.OrdinalIgnoreCase))
                : null;
            if (market is { Count: > 0 })
            {
                foreach (var good in section.Where(e => e.Name.Equals("marketgood", StringComparison.OrdinalIgnoreCase) &&
                                                        e.Count > 0))
                    yield return $"New market good '{good[0]}' at base '{market[0]}'.";
                continue;
            }
            yield return $"New {Label(diff.MergedSections, m)}.";
        }
    }

    /// <summary>The line of a route table that the build makes: the counts of the routes that change.</summary>
    private static string RouteLine(RouteDiff diff) =>
        $"OpenLancer makes this route table from the merged data: {diff.New} new, {diff.Shorter} shorter, " +
        $"{diff.Longer} longer and {diff.Lost} lost routes.";

    /// <summary>The line of a changed cutscene: the number of changed, new and removed entities and events.</summary>
    private static string Cutscene(TargetFile file)
    {
        if (file.BaseTree == null || file.MergedTree == null)
            return $"New cutscene {file.Path}.";
        var ids = ThnElementId.Counter();
        ThnScriptModel before, after;
        try
        {
            before = ThnScriptModel.From(file.BaseTree, ids);
            after = ThnScriptModel.From(file.MergedTree, ids);
        }
        catch (FormatException)
        {
            // A whole file of a mod can have another form than a cutscene of the game.
            return $"Cutscene {file.Path}: changed.";
        }
        var parts = new List<string>();
        var entities = Changes(before.Entities.Select(e => e.Table), after.Entities.Select(e => e.Table));
        var events = Changes(before.Events.Select(e => e.Table), after.Events.Select(e => e.Table));
        if (entities > 0)
            parts.Add(entities == 1 ? "1 entity changed" : $"{entities} entities changed");
        if (events > 0)
            parts.Add(events == 1 ? "1 event changed" : $"{events} events changed");
        if (before.Duration != after.Duration)
            parts.Add("the duration changed");
        return $"Cutscene {file.Path}: {(parts.Count == 0 ? "changed" : string.Join(", ", parts))}.";
    }

    /// <summary>The number of changed elements. It is the larger of two counts: the elements of the new list that the
    /// old list does not have, and the elements of the old list that the new list does not have.</summary>
    private static int Changes(IEnumerable<ThnTable> before, IEnumerable<ThnTable> after)
    {
        var left = before.ToList();
        var added = 0;
        foreach (var table in after)
        {
            var index = left.IndexOf(table);
            if (index < 0)
                added++;
            else
                left.RemoveAt(index);
        }
        return Math.Max(added, left.Count);
    }

    /// <summary>A section for a person, for example "Good 'lf_hull'" or "[SystemInfo]".</summary>
    private static string Label(IReadOnlyList<Section> sections, int index)
    {
        var id = SectionIdentity.Of(sections, index);
        return id.Key != null && id.Parent == null && id.Ordinal == 0 ? $"{id.Type} '{id.Key}'" : id.ToString();
    }

    private static string Values(Entry entry) => string.Join(", ", entry.Select(RetailText.Of));

    private static string Ordinal(int number) => (number % 100) switch
    {
        11 or 12 or 13 => $"{number}th",
        _ => (number % 10) switch
        {
            1 => $"{number}st",
            2 => $"{number}nd",
            3 => $"{number}rd",
            _ => $"{number}th",
        },
    };
}
