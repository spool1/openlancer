using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Export;

/// <summary>
/// A game that the export makes packages for (milestone M11): HD Edition 0.7.1, Freelancer 1.1, or OpenLancer. A retail
/// target needs a linked game whose check gives the target. Its packages hold the merged files of that game.
/// </summary>
/// <param name="Id">The target of <see cref="ModTargets"/>, for example <c>hde-0.7.1</c>.</param>
/// <param name="Name">The name for the user, for example "HD Edition 0.7.1".</param>
/// <param name="Game">The game of a base for the user, for example "Freelancer: HD Edition 0.7.1".</param>
public sealed record ExportTarget(string Id, string Name, string Game)
{
    public static ExportTarget HdEdition { get; } =
        new(ModTargets.HdEdition, "HD Edition 0.7.1", "Freelancer: HD Edition 0.7.1");

    public static ExportTarget Vanilla { get; } = new(ModTargets.Vanilla, "Freelancer 1.1", "Freelancer 1.0 or 1.1");

    public static ExportTarget OpenLancer { get; } = new(ModTargets.OpenLancer, "OpenLancer", "OpenLancer");

    /// <summary>The targets in the order of <see cref="ModTargets.All"/>.</summary>
    public static IReadOnlyList<ExportTarget> All { get; } = [HdEdition, Vanilla, OpenLancer];

    /// <summary>True for a target of the retail game, which needs a linked game as its base.</summary>
    public bool IsRetail => Id != ModTargets.OpenLancer;

    /// <summary>The target with the id, or null.</summary>
    public static ExportTarget? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// The reason why the export of a mod for this target cannot run, or null: a tier-2 mod and a retail target, a
    /// target that <c>mod.json</c> does not name, or a mod that needs other mods.
    /// </summary>
    public string? Refusal(ModManifest mod)
    {
        if (PlayRefusal(mod) is { } refusal)
            return refusal;
        if (mod.DependsOn.Count > 0)
            return $"Mod '{mod.Id}' needs other mods. The export of a mod that needs other mods is not available yet.";
        return null;
    }

    /// <summary>
    /// The reason why a mod cannot run in a game of this target, or null. A retail target refuses a tier-2 mod. Each
    /// target refuses a mod whose <c>mod.json</c> does not name it. Play in the game checks each mod of the profile with
    /// it (section 8.6 of the M11 design).
    /// </summary>
    public string? PlayRefusal(ModManifest mod)
    {
        if (IsRetail && mod.Tier == ModManifest.RuntimeTier)
            return $"Mod '{mod.Id}' is a tier-{ModManifest.RuntimeTier} mod. A tier-{ModManifest.RuntimeTier} mod runs " +
                   "only in OpenLancer. Pick the target OpenLancer.";
        if (!mod.Targets.Contains(Id, StringComparer.Ordinal))
            return $"Mod '{mod.Id}' does not name the target '{Id}'. Add the target to mod.json, or pick another target.";
        return null;
    }

    /// <summary>The reason why a game cannot be the base of this target, or null when it can. The report must give
    /// the target, and a retail target needs a base. A source build is never the base of an export, because its
    /// <c>freelancer.ini</c> is a development copy with other lines than the retail file.</summary>
    /// <param name="report">The check of the linked game, or null when no game is linked.</param>
    public string? BaseRefusal(GameReport? report)
    {
        if (!IsRetail)
            return null;
        return report is { CanBeBase: true, Root: not null } && report.Target == Id
            ? null
            : $"No linked game gives the target '{Id}'. Link a game folder of {Game} in File > Game link.";
    }

    /// <summary>The check of the linked game that is the base of this retail target, or null. It is the first check that
    /// <see cref="BaseRefusal"/> takes. The target OpenLancer has no base.</summary>
    /// <param name="reports">The checks of the linked games.</param>
    public GameReport? BaseIn(IReadOnlyList<GameReport> reports) =>
        IsRetail ? reports.FirstOrDefault(r => BaseRefusal(r) == null) : null;

    /// <summary>The warnings of a base: its INI files differ from the file list of its build, or it has JFLP.</summary>
    public IEnumerable<string> BaseWarnings(GameReport report)
    {
        if (report.Data == DataState.Changed)
            yield return $"The INI files of {report.Name} differ from the " +
                         $"{GameDetector.DataName(report.Reference ?? report.Build ?? Id)} files. " +
                         "Players with a clean install can see other results.";
        if (report.Jflp)
            yield return "The base has JFLP. The drop-in zip carries the JFLP version of each changed file to each player.";
    }
}
