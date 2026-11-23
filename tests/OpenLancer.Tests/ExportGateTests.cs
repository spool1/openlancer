using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The save gate before an export (#87). The export refuses a mod that the save gate refuses, with the same errors.
/// The agent tool and the Export dialog use the open session when it holds the saved mod. Else the Export dialog and
/// <c>oltool export</c> open the profile with the mod active, and that session only reads. Both give the gate of a
/// save.
/// </summary>
public sealed class ExportGateTests
{
    /// <summary>A patch that deletes the good that the unchanged market file of the game names. The save gate refuses
    /// it (decision D81), and the checks of the export do not.</summary>
    internal const string DeletePatch = "[Good]\nnickname = lf_hull\nol.delete = true\n";

    /// <summary>A profile with the mod "ol-test" with a patch, for the target Freelancer 1.1, and the mod "other".
    /// The mod "outside" is in the folder of the mods, but not in the profile.</summary>
    private static string Profile(ExportGame game, string patch = DeletePatch)
    {
        var profile = OltoolExportTests.Profile(game, patch, ", \"targets\": [ \"vanilla-1.1\" ]");
        var other = game.Mod("other", new() { [@"patches\" + ExportGame.GoodsPath] = "[Good]\nnickname = g2\nprice = 300\n" });
        game.Mod("outside", new());
        File.WriteAllText(profile, JsonSerializer.Serialize(new
        {
            @base = game.Root,
            mods = new[] { Path.Combine(game.ModsFolder, "ol-test"), other.Folder },
        }));
        return profile;
    }

    private static EditSession Open(string profile, string modId = "ol-test") =>
        EditSession.Open(ModProfile.Load(profile), modId, TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

    /// <summary>
    /// The open session gives the gate only when its active mod is the exported mod and it has no unsaved edits,
    /// because the export reads the saved files. Else a session that only reads the profile gives it, when the profile
    /// holds the mod. A mod that the profile does not hold gets no gate.
    /// </summary>
    [Fact]
    public void TheSourceOfTheGateFollowsTheOpenSession()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        using var session = Open(profile);
        string Folder(string id) => Path.Combine(game.ModsFolder, id);

        Assert.Equal(ExportGateSource.Session, ExportGate.SourceOf(session, Folder("ol-test")));
        Assert.Equal(ExportGateSource.Profile, ExportGate.SourceOf(session, Folder("other")));
        Assert.Equal(ExportGateSource.None, ExportGate.SourceOf(session, Folder("outside")));
        Assert.Equal(ExportGateSource.None, ExportGate.SourceOf(null, Folder("ol-test")));
        session.Execute(EditCommand.SetLinesFromText(new SectionRef(ExportGame.GoodsPath, new SectionIdentity("Good", "g2")),
            "price", ["400"]));
        Assert.Equal(ExportGateSource.Profile, ExportGate.SourceOf(session, Folder("ol-test")));
    }

    /// <summary>
    /// The gate of the Export dialog for each source (see <see cref="ExportGate.For"/>). The gate of the open session and
    /// the gate of the saved profile give the errors of a save of the mod, so the export refuses the mod. These runs
    /// have no refusal of their own.
    /// </summary>
    [Fact]
    public void TheGateOfTheExportDialogGivesTheErrorsOfASave()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        using var own = Open(profile);
        using var other = Open(profile, "other");
        ExportGateRun For(EditSession? session, string id) => ExportGate.For(session, Path.Combine(game.ModsFolder, id), id,
            [game.Report()], TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);
        var save = own.GateRefusal()!.Problems.Where(p => p.Severity == DiagnosticSeverity.Error).ToList();
        Assert.NotEmpty(save);

        var fromSession = For(own, "ol-test");
        var fromProfile = For(other, "ol-test");

        Assert.Equal(ExportGateSource.Session, fromSession.Source);
        Assert.Equal(save, fromSession.Errors!());
        Assert.Null(fromSession.Refusal);
        Assert.Equal(ExportGateSource.Profile, fromProfile.Source);
        Assert.Equal(save, fromProfile.Errors!());
        Assert.Null(fromProfile.Refusal);
        Assert.Empty(For(own, "other").Errors!());
        Assert.Null(For(own, "other").Refusal);
    }

    /// <summary>
    /// The Export dialog refuses a mod that no open profile holds, because it cannot run the save gate of the mod (#89).
    /// The run has the refusal and no gate call, so the dialog writes no package and starts no prepare. This also applies
    /// when no profile is open. The refusal tells the user to open a profile that holds the mod.
    /// </summary>
    [Fact]
    public void TheExportDialogRefusesAModThatNoOpenProfileHolds()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        using var own = Open(profile);
        ExportGateRun For(EditSession? session, string id) => ExportGate.For(session, Path.Combine(game.ModsFolder, id), id,
            [game.Report()], TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

        var notHeld = For(own, "outside");
        var noProfile = For(null, "ol-test");

        Assert.Equal(new ExportGateRun(ExportGate.NoProfileRefusal("outside", true)), notHeld);
        Assert.Equal(ExportGateSource.None, notHeld.Source);
        Assert.Null(notHeld.Errors);
        Assert.Equal(new ExportGateRun(ExportGate.NoProfileRefusal("ol-test", false)), noProfile);
        Assert.Equal("The export of mod 'outside' stopped. The open profile does not hold mod 'outside', so the export " +
                     "cannot run the save gate of the mod. Open a profile that holds the mod, then export again.",
            notHeld.Refusal);
        Assert.Equal("The export of mod 'ol-test' stopped. No profile is open, so the export cannot run the save gate of " +
                     "the mod. Open a profile that holds the mod, then export again.", noProfile.Refusal);
    }

    /// <summary>
    /// A session that only reads the profile with the mod active gives the gate of a writable session for the same
    /// saved mod. So the Export dialog and <c>oltool export</c> refuse what a save refuses. The gate of the other mod of
    /// the profile has no error.
    /// </summary>
    [Fact]
    public void AReadOnlySessionGivesTheGateOfAWritableSession()
    {
        using var game = new ExportGame();
        var profile = Profile(game);
        using var writable = Open(profile);
        using var reader = EditSession.OpenReadOnly(ModProfile.Load(profile), "ol-test", TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator, "The test only reads.");

        var errors = ExportGate.Errors(writable);

        Assert.Equal(writable.SaveProblems(), reader.SaveProblems());
        var error = Assert.Single(errors);
        Assert.Contains("the active mod removes the good 'lf_hull'", error.Message);
        Assert.Equal(errors, ExportGate.Errors(ModProfile.Load(profile), "ol-test", [game.Report()],
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator));
        Assert.Empty(ExportGate.Errors(ModProfile.Load(profile), "other", [game.Report()],
            TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator));
        Assert.Equal(writable.GateRefusal()!.Problems.Where(p => p.Severity == DiagnosticSeverity.Error), errors);
    }

    /// <summary>The cutscene case of #87: a saved patch deletes a costume that only an unchanged cutscene loads. The
    /// session that only reads gives the cutscene error of the writable session.</summary>
    [Fact]
    public void AReadOnlySessionGivesTheCutsceneErrorOfAWritableSession()
    {
        using var ws = ThnBreaks.Workspace("saved delete patch");
        var writable = ws.Session("act");
        using var reader = EditSession.OpenReadOnly(ModProfile.Load(ws.ProfilePath), "act", TestWorkspace.FakeDllWriter.Instance,
            ThnScripts.Evaluator, "The test only reads.");

        var errors = ExportGate.Errors(writable);

        Assert.Contains("\"trent\"", Assert.Single(errors).Message);
        Assert.Equal(writable.SaveProblems(), reader.SaveProblems());
        Assert.Equal(errors, ExportGate.Errors(reader));
    }

    /// <summary>
    /// The gate of a profile checks the mission patches of the mod on the linked game of each retail target, as the
    /// editor save does with its game links. A saved patch changes a trigger that the linked Freelancer 1.1 does not
    /// have, so the gate of the profile gives the target error of a writable session with the same games. Without a
    /// linked game, the gate checks no target.
    /// </summary>
    [Fact]
    public void TheGateOfAProfileChecksTheMissionPatchesOnTheLinkedGames()
    {
        const string script = @"DATA\MISSIONS\OL_TEST\ol_test.ini";
        const string text = "[Mission]\n\n[Trigger]\nnickname = ol_end\nInitState = ACTIVE\nCnd_True = no_params\n" +
                            "Act_ChangeState = SUCCEED\n";
        using var ws = new TestWorkspace();
        ws.WriteBase(script, text);
        ws.Mod("act", new() { [@"patches\" + script] = "[Trigger]\nnickname = ol_end\nAct_ChangeState = FAIL\n" });
        ws.WriteProfile("act");
        using var game = new TestWorkspace();
        game.WriteBase(script, text.Replace("nickname = ol_end", "nickname = ol_finish"));
        var report = new GameReport(game.Base, game.Base, GameKind.Vanilla, "1.1", VersionProof.Hash, false, null, null,
            null, ModTargets.Vanilla, ModTargets.Vanilla, DataState.NotChanged, ModTargets.Vanilla, 0, 0, [], [], []);
        var writable = ws.Session("act");
        writable.TargetGame = ExportGate.TargetGames([report]);
        IReadOnlyList<Diagnostic> Gate(params GameReport[] reports) => ExportGate.Errors(ModProfile.Load(ws.ProfilePath),
            "act", reports, TestWorkspace.FakeDllWriter.Instance, ThnScripts.Evaluator);

        var errors = Gate(report);

        var error = Assert.Single(errors);
        Assert.Equal("T01", error.Rule);
        Assert.Equal($"[Trigger] nickname = ol_end is not in {script} of Freelancer 1.1. The merge of this patch fails on " +
                     "Freelancer 1.1. Remove the edit, or remove Freelancer 1.1 from the targets of the mod.", error.Message);
        Assert.Equal(ExportGate.Errors(writable), errors);
        Assert.Empty(Gate());
    }

    [Fact]
    public void TheTextsAreSimplifiedTechnicalEnglish()
    {
        Assert.Equal("The export of mod 'm' stopped. The save gate refuses the mod with 1 error, so a save of the mod " +
                     "stops too. Correct the error, then export again.", ExportGate.Refusal("m", 1));
        Assert.Equal("The export of mod 'm' stopped. The save gate refuses the mod with 2 errors, so a save of the mod " +
                     "stops too. Correct the errors, then export again.", ExportGate.Refusal("m", 2));
        Assert.Empty(SteLint.Problems(ExportGate.Refusal("m", 2)));
        Assert.Empty(SteLint.Problems(ExportGate.NoProfileRefusal("m", true)));
        Assert.Empty(SteLint.Problems(ExportGate.NoProfileRefusal("m", false)));
    }
}
