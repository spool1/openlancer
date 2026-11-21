using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.PlayCopies;

/// <summary>The files of Play in the game and the problems of the merge and of the format checks.</summary>
public sealed record PlayBuildResult(TargetFiles Files, IReadOnlyList<Diagnostic> Problems)
{
    public bool HasErrors => Problems.Any(p => p.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// The merge of Play in the game (section 8.6 of the M11 design, decision D311). It merges all mods of the profile in
/// load order on the files of the play target. It uses the retail rules and <c>openlancer_strings.dll</c>. The editor
/// and <c>oltool install</c> use it.
/// </summary>
public static class PlayBuild
{
    /// <summary>
    /// The reason why the mods cannot play in the game, or null. The game must give a retail target. Each mod of the
    /// profile must be a tier-1 mod that names the target. The text names the mod.
    /// </summary>
    public static string? Refusal(GameReport game, IEnumerable<Mod> mods)
    {
        if (ExportTarget.Find(game.Target ?? "") is not { IsRetail: true } target)
            return $"{game.Name} gives no export target, so OpenLancer cannot install mods into it. Play in the game " +
                   "needs HD Edition 0.7.1, Freelancer 1.0 or 1.1, or a source build with the retail INI files.";
        return mods.Select(m => target.PlayRefusal(m.Manifest)).FirstOrDefault(r => r != null);
    }

    /// <summary>Merges the mods on the files of the game and runs the format checks and the content checks of its
    /// target. The files get the route tables of the game when the mods change the routes. The merge only reads the
    /// game folder.</summary>
    public static PlayBuildResult Run(GameReport game, IReadOnlyList<Mod> mods, IStringResourceWriter stringWriter,
        IThnEvaluator thn)
    {
        var files = TargetBuild.Run(game.Base!, mods, ModOverlay.StringsDllName, stringWriter, thn, routeTables: true);
        return new PlayBuildResult(files, [.. files.Diagnostics, .. FormatChecks.Run(files), .. TargetChecks.Run(files)]);
    }
}
