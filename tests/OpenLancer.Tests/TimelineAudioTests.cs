using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Logs;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The sound of the preview with a fake output (decisions D62, D132 and D133, test 3 of section 8.1 of the M8b design):
/// the cues of the START_SOUND events, the start at an offset, the restart after a jump or a pause, no sound in the
/// pre-roll, the pitch, the voice rule, and one warning for a name that the data does not have.
/// </summary>
public sealed class TimelineAudioTests
{
    private const string Script = """
        duration = 20
        entities = {
            { entity_name = "Snd_mix", type = SOUND, template_name = "mix_a", audioprops = { attenuation = -3 } },
            { entity_name = "Snd_voice", type = SOUND, template_name = "VoiceProfile_Line01", userprops = { SPEAKER = "Char_a" } },
            { entity_name = "Snd_none", type = SOUND },
            { entity_name = "Char_a", type = DEFORMABLE }
        }
        events = {
            { 1, START_SOUND, { "Snd_mix" }, { duration = 10, start_time = 500, flags = 8 } },
            { 2, START_SOUND, { "snd_VOICE" }, { duration = 3 } },
            { 3, START_SOUND, { "Snd_mix" } },
            { 4, START_SOUND, { "Snd_none" }, { duration = 1 } },
            { 5, START_SOUND, { "Char_a" }, { duration = 1 } },
            { 6, START_MOTION, { "Char_a" }, { animation = "Sc_X", duration = 1 } }
        }
        """;

    private static IReadOnlyList<SoundCue> Cues(string script = Script, ScriptPlay play = ScriptPlay.Normal) =>
        TimelineAudio.Cues(TimelineModelTests.Model(script, play),
            ThnScriptModel.From(ThnScripts.Evaluate(script), ThnElementId.Counter()));

    /// <summary>A cue from 1 s to 11 s that starts 0.5 s into its sound, and a cue from 2 s to 5 s.</summary>
    private static readonly SoundCue Mix = new(new ThnElementId(1), 1, 11, "mix_a", null, false, 0.5, 0);

    private static readonly SoundCue Line = new(new ThnElementId(2), 2, 5, "line", null, false, 0, 0);

    [Fact]
    public void TheCuesAreTheStartSoundEventsWithALengthOnASoundEntity()
    {
        var cues = Cues();

        Assert.Equal(2, cues.Count);
        var mix = cues[0];
        Assert.Equal(("mix_a", (string?)null, true, 0.5, -3f),
            (mix.Sound, mix.Speaker, mix.Loop, mix.Offset, mix.Attenuation));
        Assert.Equal((1d, 11d), (mix.Start, mix.End));
        // The target names the entity without letter case, and the key of the speaker has no letter case either.
        var voice = cues[1];
        Assert.Equal(("VoiceProfile_Line01", "Char_a", false, 0d), (voice.Sound, voice.Speaker, voice.Loop, voice.Offset));
        Assert.Equal((2d, 5d), (voice.Start, voice.End));
        // The engine stops a sound without a length in the update that starts it, so it plays nothing.
        Assert.DoesNotContain(cues, c => c.Start == 3);
        // A set script runs none of its events.
        Assert.Empty(Cues(play: ScriptPlay.Set));
    }

    /// <summary>A table can have a list part and a record part. The engine reads the attenuation and the speaker from
    /// the record part, whatever the list part holds (ThnScript.cs:305-368). So the cues read them there too.</summary>
    [Fact]
    public void TheCuesReadTheAttenuationAndTheSpeakerAlsoFromTablesWithListItems()
    {
        var cues = Cues(Script.Replace("audioprops = { attenuation = -3 }", "audioprops = { 1; attenuation = -3 }")
            .Replace("userprops = { SPEAKER = \"Char_a\" }", "userprops = { 1; SPEAKER = \"Char_a\" }"));

        Assert.Equal((-3f, "Char_a"), (cues[0].Attenuation, cues[1].Speaker));
    }

    [Fact]
    public void APlayFromZeroStartsEachCueAtItsStartWithTheOffsetOfStartTime()
    {
        var output = new FakeOutput();
        var audio = Audio(output, Mix, Line);

        audio.Frame(0, 0, true, 1);
        Assert.Empty(output.Started);
        audio.Frame(1.01, 0, true, 1);
        Assert.Equal(("mix_a", 0.51), Single(output));
        audio.Frame(2.5, 0, true, 1);
        Assert.Equal(["mix_a", "line"], output.Started.Select(s => s.Cue.Sound));
        Assert.Equal(0.5, output.Started[1].Offset, 6);
        // A cue stops when the playhead passes its end.
        audio.Frame(5.2, 0, true, 1);
        Assert.Equal(["line"], output.Stopped.Select(s => s.Cue.Sound));
        audio.Frame(11.2, 0, true, 1);
        Assert.Equal(["line", "mix_a"], output.Stopped.Select(s => s.Cue.Sound));
        Assert.Empty(audio.Playing);
    }

    /// <summary>A play from the middle of a cue starts it in its middle, as Unity starts an audio clip.</summary>
    [Fact]
    public void APlayFromInsideACueStartsItWithTheTimeSinceItsStart()
    {
        var output = new FakeOutput();
        var audio = Audio(output, Mix);

        audio.Frame(5, 0, true, 1);

        Assert.Equal(("mix_a", 4.5), Single(output));
    }

    [Fact]
    public void AnOffsetPastTheEndOfASoundDoesNotStartItAndALoopWraps()
    {
        Assert.Equal(1.5, TimelineAudio.OffsetIn(1.5, 3, false));
        Assert.Null(TimelineAudio.OffsetIn(3, 3, false));
        Assert.Null(TimelineAudio.OffsetIn(5, 3, false));
        Assert.Equal(2, TimelineAudio.OffsetIn(5, 3, true)!.Value, 6);
        Assert.Equal(0, TimelineAudio.OffsetIn(-1, 3, false));
    }

    /// <summary>A seek, a loop jump and a forward seek during a play change the count of jumps. A catch-up holds the
    /// play. Each stops all cues, and the next frame starts the active cues again at the playhead.</summary>
    [Fact]
    public void AJumpOrACatchUpStopsAllCuesAndStartsTheActiveOnesAgain()
    {
        var output = new FakeOutput();
        var audio = Audio(output, Mix, Line);
        audio.Frame(3, 0, true, 1);
        Assert.Equal(2, output.Started.Count);

        // A forward seek during the play: a small time step, and a new count of jumps.
        audio.Frame(3.05, 1, true, 1);
        Assert.Equal(2, output.Stopped.Count);
        Assert.Equal(new[] { 3.05 - 1 + 0.5, 3.05 - 2 }, output.Started.Skip(2).Select(s => s.Offset).ToArray(),
            new ToleranceComparer());

        // A loop jump back to 1.5: only the first cue is active there.
        audio.Frame(1.5, 2, true, 1);
        Assert.Equal(4, output.Stopped.Count);
        Assert.Equal(("mix_a", 1.0), (output.Started[^1].Cue.Sound, Math.Round(output.Started[^1].Offset, 6)));

        // A catch-up: the clock does not run, so the play is not running.
        audio.Frame(1.5, 2, false, 1);
        Assert.Empty(audio.Playing);
        audio.Frame(1.52, 2, true, 1);
        Assert.Single(audio.Playing);
        Assert.Equal(1.02, output.Started[^1].Offset, 6);
    }

    /// <summary>Nothing plays in the pre-roll, and a pause stops all cues.</summary>
    [Fact]
    public void APreRollPlaysNothingAndAPauseStopsAllCues()
    {
        var output = new FakeOutput();
        var audio = Audio(output, Mix);

        // The pre-roll plays from 2 to 5 at four times the speed, without sound.
        for (var t = 2.0; t < 5; t += 0.25)
            audio.Frame(t, 0, false, 4);
        Assert.Empty(output.Started);

        // The end of the pre-roll is a jump, and the play goes on with sound.
        audio.Frame(5, 1, true, 1);
        Assert.Equal(("mix_a", 4.5), Single(output));
        audio.Frame(5.1, 1, false, 1);
        Assert.Single(output.Stopped);
        Assert.Empty(audio.Playing);
    }

    /// <summary>
    /// The transport gives the sound only while it plays forward at the playhead: not in the pre-roll, not during a
    /// catch-up and not during a scrub. A seek, the end of a pre-roll and a loop jump each add one jump.
    /// </summary>
    [Fact]
    public void TheTransportPlaysSoundOnlyForwardAtThePlayheadAndCountsItsJumps()
    {
        var fakes = new CutsceneFakes();
        using var clock = fakes.Clock();
        var transport = new PreviewTransport(clock, () => 10) { Loop = true };
        var frame = TimeSpan.FromHours(1);
        Assert.False(transport.PlaysSound);
        transport.Seek(5);
        Assert.Equal(1, transport.Jumps);
        while (clock.CatchingUp || clock.Visible == null)
            clock.Frame(0, frame);

        transport.Play();
        Assert.True(transport.InPreRoll);
        while (transport.InPreRoll)
        {
            Assert.False(transport.PlaysSound);
            transport.Frame(1.0 / 60, frame);
        }

        // The end of the pre-roll is a jump.
        Assert.Equal(2, transport.Jumps);
        Assert.True(transport.PlaysSound);

        // A scrub has no sound, until one frame after its last step.
        transport.NoteScrub();
        Assert.False(transport.PlaysSound);
        transport.Frame(1.0 / 60, frame);
        Assert.False(transport.PlaysSound);
        transport.Frame(1.0 / 60, frame);
        Assert.True(transport.PlaysSound);

        // A forward seek during the play is a jump, also for a small step.
        transport.Seek(transport.Time + 0.01);
        Assert.Equal(3, transport.Jumps);

        // A loop jump at the end of the script.
        transport.Seek(9.99);
        transport.PreRoll = false;
        while (transport.Time > 1)
            transport.Frame(1.0 / 60, frame);
        Assert.Equal(5, transport.Jumps);
    }

    /// <summary>After a pause, a play at the same time starts the active cue again at its offset. Without the restart,
    /// a line that played stays silent to its end.</summary>
    [Fact]
    public void APauseAndAPlayAtTheSameTimeStartTheActiveCueAgain()
    {
        var output = new FakeOutput();
        var audio = Audio(output, Mix);
        audio.Frame(4, 0, true, 1);
        audio.Frame(4.5, 0, false, 1);

        audio.Frame(4.5, 0, true, 1);

        Assert.Equal(2, output.Started.Count);
        Assert.Equal(4.0, output.Started[1].Offset, 6);
        Assert.Single(audio.Playing);
    }

    [Fact]
    public void ThePitchFollowsTheSpeed()
    {
        var output = new FakeOutput();
        var audio = Audio(output, Mix);

        audio.Frame(2, 0, true, 0.5);
        audio.Frame(2.1, 0, true, 2);
        audio.Frame(2.2, 0, true, 2);

        Assert.Equal(0.5f, output.Started[0].Pitch);
        Assert.Equal([2f], output.Pitches);
    }

    /// <summary>The cues of a new W: a cue that is gone or changed stops. A changed cue that is still active starts
    /// again in the next frame.</summary>
    [Fact]
    public void ACueThatIsGoneStopsAndAChangedCueStartsAgain()
    {
        var output = new FakeOutput();
        var audio = Audio(output, Mix, Line);
        audio.Frame(3, 0, true, 1);

        audio.SetCues([Mix with { End = 12 }]);
        Assert.Equal(2, output.Stopped.Count);
        Assert.Empty(audio.Playing);
        audio.Frame(3.1, 0, true, 1);

        Assert.Equal(("mix_a", 12d), (Assert.Single(audio.Playing).Sound, audio.Playing.Single().End));
        Assert.Equal(2.6, output.Started[^1].Offset, 6);
    }

    [Fact]
    public void TheVoiceRuleTakesTheVoiceOfTheSpeakerThenTheVoiceDataThenTheFallbacks()
    {
        var cue = Mix with { Sound = "VoiceProfile_GCS_FM_A01aG01", Speaker = "Char_a" };
        (string?, string?)? Scene(string voice, string? actor) => (voice == "" ? null : voice, actor);
        string? NoVoice(string line) => null;

        var speaker = TimelineAudio.SourceOf(cue, _ => Scene("rvp501", null), NoVoice);
        Assert.Equal(("rvp501", "GCS_FM_A01aG01"), (speaker.Voice, speaker.Name));
        var data = TimelineAudio.SourceOf(cue, _ => Scene("", null), line => line == "GCS_FM_A01aG01" ? "rvp777" : null);
        Assert.Equal("rvp777", data.Voice);
        Assert.Equal("trent_voice", TimelineAudio.SourceOf(cue, _ => Scene("", "PLAYER"), NoVoice).Voice);
        Assert.Equal("rvp146", TimelineAudio.SourceOf(cue, _ => Scene("", "Char_a"), NoVoice).Voice);
        Assert.Equal("rvp146", TimelineAudio.SourceOf(cue, _ => null, NoVoice).Voice);
        // A sound without a speaker, or a name without VoiceProfile_, is a sound of the audio data.
        var plain = TimelineAudio.SourceOf(Mix with { Speaker = "Char_a" }, _ => Scene("rvp501", null), NoVoice);
        Assert.Null(plain.Voice);
        Assert.Equal("mix_a", plain.Name);
        Assert.Null(TimelineAudio.SourceOf(cue with { Speaker = null }, _ => Scene("rvp501", null), NoVoice).Voice);
    }

    /// <summary>A name that the data does not have gives one warning, also after many seeks, and the engine gets no
    /// call for it. A silent entry plays nothing and gives no warning.</summary>
    [Fact]
    public void ANameThatTheDataDoesNotHaveGivesOneWarningAndNoEngineCall()
    {
        var lines = new List<(ConsoleSeverity, string)>();
        var catalog = new SoundCatalog(name => name switch { "mix_a" => false, "backgroundmusic" => true, _ => null },
            (voice, line) => voice == "rvp146" && line == "Line01", _ => null, (s, m) => lines.Add((s, m)));
        var engine = new FakeOutput();
        var output = new CatalogOutput(catalog, engine);
        var missing = Mix with { Event = new ThnElementId(7), Sound = "mix_missing" };
        var silent = Line with { Event = new ThnElementId(8), Sound = "backgroundmusic" };
        var voice = Line with { Event = new ThnElementId(9), Sound = "VoiceProfile_Line01", Speaker = "Char_a" };
        var audio = Audio(output, Mix, missing, silent, voice);

        for (var jump = 0; jump < 10; jump++)
            audio.Frame(3 + jump * 0.1, jump, true, 1);

        Assert.Equal(["mix_a", "VoiceProfile_Line01"], engine.Started.Select(s => s.Cue.Sound).Distinct());
        Assert.Single(lines, l => l.Item1 == ConsoleSeverity.Warning);
        Assert.Contains(lines, l => l == (ConsoleSeverity.Warning, "The game data has no sound mix_missing."));
        // The first play of a voice line tells the voice, once.
        Assert.Single(lines, l => l.Item1 == ConsoleSeverity.Info && l.Item2.Contains("rvp146"));
        Assert.Equal(["mix_a"], catalog.Preloads([Mix, missing, silent, voice], _ => null));
    }

    private static TimelineAudio Audio(ISoundOutput output, params SoundCue[] cues)
    {
        var audio = new TimelineAudio(output);
        audio.SetCues(cues);
        return audio;
    }

    private static (string, double) Single(FakeOutput output)
    {
        var started = Assert.Single(output.Started);
        return (started.Cue.Sound, Math.Round(started.Offset, 6));
    }

    /// <summary>The engine of the tests: it records each call, and each start gives a handle.</summary>
    private sealed class FakeOutput : ISoundOutput
    {
        public List<(SoundCue Cue, double Offset, float Pitch)> Started { get; } = [];

        public List<(SoundCue Cue, double Offset, float Pitch)> Stopped { get; } = [];

        public List<float> Pitches { get; } = [];

        public object? Start(SoundCue cue, double offset, float pitch)
        {
            var handle = (cue, offset, pitch);
            Started.Add(handle);
            return new Handle(handle);
        }

        public void SetPitch(object handle, float pitch) => Pitches.Add(pitch);

        public void Stop(object handle) => Stopped.Add(((Handle)handle).Start);

        private sealed record Handle((SoundCue Cue, double Offset, float Pitch) Start);
    }

    /// <summary>An output that asks the catalog first, as <see cref="CutsceneSound"/> does, and gives only the playable
    /// cues to the fake engine.</summary>
    private sealed class CatalogOutput(SoundCatalog catalog, FakeOutput engine) : ISoundOutput
    {
        public object? Start(SoundCue cue, double offset, float pitch) =>
            catalog.Playable(cue, _ => null) is null ? null : engine.Start(cue, offset, pitch);

        public void SetPitch(object handle, float pitch) => engine.SetPitch(handle, pitch);

        public void Stop(object handle) => engine.Stop(handle);
    }

    private sealed class ToleranceComparer : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) < 1e-6;

        public int GetHashCode(double obj) => 0;
    }
}
