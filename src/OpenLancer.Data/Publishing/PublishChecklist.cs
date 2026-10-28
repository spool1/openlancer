using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>What a button of the publish checklist does. The Mods window runs the action.</summary>
public enum ChecklistAction
{
    /// <summary>Selects the field of the step in the Settings tab.</summary>
    GoTo,

    /// <summary>Writes the version of <see cref="ChecklistButton.Argument"/> into the draft.</summary>
    SetVersion,

    CheckNow,
    ShowInConsole,
    GameLink,

    /// <summary>Removes the target <see cref="ChecklistButton.Argument"/> from the draft.</summary>
    RemoveTarget,

    /// <summary>Plays the mod alone in the game of the target <see cref="ChecklistButton.Argument"/>.</summary>
    Play,

    MarkAsTested,
    Export,
    AddImages,
}

/// <summary>A button of the checklist.</summary>
/// <param name="Field">The field of <see cref="ChecklistAction.GoTo"/>, else null.</param>
/// <param name="Argument">The version or the target of the action, else null.</param>
public sealed record ChecklistButton(ChecklistAction Action, string Label, PublishField? Field = null, string? Argument = null);

/// <summary>A step of the checklist.</summary>
/// <param name="Missing">What is missing and why it is necessary. Empty when the step is complete.</param>
public sealed record ChecklistStep(int Number, string Name, IReadOnlyList<string> Missing, IReadOnlyList<ChecklistButton> Buttons)
{
    public bool Complete => Missing.Count == 0;
}

/// <summary>A recommended item. It does not count as a step.</summary>
public sealed record ChecklistItem(string Text, ChecklistButton? Button);

/// <summary>What the checklist reads.</summary>
/// <param name="Content">The content fingerprint of the saved files.</param>
/// <param name="Page">The page fingerprint of the saved files.</param>
/// <param name="BaseOf">The base key (see <see cref="TargetCheck.BaseKey"/>) of the linked game that gives a retail
/// target, or null when no linked game gives it.</param>
/// <param name="PlayTargets">The retail targets whose linked game has a game program, for Play.</param>
public sealed record ChecklistInput(
    ModManifest Manifest,
    PublishSettings Settings,
    PublishHistory History,
    string Content,
    string Page,
    Func<ExportTarget, string?> BaseOf,
    IReadOnlyList<ExportTarget> PlayTargets)
{
    /// <summary>
    /// The input of a mod with the bases of the linked games: each retail target takes the base key of its base (see
    /// <see cref="ExportTarget.BaseIn"/>). The Mods window, the Export dialog, <c>oltool publish</c> and the agent tool
    /// <c>publish_checklist</c> build the input here, so all of them count the same steps.
    /// </summary>
    /// <param name="reports">The checks of the linked games.</param>
    /// <param name="playTargets">The retail targets whose linked game has a game program, for Play.</param>
    public static ChecklistInput Of(ModManifest manifest, PublishSettings settings, PublishHistory history, string content,
        string page, IReadOnlyList<GameReport> reports, IReadOnlyList<ExportTarget> playTargets) =>
        new(manifest, settings, history, content, page,
            target => target.BaseIn(reports) is { } report ? TargetCheck.BaseKey(report) : null, playTargets);

    /// <summary>
    /// The input of a mod from its saved files: its <c>publish.json</c>, the publish history of a folder and the
    /// fingerprints of the files of the mod. <c>oltool publish</c> and the agent tool <c>publish_checklist</c> read it
    /// here. The fingerprints read each file of the mod, so a window calls it on a worker thread.
    /// </summary>
    /// <param name="historyFolder">The folder of the publish history, for example the default export folder of the
    /// mod.</param>
    /// <param name="problems">The problems of the reads of <c>publish.json</c> and of the history. The checklist then
    /// counts without the part that has the problem.</param>
    /// <exception cref="IOException">A file of the mod cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A file of the mod cannot be read.</exception>
    public static ChecklistInput Read(Mod mod, string historyFolder, IReadOnlyList<GameReport> reports,
        IReadOnlyList<ExportTarget> playTargets, out IReadOnlyList<string> problems)
    {
        var publish = PublishSettings.Load(mod.Folder);
        var history = PublishHistory.Load(historyFolder, mod.Id);
        problems = [.. publish.Problems.Append(history.Problem).OfType<string>()];
        var fingerprint = new ModFingerprint();
        return Of(mod.Manifest, publish.Settings, history.History, fingerprint.Content(mod.Manifest, mod.Folder),
            fingerprint.Page(mod.Manifest, mod.Folder, publish.Settings), reports, playTargets);
    }
}

/// <summary>
/// The publish checklist of a mod (section 11 of the M11c design, AGENTS.md UI rule 12). It has six steps, or five for
/// a mod with only the target OpenLancer. The recommended items do not count. It is pure: it reads only its input.
/// </summary>
public sealed record PublishChecklist(IReadOnlyList<ChecklistStep> Steps, IReadOnlyList<ChecklistItem> Recommended)
{
    public int CompleteCount => Steps.Count(s => s.Complete);

    public bool AllComplete => Steps.All(s => s.Complete);

    /// <summary>For example "4 of 6 steps complete".</summary>
    public string Summary => $"{CompleteCount} of {Steps.Count} steps complete";

    public static PublishChecklist Of(ChecklistInput input)
    {
        var manifest = input.Manifest;
        var targets = ExportTarget.All.Where(t => manifest.Targets.Contains(t.Id)).ToList();
        var steps = new List<ChecklistStep>
        {
            DisplayName(manifest),
            SummaryStep(input.Settings),
            Version(manifest, input.History),
            Checks(input, targets),
        };
        if (targets.Any(t => t.IsRetail))
            steps.Add(TestPlay(input, targets));
        steps.Add(Export(input, targets));
        steps = [.. steps.Select((s, i) => s with { Number = i + 1 })];
        return new PublishChecklist(steps, RecommendedItems(input, targets));
    }

    private static ChecklistStep DisplayName(ModManifest manifest)
    {
        var name = manifest.Name.Trim();
        var missing = name.Length == 0 ? PublishMessages.DisplayNameEmpty
            : name == ModWorkspace.StarterModName ? PublishMessages.StarterName
            : name == manifest.Id ? PublishMessages.NameIsId(manifest.Id)
            : null;
        return Step("Display name", missing, GoTo(PublishField.DisplayName));
    }

    private static ChecklistStep SummaryStep(PublishSettings settings) =>
        Step("Summary", settings.Summary.Trim().Length == 0
            ? "Write a summary of one or two sentences. Nexus shows the summary below the name of the mod."
            : null, GoTo(PublishField.Summary));

    private static ChecklistStep Version(ModManifest manifest, PublishHistory history)
    {
        var version = ModVersion.Parse(manifest.Version);
        if (version == null || !ModVersion.IsSafe(manifest.Version))
            return Step("Version", "Use the form 1.2.3. Players and Nexus compare versions in this form.",
                new ChecklistButton(ChecklistAction.SetVersion, "Use 1.0.0", Argument: "1.0.0"), GoTo(PublishField.Version));
        var published = history.Published
            .Select(m => (Text: m.Version, Parsed: ModVersion.Parse(m.Version)))
            .Where(m => m.Parsed != null && m.Parsed.CompareTo(version) >= 0)
            .OrderByDescending(m => m.Parsed)
            .ToList();
        if (published.Count == 0)
            return Step("Version", null);
        var next = ModVersion.Next(manifest.Version, VersionPart.Patch, history.Versions)!;
        // The next version must also be higher than each published version.
        while (ModVersion.Parse(next)!.CompareTo(published[0].Parsed) <= 0)
            next = ModVersion.Parse(next)!.Increase(VersionPart.Patch).ToString();
        return Step("Version", PublishMessages.VersionPublished(published[0].Text),
            new ChecklistButton(ChecklistAction.SetVersion, $"Increase to {next}", Argument: next), GoTo(PublishField.Version));
    }

    /// <summary>Step 4: for each target, the last check has the current content, the same base and no error.</summary>
    private static ChecklistStep Checks(ChecklistInput input, IReadOnlyList<ExportTarget> targets)
    {
        var missing = new List<string>();
        var buttons = new List<ChecklistButton>();
        foreach (var target in targets)
        {
            var baseKey = target.IsRetail ? input.BaseOf(target) : "";
            if (baseKey == null)
            {
                missing.Add($"Link a game folder of {target.Game} in File > Game link. The check needs the files of the game.");
                Add(new ChecklistButton(ChecklistAction.GameLink, "Game link..."));
                if (targets.Count > 1)
                    Add(new ChecklistButton(ChecklistAction.RemoveTarget, $"Remove the target {target.Name}", Argument: target.Id));
                continue;
            }
            var check = input.History.CheckOf(target.Id);
            if (check == null)
                missing.Add($"OpenLancer did not check {target.Name} yet. Check the current content.");
            else if (check.Content != input.Content)
                missing.Add($"The content changed after the last check of {target.Name}. Check the current content.");
            else if (!string.Equals(check.Base, baseKey, StringComparison.OrdinalIgnoreCase))
                missing.Add($"The linked game of {target.Name} changed after the last check. Check the current content.");
            else if (check.Errors > 0)
            {
                missing.Add($"The check of {target.Name} found {check.Errors} errors. The export stops with these errors.");
                Add(new ChecklistButton(ChecklistAction.ShowInConsole, "Show in Console"));
            }
            else
                continue;
            Add(new ChecklistButton(ChecklistAction.CheckNow, "Check now"));
        }
        return new ChecklistStep(0, "Checks for each target", missing, buttons);

        void Add(ChecklistButton button)
        {
            if (!buttons.Contains(button))
                buttons.Add(button);
        }
    }

    /// <summary>Step 5: a test play of the current content on a retail target, or a manual mark.</summary>
    private static ChecklistStep TestPlay(ChecklistInput input, IReadOnlyList<ExportTarget> targets)
    {
        var done = input.History.Plays.Any(p => p.Content == input.Content &&
                                                (p.Manual || p.Target != null && ExportTarget.Find(p.Target) is { IsRetail: true }));
        var buttons = input.PlayTargets.Where(targets.Contains)
            .Select(t => new ChecklistButton(ChecklistAction.Play, $"Play in {t.Name}", Argument: t.Id))
            .Append(new ChecklistButton(ChecklistAction.MarkAsTested, "Mark as tested"))
            .ToArray();
        return Step("Test play in the game", done ? null
            : "Play the current content in the real game once. OpenLancer cannot prove that the retail game accepts all data.",
            buttons);
    }

    /// <summary>Step 6: the exports of the current version with the current fingerprints, without
    /// <c>replacedBy</c>, together have a package for each target (finding R7).</summary>
    private static ChecklistStep Export(ChecklistInput input, IReadOnlyList<ExportTarget> targets)
    {
        var version = input.Manifest.Version;
        var exported = input.History.ValidExports(version)
            .Where(e => e.Content == input.Content && e.Page == input.Page)
            .SelectMany(e => e.Packages.Select(p => p.Target))
            .ToHashSet();
        var done = targets.All(t => exported.Contains(t.Id));
        return Step("Export made", done ? null
            : $"Export version {version}. The step counts an export of the current content and texts, with a package for each target.",
            new ChecklistButton(ChecklistAction.Export, "Export..."));
    }

    private static List<ChecklistItem> RecommendedItems(ChecklistInput input, IReadOnlyList<ExportTarget> targets)
    {
        var settings = input.Settings;
        var version = input.Manifest.Version;
        var items = new List<ChecklistItem>();
        if (settings.Description.Trim().Length == 0)
            items.Add(new ChecklistItem("Write a description.", GoTo(PublishField.Description)));
        if (!settings.Authors.Any(a => a.Trim().Length > 0))
            items.Add(new ChecklistItem("Name the authors.", GoTo(PublishField.Authors)));
        if (!settings.ChangesOf(version).Any(c => c.Trim().Length > 0))
            items.Add(new ChecklistItem($"Write the changes of version {version}.", GoTo(PublishField.Changelog)));
        if (settings.Screenshots.Count == 0)
            items.Add(new ChecklistItem("Add one screenshot or more for the Nexus page.",
                new ChecklistButton(ChecklistAction.AddImages, "Add images...")));
        // The item names the field, and Go to selects it (decision D580).
        if (targets.Any(t => t.IsRetail))
        {
            foreach (var lost in new PublishTexts(input.Manifest, settings).FlmmLost())
                items.Add(new ChecklistItem(lost.Message, GoTo(lost.Field)));
        }
        if (input.History.Published.Count > 0 && settings.PageUrl.Length == 0)
            items.Add(new ChecklistItem("Add the page link after the upload.", GoTo(PublishField.PageUrl)));
        if (!FlmmPackage.Checked && input.History.ValidExports(version).Any(e => e.Packages.Any(p => p.Form == PackageForms.Flmm)))
            items.Add(new ChecklistItem("The FLMM package is a preview. Check it with FLMM before you publish it.", null));
        return items;
    }

    private static ChecklistButton GoTo(PublishField field) => new(ChecklistAction.GoTo, "Go to", field);

    private static ChecklistStep Step(string name, string? missing, params ChecklistButton[] buttons) =>
        new(0, name, missing == null ? [] : [missing], missing == null ? [] : buttons);
}
