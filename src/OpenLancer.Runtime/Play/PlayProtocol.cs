using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLancer.Runtime.Play;

/// <summary>A command of the editor to the play process.</summary>
public enum PlayCommandKind
{
    /// <summary>The validation passed. The game starts the play.</summary>
    Start,

    /// <summary>The game exits.</summary>
    Stop,

    /// <summary>The game loads the files of a save again, at the same place (hot reload).</summary>
    Reload,

    /// <summary>The game loads the files of a save again and starts the mission play again from its start save (section
    /// 5.8 of the M6c design).</summary>
    Restart,

    /// <summary>The game writes an image of its next frame as a PNG file, then sends captured (the agent tool
    /// <c>play_capture</c>, shared step SH9).</summary>
    Capture,
}

/// <param name="Save">For <see cref="PlayCommandKind.Reload"/> and <see cref="PlayCommandKind.Restart"/>: the number of
/// the save. The other commands ignore it.</param>
/// <param name="Request">For <see cref="PlayCommandKind.Capture"/>: the number of the request. The event of the capture
/// gives it back. The other commands ignore it.</param>
public sealed record PlayCommand(PlayCommandKind Kind, int Save = 0, int Request = 0);

/// <summary>An event of the play process to the editor.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(PlayEvent.Waiting), "waiting")]
[JsonDerivedType(typeof(PlayEvent.Spawned), "spawned")]
[JsonDerivedType(typeof(PlayEvent.Reloaded), "reloaded")]
[JsonDerivedType(typeof(PlayEvent.ReloadFailed), "reload-failed")]
[JsonDerivedType(typeof(PlayEvent.Restarted), "restarted")]
[JsonDerivedType(typeof(PlayEvent.RestartFailed), "restart-failed")]
[JsonDerivedType(typeof(PlayEvent.MissionChanges), "mission")]
[JsonDerivedType(typeof(PlayEvent.Captured), "captured")]
[JsonDerivedType(typeof(PlayEvent.CaptureFailed), "capture-failed")]
[JsonDerivedType(typeof(PlayEvent.Error), "error")]
public abstract record PlayEvent
{
    private PlayEvent()
    {
    }

    /// <summary>The game loaded its data and waits for <see cref="PlayCommandKind.Start"/>.</summary>
    /// <param name="LoadMs">The time from the process start to the end of the load.</param>
    public sealed record Waiting(long LoadMs) : PlayEvent;

    /// <summary>The player is in the game.</summary>
    /// <param name="System">The nickname of the system.</param>
    /// <param name="Base">The nickname of the base, or null in space.</param>
    /// <param name="Position">The position of the ship, or null at a base.</param>
    /// <param name="Ship">The nickname of the ship.</param>
    /// <param name="ShipName">The name of the ship, from its text.</param>
    /// <param name="StartMs">The time from the start of the play to the spawn. The play starts when the game has its
    /// data and <see cref="PlayCommandKind.Start"/>. So a start that comes during the load does not add the rest of
    /// the load to this time.</param>
    public sealed record Spawned(string System, string? Base, [property: JsonPropertyName("pos")] Vector3? Position,
        string Ship, string ShipName, long StartMs) : PlayEvent;

    /// <summary>A reload completed. The player is at the same place.</summary>
    /// <param name="Ms">The time of the reload in the game.</param>
    public sealed record Reloaded(int Save, long Ms, string System, string? Base) : PlayEvent;

    /// <summary>A reload failed. The game continues with the data of the last good save.</summary>
    public sealed record ReloadFailed(int Save, string Message) : PlayEvent;

    /// <summary>A restart of a mission play completed (section 5.8 of the M6c design). The player is at the place of the
    /// start save, and the mission starts again.</summary>
    /// <param name="Ms">The time of the restart in the game.</param>
    public sealed record Restarted(int Save, long Ms, string System, string? Base) : PlayEvent;

    /// <summary>A restart failed. The game continues at the place of the player with the data of the last good
    /// save.</summary>
    public sealed record RestartFailed(int Save, string Message) : PlayEvent;

    /// <summary>
    /// The trigger changes of the mission of the play since the last line (section 6.2 of the M6c design). The game
    /// writes at most one line each 100 ms, with at most 500 changes.
    /// </summary>
    /// <param name="Mission">The name of the mission, for example <c>Mission_02</c>, or the type of a random
    /// mission.</param>
    /// <param name="Reset">True in the first line after the game loaded the mission. The editor then clears the live
    /// trace of the play.</param>
    /// <param name="Dropped">The changes that the game dropped because its queue was full.</param>
    public sealed record MissionChanges(string Mission, bool Reset, int Dropped, IReadOnlyList<MissionChange> Changes)
        : PlayEvent;

    /// <summary>The game wrote the image of a capture request. The file is complete when this event comes, because the
    /// game writes it under another name first and then moves it.</summary>
    /// <param name="Request">The number of the request.</param>
    /// <param name="File">The name of the PNG file in the capture folder of the play folder
    /// (<see cref="PlayFolder.CaptureName"/>).</param>
    /// <param name="Width">The width of the image in pixels: the width of the game window.</param>
    /// <param name="Height">The height of the image in pixels.</param>
    public sealed record Captured(int Request, string File, int Width, int Height) : PlayEvent;

    /// <summary>The game could not write the image of a capture request.</summary>
    /// <param name="Request">The number of the request.</param>
    public sealed record CaptureFailed(int Request, string Message) : PlayEvent;

    /// <summary>The play cannot continue. The game exits after this event.</summary>
    /// <param name="Crash">True when an unhandled exception ended the game.</param>
    public sealed record Error(string Message, bool Crash) : PlayEvent;
}

/// <summary>A change of a mission trigger in the game (section 6.1 of the M6c design).</summary>
public enum MissionChangeKind
{
    /// <summary>The trigger became active.</summary>
    Started,

    /// <summary>The trigger ran its actions.</summary>
    Ran,

    /// <summary>A stop removed the trigger from the active triggers.</summary>
    Stopped,
}

/// <summary>One trigger change of a <see cref="PlayEvent.MissionChanges"/> event.</summary>
/// <param name="Ms">The time of the change since the game loaded the mission, in milliseconds.</param>
/// <param name="Trigger">The nickname of the trigger.</param>
public sealed record MissionChange(long Ms, string Trigger, MissionChangeKind Change);

/// <summary>
/// The channel between the editor and the play process (decision D42). The editor writes a command on the standard
/// input of the game, one line each. The game writes an event on its standard output, as a line with
/// <see cref="EventPrefix"/> and one JSON object. The other output lines of the game are engine log lines.
/// </summary>
public static class PlayProtocol
{
    public const string EventPrefix = "@ol ";

    private const string StartText = "start";
    private const string StopText = "stop";
    private const string ReloadText = "reload";
    private const string RestartText = "restart";
    private const string CaptureText = "capture";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // A line that does not have each field with a value of its type is not an event.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new Vector3Converter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static string Write(PlayCommand command) => command.Kind switch
    {
        PlayCommandKind.Start => StartText,
        PlayCommandKind.Stop => StopText,
        PlayCommandKind.Reload => $"{ReloadText} {command.Save.ToString(CultureInfo.InvariantCulture)}",
        PlayCommandKind.Restart => $"{RestartText} {command.Save.ToString(CultureInfo.InvariantCulture)}",
        PlayCommandKind.Capture => $"{CaptureText} {command.Request.ToString(CultureInfo.InvariantCulture)}",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null),
    };

    /// <summary>The command of a line, or null when the line is not a command.</summary>
    public static PlayCommand? ReadCommand(string line)
    {
        var parts = line.Trim().Split(' ');
        return parts switch
        {
            [StartText] => new PlayCommand(PlayCommandKind.Start),
            [StopText] => new PlayCommand(PlayCommandKind.Stop),
            [ReloadText, var save] when int.TryParse(save, NumberStyles.None, CultureInfo.InvariantCulture, out var n) =>
                new PlayCommand(PlayCommandKind.Reload, n),
            [RestartText, var save] when int.TryParse(save, NumberStyles.None, CultureInfo.InvariantCulture, out var n) =>
                new PlayCommand(PlayCommandKind.Restart, n),
            [CaptureText, var request] when int.TryParse(request, NumberStyles.None, CultureInfo.InvariantCulture,
                out var n) => new PlayCommand(PlayCommandKind.Capture, Request: n),
            _ => null,
        };
    }

    /// <summary>
    /// The line of an event. The line has no line break, also when a text of the event has one. The writer escapes
    /// each character outside ASCII, and also quote marks, so the line stays the same in each console code page.
    /// </summary>
    /// <exception cref="ArgumentException">A number of the event is not finite, for example a position of
    /// NaN.</exception>
    public static string Write(PlayEvent e) => EventPrefix + JsonSerializer.Serialize(e, JsonOptions);

    /// <summary>The event of an output line of the game, or null for an engine log line. A line with the prefix that
    /// is not a valid event is a log line too.</summary>
    public static PlayEvent? ReadEvent(string line)
    {
        if (!line.StartsWith(EventPrefix, StringComparison.Ordinal))
            return null;
        try
        {
            return JsonSerializer.Deserialize<PlayEvent>(line.AsSpan(EventPrefix.Length), JsonOptions);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException)
        {
            // An ArgumentException comes from a character that UTF-8 cannot encode, for example a lone surrogate.
            return null;
        }
    }

    /// <summary>Writes a vector as <c>[x, y, z]</c>. A read gives only finite numbers, as a write takes only
    /// them.</summary>
    private sealed class Vector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("A vector is an array of three numbers.");
            Span<float> values = stackalloc float[3];
            for (var i = 0; i < 3; i++)
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                    throw new JsonException("A vector is an array of three numbers.");
                values[i] = reader.GetSingle();
                if (!float.IsFinite(values[i]))
                    throw new JsonException("A number of a vector is too large for a float.");
            }
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("A vector is an array of three numbers.");
            return new Vector3(values);
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }
}
