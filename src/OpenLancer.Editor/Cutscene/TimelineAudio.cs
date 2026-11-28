using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Thn;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Editor.Cutscene;

/// <summary>One START_SOUND event of the open script that the preview plays (section 5.3 of the M8b design).</summary>
/// <param name="Event">The id of the event in W.</param>
/// <param name="Start">The time of the event, in seconds.</param>
/// <param name="End">The time plus <c>props.duration</c>. The engine stops the sound then (StartSoundEvent.cs:56-70).</param>
/// <param name="Sound">The <c>template_name</c> of the SOUND entity (ThnSound.cs:54-63).</param>
/// <param name="Speaker">The <c>userprops.speaker</c> of the entity, or null (ThnScript.cs:331-334).</param>
/// <param name="Loop">True when <c>props.flags</c> has LOOP, the value 8 (StartSoundEvent.cs:36).</param>
/// <param name="Offset"><c>props.start_time</c> in seconds: the sound starts this far into its data
/// (ThnSound.cs:23-27).</param>
/// <param name="Attenuation">The <c>audioprops.attenuation</c> of the entity. The game sets it in each update of the
/// sound (ThnSound.cs:37).</param>
internal sealed record SoundCue(ThnElementId Event, double Start, double End, string Sound, string? Speaker,
    bool Loop, double Offset, float Attenuation);

/// <summary>Where the sounds of the preview go: the engine, or a fake in the tests.</summary>
internal interface ISoundOutput
{
    /// <summary>Starts a cue at an offset in seconds. Gives null when the game data has no such sound, or when the
    /// offset is past the end of a sound that does not loop.</summary>
    object? Start(SoundCue cue, double offset, float pitch);

    void SetPitch(object handle, float pitch);

    void Stop(object handle);
}

/// <summary>What a cue plays: a sound of the audio data, or a line of a voice.</summary>
/// <param name="Voice">The voice of a voice line, or null for a sound.</param>
/// <param name="Name">The name of the sound, or the line of the voice without <c>VoiceProfile_</c>.</param>
/// <param name="Rule">Which step of the voice rule gave the voice, or null for a sound.</param>
internal sealed record SoundSource(string? Voice, string Name, string? Rule)
{
    /// <summary>The name for a message: the sound, or the line and the voice.</summary>
    public string Text => Voice == null ? Name : $"{Name} of the voice {Voice}";
}

/// <summary>
/// The sound of the preview (decisions D62, D132 and D133 of the M8b design). The preview players stay silent. The
/// editor plays the START_SOUND events of the open script itself, in 2D, in time with the playhead.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The sound plays only while the preview plays forward: not in the pre-roll, not during a catch-up and not
/// while the user drags the playhead. The pitch follows the speed.</item>
/// <item>While the play goes on, a cue starts when the playhead passes its start, and stops when the playhead passes
/// its end.</item>
/// <item>After a jump of the playhead, or when the play starts again, all cues stop. Then each cue that is active at the
/// playhead starts at its offset. So a play from the middle of a voice line starts in the middle of the line, as Unity
/// starts an audio clip.</item>
/// </list>
/// </remarks>
internal sealed class TimelineAudio(ISoundOutput output)
{
    private const string VoicePrefix = "VoiceProfile_";
    private const string PlayerActor = "player";
    private const string PlayerVoice = "trent_voice";
    private const string FallbackVoice = "rvp146";

    private readonly Dictionary<ThnElementId, (SoundCue Cue, object Handle)> playing = [];

    // The cues that a change of W stopped while they played. The next frame that goes on starts them again.
    private readonly HashSet<ThnElementId> changed = [];

    private IReadOnlyList<SoundCue> cues = [];
    private double lastTime;
    private int lastJumps;
    private bool lastRunning;
    private float pitch = 1;

    /// <summary>The cues that play now.</summary>
    public IReadOnlyCollection<SoundCue> Playing => playing.Values.Select(p => p.Cue).ToList();

    /// <summary>
    /// The cues of the START_SOUND events of W that the context runs (<see cref="TimelineEvent.Ignored"/> is null). The
    /// first target must name a SOUND entity of W. The engine uses the last entity of a name. An event without a
    /// length gives no cue, because the engine stops its sound in the update that starts it.
    /// </summary>
    public static IReadOnlyList<SoundCue> Cues(TimelineModel model, ThnScriptModel working)
    {
        var symbols = ThornEvaluator.Instance.Symbols;
        var flags = ThnSchema.Find(ThnElementKind.Event, "START_SOUND", new ThnPath("props.flags"));
        var cues = new List<SoundCue>();
        foreach (var ev in model.Events)
        {
            if (ev.Kind != EventTypes.StartSound || ev.Ignored != null || !ev.IsBar || ev.Targets.Count == 0 ||
                ev.Index >= working.Events.Count || working.EntityNamed(ev.Targets[0]) is not { } entity ||
                CueOf(entity.Table, ev.Ref.Id) is not { } sound)
            {
                continue;
            }

            var table = working.Events[ev.Index].Table;
            var props = table.List.Length > 3 ? table.List[3] as ThnTable : null;
            var loop = false;
            if (flags != null)
            {
                try
                {
                    loop = ThnFlags.BitsOn(props?["flags"], flags, symbols).Contains("LOOP");
                }
                catch (InvalidOperationException)
                {
                    // The checks of the script report a flags value that the engine cannot read.
                }
            }

            var offset = props?["start_time"] is ThnNumber start ? start.Value / 1000.0 : 0;
            cues.Add(sound with { Start = ev.Time, End = ev.End, Loop = loop, Offset = offset });
        }

        return cues;
    }

    /// <summary>
    /// The sound of a SOUND entity from its start, once, for the Preview row of the Inspector: the template, the speaker
    /// and the attenuation of the entity. Null for an entity that is not a SOUND or that has no template. The engine
    /// reads the attenuation from the record part of <c>audioprops</c>, whatever its list part holds
    /// (ThnScript.cs:357-368).
    /// </summary>
    /// <param name="id">The id of the cue.</param>
    public static SoundCue? CueOf(ThnTable entity, ThnElementId id)
    {
        if (!ThnFlags.IsSound(entity, ThornEvaluator.Instance.Symbols) ||
            entity["template_name"] is not ThnText { Value.Length: > 0 } template)
        {
            return null;
        }

        var attenuation = entity["audioprops"] is ThnTable audio && audio["attenuation"] is ThnNumber a
            ? a.Value
            : 0;
        return new SoundCue(id, 0, double.PositiveInfinity, template.Value, Speaker(entity), false, 0, attenuation);
    }

    /// <summary>
    /// What a cue plays, with the voice rule of ThnSound.cs:78-102. A <c>VoiceProfile_</c> sound with a speaker is a
    /// line of a voice: the voice of the scene object of the speaker, else the first voice of the voice data that has
    /// the line, else <c>trent_voice</c> for the actor <c>player</c>, else <c>rvp146</c>. The step of the voice data is a
    /// rule of the preview, not of the engine: the preview context of most scripts with voice lines binds no NPC with a
    /// voice.
    /// </summary>
    /// <param name="scene">The voice and the actor of a scene object, or null when the scene has no such object.</param>
    /// <param name="voiceWithLine">The first voice of the voice data that has a line, or null.</param>
    public static SoundSource SourceOf(SoundCue cue, Func<string, (string? Voice, string? Actor)?> scene,
        Func<string, string?> voiceWithLine)
    {
        if (cue.Speaker == null || !cue.Sound.StartsWith(VoicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new SoundSource(null, cue.Sound, null);
        }

        var line = cue.Sound[VoicePrefix.Length..];
        var speaker = scene(cue.Speaker);
        if (speaker?.Voice is { } voice)
        {
            return new SoundSource(voice, line, $"the voice of {cue.Speaker}");
        }

        if (voiceWithLine(line) is { } withLine)
        {
            return new SoundSource(withLine, line, "the first voice of the voice data that has the line");
        }

        return PlayerActor.Equals(speaker?.Actor, StringComparison.OrdinalIgnoreCase)
            ? new SoundSource(PlayerVoice, line, "the voice of the player")
            : new SoundSource(FallbackVoice, line, "the voice of the engine for a speaker without a voice");
    }

    /// <summary>
    /// The offset in the data of a sound of a length: the offset itself, or for a loop the offset wrapped at the
    /// length. Null when the offset is past the end of a sound that does not loop. OpenAL plays such a sound from its
    /// start, so the preview does not start it (OL-0030).
    /// </summary>
    public static double? OffsetIn(double offset, double length, bool loop)
    {
        if (offset < length)
        {
            return Math.Max(0, offset);
        }

        return loop && length > 0 ? offset % length : null;
    }

    /// <summary>Takes the cues of a new W. The cues that are gone or changed stop. A changed cue that is still active
    /// starts again in the next frame that runs.</summary>
    public void SetCues(IReadOnlyList<SoundCue> next)
    {
        cues = next;
        var kept = next.ToHashSet();
        foreach (var (id, (cue, handle)) in playing.ToList())
        {
            if (!kept.Contains(cue))
            {
                output.Stop(handle);
                playing.Remove(id);
                changed.Add(id);
            }
        }
    }

    /// <summary>One frame of the preview.</summary>
    /// <param name="time">The playhead, in seconds.</param>
    /// <param name="jumps"><see cref="PreviewTransport.Jumps"/>. A change of it, or a <paramref name="running"/> that
    /// turns true, starts the active cues again at the playhead.</param>
    /// <param name="running">True while the preview plays forward with sound: the transport plays, no pre-roll plays,
    /// no catch-up runs, no scrub runs, and the Sound toggle is on.</param>
    /// <param name="speed">The speed of the play. The pitch follows it, so the sound stays in time.</param>
    public void Frame(double time, int jumps, bool running, double speed)
    {
        var goesOn = running && lastRunning && jumps == lastJumps;
        var newPitch = (float)speed;
        if (!running)
        {
            StopAll();
        }
        else if (!goesOn)
        {
            StopAll();
            pitch = newPitch;
            foreach (var cue in cues.Where(c => c.Start <= time && time < c.End))
            {
                StartCue(cue, time);
            }
        }
        else
        {
            if (newPitch != pitch)
            {
                pitch = newPitch;
                foreach (var (_, handle) in playing.Values)
                {
                    output.SetPitch(handle, pitch);
                }
            }

            foreach (var (id, (cue, handle)) in playing.ToList())
            {
                if (time >= cue.End)
                {
                    output.Stop(handle);
                    playing.Remove(id);
                }
            }

            foreach (var cue in cues.Where(c => (lastTime < c.Start || changed.Contains(c.Event)) && c.Start <= time &&
                                                time < c.End))
            {
                StartCue(cue, time);
            }
        }

        changed.Clear();
        (lastTime, lastJumps, lastRunning) = (time, jumps, running);
    }

    /// <summary>Stops all cues. The next frame that runs starts the active cues again.</summary>
    public void StopAll()
    {
        foreach (var (_, handle) in playing.Values)
        {
            output.Stop(handle);
        }

        playing.Clear();
        lastRunning = false;
    }

    private void StartCue(SoundCue cue, double time)
    {
        if (playing.ContainsKey(cue.Event))
        {
            return;
        }

        if (output.Start(cue, time - cue.Start + cue.Offset, pitch) is { } handle)
        {
            playing[cue.Event] = (cue, handle);
        }
    }

    /// <summary>The <c>speaker</c> of the userprops of an entity, without letter case. The engine keeps the last key,
    /// and it reads the record part of the table, whatever the list part holds (ThnScript.cs:305-334).</summary>
    private static string? Speaker(ThnTable entity) =>
        entity["userprops"] is ThnTable props
            ? props.Record.LastOrDefault(f => f.Key.Equals("speaker", StringComparison.OrdinalIgnoreCase)).Value switch
            {
                null => null,
                ThnText text => text.Value,
                var value => ThnWriter.Value(value),
            }
            : null;
}
