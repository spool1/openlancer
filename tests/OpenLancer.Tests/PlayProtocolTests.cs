using System;
using System.Numerics;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>The commands and events between the editor and the play process (decision D42).</summary>
public sealed class PlayProtocolTests
{
    [Theory]
    [InlineData(PlayCommandKind.Start, 0, "start")]
    [InlineData(PlayCommandKind.Stop, 0, "stop")]
    [InlineData(PlayCommandKind.Reload, 7, "reload 7")]
    [InlineData(PlayCommandKind.Reload, 0, "reload 0")]
    [InlineData(PlayCommandKind.Restart, 3, "restart 3")]
    public void EachCommandIsWrittenAndRead(PlayCommandKind kind, int save, string line)
    {
        var command = new PlayCommand(kind, save);

        Assert.Equal(line, PlayProtocol.Write(command));
        Assert.Equal(command, PlayProtocol.ReadCommand(line));
        Assert.Equal(command, PlayProtocol.ReadCommand(line + "\r"));
    }

    /// <summary>A capture command has the number of its request, not a save (the agent tool play_capture).</summary>
    [Fact]
    public void TheCaptureCommandHasTheNumberOfItsRequest()
    {
        var command = new PlayCommand(PlayCommandKind.Capture, Request: 12);

        Assert.Equal("capture 12", PlayProtocol.Write(command));
        Assert.Equal(command, PlayProtocol.ReadCommand("capture 12"));
        Assert.Equal(command, PlayProtocol.ReadCommand("capture 12\r"));
        Assert.Equal(0, PlayProtocol.ReadCommand("capture 12")!.Save);
        // A reload keeps its save in the save field.
        Assert.Equal(new PlayCommand(PlayCommandKind.Reload, 12), PlayProtocol.ReadCommand("reload 12"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("go")]
    [InlineData("Start")]
    [InlineData("start now")]
    [InlineData("reload")]
    [InlineData("reload x")]
    [InlineData("reload -1")]
    [InlineData("reload 1 2")]
    [InlineData("restart")]
    [InlineData("restart x")]
    [InlineData("capture")]
    [InlineData("capture x")]
    [InlineData("capture -1")]
    [InlineData("capture 1 2")]
    public void AnUnknownLineIsNoCommand(string line)
    {
        Assert.Null(PlayProtocol.ReadCommand(line));
    }

    public static TheoryData<string, string> EventLines => new()
    {
        { "waiting", """@ol {"event":"waiting","loadMs":2345}""" },
        { "spawned in space", """@ol {"event":"spawned","system":"li01","base":null,"pos":[-33270.5,0,-25039.25],"ship":"ge_fighter","shipName":"Starflier","startMs":812}""" },
        { "spawned at a base", """@ol {"event":"spawned","system":"li01","base":"li01_01_base","pos":null,"ship":"ge_fighter","shipName":"Starflier","startMs":640}""" },
        { "reloaded", """@ol {"event":"reloaded","save":3,"ms":2100,"system":"li01","base":null}""" },
        { "reload-failed", """@ol {"event":"reload-failed","save":4,"message":"Caf\u00E9 is not complete.\nOpen the profile.\u0022"}""" },
        { "restarted", """@ol {"event":"restarted","save":5,"ms":1800,"system":"li01","base":"li01_01_base"}""" },
        { "restart-failed", """@ol {"event":"restart-failed","save":6,"message":"The new data cannot start the mission."}""" },
        { "error with crash", """@ol {"event":"error","message":"No hull \u0027no_such_hull\u0027.","crash":true}""" },
        { "error", """@ol {"event":"error","message":"No package \u0027x\u0027.","crash":false}""" },
        { "captured", """@ol {"event":"captured","request":3,"file":"capture-3.png","width":1280,"height":720}""" },
        { "capture-failed", """@ol {"event":"capture-failed","request":4,"message":"The game could not write the image: disk full."}""" },
    };

    private static PlayEvent Event(string name) => name switch
    {
        "waiting" => new PlayEvent.Waiting(2345),
        "spawned in space" => new PlayEvent.Spawned("li01", null, new Vector3(-33270.5f, 0, -25039.25f), "ge_fighter",
            "Starflier", 812),
        "spawned at a base" => new PlayEvent.Spawned("li01", "li01_01_base", null, "ge_fighter", "Starflier", 640),
        "reloaded" => new PlayEvent.Reloaded(3, 2100, "li01", null),
        "reload-failed" => new PlayEvent.ReloadFailed(4, "Caf\u00e9 is not complete.\nOpen the profile.\""),
        "restarted" => new PlayEvent.Restarted(5, 1800, "li01", "li01_01_base"),
        "restart-failed" => new PlayEvent.RestartFailed(6, "The new data cannot start the mission."),
        "error with crash" => new PlayEvent.Error("No hull 'no_such_hull'.", true),
        "error" => new PlayEvent.Error("No package 'x'.", false),
        "captured" => new PlayEvent.Captured(3, "capture-3.png", 1280, 720),
        "capture-failed" => new PlayEvent.CaptureFailed(4, "The game could not write the image: disk full."),
        _ => throw new System.ArgumentException(name),
    };

    /// <summary>Each event has the fields of the design table, on one line of ASCII characters.</summary>
    [Theory]
    [MemberData(nameof(EventLines))]
    public void EachEventIsWrittenAndRead(string name, string line)
    {
        var e = Event(name);

        Assert.Equal(line, PlayProtocol.Write(e));
        Assert.Equal(e, PlayProtocol.ReadEvent(line));
    }

    /// <summary>The mission event of the live view (section 6.2 of the M6c design), with its dropped count.</summary>
    [Fact]
    public void TheMissionEventIsWrittenAndRead()
    {
        const string line = """@ol {"event":"mission","mission":"Mission_02","reset":true,"dropped":3,"changes":[{"ms":120,"trigger":"ol_play_setup","change":"ran"},{"ms":120,"trigger":"ol_wait_for_kill","change":"started"},{"ms":900,"trigger":"ol_timer","change":"stopped"}]}""";
        var e = new PlayEvent.MissionChanges("Mission_02", true, 3,
        [
            new MissionChange(120, "ol_play_setup", MissionChangeKind.Ran),
            new MissionChange(120, "ol_wait_for_kill", MissionChangeKind.Started),
            new MissionChange(900, "ol_timer", MissionChangeKind.Stopped),
        ]);

        Assert.Equal(line, PlayProtocol.Write(e));
        var read = Assert.IsType<PlayEvent.MissionChanges>(PlayProtocol.ReadEvent(line));
        Assert.Equal((e.Mission, e.Reset, e.Dropped), (read.Mission, read.Reset, read.Dropped));
        Assert.Equal(e.Changes, read.Changes);
    }

    [Theory]
    [InlineData("""@ol {"event":"mission","mission":"Mission_02","reset":false,"dropped":0}""")]
    [InlineData("""@ol {"event":"mission","mission":"Mission_02","reset":false,"dropped":0,"changes":[{"ms":1,"trigger":"t","change":"jumped"}]}""")]
    [InlineData("""@ol {"event":"mission","mission":"Mission_02","reset":false,"dropped":0,"changes":[{"ms":1,"trigger":"t","change":1}]}""")]
    public void AMissionLineWithoutItsChangesIsALogLine(string line)
    {
        Assert.Null(PlayProtocol.ReadEvent(line));
    }

    [Fact]
    public void TheEventFieldCanComeLast()
    {
        Assert.Equal(new PlayEvent.Waiting(5), PlayProtocol.ReadEvent("""@ol {"loadMs":5,"event":"waiting"}"""));
    }

    [Theory]
    [InlineData("[Warning: 10:00:00] GameItemDb: Unknown entry hit_ptz")]
    [InlineData("[Info: 10:00:00] Server: started")]
    [InlineData("")]
    [InlineData("@ol")]
    [InlineData("@ol ")]
    [InlineData("@ol {\"event\":\"waiting\",\"loadMs\":")]
    [InlineData("@ol null")]
    [InlineData("@ol []")]
    [InlineData("@ol {}")]
    [InlineData("@ol {\"event\":\"unknown\"}")]
    [InlineData("@ol {\"event\":\"waiting\"}")]
    [InlineData("@ol {\"event\":\"waiting\",\"loadMs\":\"5\"}")]
    [InlineData("@ol {\"event\":\"error\",\"message\":null,\"crash\":true}")]
    [InlineData("@ol {\"event\":\"spawned\",\"system\":\"li01\",\"base\":null,\"pos\":[1,2],\"ship\":\"s\",\"shipName\":\"S\",\"startMs\":1}")]
    [InlineData("@ol {\"event\":\"spawned\",\"system\":\"li01\",\"base\":null,\"pos\":[1e39,0,0],\"ship\":\"s\",\"shipName\":\"S\",\"startMs\":1}")]
    [InlineData("@ol {\"event\":\"error\",\"message\":\"\\uD800\",\"crash\":false}")]
    [InlineData("ol {\"event\":\"waiting\",\"loadMs\":5}")]
    [InlineData(" @ol {\"event\":\"waiting\",\"loadMs\":5}")]
    public void ALineThatIsNoEventIsALogLine(string line)
    {
        Assert.Null(PlayProtocol.ReadEvent(line));
    }

    /// <summary>A line with a lone surrogate, which UTF-8 cannot encode, is a log line.</summary>
    [Fact]
    public void ALineWithALoneSurrogateIsALogLine()
    {
        var line = PlayProtocol.EventPrefix + "{\"event\":\"error\",\"message\":\"" + (char)0xD800 +
                   "\",\"crash\":false}";

        Assert.Null(PlayProtocol.ReadEvent(line));
    }

    /// <summary>JSON has no number for NaN or infinity, so an event with such a number has no line.</summary>
    [Fact]
    public void AnEventWithANumberThatIsNotFiniteHasNoLine()
    {
        var e = new PlayEvent.Spawned("li01", null, new Vector3(float.NaN, 0, 0), "s", "S", 1);

        Assert.Throws<ArgumentException>(() => PlayProtocol.Write(e));
    }
}
