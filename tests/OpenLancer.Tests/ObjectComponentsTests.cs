using System;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Inspector;
using Xunit;
using static OpenLancer.Tests.SceneWorkspace;

namespace OpenLancer.Tests;

/// <summary>The components of the sections of a system file (section 5.4 of the M3 design).</summary>
public sealed class ObjectComponentsTests
{
    [Fact]
    public void EachSectionGivesItsComponents()
    {
        using var ws = Create();
        var session = ws.Session("act");

        Assert.Equal([ObjectComponent.Solar, ObjectComponent.LoadoutUser, ObjectComponent.DockableStation,
            ObjectComponent.BaseObject, ObjectComponent.FactionMember], Kinds(session, Object("Li01_01")));
        Assert.Equal([ObjectComponent.Solar, ObjectComponent.JumpConnection], Kinds(session, Object("Li01_to_Li02")));
        Assert.Equal([ObjectComponent.Solar, ObjectComponent.TradeLaneRing], Kinds(session, Object("ring1")));
        Assert.Equal([ObjectComponent.Solar, ObjectComponent.PlanetRing], Kinds(session, Object("Li01_planet")));
        Assert.Equal([ObjectComponent.FieldReferences], Kinds(session, Li01("Nebula", "Zone_cloud")));
        Assert.Empty(Kinds(session, Zone("Zone_hole")));
        Assert.Empty(ObjectComponents.Of(session, new SectionRef(ListWorkspace.SolarPath, new SectionIdentity("Solar", "station1"))));
    }

    [Fact]
    public void AddAndRemoveAreOneEntryEach()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var dock = Object("Li01_dock");

        Assert.Equal([ObjectComponent.LoadoutUser, ObjectComponent.BaseObject, ObjectComponent.FactionMember],
            ObjectComponents.CanAdd(session, dock).Select(c => c.Kind));
        session.Execute(ObjectComponents.Add(dock, ObjectComponent.LoadoutUser, "lo_station"));
        Assert.Equal("Add Loadout user", session.UndoLabel);
        Assert.Contains(ObjectComponent.LoadoutUser, Kinds(session, dock));
        session.Execute(ObjectComponents.Add(dock, ObjectComponent.FactionMember, "li_n_grp"));
        session.Execute(ObjectComponents.Remove(dock, ObjectComponent.FactionMember));
        Assert.Equal("Remove Faction member", session.UndoLabel);
        Assert.DoesNotContain(ObjectComponent.FactionMember, Kinds(session, dock));
        Assert.Throws<ArgumentException>(() => ObjectComponents.Add(dock, ObjectComponent.JumpConnection, "x"));
    }

    /// <summary>Only Solar is required, and it cannot go (Unity RequireComponent). The connections and the encounter
    /// lines of a zone also cannot go in the Inspector.</summary>
    [Fact]
    public void SolarAndTheConnectionsCannotGo()
    {
        Assert.Equal("Solar: Each object needs an archetype.",
            Assert.Throws<InvalidOperationException>(() => ObjectComponents.Remove(Object("x"), ObjectComponent.Solar)).Message);
        // A delete of the object deletes the pair, so the remove of the goto line names the delete.
        Assert.Equal("The goto line links the object to its partner. To delete a jump gate pair or a jump hole pair, " +
                     "delete one of its objects.", ObjectComponents.RemoveProblem(ObjectComponent.JumpConnection));
        Assert.Equal("This version cannot change the links of trade lane rings.",
            ObjectComponents.RemoveProblem(ObjectComponent.TradeLaneRing));
        Assert.Null(ObjectComponents.RemoveProblem(ObjectComponent.PlanetRing));
        // The agent tool scene_set_encounters edits the encounter lines, so only the Inspector cannot remove them.
        Assert.Equal("The Inspector cannot edit the encounters of a zone.",
            ObjectComponents.RemoveProblem(ObjectComponent.EncounterZone));
    }

    [Fact]
    public void ADockableStationNeedsADockingSphere()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Object("Li01_dock"), "archetype", ["station_nodock"]));

        var dock = ObjectComponents.CanAdd(session, Object("Li01_child")).Single(c => c.Kind == ObjectComponent.DockableStation);
        Assert.Null(dock.Unavailable);
        session.Execute(EditCommand.RemoveName(Object("Li01_dock"), "dock_with"));
        Assert.Equal("Pick an archetype with a docking sphere first.",
            ObjectComponents.CanAdd(session, Object("Li01_dock")).Single(c => c.Kind == ObjectComponent.DockableStation).Unavailable);
    }

    /// <summary>Decision D258: the keys base and dock_with are two components, so a remove of one keeps the other.</summary>
    [Fact]
    public void TheBaseAndTheDockAreTwoComponents()
    {
        using var ws = Create();
        var session = ws.Session("act");

        session.Execute(ObjectComponents.Remove(Object("Li01_01"), ObjectComponent.DockableStation));
        Assert.Equal([ObjectComponent.Solar, ObjectComponent.LoadoutUser, ObjectComponent.BaseObject,
            ObjectComponent.FactionMember], Kinds(session, Object("Li01_01")));
        session.Undo();
        session.Execute(ObjectComponents.Remove(Object("Li01_01"), ObjectComponent.BaseObject));
        Assert.Contains(ObjectComponent.DockableStation, Kinds(session, Object("Li01_01")));
        Assert.DoesNotContain(ObjectComponent.BaseObject, Kinds(session, Object("Li01_01")));
    }

    [Fact]
    public void AComponentShowsTheProblemsOfItsKeys()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.SetLinesFromText(Object("Li01_dock"), "loadout", ["lo_nothing"]));

        var loadout = ObjectComponents.Of(session, Object("Li01_dock")).Single(c => c.Kind == ObjectComponent.LoadoutUser);

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(loadout.Problems).Severity);
        Assert.Empty(ObjectComponents.Of(session, Object("Li01_dock")).Single(c => c.Kind == ObjectComponent.Solar).Problems);
    }

    private static ObjectComponent[] Kinds(EditSession session, SectionRef section) =>
        ObjectComponents.Of(session, section).Select(c => c.Kind).ToArray();
}

/// <summary>The component checks (layer 3, section 5.5 of the M3 design).</summary>
public sealed class ComponentChecksTests
{
    /// <summary>Decision D240: a zone of the active mod whose nickname another zone of any system has is an error that
    /// stops the save. Retail drops one of the two zones.</summary>
    [Fact]
    public void AZoneCollisionStopsTheSave()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(Li01Path, "zone", [ListWorkspace.Line("nickname", "ZONE_LI02")]));

        var error = Assert.Single(session.SaveProblems());
        Assert.Equal("ZONE_LI02: the zone Zone_li02 in Li02 has the same nickname or the same FLHash. The game drops one of " +
                     "the two zones. Give the zone another nickname.", error.Message);
        Assert.Equal((Zone("ZONE_LI02"), "nickname"), (error.Target, error.Key));
        Assert.False(session.Save().Saved);
        // An object with a taken nickname is a warning only.
        session.Undo();
        session.Execute(EditCommand.AddSection(Li01Path, "Object",
            [ListWorkspace.Line("nickname", "Li02_01"), ListWorkspace.Line("archetype", "station1")]));
        Assert.Empty(session.SaveProblems());
        Assert.Contains(ComponentChecks.Check(session, Object("Li02_01")),
            p => p.Severity == DiagnosticSeverity.Warning && p.Message.Contains("The game can confuse the two objects."));
    }

    /// <summary>The save-error strip of the Inspector counts the zone collision, because the collision stops the
    /// save.</summary>
    [Fact]
    public void TheInspectorCountsAZoneCollisionAsAnErrorThatBlocksTheSave()
    {
        using var ws = Create();
        var session = ws.Session("act");
        session.Execute(EditCommand.AddSection(Li01Path, "zone", [ListWorkspace.Line("nickname", "ZONE_LI02")]));

        Assert.Single(session.SaveProblems());
        Assert.Equal(1, InspectorPanel.SaveErrorCount(session, Zone("ZONE_LI02"), session.Get(Zone("ZONE_LI02"))!));
        Assert.Equal(0, InspectorPanel.SaveErrorCount(session, Li02("zone", "Zone_li02"), session.Get(Li02("zone", "Zone_li02"))!));
    }

    [Fact]
    public void TheDockChecksAreWarnings()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var other = Assert.Single(ComponentChecks.Check(session, Li02("Object", "Li02_01"), onlyActiveMod: false));
        Assert.Equal("Li02_01: The base Li01_01_Base is in Li01. After a launch, the game puts the player in Li01.", other.Message);
        session.Execute(EditCommand.SetLinesFromText(Object("Li01_dock"), "archetype", ["station_nodock"]));
        Assert.Contains(ComponentChecks.Check(session, Object("Li01_dock")),
            p => p.Message == "Li01_dock: The player cannot dock here: the archetype station_nodock has no docking sphere.");
        Assert.Empty(ComponentChecks.Check(session, Li02("Object", "Li02_01")));
    }

    /// <summary>Decision D254: a base without a docking object or without a base object in its system gives two
    /// warnings. The bases of the workspace come after their systems, so the universe checks warn about them too. This
    /// test leaves out those warnings.</summary>
    [Fact]
    public void ABaseWithoutObjectsGivesTwoWarnings()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var li02Base = new SectionRef(ListWorkspace.UniversePath, new SectionIdentity("Base", "Li02_01_Base"));
        static bool NotOrder(Diagnostic d) => !d.Message.StartsWith("[Base]", StringComparison.Ordinal);

        var warnings = ComponentChecks.Check(session, li02Base, onlyActiveMod: false).Where(NotOrder);

        Assert.Equal(["Li02_01_Base: No object in Li02 docks with Li02_01_Base. The game puts the player at the center of " +
                      "Li02 after a launch.",
            "Li02_01_Base: No object in Li02 has 'base = Li02_01_Base'. The navigation map does not list the base, and its " +
            "rooms show no planet."], warnings.Select(w => w.Message));
        Assert.DoesNotContain(ComponentChecks.Check(session,
            new SectionRef(ListWorkspace.UniversePath, new SectionIdentity("Base", "Li01_01_Base")), onlyActiveMod: false),
            NotOrder);
    }
}
