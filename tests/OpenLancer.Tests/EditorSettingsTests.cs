using System;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The toggles of the editor settings that have no test file of their own.</summary>
public sealed class EditorSettingsTests
{
    private static readonly DateTime Saved = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The toggle "Script objects" of a space context is on by default and survives a save and a read
    /// (decision D645).</summary>
    [Fact]
    public void TheScriptObjectsToggleIsOnByDefaultAndSurvivesASave()
    {
        Assert.True(EditorSettings.Parse("{}", Saved).CutsceneScriptObjects);
        Assert.True(new EditorSettings().CutsceneScriptObjects);

        var settings = new EditorSettings { CutsceneScriptObjects = false };
        var json = settings.ToJson();

        Assert.Contains("\"CutsceneScriptObjects\": false", json);
        Assert.False(EditorSettings.Parse(json, Saved).CutsceneScriptObjects);
    }

    private const string Profile = @"C:\p\sample.json";

    private const string Script = @"DATA\MISSIONS\M99\m99.ini";

    /// <summary>The field "Ship" of the Play start page (issue #48). A settings file of an earlier version has no ship,
    /// so the play start uses the ship of the profile. A write keeps the ship, and a read gives it back.</summary>
    [Fact]
    public void ThePlayStartKeepsTheShipOfTheFieldAndAnOldFileHasNone()
    {
        const string old = """
            { "PlayStarts": { "C:\\p\\sample.json": { "DATA\\MISSIONS\\M99\\m99.ini": { "Trigger": "t_kill", "Place": "camera" } } } }
            """;

        var read = EditorSettings.Parse(old, Saved).PlayStartOf(Profile, Script);

        Assert.Equal(new PlayStartSetting("t_kill", "camera"), read);
        Assert.Null(read.Ship);
        Assert.Null(read.PackageOr(null));
        Assert.Equal("ol_profile_package", read.PackageOr("ol_profile_package"));
        Assert.DoesNotContain("\"Ship\"", EditorSettings.Parse(old, Saved).ToJson());

        var settings = EditorSettings.Transient();
        settings.SetPlayStart(Profile, Script, read with { Ship = "ol_field_package" });
        var again = EditorSettings.Parse(settings.ToJson(), Saved).PlayStartOf(Profile, Script);

        Assert.Equal("ol_field_package", again.ShipPackage);
        Assert.Equal("ol_field_package", again.PackageOr("ol_profile_package"));
        Assert.Null((again with { Ship = "  " }).ShipPackage);
    }

    /// <summary>The settings keep no entry for the default play start: the mission start at the suggested place with the
    /// ship of the profile. A ship in the field keeps the entry also at the mission start and the suggested place. A
    /// change of the place keeps the start and the ship.</summary>
    [Fact]
    public void AShipKeepsTheEntryAndAChangeOfThePlaceKeepsTheShip()
    {
        var settings = EditorSettings.Transient();
        var ship = PlayStartSetting.Default with { Ship = "ol_field_package" };

        settings.SetPlayStart(Profile, Script, ship);

        Assert.Equal(ship, Assert.Single(settings.PlayStarts[Profile]).Value);
        var moved = settings.PlayStartOf(Profile, Script).WithPlace(StartPlaceChoice.Base, "ol_base_a");
        Assert.Equal(new PlayStartSetting("", "base:ol_base_a", "ol_field_package"), moved);
        Assert.Equal(new PlayStartSetting("t_kill", "camera", "ol_field_package"),
            (moved with { Trigger = "t_kill" }).WithPlace(StartPlaceChoice.Camera, null));

        settings.SetPlayStart(Profile, Script, ship with { Ship = null });

        Assert.Empty(settings.PlayStarts[Profile]);
        Assert.Equal(PlayStartSetting.Default, settings.PlayStartOf(Profile, Script));
    }
}
