using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Schema;
using LibreLancer.Thn;
using LibreLancer.Utf.Anm;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Ui;
using Xunit;
using static OpenLancer.Tests.ThnScripts;

namespace OpenLancer.Tests;

/// <summary>
/// The animation picker of a START_MOTION event without ImGui (section 12.1 of the M8b design). The tests cover the row
/// choice, the groups and the filter of the list, the labels, the clip preview and the state of the session. The clip,
/// file and character names are made up.
/// </summary>
public sealed class ThnAnimationPickerTests
{
    private const string BodyMale = @"DATA\characters\animations\body_m.anm";
    private const string HeadMale = @"DATA\characters\animations\head_m.anm";
    private const string BodyFemale = @"DATA\characters\animations\body_f.anm";
    private const string Shared = @"DATA\characters\animations\shared.anm";
    private const string Hands = @"DATA\characters\animations\hands.anm";
    private const string Mod = @"DATA\characters\animations\mod.anm";

    private const string FemaleIdle = "Sc_FMBODY_STND_IDLE_a";
    private const string HandClip = "Sc_hand_a";
    private const string MaleIdle = "Sc_MLBODY_STND_IDLE_a";
    private const string MaleWalk = "Sc_MLBODY_WALK_a";
    private const string MaleNod = "Sc_MLHEAD_NOD_a";
    private const string ModClip = "Sc_mod_a";
    private const string SharedClip = "Sc_shared_a";

    /// <summary>
    /// A script with three characters, a COMPOUND, and two names that two entities have. For such a name, the last entity
    /// counts, as in the engine.
    /// </summary>
    private const string Script = """
        duration = 10
        entities = {
            { entity_name = "Hero", type = DEFORMABLE, template_name = "hero_costume" },
            { entity_name = "Door", type = COMPOUND, template_name = "door_model" },
            { entity_name = "Twin", type = COMPOUND },
            { entity_name = "TWIN", type = DEFORMABLE, template_name = "twin_costume" },
            { entity_name = "Shifted", type = DEFORMABLE },
            { entity_name = "shifted", type = COMPOUND },
            { entity_name = "Guard", type = DEFORMABLE }
        }
        events = {
            { 1, START_MOTION, { "Hero" }, { animation = "Sc_walk" } }
        }
        """;

    /// <summary>
    /// Risk R5 of the M8b design: a COMPOUND plays the scripts of its model, so only a DEFORMABLE first target gets the
    /// picker. The type comes from the entity that the engine uses for the target name. A target that names no entity,
    /// an event without a target, another key and another event keep the text row.
    /// </summary>
    [Fact]
    public void OnlyTheAnimationOfACharacterGetsThePicker()
    {
        var script = ThnScriptModel.From(Evaluate(Script), ThnElementId.Counter());
        const string path = ThnTemplateRules.AnimationPath;

        Assert.True(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, script, ["Hero"]));
        Assert.True(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, script, ["hero", "Door"]));
        Assert.True(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, script, ["twin"]));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, script, ["Door", "Hero"]));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, script, ["Shifted"]));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, script, ["Nobody"]));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, script, []));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, path, null, ["Hero"]));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartMotion, "props.start_time", script, ["Hero"]));
        Assert.False(ThnAnimationPicker.Applies(EventTypes.StartSound, path, script, ["Hero"]));
        Assert.False(ThnAnimationPicker.Applies(null, path, script, ["Hero"]));
        var entities = script.Entities.Select(e => e.Table).ToList();
        Assert.Equal("COMPOUND", ThnTemplateRules.TargetType(entities, ["Door"], Evaluator.Symbols));
        Assert.Null(ThnTemplateRules.TargetType(entities, ["Nobody"], Evaluator.Symbols));
    }

    /// <summary>
    /// The picker and the save gate use one rule for the clip list (<see cref="ThnTemplateRules.PlaysClips"/>). So the
    /// gate checks the clip of a START_MOTION event exactly where the row gets the picker: on a DEFORMABLE, not on a
    /// COMPOUND, on the last entity of a name of two entities, and not on a target that names no entity.
    /// </summary>
    [Fact]
    public void ThePickerAndTheSaveGateUseOneRuleForTheClipList()
    {
        string[] targets = ["Hero", "hero", "Door", "Twin", "Shifted", "Guard", "Nobody"];
        var events = string.Join(",\n", targets.Select((t, i) =>
            $$"""{ {{i + 1}}, START_MOTION, { "{{t}}" }, { animation = "Sc_unknown" } }"""));
        var text = Script[..Script.IndexOf("events = {", StringComparison.Ordinal)] + $"events = {{\n{events}\n}}\n";
        var globals = Evaluate(text);
        var script = ThnScriptModel.From(globals, ThnElementId.Counter());

        var checkedTargets = ThnChecks.NameProblems(@"DATA\SCRIPTS\x.thn", globals, Evaluator, new NoClips())
            .Select(p => targets[p.Cutscene!.Index]).ToList();
        var pickerTargets = targets
            .Where(t => ThnAnimationPicker.Applies(EventTypes.StartMotion, ThnTemplateRules.AnimationPath, script, [t]))
            .ToList();

        Assert.Equal(["Hero", "hero", "Twin", "Guard"], pickerTargets);
        Assert.Equal(pickerTargets, checkedTargets);
    }

    /// <summary>The field, the side pane and the save gate give one text for a clip that the game does not have: the
    /// reason and the consequence come from <see cref="ThnTemplateRules.ClipProblem"/>.</summary>
    [Fact]
    public void TheFieldAndTheSaveGateShareTheTextOfAnUnknownClip()
    {
        var clips = new AnimationClips([new AnimationClip(MaleWalk, BodyMale)], []);
        var text = ThnTemplateRules.ClipProblem("Sc_typo");
        var gate = ThnChecks.NameProblems(@"DATA\SCRIPTS\x.thn", Evaluate(Script.Replace("Sc_walk", "Sc_typo",
            StringComparison.Ordinal)), Evaluator, new NoClips()).Single();

        Assert.Equal("The game has no animation \"Sc_typo\". The character does not move.", text);
        Assert.EndsWith(ThnTemplateRules.ClipConsequence, text, StringComparison.Ordinal);
        Assert.StartsWith(text, ThnAnimationPicker.FieldTooltip(clips, null, "Sc_typo", null), StringComparison.Ordinal);
        Assert.Equal($"event {{ 1, START_MOTION, {{ \"Hero\" }} }}, props.animation: {text}", gate.Message);
    }

    /// <summary>
    /// The closed field shows a known clip in the text color. An empty or an unknown clip has the color and the red
    /// frame of an error when the save gate blocks it. Else it has the warning color, for example for a clip that the
    /// lower cutscene has with the same problem. ThnClipGateTests reads the gate state.
    /// </summary>
    [Fact]
    public void TheFieldShowsAClipThatTheSaveGateBlocksAsAnError()
    {
        Assert.Equal((MaleWalk, EditorTheme.Text, false), ThnAnimationPicker.FieldLook(MaleWalk, true, false));
        Assert.Equal(("Missing (Animation): Sc_typo", EditorTheme.Error, true),
            ThnAnimationPicker.FieldLook("Sc_typo", false, true));
        Assert.Equal(("Missing (Animation): Sc_typo", EditorTheme.Warning, false),
            ThnAnimationPicker.FieldLook("Sc_typo", false, false));
        Assert.Equal(("Not set", EditorTheme.Error, true), ThnAnimationPicker.FieldLook("", false, true));
        Assert.Equal(("Not set", EditorTheme.Warning, false), ThnAnimationPicker.FieldLook("", false, false));
    }

    /// <summary>Game data that has each item and no clip.</summary>
    private sealed class NoClips : IThnGameData
    {
        public bool MakesCompounds => true;

        public ThnItemState? Item(ThnItemKind kind, string template) =>
            kind == ThnItemKind.Animation ? ThnItemState.Missing : ThnItemState.Ready;
    }

    /// <summary>"Preview on" lists the characters of the script, each name once, in the order of the script. A name whose
    /// last entity is a COMPOUND is not a character. The costume of a character comes from its template_name.</summary>
    [Fact]
    public void TheCharactersAndTheirTemplatesComeFromTheScript()
    {
        var script = ThnScriptModel.From(Evaluate(Script), ThnElementId.Counter());

        Assert.Equal(["Hero", "Twin", "Guard"], ThnAnimationPicker.Characters(script));
        Assert.Empty(ThnAnimationPicker.Characters(null));
        Assert.Equal("hero_costume", ThnAnimationPicker.Template(script, "HERO"));
        Assert.Equal("twin_costume", ThnAnimationPicker.Template(script, "Twin"));
        Assert.Null(ThnAnimationPicker.Template(script, "Guard"));
        Assert.Null(ThnAnimationPicker.Template(script, "Nobody"));
        Assert.Null(ThnAnimationPicker.Template(null, "Hero"));
    }

    /// <summary>A word fits only at the start of a part in capitals. So a word inside a part and the name of a character
    /// give no label, and a name without a word gets no label.</summary>
    [Fact]
    public void ALabelComesFromAWordAtTheStartOfAPartInCapitals()
    {
        Assert.Equal("Walk", AnimationClips.Label("Sc_MLBODY_WALK_000LV_TEST"));
        Assert.Equal("Walk", AnimationClips.Label("Sc_FMBODY_WLKG_TEST"));
        Assert.Equal("Gesture", AnimationClips.Label("Sc_FMBODY_CHRB_GESTL_TEST"));
        Assert.Equal("Sit", AnimationClips.Label("Sc_MLBODY_SITCL_TEST"));
        Assert.Equal("Idle", AnimationClips.Label("Sc_MLBODY_STND_IDLE_test"));
        // TURN inside RETURNL and the character name Walker give no label.
        Assert.Equal("", AnimationClips.Label("Sc_MLBODY_STND_RETURNL_TEST"));
        Assert.Equal("", AnimationClips.Label("Sc_dx_s001_Walker"));
        Assert.Equal("", AnimationClips.Label("Sc_MLHEAD_MOTION_TEST"));
        // A word before another word in the list wins.
        Assert.Equal("Walk", AnimationClips.Label("Sc_TALK_WALK"));
    }

    /// <summary>
    /// Decision D119: the clips of the skeletons of the costume of the target come first, then the other clips. Each group
    /// is sorted by name. A target without a costume gets one group without a header, and a value outside the list gets
    /// the "Missing" row first. The closed field takes the label that the list made.
    /// </summary>
    [Fact]
    public void TheSkeletonsOfTheCostumeOfTheTargetComeFirst()
    {
        var clips = Clips();

        var model = clips.Model("Sc_typo", Set("BODY_M1", "head_m1"), "Hero");

        Assert.Equal(ListRowKind.Missing, model.Rows[0].Kind);
        Assert.Equal(0, model.Current);
        var entries = model.Rows.Skip(1).ToList();
        Assert.Equal([MaleIdle, MaleWalk, MaleNod, SharedClip, FemaleIdle, HandClip, ModClip], entries.Select(r => r.Value));
        Assert.Equal(Enumerable.Repeat("Skeletons of Hero", 4).Concat(Enumerable.Repeat(AnimationClips.OtherSkeletons, 3)),
            entries.Select(r => r.Group));

        var plain = clips.Model("sc_mlhead_NOD_a", null, "Hero");
        Assert.Equal(clips.Clips.Select(c => c.Name), plain.Rows.Select(r => r.Value));
        Assert.All(plain.Rows, r => Assert.Null(r.Group));
        Assert.Equal(MaleNod, plain.Rows[plain.Current].Value);
        Assert.Equal(["Idle", "", "Idle", "Walk", "Nod", "", ""], plain.Rows.Select(r => r.Note));
        Assert.Equal("Walk", clips.LabelOf(clips.Find(MaleWalk)!));
    }

    /// <summary>
    /// A name in two files belongs to the skeletons of both files, as decision D119 groups the scripts of the files of a
    /// skeleton. The filter and the idle use both files. The game plays the clip of the last file, and the pane says so.
    /// </summary>
    [Fact]
    public void ANameInTwoFilesBelongsToTheSkeletonsOfBothFiles()
    {
        AnimationClip blink = new("Sc_blink", BodyFemale) { Archives = [HeadMale, BodyFemale] };
        var clips = new AnimationClips([blink, new(MaleIdle, BodyMale)], Skeletons());

        var model = clips.Model("", Set("head_m1"), "Hero");

        Assert.Equal(["Skeletons of Hero", AnimationClips.OtherSkeletons], model.Rows.Select(r => r.Group));
        Assert.Equal("Sc_blink", model.Rows[0].Value);
        Assert.Equal(SkeletonParts.Body | SkeletonParts.Head, clips.PartsOf(blink));
        Assert.True(clips.Shows(model.Rows[0], ClipPart.Head));
        Assert.Equal("Skeletons: male head, female body", clips.SkeletonText(blink));
        Assert.Equal("Files: head_m.anm, body_f.anm. The game plays the clip of the last file, body_f.anm.",
            AnimationClips.FileText(blink));
        Assert.Equal("File: body_m.anm", AnimationClips.FileText(new AnimationClip(MaleWalk, BodyMale)));
    }

    /// <summary>The body part filter shows the clips of the files of the skeletons with that part. Other shows the clips
    /// of the files that no skeleton lists.</summary>
    [Fact]
    public void TheBodyPartFilterUsesTheSkeletonsOfTheFile()
    {
        var clips = Clips();
        var model = clips.Model("", null, "Hero");

        string[] Shown(ClipPart part) =>
            model.Visible("", r => clips.Shows(r, part)).Select(i => model.Rows[i].Value).ToArray();

        Assert.Equal(model.Rows.Count, Shown(ClipPart.All).Length);
        Assert.Equal([FemaleIdle, MaleIdle, MaleWalk, SharedClip], Shown(ClipPart.Body));
        Assert.Equal([MaleNod, SharedClip], Shown(ClipPart.Head));
        Assert.Equal([HandClip], Shown(ClipPart.Hands));
        Assert.Equal([ModClip], Shown(ClipPart.Other));
        Assert.Equal(SkeletonParts.Body | SkeletonParts.Head, clips.PartsOf(clips.Find(SharedClip.ToUpperInvariant())!));
    }

    [Fact]
    public void ThePaneNamesTheSkeletonsOfTheFile()
    {
        var clips = Clips();

        Assert.Equal("Skeletons: male body, male head", clips.SkeletonText(clips.Find(SharedClip)!));
        Assert.Equal("Skeletons: hands", clips.SkeletonText(clips.Find(HandClip)!));
        Assert.Equal("No skeleton of bodyparts.ini lists this file.", clips.SkeletonText(clips.Find(ModClip)!));
        Assert.True(clips.Contains(ModClip.ToUpperInvariant()));
        Assert.False(clips.Contains("Sc_typo"));
    }

    /// <summary>The game finds a clip by the name as it is. So a value with a space at the start or at the end is not a
    /// clip. The list then shows the "Missing" row, so a click on the clip corrects the value.</summary>
    [Fact]
    public void AValueWithSpacesIsNotAClip()
    {
        var clips = Clips();

        Assert.NotNull(clips.Find(MaleWalk.ToUpperInvariant()));
        Assert.Null(clips.Find($" {MaleWalk} "));
        var model = clips.Model($" {MaleWalk} ", null, "Hero");
        Assert.Equal(ListRowKind.Missing, model.Rows[model.Current].Kind);
        Assert.Contains(model.Rows, r => r.Kind == ListRowKind.Entry && r.Value == MaleWalk);
    }

    /// <summary>The tooltip of the field gives the reason of the read-only state in place of the click hint. The
    /// <b>Preview</b> button gives the reason when it is disabled.</summary>
    [Fact]
    public void TheFieldAndTheButtonGiveTheirReasons()
    {
        var clips = Clips();
        var walk = clips.Find(MaleWalk)!;
        const string readOnly = "The lower cutscene is read-only.";

        Assert.Equal($"{MaleWalk}\nSkeletons: male body\n\n{ThnAnimationPicker.ClickToPick}",
            ThnAnimationPicker.FieldTooltip(clips, walk, MaleWalk, null));
        Assert.Equal($"{MaleWalk}\nSkeletons: male body\n\n{readOnly}",
            ThnAnimationPicker.FieldTooltip(clips, walk, MaleWalk, readOnly));
        Assert.Equal($"{ThnTemplateRules.ClipProblem("Sc_typo")}\n\n{ThnAnimationPicker.ClickToPick}",
            ThnAnimationPicker.FieldTooltip(clips, null, "Sc_typo", null));
        Assert.Equal($"{ThnAnimationPicker.NoClip} {ThnTemplateRules.ClipConsequence}\n\n{readOnly}",
            ThnAnimationPicker.FieldTooltip(clips, null, "", readOnly));

        Assert.Null(ThnAnimationPicker.PreviewDisabled(MaleWalk, true, null));
        Assert.Equal(ThnAnimationPicker.NoClip, ThnAnimationPicker.PreviewDisabled("", true, null));
        Assert.Equal("No room fits.", ThnAnimationPicker.PreviewDisabled(MaleWalk, false, "No room fits."));
        Assert.Equal(ThnAnimationPicker.NoPreview, ThnAnimationPicker.PreviewDisabled(MaleWalk, false, null));
    }

    /// <summary>The clip preview plays on the character that the user chose, else on the target of the event, else on the
    /// first character of the script.</summary>
    [Fact]
    public void TheClipPreviewPlaysOnTheChosenCharacterOrTheTarget()
    {
        string[] characters = ["Hero", "Twin", "Guard"];

        Assert.Equal("Twin", AnimationClips.PreviewOn(characters, null, "twin"));
        Assert.Equal("Guard", AnimationClips.PreviewOn(characters, "GUARD", "Twin"));
        Assert.Equal("Twin", AnimationClips.PreviewOn(characters, "gone", "Twin"));
        Assert.Equal("Hero", AnimationClips.PreviewOn(characters, null, "Not_a_character"));
        Assert.Null(AnimationClips.PreviewOn([], null, "Twin"));
    }

    /// <summary>A head or a hand clip plays over the standing idle of a body skeleton of the sex of the body. A body clip
    /// gets no idle. Without a sex, the idle has the sex of the skeletons of the clip.</summary>
    [Fact]
    public void AHeadClipPlaysOverTheIdleOfTheSexOfTheBody()
    {
        var clips = Clips();
        var nod = clips.Find(MaleNod)!;

        Assert.Equal(MaleIdle, clips.Idle(FLGender.male));
        Assert.Equal(FemaleIdle, clips.Idle(FLGender.female));
        Assert.Null(clips.Idle(FLGender.unset));
        Assert.Equal(FemaleIdle, clips.IdleFor(nod, FLGender.female));
        Assert.Equal(MaleIdle, clips.IdleFor(nod, FLGender.unset));
        Assert.Equal(MaleIdle, clips.IdleFor(clips.Find(HandClip)!, FLGender.male));
        Assert.Null(clips.IdleFor(clips.Find(MaleWalk)!, FLGender.female));
        Assert.Null(clips.IdleFor(clips.Find(SharedClip)!, FLGender.male));
    }

    /// <summary>
    /// The state makes the clips once for each data of the list <c>animation</c>. New data, for example after an edit of
    /// the <c>[Animations]</c> lines, makes them again. No data and data without clips give the reason, and the row keeps
    /// its text field.
    /// </summary>
    [Fact]
    public void TheStateMakesTheClipsOnceForEachDataOfTheList()
    {
        var state = new ClipPickerState();
        var data = new AnimationData(Clips().Clips, Skeletons(), []);

        var clips = state.Get(data, out var reason);
        Assert.NotNull(clips);
        Assert.Null(reason);
        Assert.Same(clips, state.Get(data, out _));
        Assert.NotSame(clips, state.Get(data with { }, out _));
        Assert.Null(state.Get(null, out reason));
        Assert.Equal(ClipPickerState.NoBodyparts, reason);
        Assert.Null(state.Get(new AnimationData([], [], []), out reason));
        Assert.Equal("The editor found no animation clips in the game data.", reason);
    }

    /// <summary>The chosen character belongs to one event, so another event forgets it. Reset forgets the data of the
    /// list, the chosen character, the shown event and the filter, so the next profile starts clean.</summary>
    [Fact]
    public void ResetForgetsTheGameDataAndTheChoices()
    {
        var state = new ClipPickerState();
        var data = new AnimationData(Clips().Clips, Skeletons(), []);
        var first = new ThnRef("a.thn", ThnElementKind.Event, new ThnElementId(1));
        var second = new ThnRef("a.thn", ThnElementKind.Event, new ThnElementId(2));
        var clips = state.Get(data, out _);
        state.Show(first);
        state.Chosen = "Guard";
        state.Part = ClipPart.Head;

        state.Show(first with { });
        Assert.Equal("Guard", state.Chosen);
        state.Show(second);
        Assert.Null(state.Chosen);

        state.Chosen = "Guard";
        state.Reset();
        Assert.Null(state.Chosen);
        Assert.Equal(ClipPart.All, state.Part);
        Assert.NotSame(clips, state.Get(data, out _));
        state.Chosen = "Guard";
        state.Show(second);
        Assert.Null(state.Chosen);
    }

    /// <summary>
    /// The picker reads the data list <c>animation</c> of the session, so an unsaved <c>[Animations]</c> line shows its
    /// clips at once. A file of the list that does not read gives a warning, which the side pane shows, and the other
    /// files keep their clips.
    /// </summary>
    [Fact]
    public void ThePickerReadsTheSessionListWithTheUnsavedEdits()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        var state = new ClipPickerState();

        var before = state.Get(session.Lists.Animations(), out _)!;
        session.Execute(AnimationWorkspace.AddLine(@"characters\animations\extra.anm"));
        var after = state.Get(session.Lists.Animations(), out _)!;
        session.Execute(AnimationWorkspace.AddLine(@"characters\animations\broken.anm"));
        var broken = state.Get(session.Lists.Animations(), out _)!;

        Assert.False(before.Contains("Sc_Extra"));
        Assert.True(after.Contains("Sc_Extra"));
        Assert.Empty(after.Warnings);
        var warning = Assert.Single(broken.Warnings);
        Assert.Equal("broken.anm", AnimationClips.FileName(warning.Source), ignoreCase: true);
        Assert.True(broken.Contains("Sc_Extra"));
    }

    /// <summary>
    /// Decision D119 with a costume that only W has: the costume of the target comes from the costume list of W, so an
    /// unsaved new costume orders the clips too. Its body gives the skeletons of the first group and the sex of the idle.
    /// A template that the list does not have gives no costume, so the list has one group without a header.
    /// </summary>
    [Fact]
    public void ACostumeThatOnlyWHasOrdersTheClips()
    {
        using var ws = AnimationWorkspace.Create();
        var session = ws.Session("act");
        ThnDataWorkspace.SetTemplate(session, "Man", "newbie");
        var clips = AnimationClips.From(session.Lists.Animations()!);
        Assert.Null(ThnAnimationPicker.CostumeOf(session, ThnSessions.W(session), "Man"));

        session.Execute(EditCommand.AddSection(ThnDataWorkspace.CostumesPath, "Costume",
            [ThnDataWorkspace.Line("nickname", "newbie"), ThnDataWorkspace.Line("body", "body1"),
                ThnDataWorkspace.Line("head", "head1")]));
        var parts = ThnAnimationPicker.CostumeOf(session, ThnSessions.W(session), "MAN")!;
        var model = clips.Model("", parts.Names, "Man");

        Assert.Equal("body1", parts.Body);
        Assert.True(parts.Names.SetEquals(["BODY1", "head1"]));
        Assert.Equal([("Sc_Run", "Skeletons of Man"), ("SC_WALK", "Skeletons of Man"), ("Sc_Sit", AnimationClips.OtherSkeletons)],
            model.Rows.Select(r => (r.Value, r.Group)));
        Assert.Equal(FLGender.male, clips.SexOf(parts));
        Assert.Equal(FLGender.unset, clips.SexOf(null));
        Assert.Equal(FLGender.unset, clips.SexOf(new CostumeParts("body_x", Set("body_x"))));
        Assert.Null(ThnAnimationPicker.CostumeOf(session, ThnSessions.W(session), "Ship"));
    }

    /// <summary>
    /// The clip preview plays the scripts of the table of the game data of the last save. A clip of the list that the
    /// table does not have gives the text that tells the user to save. A table that does not read gives the reason, and
    /// the editor does not stop. The engine then keeps a part of the table, so the error stays for that game data. Other
    /// game data reads its own table.
    /// </summary>
    [Fact]
    public void TheClipPreviewGivesTheReasonForAClipThatTheSavedDataDoesNotPlay()
    {
        var saved = new SavedClips();
        var data = new object();
        var table = new AnmFile();
        table.Scripts[MaleNod] = new Script(MaleNod);
        table.Scripts[MaleIdle] = new Script(MaleIdle);
        var broken = new object();
        var reads = 0;

        AnmFile Unreadable()
        {
            reads++;
            throw new Exception("Anm Header malformed");
        }

        Assert.Same(table.Scripts[MaleNod], saved.Find(data, () => table, MaleNod, MaleIdle, out var idle, out var problem));
        Assert.Same(table.Scripts[MaleIdle], idle);
        Assert.Null(problem);
        Assert.Null(saved.Find(data, () => table, ModClip, MaleIdle, out idle, out problem));
        Assert.Null(idle);
        Assert.Equal(SavedClips.NotSaved, problem);
        Assert.Null(saved.Find(broken, Unreadable, MaleNod, null, out _, out problem));
        Assert.Equal($"{SavedClips.Unreadable} Anm Header malformed", problem);
        Assert.Null(saved.Find(broken, () => table, MaleNod, null, out _, out problem));
        Assert.Equal($"{SavedClips.Unreadable} Anm Header malformed", problem);
        Assert.Equal(1, reads);
        Assert.NotNull(saved.Find(new object(), () => table, MaleNod, null, out _, out problem));
        Assert.Null(problem);
    }

    /// <summary>The clip preview keeps its character while the side pane draws in each frame. It frees the character at
    /// the start of the first frame after a frame without the pane, for example after a click on another element of the
    /// Timeline.</summary>
    [Fact]
    public void TheClipPreviewFreesItsCharacterAfterAFrameWithoutThePane()
    {
        // The pane never drew.
        Assert.False(ThnAnimationPicker.Releases(-1, 10));
        // The pane drew in the last frame.
        Assert.False(ThnAnimationPicker.Releases(9, 10));
        // The pane did not draw in the last frame.
        Assert.True(ThnAnimationPicker.Releases(8, 10));
        Assert.True(ThnAnimationPicker.Releases(2, 10));
    }

    /// <summary>Reset and Dispose of the picker free a clip preview that never drew, and they can run more than once. The
    /// profile close calls Reset, and the window close calls Dispose after it.</summary>
    [Fact]
    public void ResetAndDisposeRunWithoutAClipPreview()
    {
        var picker = new ThnAnimationPicker(null!, null!);

        picker.Reset();
        picker.Reset();
        picker.Dispose();
        picker.Reset();
        picker.Dispose();
    }

    /// <summary>
    /// Clips of a male body, a male head, a female body, a file of both male skeletons, hands, and a mod file that no
    /// skeleton lists. The body files have a standing idle each.
    /// </summary>
    private static AnimationClips Clips()
    {
        AnimationClip[] clips =
        [
            new(FemaleIdle, BodyFemale), new(HandClip, Hands), new(MaleIdle, BodyMale), new(MaleWalk, BodyMale),
            new(MaleNod, HeadMale), new(ModClip, Mod), new(SharedClip, Shared),
        ];
        return new AnimationClips(clips, Skeletons());
    }

    private static AnimationSkeleton[] Skeletons() =>
    [
        new([BodyMale, Shared], FLGender.male, SkeletonParts.Body, Set("body_m1")),
        new([HeadMale, Shared], FLGender.male, SkeletonParts.Head, Set("head_m1")),
        new([BodyFemale], FLGender.female, SkeletonParts.Body, Set("body_f1")),
        new([Hands], FLGender.unset, SkeletonParts.Hands, Set("hand_l", "hand_r")),
    ];

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);
}
