using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The item references of the command layer (section 4.0.7 of the M6b design): the parser and the text form of each
/// kind, and the resolve of the scene, data and cutscene forms (MCP1 section 13.1 item 11).
/// </summary>
public sealed class ItemRefsTests
{
    [Theory]
    [InlineData("system:ol_system")]
    [InlineData("obj:ol_station_1")]
    [InlineData("zone:Zone_ol_test_01")]
    [InlineData("light:ol_system/ol_light")]
    [InlineData("nebula:Zone_ol_cloud")]
    [InlineData("field:Zone_ol_field")]
    [InlineData("base:ol_base_1")]
    [InlineData("list:loadout/ol_wreck_loot")]
    [InlineData(@"sec:DATA\SHIPS\loadouts.ini#Loadout/ol_loadout")]
    [InlineData(@"sec:DATA\MISSIONS\OL_TEST\ol_test.ini#Mission")]
    [InlineData(@"sec:DATA\MISSIONS\OL_TEST\ol_test.ini#MsnShip/ol_ship~1")]
    [InlineData(@"sec:DATA\UNIVERSE\mbases.ini#MBase/ol_base>MRoom/bar")]
    [InlineData(@"msn:DATA\MISSIONS\M01A\m01a.ini")]
    [InlineData(@"trg:DATA\MISSIONS\M01A\m01a.ini#ol_trigger")]
    [InlineData(@"line:DATA\MISSIONS\M01A\m01a.ini#Trigger/ol_trigger/2")]
    [InlineData(@"line:DATA\MISSIONS\OL_TEST\ol_test.ini#MsnShip/ol_ship~1/3")]
    [InlineData(@"label:DATA\MISSIONS\M01A\m01a.ini#ol_wing")]
    [InlineData(@"inst:DATA\MISSIONS\M01A\m01a.ini#ol_wave_1")]
    [InlineData(@"inst:DATA\MISSIONS\M01A\m01a.ini#spawn_wave/ol_core")]
    [InlineData(@"act:DATA\MISSIONS\M01A\m01a.ini#act_ambush")]
    [InlineData("prefab:builtin:spawn_wave")]
    [InlineData("prefab:mod:ol_gate_ambush")]
    [InlineData("trg:prefab:mod:ol_gate_ambush#spawn")]
    [InlineData(@"thn:DATA\MODS\ol_mod\CUTSCENES\ol_scene.thn")]
    [InlineData(@"thn:DATA\MODS\ol_mod\CUTSCENES\ol_scene.thn#entity/Camera_01")]
    [InlineData(@"thn:DATA\MODS\ol_mod\CUTSCENES\ol_scene.thn#event/4")]
    [InlineData("group:ol_outpost")]
    [InlineData("group:ol_mod/ol_outpost")]
    [InlineData("asset:solar/ol_archetype")]
    public void EachFormRoundTripsThroughItsParts(string text)
    {
        var reference = ItemRefs.Parse(text);

        Assert.Equal(text, reference.ToString());
        Assert.Equal(reference, ItemRefs.Parse(reference.ToString()));
    }

    [Fact]
    public void TheParserReadsTheMissionFormsIntoTheirParts()
    {
        var line = ItemRefs.Parse("line:DATA/MISSIONS/OL_TEST/./ol_test.ini#MsnShip/ol_ship~1/3");
        Assert.Equal(ItemRefKind.Line, line.Kind);
        Assert.Equal(@"DATA\MISSIONS\OL_TEST\ol_test.ini", line.Path);
        Assert.Equal(new SectionIdentity("MsnShip", "ol_ship", null, 1), line.Section);
        Assert.Equal(3, line.Row);

        var child = ItemRefs.Parse(@"sec:DATA\UNIVERSE\mbases.ini#MBase/ol_base>MRoom/bar~2");
        Assert.Equal(new SectionIdentity("MRoom", "bar", new SectionIdentity("MBase", "ol_base"), 2), child.Section);

        var keyless = ItemRefs.Parse(@"sec:DATA\MISSIONS\OL_TEST\ol_test.ini#Mission");
        Assert.Equal(new SectionIdentity("Mission", null), keyless.Section);

        var trigger = ItemRefs.Parse(@"trg:DATA\MISSIONS\M01A\m01a.ini#ol_trigger");
        Assert.Equal((ItemRefKind.Trigger, "ol_trigger"), (trigger.Kind, trigger.Name));

        var instance = ItemRefs.Parse(@"inst:DATA\MISSIONS\M01A\m01a.ini#spawn_wave/ol_core");
        Assert.Equal("spawn_wave/ol_core", instance.Name);

        var template = ItemRefs.Parse("trg:prefab:mod:ol_gate_ambush#spawn");
        Assert.Equal(("prefab:mod:ol_gate_ambush", "spawn"), (template.Path, template.Name));

        var prefab = ItemRefs.Parse("prefab:builtin:spawn_wave");
        Assert.Equal(("builtin", "spawn_wave"), (prefab.Scope, prefab.Name));
    }

    [Theory]
    [InlineData("ol_station_1")]
    [InlineData("unknown:ol_station_1")]
    [InlineData("obj:")]
    [InlineData("light:ol_light")]
    [InlineData(@"sec:DATA\SHIPS\loadouts.ini")]
    [InlineData(@"sec:DATA\SHIPS\loadouts.ini#Loadout/")]
    [InlineData(@"line:DATA\MISSIONS\M01A\m01a.ini#Trigger/ol_trigger")]
    [InlineData(@"line:DATA\MISSIONS\M01A\m01a.ini#Trigger/ol_trigger/-1")]
    [InlineData("prefab:other:ol_x")]
    [InlineData(@"thn:DATA\x.thn#camera/1")]
    [InlineData(@"sec:C:\DATA\x.ini#Good/g1")]
    [InlineData(@"msn:..\x.ini")]
    [InlineData(@"msn:DATA\x.ini:stream")]
    public void AnInvalidReferenceGivesInvalidArgument(string text)
    {
        var error = Assert.Throws<EditException>(() => ItemRefs.Parse(text)).Error;

        Assert.Equal(EditErrorKind.InvalidArgument, error.Kind);
    }

    [Fact]
    public void EachSceneAndDataFormResolvesToItsSectionAndBack()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        var cases = new Dictionary<string, SectionRef>
        {
            ["system:Li02"] = new(ListWorkspace.UniversePath, new SectionIdentity("system", "Li02")),
            ["base:Li01_01_Base"] = new(ListWorkspace.UniversePath, new SectionIdentity("Base", "Li01_01_Base")),
            ["obj:Li01_child"] = SceneWorkspace.Object("Li01_child"),
            ["obj:Li02_01"] = SceneWorkspace.Li02("Object", "Li02_01"),
            ["zone:Zone_hole"] = SceneWorkspace.Zone("Zone_hole"),
            ["light:Li01/light1"] = SceneWorkspace.Li01("LightSource", "light1"),
            ["nebula:Zone_cloud"] = SceneWorkspace.Li01("Nebula", "Zone_cloud"),
            ["field:Zone_cloud"] = SceneWorkspace.Li01("Asteroids", "Zone_cloud"),
            [$@"sec:{ListWorkspace.ShipLoadoutsPath}#Loadout/lo_station"] =
                new(ListWorkspace.ShipLoadoutsPath, new SectionIdentity("Loadout", "lo_station")),
        };

        foreach (var (text, expected) in cases)
        {
            var section = ItemRefs.ResolveSection(session, ItemRefs.Parse(text));
            Assert.Equal(expected, section);
            // A result gives the path as the session stores it, so a sec: form can differ in letter case.
            Assert.Equal(text, ItemRefs.Canonical(session, section), ignoreCase: true);
        }
    }

    [Fact]
    public void AListEntryResolvesToItsSectionAndAResultGivesTheCanonicalForm()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");

        var section = ItemRefs.ResolveSection(session, ItemRefs.Parse("list:base/Li02_01_Base"));
        var bySection = ItemRefs.ResolveSection(session,
            ItemRefs.Parse($@"sec:{ListWorkspace.UniversePath}#Base/Li02_01_Base"));

        Assert.Equal(new SectionRef(ListWorkspace.UniversePath, new SectionIdentity("Base", "Li02_01_Base")), section);
        Assert.Equal(section, bySection);
        // A result gives the most specific form, not sec:.
        Assert.Equal("base:Li02_01_Base", ItemRefs.Canonical(session, bySection));
        // A repeated light has no light: form that names one of the two.
        var twin = SceneWorkspace.Li01("LightSource", "twin") with
        {
            Id = new SectionIdentity("LightSource", "twin", null, 1),
        };
        Assert.Equal($@"sec:{SceneWorkspace.Li01Path}#LightSource/twin~1", ItemRefs.Canonical(session, twin));
    }

    /// <summary>A trigger of a mission script and of the template of a prefab in prefab mode gets the trg: form. Another
    /// section of a template keeps the sec: form (section 5.6 of the MCP1 design).</summary>
    [Fact]
    public void ATriggerOfAScriptAndOfATemplateGetsTheTriggerForm()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        const string template = "prefab:mod:ol_gate";

        Assert.Equal(@"trg:DATA\MISSIONS\OL_X\ol_x.ini#ol_go",
            ItemRefs.Canonical(session, new SectionRef(@"DATA\MISSIONS\OL_X\ol_x.ini", new SectionIdentity("Trigger", "ol_go"))));
        Assert.Equal("trg:prefab:mod:ol_gate#spawn",
            ItemRefs.Canonical(session, new SectionRef(template, new SectionIdentity("Trigger", "spawn"))));
        Assert.Equal("sec:prefab:mod:ol_gate#MsnShip/ship",
            ItemRefs.Canonical(session, new SectionRef(template, new SectionIdentity("MsnShip", "ship"))));
    }

    [Fact]
    public void AReferenceMatchesWithoutLetterCaseAndGivesTheStoredPath()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");

        var section = ItemRefs.ResolveSection(session,
            ItemRefs.Parse("sec:data/universe/systems/li01/LI01.INI#OBJECT/li01_child"));

        // The session stores the path in the form that opened the file first.
        Assert.Equal(SceneWorkspace.Li01Path, section.Path, ignoreCase: true);
        Assert.Equal("Li01_child", section.Id.Key);
        Assert.Equal("obj:Li01_child", ItemRefs.Canonical(session, section));
    }

    [Fact]
    public void AKeyWithASpaceResolves()
    {
        using var ws = SceneWorkspace.Create();
        ws.WriteBase(SceneWorkspace.Li02Path, SceneWorkspace.Li02Text + "\n[Object]\nnickname = Li02 old depot\narchetype = station1\n");
        var session = ws.Session("act");

        var section = ItemRefs.ResolveSection(session, ItemRefs.Parse("obj:Li02 old depot"));

        Assert.Equal(SceneWorkspace.Li02("Object", "Li02 old depot"), section);
        Assert.Equal("obj:Li02 old depot", ItemRefs.Canonical(session, section));
    }

    [Fact]
    public void AReferenceThatDoesNotResolveGivesNotFoundWithTheClosestNames()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");

        var error = Assert.Throws<EditException>(() =>
            ItemRefs.ResolveSection(session, ItemRefs.Parse("obj:Li01_chlid"))).Error;

        Assert.Equal(EditErrorKind.NotFound, error.Kind);
        Assert.Equal("obj:Li01_chlid", error.Target);
        Assert.Equal("The edit view has no object 'Li01_chlid'. Use a name that the edit view has. The closest names " +
                     "are: Li01_child.", error.Message);
        Assert.Equal(EditErrorKind.NotFound, Assert.Throws<EditException>(() =>
            ItemRefs.ResolveSection(session, ItemRefs.Parse("zone:Li01_child"))).Error.Kind);
        Assert.Equal(EditErrorKind.InvalidArgument, Assert.Throws<EditException>(() =>
            ItemRefs.ResolveSection(session, ItemRefs.Parse("group:ol_outpost"))).Error.Kind);
    }

    [Fact]
    public void ADeletedSectionKeepsItsSpecificForm()
    {
        using var ws = SceneWorkspace.Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.DeleteSection(SceneWorkspace.Object("Li01_dock")));

        Assert.Equal("obj:Li01_dock", ItemRefs.Canonical(session, SceneWorkspace.Object("Li01_dock")));
    }

    [Fact]
    public void CutsceneFormsResolveAndAnEventGuardFindsAChange()
    {
        using var ws = ThnSessions.Workspace();
        var session = ws.Session("act");
        var path = ThnSessions.ScriptPath;

        Assert.Equal(path, ItemRefs.ResolveCutscene(session, ItemRefs.Parse($"thn:{path}")));
        Assert.Equal(path, ItemRefs.ResolveCutscene(session, ItemRefs.Parse($"thn:{path.ToLowerInvariant()}")));
        var entity = ItemRefs.ResolveCutsceneElement(session, ItemRefs.Parse($"thn:{path}#entity/voice_trent"));
        Assert.Equal(ThnSessions.Entity(session, "Voice_trent"), entity);
        Assert.Equal($"thn:{path}#entity/Voice_trent", ItemRefs.Of(session, entity));

        var reference = ItemRefs.Parse($"thn:{path}#event/4");
        var sound = ItemRefs.ResolveCutsceneElement(session, reference, "START_SOUND", 6);
        Assert.Equal(ThnSessions.Event(session, 4), sound);
        Assert.Equal(reference.ToString(), ItemRefs.Of(session, sound));

        var stale = Assert.Throws<EditException>(() =>
            ItemRefs.ResolveCutsceneElement(session, reference, "START_PSYS")).Error;
        Assert.Equal(EditErrorKind.StaleRevision, stale.Kind);
        Assert.Equal("RowChanged", stale.Detail);
        Assert.Equal(EditErrorKind.StaleRevision, Assert.Throws<EditException>(() =>
            ItemRefs.ResolveCutsceneElement(session, reference, expectTime: 5)).Error.Kind);
        Assert.Equal(EditErrorKind.NotFound, Assert.Throws<EditException>(() =>
            ItemRefs.ResolveCutsceneElement(session, ItemRefs.Parse($"thn:{path}#event/6"))).Error.Kind);
        Assert.Equal(EditErrorKind.NotFound, Assert.Throws<EditException>(() =>
            ItemRefs.ResolveCutscene(session, ItemRefs.Parse(@"thn:DATA\x.thn"))).Error.Kind);
    }

    [Theory]
    [InlineData(@"sec:DATA\MISSIONS\OL_TEST\ol_test.ini#MsnShip/ol_a~1~0", "MsnShip", "ol_a~1", 0)]
    [InlineData(@"sec:DATA\MISSIONS\OL_TEST\ol_test.ini#MsnShip/ol_a~1~2", "MsnShip", "ol_a~1", 2)]
    [InlineData(@"sec:DATA\MISSIONS\OL_TEST\ol_test.ini#MsnShip/ol_a~", "MsnShip", "ol_a~", 0)]
    [InlineData(@"sec:DATA\EQUIPMENT\goods.ini#Good/ol_a>ol_b", "Good", "ol_a>ol_b", 0)]
    public void AKeyWithTheMarksOfTheSectionPartRoundTrips(string text, string type, string key, int ordinal)
    {
        var reference = ItemRefs.Parse(text);

        Assert.Equal(new SectionIdentity(type, key, null, ordinal), reference.Section);
        Assert.Equal(text, reference.ToString());
    }

    [Fact]
    public void AGreaterThanSignStartsAChildOnlyBeforeAChildType()
    {
        var child = new SectionIdentity("MRoom", "bar", new SectionIdentity("MBase", "ol>base"));
        var text = ItemRefs.Of(new SectionRef(@"DATA\UNIVERSE\mbases.ini", child));

        Assert.Equal(@"sec:DATA\UNIVERSE\mbases.ini#MBase/ol>base>MRoom/bar", text);
        Assert.Equal(child, ItemRefs.Parse(text).Section);
        // A key that ends with the ordinal mark keeps the ordinal 0.
        Assert.Equal("MsnShip/ol_a~1~0", ItemRefs.SectionText(new SectionIdentity("MsnShip", "ol_a~1")));
    }

    [Fact]
    public void ATriggerOfAMissionScriptGivesTheTriggerFormAndItsOwnErrors()
    {
        using var ws = OlReplaceMergeTests.Workspace(OlReplaceMergeTests.Script +
                                                     "\n\n[Trigger]\nnickname = ol_wave\nCnd_True = no_params\n");
        ws.Mod("act", new Dictionary<string, string>());
        ws.WriteProfile("act");
        var session = ws.Session("act");
        var path = OlReplaceMergeTests.ScriptPath;
        var wave = new SectionRef(path, new SectionIdentity("Trigger", "ol_wave"));

        Assert.Equal($"trg:{path}#ol_wave", ItemRefs.Canonical(session, wave));
        Assert.Equal(wave, ItemRefs.ResolveSection(session, ItemRefs.Parse($"trg:{path}#OL_WAVE")));
        // The trg: form names the first trigger with a nickname, so the second keeps the sec: form.
        var second = wave with { Id = new SectionIdentity("Trigger", "ol_wave", null, 1) };
        Assert.Equal($"sec:{path}#Trigger/ol_wave~1", ItemRefs.Canonical(session, second));
        // Another section of a mission file keeps the sec: form.
        Assert.Equal($"sec:{path}#MsnShip/ol_ship_a",
            ItemRefs.Canonical(session, new SectionRef(path, new SectionIdentity("MsnShip", "ol_ship_a"))));

        var trigger = Assert.Throws<EditException>(() =>
            ItemRefs.ResolveSection(session, ItemRefs.Parse($"trg:{path}#ol_wav"))).Error;
        Assert.Equal((EditErrorKind.NotFound, OpenLancer.Data.Missions.Editing.MissionErrors.TriggerNotFound), (trigger.Kind, trigger.Detail));
        Assert.Equal($"Script {path} has no trigger 'ol_wav'. Pick a trigger of this script. The closest names are: " +
                     "ol_wave.", trigger.Message);
        var script = Assert.Throws<EditException>(() =>
            ItemRefs.ResolveSection(session, ItemRefs.Parse(@"trg:DATA\MISSIONS\OL_NONE\ol_none.ini#ol_wave"))).Error;
        Assert.Equal((EditErrorKind.NotFound, OpenLancer.Data.Missions.Editing.MissionErrors.ScriptNotFound), (script.Kind, script.Detail));
        Assert.Equal(@"The game data and the mods have no mission script DATA\MISSIONS\OL_NONE\ol_none.ini. Pick a " +
                     "script from the Missions tab.", script.Message);
    }

    [Fact]
    public void TheClosestNamesAreAtMostThreeAndNear()
    {
        var names = new[] { "ol_station_1", "ol_station_2", "ol_station_10", "ol_station_3", "far_away" };

        Assert.Equal(["ol_station_1", "ol_station_2", "ol_station_3"], ItemRefs.Closest("ol_station_4", names));
        Assert.Empty(ItemRefs.Closest("nothing", names.Take(1)));
    }

    /// <summary>The distance of two names costs the product of their lengths, and a call runs on the thread of the
    /// session. So a long name gets no closest names, and the names of another length do not count.</summary>
    [Fact]
    public void ALongNameGetsNoClosestNames()
    {
        var name = new string('x', ItemRefs.MaxHintName);
        var names = Enumerable.Range(1000, 100).Select(i => new string('x', ItemRefs.MaxHintName - 4) + i).ToList();

        Assert.Empty(ItemRefs.Closest(name + "x", [name]));
        Assert.Equal([name], ItemRefs.Closest(name[..^1] + "y", [name, "x"]));
        Assert.Equal(3, ItemRefs.Closest(name, names).Count);
    }
}
