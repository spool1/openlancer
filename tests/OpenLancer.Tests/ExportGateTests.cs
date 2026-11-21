using System.IO;
using System.Linq;
using System.Text.Json;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Export;
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
    /// the gate of the saved profile give the errors of a save of the mod, so the export refuses the mod. A mod that no
    /// open profile holds gets no gate.
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
        Assert.Equal(ExportGateSource.Profile, fromProfile.Source);
        Assert.Equal(save, fromProfile.Errors!());
        Assert.Empty(For(own, "other").Errors!());
        Assert.Equal(new ExportGateRun(ExportGateSource.None, null), For(own, "outside"));
        Assert.Equal(new ExportGateRun(ExportGateSource.None, null), For(null, "ol-test"));
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

    [Fact]
    public void TheTextsAreSimplifiedTechnicalEnglish()
    {
        Assert.Equal("The export of mod 'm' stopped. The save gate refuses the mod with 1 error, so a save of the mod " +
                     "stops too. Correct the error, then export again.", ExportGate.Refusal("m", 1));
        Assert.Equal("The export of mod 'm' stopped. The save gate refuses the mod with 2 errors, so a save of the mod " +
                     "stops too. Correct the errors, then export again.", ExportGate.Refusal("m", 2));
        Assert.Empty(SteLint.Problems(ExportGate.Refusal("m", 2)));
        Assert.Equal("The open profile does not hold mod 'm', so the export does not run the save gate of mod 'm'. The " +
                     "packages can have errors that a save refuses. To check the mod, open a profile that holds it, then " +
                     "export again.", ExportGate.NoGate("m", true));
        Assert.StartsWith("No profile is open, so the export does not run the save gate of mod 'm'.",
            ExportGate.NoGate("m", false));
        Assert.Empty(SteLint.Problems(ExportGate.NoGate("m", true)));
        Assert.Empty(SteLint.Problems(ExportGate.NoGate("m", false)));
    }
}
