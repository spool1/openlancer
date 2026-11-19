using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.IO;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Thn;
using OpenLancer.Data.Validation;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The template checks of the save gate on the retail data (#19, section 12.1 of the M8b design). The gate checks the
/// templates and the costumes with the data lists of the session. With that data, the 1,506 retail cutscenes have no
/// error, so a rename of a retail entity cannot turn a retail error into a false gate error. They have one name
/// problem: the PSYS "ring_2" of an intro scene names the effect "ring", which the game data does not have. So the gate
/// compares the name problems by their elements, and a rename of that entity adds no problem. The 1,524 cutscenes of HD
/// Edition have no problem. The lists of the templates hold the items of the engine, and the session data gives each
/// item the model state of the engine. The gate also checks each cutscene that names an item that the active mod
/// removes or breaks (#81). This is true also when the active mod does not change the cutscene.
/// </summary>
public sealed class ThnNameGateRetailTests(ITestOutputHelper output)
{
    static ThnNameGateRetailTests() => AppHandler.ConsoleInit(silent: true);

    [Fact]
    public void TheRetailCutscenesHaveNoProblemWithTheDataOfASession()
    {
        var (files, errors, names) = CheckEachCutscene(GameDataPaths.RequireBase(), "m8cproof");

        Assert.Equal(1506, files);
        Assert.Empty(errors);
        var name = Assert.Single(names);
        Assert.Equal((CutsceneCheckRetailTests.WaterPlanet, CutsceneCheckRetailTests.RetailNameProblem),
            (name.Source, name.Message));
    }

    /// <summary>The cutscenes of HD Edition have no error and no name problem with the data of a session. So a rename
    /// of an entity of HD Edition cannot turn a problem of the lower data into a false gate error.</summary>
    [Fact]
    public void TheHdEditionCutscenesHaveNoProblemWithTheDataOfASession()
    {
        var (files, errors, names) = CheckEachCutscene(GameDataPaths.RequireHde(), "m8chde");

        Assert.Equal(1524, files);
        Assert.Empty(errors);
        Assert.Empty(names);
    }

    /// <summary>The session data gives each solar archetype, ship, equipment and asteroid of the template lists the
    /// state of the engine: the engine has no such item, the item has no model, or it has a model. This is also true for
    /// the equipment of each section type (<see cref="ThnTemplateRules.EquipmentModel"/>). The room fills in a template
    /// that starts with <c>$</c>, so the session data gives no state for such a name.</summary>
    [Fact]
    public void TheSessionDataGivesEachItemTheModelStateOfTheEngine()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8cproof");
        var session = workspace.Open();
        var data = new SessionThnData(session, CutsceneCheckRetailTests.WaterPlanet);
        var db = EngineDataLoader.Load(FileSystem.FromPath(GameDataPaths.RequireBase())).RequireComplete();
        static ThnItemState State<T>(T? item, Func<T, ResolvedModel?> model) where T : class =>
            item == null ? ThnItemState.Missing : model(item)?.ModelFile == null ? ThnItemState.NoModel : ThnItemState.Ready;
        ThnItemState Engine(ThnItemKind kind, string name) => kind switch
        {
            ThnItemKind.Archetype => State(db.Archetypes.Get(name), a => a.ModelFile),
            ThnItemKind.Ship => State(db.Ships.Get(name), s => s.ModelFile),
            ThnItemKind.Equipment => State(db.Equipment.Get(name), e => e.ModelFile),
            ThnItemKind.Asteroid => State(db.Asteroids.Get(name), a => a.ModelFile),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var differences = new List<string>();
        var counts = new Dictionary<ThnItemState, int>();
        foreach (var kind in Enum.GetValues<ThnItemKind>().Where(ThnTemplateRules.NeedsModel))
        {
            foreach (var entry in session.Lists.List(ThnTemplateRules.ListsOf(kind))!.Entries.Where(e => !e.Value.StartsWith('$')))
            {
                var (engine, now) = (Engine(kind, entry.Value), data.Item(kind, entry.Value));
                counts[engine] = counts.GetValueOrDefault(engine) + 1;
                if (now != engine)
                    differences.Add($"{kind} {entry.Value}: the engine gives {engine}, the session data gives {now}.");
            }
        }

        output.WriteLine(string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")));
        foreach (var difference in differences.Take(40))
            output.WriteLine(difference);
        Assert.Empty(differences);
        Assert.True(counts.GetValueOrDefault(ThnItemState.NoModel) > 0);
    }

    /// <summary>A rename of the entity of the retail name problem adds no problem to the save gate, because the gate
    /// compares the name problems by their elements. A new missing effect of that entity blocks the save.</summary>
    [Fact]
    public void ARenameOfTheEntityOfTheRetailNameProblemAddsNoGateError()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8cproof");
        var session = workspace.Open();
        var ring = session.Cutscene(CutsceneCheckRetailTests.WaterPlanet)!.Entities
            .Single(e => ThnScriptModel.NameOf(e.Table) == "ring_2");
        var path = session.CutscenePath(CutsceneCheckRetailTests.WaterPlanet)!;
        var target = new ThnRef(path, ThnElementKind.Entity, ring.Id);

        session.Execute(ThnEdits.RenameEntity(target, "my_ring"));
        Assert.Empty(session.CutsceneErrors(path));
        Assert.Empty(session.SaveProblems());

        session.Execute(ThnEdits.Set(target, new ThnPath("template_name"), new ThnText("no_such_effect")));
        Assert.Equal(["entity \"my_ring\", template_name: The game data has no effect \"no_such_effect\". The scene does not " +
                      "show this entity."], session.CutsceneErrors(path).Select(e => e.Message));
    }

    /// <summary>
    /// A saved <c>ol.delete</c> patch of the costume that the most retail cutscenes load stops the save. The save gate
    /// checks each cutscene that names the costume, also when the active mod does not change it (#81). The oracle
    /// checks each cutscene of the edit view without the search of the gate. The gate gives exactly the errors of the
    /// oracle, and each error names the costume.
    /// </summary>
    /// <remarks>
    /// The first gate call includes the build of the word index and the check of each cutscene that the search finds.
    /// Measured on 2026-11-19: 19 cutscenes load the costume, and the search finds 851 cutscenes in each game. Most of
    /// them name the costume on a DEFORMABLE with <c>userprops.Actor</c>, which does not load it. The first call took
    /// 4.5 to 4.6 s for the retail game and 5.0 to 5.1 s for HD Edition. The second call took 6 ms. The budget is about
    /// two times the measured time, and at most 10 s.
    /// </remarks>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void ADeleteOfTheRetailCostumeThatTheMostCutscenesNameBlocksEachOfThem() =>
        CheckADeletedCostume(GameDataPaths.RequireBase(), "m8dproof");

    /// <inheritdoc cref="ADeleteOfTheRetailCostumeThatTheMostCutscenesNameBlocksEachOfThem"/>
    [Fact]
    [Trait(Timing.Category, Timing.Performance)]
    public void ADeleteOfTheHdEditionCostumeThatTheMostCutscenesNameBlocksEachOfThem() =>
        CheckADeletedCostume(GameDataPaths.RequireHde(), "m8dhde");

    /// <summary>
    /// A saved patch that removes the <c>DA_archetype</c> line of the retail equipment that the most COMPOUNDs name
    /// stops the save. The game crashes when it builds such a COMPOUND. The gate gives exactly the errors of the oracle.
    /// The game makes no COMPOUND of a script that only <c>Act_CallThorn</c> lines name, so such a script gives no
    /// error.
    /// </summary>
    [Fact]
    public void ARemovedModelLineOfARetailEquipmentBlocksEachCutsceneThatBuildsIt() =>
        CheckARemovedModelLine(GameDataPaths.RequireBase(), "m8dgear");

    /// <inheritdoc cref="ARemovedModelLineOfARetailEquipmentBlocksEachCutsceneThatBuildsIt"/>
    [Fact]
    public void ARemovedModelLineOfAnHdEditionEquipmentBlocksEachCutsceneThatBuildsIt() =>
        CheckARemovedModelLine(GameDataPaths.RequireHde(), "m8dgearhde");

    /// <summary>A patch that changes a value of each retail ship and each costume keeps each answer of the game data. So
    /// the save gate adds no cutscene error, opens no cutscene and does not start the word index.</summary>
    [Fact]
    public void APatchThatKeepsEachAnswerOfTheRetailDataOpensNoCutscene() =>
        CheckAPatchThatKeepsEachAnswer(GameDataPaths.RequireBase(), "m8dsame");

    /// <inheritdoc cref="APatchThatKeepsEachAnswerOfTheRetailDataOpensNoCutscene"/>
    [Fact]
    public void APatchThatKeepsEachAnswerOfTheHdEditionDataOpensNoCutscene() =>
        CheckAPatchThatKeepsEachAnswer(GameDataPaths.RequireHde(), "m8dsamehde");

    private void CheckADeletedCostume(string game, string modId)
    {
        using var workspace = new RetailWorkspace(game, modId);
        var reader = workspace.Open();
        var (costume, section, scripts) = MostNamed(reader, Templates(game).Costumes, "costume", _ => true);
        WritePatch(workspace, section.Path, $"[{section.Id.Type}]\nnickname = {costume}\nol.delete = true\n");
        reader.Dispose();
        var session = workspace.Open();

        SaveResult? refusal = null;
        var first = Timing.Measure(() => refusal = session.GateRefusal());
        Assert.NotNull(refusal);
        var gate = CutsceneErrors(refusal.Problems);
        var second = new TimingGates().WarmGate(TimeSpan.FromMilliseconds(100), 3, () => session.GateRefusal());
        output.WriteLine($"The costume \"{costume}\" of {scripts} scripts: the search finds " +
                         $"{session.ReferrerCutscenes().Count} cutscenes, {gate.Count} gate errors in " +
                         $"{gate.Select(e => e.Source).Distinct().Count()} cutscenes, first gate call " +
                         $"{first.TotalSeconds:0.00} s, second {second.TotalMilliseconds:0} ms.");

        Assert.Equal(Oracle(session), gate.Order());
        Assert.True(gate.Select(e => e.Source).Distinct().Count() >= 2);
        Assert.All(gate, e => Assert.Contains($"\"{costume}\"", e.Message));
        Assert.True(first < TimeSpan.FromSeconds(10), $"The first gate call took {first.TotalSeconds:0.00} s.");
        Assert.True(second < TimeSpan.FromMilliseconds(100), $"The second gate call took {second.TotalMilliseconds:0} ms.");
    }

    private void CheckARemovedModelLine(string game, string modId)
    {
        using var workspace = new RetailWorkspace(game, modId);
        var reader = workspace.Open();
        var data = new SessionThnData(reader, CutsceneCheckRetailTests.WaterPlanet);
        var (equipment, section, scripts) = MostNamed(reader, Templates(game).Equipment, "equipment",
            n => data.Item(ThnItemKind.Equipment, n) == ThnItemState.Ready);
        WritePatch(workspace, section.Path, $"[{section.Id.Type}]\nnickname = {equipment}\nol.remove = DA_archetype\n");
        reader.Dispose();
        var session = workspace.Open();

        var gate = CutsceneErrors(session.SaveProblems());
        output.WriteLine($"The equipment \"{equipment}\" of {scripts} scripts: {gate.Count} gate errors in " +
                         $"{gate.Select(e => e.Source).Distinct().Count()} cutscenes.");

        Assert.Equal(Oracle(session), gate.Order());
        Assert.NotEmpty(gate);
        Assert.All(gate, e => Assert.Contains($"\"{equipment}\" has no model", e.Message));
    }

    private void CheckAPatchThatKeepsEachAnswer(string game, string modId)
    {
        using var workspace = new RetailWorkspace(game, modId);
        var reader = workspace.Open();
        var patches = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        void Add(SectionRef section, string line) =>
            (patches.TryGetValue(section.Path, out var text) ? text : patches[section.Path] = new StringBuilder())
            .Append($"[{section.Id.Type}]\nnickname = {section.Id.Key}\n{line}\n");
        var ships = reader.Lists.List("ship")!.Entries.Where(e => e.Section != null).ToList();
        var costumes = reader.Lists.List("costume")!.Entries.Where(e => e.Section != null).ToList();
        foreach (var ship in ships)
            Add(ship.Section!, "hit_pts = 12345");
        // The engine finds a body part without letter case, so the body in capitals keeps the answer.
        foreach (var costume in costumes)
        {
            if (reader.Get(costume.Section!)!.Entries.LastOrDefault(e =>
                    e.Name.Equals("body", StringComparison.OrdinalIgnoreCase)) is { Values.Count: > 0 } body)
                Add(costume.Section!, $"body = {body.Values[0].ToString()!.ToUpperInvariant()}");
        }
        foreach (var (path, text) in patches)
            WritePatch(workspace, path, text.ToString());
        reader.Dispose();
        var session = workspace.Open();

        var problems = session.SaveProblems();
        output.WriteLine($"{ships.Count} ships and {costumes.Count} costumes: {problems.Count} save problems.");

        Assert.Empty(CutsceneErrors(problems));
        Assert.Empty(session.BrokenCutsceneItems());
        Assert.Empty(session.OpenCutscenes);
        Assert.False(session.Mentions.IsStarted);
    }

    /// <summary>The errors of the save gate that a cutscene gives, as their sources and messages.</summary>
    private static List<(string Source, string Message)> CutsceneErrors(IEnumerable<Diagnostic> problems) =>
    [
        .. problems.Where(p => p is { Severity: DiagnosticSeverity.Error, Cutscene: not null })
            .Select(p => (p.Source, p.Message)),
    ];

    /// <summary>The errors of each cutscene of the edit view (see <see cref="EditSession.CutsceneErrors"/>), in order.
    /// The loop does not use the search of the save gate, so it is the oracle of the gate.</summary>
    private static IEnumerable<(string Source, string Message)> Oracle(EditSession session) =>
        VfsFiles.Find(session.EditView.CreateFileSystem(), ".thn")
            .SelectMany(file => session.CutsceneErrors(VfsPath.Normalize(file) ?? file))
            .Select(e => (e.Source, e.Message))
            .Order()
            .ToList();

    /// <summary>
    /// The template of a kind that the most cutscenes name, with its section and the number of the cutscenes. The
    /// template must be in the data list with a section and fit the filter. A tie goes to the first name in the order
    /// of the names, so the choice is fixed.
    /// </summary>
    private static (string Name, SectionRef Section, int Scripts) MostNamed(EditSession session,
        IReadOnlyDictionary<string, SortedSet<string>> templates, string list, Func<string, bool> filter)
    {
        var entries = session.Lists.List(list)!;
        var (name, files) = templates
            .Where(t => !t.Key.StartsWith('$') && entries.Find(t.Key)?.Section != null && filter(t.Key))
            .OrderByDescending(t => t.Value.Count).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .First();
        var entry = entries.Find(name)!;
        return (entry.Value, entry.Section!, files.Count);
    }

    /// <summary>Writes a patch file into the mod of the workspace, at an install path.</summary>
    private static void WritePatch(RetailWorkspace workspace, string path, string text)
    {
        var file = Path.Combine(workspace.ModFolder, Mod.PatchesFolder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    /// <summary>The scans of <see cref="Templates"/>, for each game folder.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<ThnTemplateScan>> Scans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The templates of the cutscenes of a game, with the cutscenes that name each template.</summary>
    private sealed record ThnTemplateScan(IReadOnlyDictionary<string, SortedSet<string>> Costumes,
        IReadOnlyDictionary<string, SortedSet<string>> Equipment);

    /// <summary>
    /// The costume of each DEFORMABLE that loads one, and the equipment of each COMPOUND of the category equipment, in
    /// each cutscene of a game. The scan reads the scripts with the engine evaluator, without a session, so it does not
    /// use the search of the save gate. The names compare without letter case.
    /// </summary>
    private static ThnTemplateScan Templates(string game) => Scans.GetOrAdd(game, g => new Lazy<ThnTemplateScan>(() =>
    {
        var view = FileSystem.FromPath(g);
        var costumes = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var equipment = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            var script = ThnScriptModel.From(ThornEvaluator.Instance.Evaluate(view.ReadAllBytes(file), file),
                ThnElementId.Counter());
            foreach (var entity in script.Entities.Select(e => e.Table))
            {
                if (entity["template_name"] is not ThnText { Value.Length: > 0 } template)
                    continue;
                var found = IsType(entity, "DEFORMABLE") && ThnTemplateRules.LoadsCostume(entity)
                    ? costumes
                    : IsType(entity, "COMPOUND") &&
                      ThnTemplateRules.UserProp(entity, "category") is ThnText { Value: var category } &&
                      category.Equals("equipment", StringComparison.OrdinalIgnoreCase)
                        ? equipment
                        : null;
                if (found == null)
                    continue;
                if (!found.TryGetValue(template.Value, out var files))
                    found[template.Value] = files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                files.Add(VfsPath.Normalize(file) ?? file);
            }
        }
        return new ThnTemplateScan(costumes, equipment);
    })).Value;

    /// <summary>True when an entity has a type. A script can give the type as a name, for example <c>COMPOUND</c>, or
    /// as the number of the name.</summary>
    private static bool IsType(ThnTable entity, string type) => entity["type"] switch
    {
        ThnSymbol symbol => symbol.Name.Equals(type, StringComparison.OrdinalIgnoreCase),
        ThnNumber number => ThornEvaluator.Instance.Symbols.TryGetValue(type, out var info) && info.Value == (long)number.Value,
        _ => false,
    };

    /// <summary>Checks each cutscene of a game with the data of a session, as the save gate does.</summary>
    private (int Files, List<Diagnostic> Errors, List<Diagnostic> NameProblems) CheckEachCutscene(string game, string modId)
    {
        using var workspace = new RetailWorkspace(game, modId);
        var session = workspace.Open();
        var view = FileSystem.FromPath(game);
        var evaluator = ThornEvaluator.Instance;
        var clock = Stopwatch.StartNew();
        var (files, errors, names) = (0, new List<Diagnostic>(), new List<Diagnostic>());
        foreach (var file in VfsFiles.Find(view, ".thn"))
        {
            files++;
            var result = ThnChecks.Check(file, evaluator.Evaluate(view.ReadAllBytes(file), file), evaluator,
                new SessionThnData(session, file));
            errors.AddRange(result.Errors);
            names.AddRange(result.NameProblems);
        }

        output.WriteLine($"{files} files: {errors.Count} errors and {names.Count} name problems with the data of a session " +
                         $"in {clock.Elapsed.TotalSeconds:0.0} s.");
        foreach (var problem in errors.Concat(names).Take(40))
            output.WriteLine(problem.ToString());
        return (files, errors, names);
    }

    /// <summary>The petalDB lists hold the rooms, the props and the carts of the engine, and the asteroid lists
    /// together hold the asteroids and the mines of the engine. The template lists of a PSYS hold each effect and
    /// visual effect of the engine.</summary>
    [Fact]
    public void TheTemplateListsHoldTheItemsOfTheEngine()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "m8cproof");
        var session = workspace.Open();
        var lists = session.Lists;
        var db = EngineDataLoader.Load(FileSystem.FromPath(GameDataPaths.RequireBase())).RequireComplete();

        string[] Names(ThnItemKind kind) =>
        [
            .. lists.List(ThnTemplateRules.ListsOf(kind))!.Entries.Select(e => e.Value.ToLowerInvariant()).Order(StringComparer.Ordinal),
        ];
        static string[] Engine(IEnumerable<string> names) =>
            [.. names.Select(n => n.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal)];

        output.WriteLine($"rooms {db.Ini.PetalDb.Rooms.Count}, props {db.Ini.PetalDb.Props.Count}, carts " +
                         $"{db.Ini.PetalDb.Carts.Count}, asteroids {db.Asteroids.Count()}, mines " +
                         $"{lists.List("asteroid_mine")?.Entries.Count}");
        Assert.Equal(Engine(db.Ini.PetalDb.Rooms.Keys), Names(ThnItemKind.Room));
        Assert.Equal(Engine(db.Ini.PetalDb.Props.Keys), Names(ThnItemKind.Prop));
        Assert.Equal(Engine(db.Ini.PetalDb.Carts.Keys), Names(ThnItemKind.Cart));
        Assert.Equal(Engine(db.Asteroids.Select(a => a.Nickname)), Names(ThnItemKind.Asteroid));
        Assert.NotEmpty(lists.List("asteroid_mine")!.Entries);
        Assert.Equal(Engine(db.Effects.Select(e => e.Nickname).Concat(db.VisEffects.Select(e => e.Nickname))),
            Names(ThnItemKind.Effect));
        Assert.Equal(Engine(db.Costumes.Select(c => c.Nickname)), Names(ThnItemKind.Costume));
    }
}
