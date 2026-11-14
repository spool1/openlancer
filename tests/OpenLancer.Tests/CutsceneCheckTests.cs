using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.IO;
using LibreLancer.Data.Schema.PetalDb;
using OpenLancer.Data;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Validation;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The cutscene checks of the validation (test 13.1 item 17 of the M8 design): a mod with a broken patched cutscene
/// gives an error with its place, and the same problem in the lower data gives no new problem. The check reads the
/// game data of each view, the terrain of each base that plays a script, and the INI lines that tell when the game plays
/// a script only in space.
/// </summary>
public sealed class CutsceneCheckTests
{
    // The native compiler loads from the x64 folder of the test output, also when a test of this class runs alone.
    static CutsceneCheckTests() => AppHandler.ConsoleInit(silent: true);

    private const string Path = ModOverlayThnTests.ScriptPath;
    private const string PatchFile = ModOverlayThnTests.PatchFile;

    private const string Script = """
        duration = 10
        entities = {
            { entity_name = "Cam_A", type = CAMERA, cameraprops = { fovh = 10, hvaspect = 1.5 } },
            { entity_name = "Cam_B", type = CAMERA, cameraprops = { fovh = 30 } },
            { entity_name = "Monitor", type = MONITOR }
        }
        events = { { 0, SET_CAMERA, { "Monitor", "Cam_A" } } }
        """;

    private static string Patch(string entries) => $"ol_thn_patch = 1\nentities = {{ {entries} }}\n";

    /// <summary>A workspace whose freelancer.ini lists no resource DLL, so that the engine can read the game data of
    /// its base folder.</summary>
    private static TestWorkspace Workspace()
    {
        var ws = new TestWorkspace();
        ws.WriteBase(@"EXE\freelancer.ini", "[Freelancer]\ndata path = ..\\data\n");
        return ws;
    }

    /// <summary>
    /// A patch that makes a value unreadable gives one error with the place of the value in the merged file. The same
    /// problem in the lower data gives no new problem, also when the patch deletes an element before it, so that its
    /// place in the list changes. <see cref="ProfileValidator"/> adds these problems to the validation, and the retail
    /// test <c>CutsceneCheckRetailTests</c> runs it through oltool.
    /// </summary>
    [Fact]
    public void TheValidatorReportsABrokenPatchedCutsceneAndNotAProblemOfTheLowerData()
    {
        using (var ws = Workspace())
        {
            ws.WriteBase(Path, Script);
            var overlay = ws.Build(ws.Mod("m", new()
            {
                [PatchFile] = Patch("""{ ol_match = "Cam_B", ol_set = { ["cameraprops.fovh"] = "wide" } }"""),
            }));

            var problems = CutsceneCheck.NewProblems(FileSystem.FromPath(ws.Base), overlay.CreateFileSystem(),
                overlay.CutscenePaths, null, null);

            Assert.Equal(Diagnostic.Error(Path, "entity \"Cam_B\", cameraprops.fovh: The value is \"wide\", but the engine reads a " +
                                                "number there.") with
            {
                Cutscene = new ThnLocation(Path, ThnElementKind.Entity, 1, "cameraprops.fovh", "\"Cam_B\""),
                // The subject names the problem without its element.
                Subject = "cameraprops.fovh: The value is \"wide\", but the engine reads a number there.",
            }, Assert.Single(problems));
        }

        using (var ws = Workspace())
        {
            ws.WriteBase(Path, Script.Replace("fovh = 30", "fovh = \"wide\""));
            var overlay = ws.Build(ws.Mod("m", new()
            {
                [PatchFile] = Patch("""{ ol_match = "Cam_A", ol_delete = Y }, { ol_match = "Cam_B", ol_set = { ["cameraprops.hvaspect"] = 2 } }"""),
            }));
            var modded = overlay.CreateFileSystem();
            // The problem is at place 1 below and at place 0 above.
            Assert.Equal(1, Assert.Single(CutsceneCheck.Errors(FileSystem.FromPath(ws.Base), Path, null)).Cutscene!.Index);
            Assert.Equal(0, Assert.Single(CutsceneCheck.Errors(modded, Path, null)).Cutscene!.Index);

            Assert.Empty(CutsceneCheck.NewProblems(FileSystem.FromPath(ws.Base), modded, overlay.CutscenePaths, null, null));
        }
    }

    /// <summary>A whole file that does not run is one error of the script: a text that does not compile, or a number
    /// that is not finite. A text with a character outside ASCII is an error, also in a comment. A patch that does not
    /// apply is a merge error with the place of the cutscene.</summary>
    [Fact]
    public void AScriptThatDoesNotRunIsOneErrorAndAPatchThatDoesNotApplyIsAMergeError()
    {
        using var ws = Workspace();
        ws.WriteBase(Path, Script);
        const string Infinite = @"DATA\SCRIPTS\infinite.thn";
        const string Broken = @"DATA\SCRIPTS\broken.thn";
        const string Accent = @"DATA\SCRIPTS\accent.thn";
        var mod = ws.Mod("m", new()
        {
            [@"files\" + Infinite] = "duration = 1e39\nentities = {}\nevents = {}\n",
            [@"files\" + Broken] = "duration = \nentities = {}\n",
            [@"files\" + Accent] = "",
            [PatchFile] = Patch("""{ ol_match = "Cam_C", ol_set = { ["cameraprops.fovh"] = 20 } }"""),
        });
        // The workspace writes ASCII, so this file gets its UTF-8 bytes here.
        File.WriteAllText(System.IO.Path.Combine(ws.ModFolder("m"), "files", Accent),
            "duration = 1\n-- caf\u00e9\nentities = {}\nevents = {}\n", new UTF8Encoding(false));
        var overlay = ws.Build(mod);

        var merge = Assert.Single(overlay.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, merge.Severity);
        Assert.Equal(new ThnLocation(Path), merge.Cutscene);

        var problems = CutsceneCheck.NewProblems(FileSystem.FromPath(ws.Base), overlay.CreateFileSystem(),
            overlay.CutscenePaths, null, null);

        Assert.Equal([Accent, Broken, Infinite], problems.Select(p => p.Source));
        Assert.Equal("Line 2 has a character outside ASCII. The text of a THN script is ASCII.", problems[0].Message);
        Assert.Contains("does not compile", problems[1].Message);
        Assert.Equal($"{Infinite}: The value at duration is Infinity. A THN number must be finite.", problems[2].Message,
            ignoreCase: true);
        Assert.All(problems, p => Assert.Equal((DiagnosticSeverity.Error, new ThnLocation(p.Source)), (p.Severity, p.Cutscene)));
    }

    /// <summary>
    /// The rooms of a base fill in the <c>$terrain_*</c> templates with the terrain of the base (ThnRoomHandler.cs:23-51).
    /// The check reads the INI lines that name the script, and checks the template of each base that plays it. The
    /// game data gives the state of each item: a ship without a model, a costume without a body, and a room that
    /// petalDB does not have.
    /// </summary>
    [Fact]
    public void TheCheckFillsInTheTerrainOfEachBaseThatPlaysTheScriptAndReadsTheGameData()
    {
        using var ws = Workspace();
        const string Ambient = @"DATA\SCRIPTS\ambi.thn";
        ws.WriteBase(Ambient, """
            duration = 10
            entities = {
                { entity_name = "Rock", type = COMPOUND, template_name = "$terrain_dyna_01", userprops = { category = "solar" } },
                { entity_name = "Ship", type = COMPOUND, template_name = "hulk", userprops = { category = "ship" } },
                { entity_name = "Room", type = COMPOUND, template_name = "no_room", userprops = { category = "room" } },
                { entity_name = "Man", type = DEFORMABLE, template_name = "ghost" }
            }
            events = {}
            """);
        var db = new GameItemDb(FileSystem.FromPath(ws.Base));
        db.Ini.PetalDb = new PetalDbIni();
        Add(db.Archetypes, new Archetype { Nickname = "rock_arch", ModelFile = new ResolvedModel { ModelFile = "rock.cmp", SourcePath = "x" } });
        Add(db.Ships, new Ship { Nickname = "hulk" });
        Add(db.Costumes, new Costume { Nickname = "ghost" });
        Add(db.Bases, new Base { Nickname = "B1", TerrainDyna1 = "rock_arch" });
        Add(db.Bases, new Base { Nickname = "B2" });
        string[] others =
        [
            "entity \"Ship\", template_name: The ship \"hulk\" has no model.",
            "entity \"Room\", template_name: The game data has no room of petalDB \"no_room\".",
            "entity \"Man\", template_name: The costume \"ghost\" has no body. A DEFORMABLE without userprops.Actor needs it.",
        ];
        const string Rock = "entity \"Rock\", template_name: The game data has no solar archetype \"$terrain_dyna_01\".";

        // No room plays the script, so the engine reads the template as it is.
        Assert.Equal([Rock, .. others], Messages());
        // Without the game data, the checks of the templates and the costumes do not run.
        Assert.Empty(CutsceneCheck.Errors(FileSystem.FromPath(ws.Base), Ambient, null));

        ws.WriteBase(@"DATA\UNIVERSE\universe.ini",
            "[Base]\nnickname = B1\nfile = Universe\\b1.ini\n[Base]\nnickname = B2\nfile = Universe\\b2.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\b1.ini", "[Room]\nnickname = Bar\nfile = Universe\\bar1.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\bar1.ini", "[Room_Info]\nscene = all, ambient, Scripts\\ambi.thn\n");
        Assert.Equal(others, Messages());

        // B2 has no dynamic terrain, so the template stays, and the engine stops in a room of B2.
        ws.WriteBase(@"DATA\UNIVERSE\b2.ini", "[Room]\nnickname = Bar\nfile = Universe\\bar2.ini\n");
        ws.WriteBase(@"DATA\UNIVERSE\bar2.ini", "[Room_Info]\nscene = all, ambient, Scripts\\ambi.thn\n");
        Assert.Equal([Rock, .. others], Messages());

        string[] Messages() => [.. CutsceneCheck.Errors(FileSystem.FromPath(ws.Base), Ambient, db).Select(p => p.Message)];
    }

    /// <summary>
    /// The engine loads the model of a solar archetype, and stops when the file of its <c>DA_archetype</c> line does
    /// not exist: the game data then gives the model no file (GameItemDb.cs:122-138, :2088-2105). The validation gives
    /// the name problems as errors too: a missing prop of petalDB and a missing effect. The engine reads the template of
    /// a PSYS without the terrain of the room, gives PlayerShipEngines to the engine of the player ship, and finds an
    /// effect also among the visual effects and an asteroid also among the mines.
    /// </summary>
    [Fact]
    public void AModelFileThatDoesNotExistStopsTheEngineAndTheNameProblemsAreErrors()
    {
        using var ws = Workspace();
        const string Shot = @"DATA\SCRIPTS\shot.thn";
        ws.WriteBase(Shot, """
            duration = 10
            entities = {
                { entity_name = "Lost", type = COMPOUND, template_name = "lost_arch", userprops = { category = "solar" } },
                { entity_name = "Cup", type = COMPOUND, template_name = "no_cup", userprops = { category = "prop" } },
                { entity_name = "Mug", type = COMPOUND, template_name = "mug", userprops = { category = "prop" } },
                { entity_name = "Mine", type = COMPOUND, template_name = "mine1", userprops = { category = "asteroid" } },
                { entity_name = "Fx", type = PSYS, template_name = "no_fx" },
                { entity_name = "Vis", type = PSYS, template_name = "vis1" },
                { entity_name = "Terrain", type = PSYS, template_name = "$terrain_dyna_01" },
                { entity_name = "Engines", type = PSYS, template_name = "PlayerShipEngines" }
            }
            events = {}
            """);
        var db = new GameItemDb(FileSystem.FromPath(ws.Base));
        db.Ini.PetalDb = new PetalDbIni();
        db.Ini.PetalDb.Props["mug"] = @"props\mug.3db";
        Add(db.Archetypes, new Archetype
        {
            Nickname = "lost_arch", ModelFile = new ResolvedModel { ModelFile = null, SourcePath = @"solar\lost.cmp" },
        });
        Add(db.Asteroids, new Asteroid { Nickname = "mine1", ModelFile = new ResolvedModel { ModelFile = "mine1.3db", SourcePath = "x" } });
        Add(db.VisEffects, new ResolvedFx { Nickname = "vis1", LibraryFiles = [] });

        Assert.Equal([
            "entity \"Lost\", template_name: The solar archetype \"lost_arch\" has no model.",
            "entity \"Cup\", template_name: The game data has no prop of petalDB \"no_cup\". The scene shows the entity without a model.",
            "entity \"Fx\", template_name: The game data has no effect \"no_fx\". The scene does not show this entity.",
            "entity \"Terrain\", template_name: The game data has no effect \"$terrain_dyna_01\". The scene does not show this entity.",
        ], CutsceneCheck.Errors(FileSystem.FromPath(ws.Base), Shot, db).Select(p => p.Message));
        Assert.All(CutsceneCheck.Errors(FileSystem.FromPath(ws.Base), Shot, db),
            p => Assert.Equal(DiagnosticSeverity.Error, p.Severity));
    }

    /// <summary>
    /// A message names its entity, and the retail data has a name problem. So the validation compares the name problems
    /// by their subjects: a patch that renames the entity of a lower name problem adds no problem, and a patch that
    /// sets another missing effect adds one.
    /// </summary>
    [Fact]
    public void ARenameOfAnEntityWithALowerNameProblemAddsNoProblem()
    {
        static string[] Problems(string patch)
        {
            using var ws = Workspace();
            var db = new GameItemDb(FileSystem.FromPath(ws.Base));
            ws.WriteBase(Path, """
                duration = 10
                entities = { { entity_name = "Fx", type = PSYS, template_name = "no_fx" } }
                events = {}
                """);
            var overlay = ws.Build(ws.Mod("m", new() { [PatchFile] = Patch(patch) }));
            return [.. CutsceneCheck.NewProblems(FileSystem.FromPath(ws.Base), overlay.CreateFileSystem(), overlay.CutscenePaths,
                db, db).Select(p => p.Message)];
        }

        Assert.Empty(Problems("""{ ol_match = "Fx", ol_set = { entity_name = "Fx_2" } }"""));
        Assert.Equal(["entity \"Fx\", template_name: The game data has no effect \"other_fx\". The scene does not show this entity."],
            Problems("""{ ol_match = "Fx", ol_set = { template_name = "other_fx" } }"""));
    }

    /// <summary>
    /// The validation checks the clip of a START_MOTION event on a character with the clips of the animation files that
    /// the <c>[Animations]</c> lines of the loaded data name. It skips a file that does not exist, as the engine does. A
    /// COMPOUND plays the scripts of its model, so its clip gets no check. Without the game data, no clip check runs.
    /// An animation file of a mod that does not read gives one warning and no exception, and its clips are then not
    /// known.
    /// </summary>
    [Fact]
    public void AnUnknownClipIsAnErrorAndAnAnimationFileThatDoesNotReadIsAWarning()
    {
        using var ws = Workspace();
        ws.WriteBase(@"DATA\CHARACTERS\bodyparts.ini", "[Animations]\nanim = characters\\walk.anm\nanim = characters\\gone.anm\n");
        AnimationWorkspace.Write(System.IO.Path.Combine(ws.Base, @"DATA\CHARACTERS\walk.anm"), "Sc_Walk");
        ws.WriteBase(Path, """
            duration = 10
            entities = {
                { entity_name = "Man", type = DEFORMABLE, userprops = { Actor = "Man" } },
                { entity_name = "Door", type = COMPOUND }
            }
            events = {
                { 1, START_MOTION, { "Man" }, { animation = "sc_walk" } },
                { 2, START_MOTION, { "Man" }, { animation = "Sc_Typo" } },
                { 3, START_MOTION, { "Door" }, { animation = "Sc_loop" } }
            }
            """);
        var lower = FileSystem.FromPath(ws.Base);
        var db = Loaded(lower);
        const string Typo = "event { 2, START_MOTION, { \"Man\" } }, props.animation: The game has no animation \"Sc_Typo\". " +
                            "The character does not move.";

        Assert.Equal([Typo], CutsceneCheck.Errors(lower, Path, db).Select(p => p.Message));
        Assert.Empty(CutsceneCheck.Errors(lower, Path, null));

        var overlay = ws.Build(ws.Mod("m", new()
        {
            [PatchFile] = Patch("""{ ol_match = "Door", ol_set = { template_name = "" } }"""),
            [@"files\DATA\CHARACTERS\walk.anm"] = "not an animation file",
        }));
        var modded = overlay.CreateFileSystem();
        var problems = CutsceneCheck.NewProblems(lower, modded, overlay.CutscenePaths, db, Loaded(modded));

        Assert.Equal(["event { 1, START_MOTION, { \"Man\" } }, props.animation: The game has no animation \"sc_walk\". " +
                      "The character does not move."], problems.Where(p => p.Severity == DiagnosticSeverity.Error).Select(p => p.Message));
        var warning = Assert.Single(problems, p => p.Severity == DiagnosticSeverity.Warning);
        Assert.EndsWith("walk.anm", warning.Source, System.StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("OpenLancer cannot read this animation file, so the clip list has no clips from it. ", warning.Message);

        // The game data of a view, with the [Animations] lines of its bodyparts.ini.
        static GameItemDb Loaded(FileSystem fs)
        {
            var data = new GameItemDb(fs);
            data.Ini.Bodyparts = new LibreLancer.Data.Schema.Characters.BodypartsIni(data.DataPath(@"characters\bodyparts.ini")!, data.Ini);
            return data;
        }
    }

    /// <summary>
    /// The game makes no COMPOUND of a script that it plays only in space (Cutscene.cs:101-113). The check reads the INI
    /// lines that name the script, also without the game data: a script that only <c>[Trigger] Act_CallThorn</c> names
    /// has no COMPOUND error. A mission ambient plays the script in a room, so then the COMPOUND error is back.
    /// </summary>
    [Fact]
    public void AScriptThatTheGamePlaysOnlyInSpaceHasNoCompoundError()
    {
        using var ws = Workspace();
        const string Space = @"DATA\MISSIONS\M01A\space.thn";
        const string Mission = @"DATA\MISSIONS\M01A\m01a.ini";
        ws.WriteBase(Space, """
            duration = 10
            entities = {
                { entity_name = "Ship", type = COMPOUND, template_name = "msn_ship" },
                { entity_name = "L", type = LIGHT }
            }
            events = {}
            """);
        const string Compound = "entity \"Ship\", userprops.category: The key is missing. The engine needs it for a COMPOUND with " +
                                "a template.";
        const string Light = "entity \"L\", lightprops: The key is missing. The engine needs it for a LIGHT.";

        // No INI line names the script.
        Assert.Equal([Compound, Light], Messages());

        // The trigger doubles the backslashes, as 67 of 70 retail Act_CallThorn values do.
        ws.WriteBase(Mission, """
            [Trigger]
            nickname = t1
            Act_CallThorn = Missions\\M01A\\space.thn
            """);
        Assert.Equal([Light], Messages());

        ws.WriteBase(Mission, """
            [Trigger]
            nickname = t1
            Act_CallThorn = Missions\\M01A\\space.thn
            Act_AddAmbient = Missions\M01A\space.thn, Bar, B1
            """);
        Assert.Equal([Compound, Light], Messages());

        string[] Messages() => [.. CutsceneCheck.Errors(FileSystem.FromPath(ws.Base), Space, null).Select(p => p.Message)];
    }

    private static void Add<T>(GameItemCollection<T> collection, T item) where T : IdentifiableItem
    {
        item.CRC = FLHash.CreateID(item.Nickname);
        collection.Add(item);
    }
}
