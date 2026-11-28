using System.Collections.Generic;
using OpenLancer.Data;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.PlayCopies;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Runtime.Validation;

/// <summary>The problems of a target check, and the notes for the user.</summary>
public sealed record TargetValidationResult(IReadOnlyList<Diagnostic> Problems, IReadOnlyList<string> Notes);

/// <summary>
/// The checks of a profile on a retail target game (section 9.2 of the M11 design): all mods of the profile merged on
/// the files of the target game, with the problems of the merge, of the format checks and of the content checks. When
/// the target is HD Edition 0.7.1 and the base of the profile is Freelancer 1.0 or 1.1, it also lists the changed lines
/// of HD Edition that the mods replace. <c>oltool validate --target</c> and the tool <c>check</c> of the agent server
/// run it. It reads the saved files of the mods and the game folder, and writes nothing.
/// </summary>
public static class TargetValidation
{
    /// <param name="target">A retail target (<see cref="ExportTarget.IsRetail"/>).</param>
    /// <param name="gameFolder">The game folder of the target, or null when the base of the profile gives the
    /// target.</param>
    /// <param name="way">How the program that runs the check links a game. The error of a missing game tells
    /// it.</param>
    /// <exception cref="ModLoadException">A mod does not load.</exception>
    public static TargetValidationResult Run(ModProfile profile, ExportTarget target, string? gameFolder,
        GameLinkWay way)
    {
        var profileBase = GameDetector.CheckRoot(profile.BaseFolder);
        var report = gameFolder != null ? GameDetector.Detect(gameFolder) : profileBase;
        var problems = new List<Diagnostic>();
        var notes = new List<string>();
        if ((target.BaseRefusal(report, way) ?? PlayBuild.Refusal(report, profile.Mods)) is { } refusal)
            problems.Add(Diagnostic.Error(gameFolder ?? profile.BaseFolder, refusal));
        else
        {
            var build = PlayBuild.Run(report, profile.Mods, DllStringResourceWriter.Instance, ThornEvaluator.Instance);
            problems.AddRange(build.Problems);
            if (target == ExportTarget.HdEdition &&
                profileBase is { CanBeBase: true, Target: ModTargets.Vanilla, Base: { } retail })
                problems.AddRange(TargetChecks.HdEditionLines(build.Files, retail));
            if (TargetChecks.ChangesGameplay(build.Files))
                notes.Add(TargetChecks.GameplayNote);
        }
        return new TargetValidationResult(problems, notes);
    }
}
