using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;

namespace OpenLancer.Data.Missions.Run;

/// <summary>The place of a start save: a base with its system, or a point in a system with a heading.</summary>
/// <param name="System">The nickname of the system.</param>
/// <param name="Base">The nickname of the base of a base start, else null.</param>
public sealed record StartSavePlace(string System, string? Base, Vector3 Position, Quaternion Orientation)
{
    /// <summary>A start docked at a base of a system.</summary>
    public static StartSavePlace AtBase(string system, string dock) =>
        new(system, dock, Vector3.Zero, Quaternion.Identity);
}

/// <summary>The ship part of a save: the ship, the equipment that it mounts and the goods in its hold.</summary>
/// <param name="Ship">The nickname of the <c>[Ship]</c>.</param>
/// <param name="Equip">The equipment that the ship mounts, each with its hardpoint. The game mounts an item without a
/// hardpoint at <c>internal</c>.</param>
/// <param name="Cargo">The goods in the hold, each with its count.</param>
public sealed record StartSaveShip(
    string Ship,
    IReadOnlyList<(string Item, string? Hardpoint)> Equip,
    IReadOnlyList<(string Item, int Count)> Cargo)
{
    /// <summary>
    /// The ship part that a ship package gives (decision D47). This is the one statement of the addon rule. The game
    /// and the editor both use it:
    /// <list type="bullet">
    /// <item>An addon with the amount 1 mounts its equipment at its hardpoint.</item>
    /// <item>An addon with a larger amount puts that amount of the equipment into the hold.</item>
    /// <item>An addon with an amount below 1 gives nothing.</item>
    /// </list>
    /// </summary>
    /// <param name="ship">The nickname of the ship of the package.</param>
    /// <param name="addons">The addons of the package in their order: the nickname of the equipment, the hardpoint and
    /// the amount.</param>
    public static StartSaveShip FromAddons(string ship,
        IEnumerable<(string Equipment, string? Hardpoint, int Amount)> addons)
    {
        var equip = new List<(string, string?)>();
        var cargo = new List<(string, int)>();
        foreach (var (equipment, hardpoint, amount) in addons)
        {
            if (amount == 1)
                equip.Add((equipment, hardpoint));
            else if (amount > 1)
                cargo.Add((equipment, amount));
        }
        return new StartSaveShip(ship, equip, cargo);
    }
}

/// <summary>
/// The start save of a play from a trigger (section 5.5 of the M6c design, decisions D750 and D753), as plain text. Each
/// line comes from the one canonical line writer (<see cref="IniLines"/>). The OpenLancer form (F5) has a short
/// <c>[Player]</c> section, then the mission part: <c>[StoryInfo]</c>, <c>[MissionState]</c>, each
/// <c>[TriggerSave]</c>, and <c>[BStoryMissionDone]</c>. The reader of the mission state stops at that last header, so
/// the mission part always comes before it.
/// </summary>
public static class StartSaveText
{
    /// <summary>The value of <c>delta_worth</c> that the game reads as "no worth rule".</summary>
    private const string NoWorth = "-1";

    /// <summary>The text of the OpenLancer form of a plan that can start.</summary>
    /// <param name="time">The time of the write: <c>tstamp</c>, so the load menu lists the file first.</param>
    /// <param name="ship">The ship part of the save, or null for no ship lines. Without ship lines, the game uses the
    /// ship of the play.</param>
    /// <exception cref="ArgumentException">The plan cannot start, or it has no story slot.</exception>
    public static string OpenLancer(StartPlan plan, StartSavePlace place, DateTime time, StartSaveShip? ship = null) =>
        Text([PlayerSection(plan, place, time, ship), .. MissionPart(plan)]);

    /// <summary>The text of the description: "OpenLancer start: {slot}, {start}" (section 5.5 of the M6c
    /// design).</summary>
    public static string Description(StartPlan plan) =>
        $"OpenLancer start: {plan.Slot?.Slot ?? "test script"}, {plan.StartName}";

    /// <summary>The hex form of a text that a save reads: four hex digits for each UTF-16 unit, high byte first. The text
    /// of the description has a comma, so the plain form cannot hold it.</summary>
    public static string Hex(string text) => Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text));

    /// <summary>
    /// The sections of the mission part, in this order: <c>[StoryInfo]</c> with <c>Mission</c> before
    /// <c>MissionNum</c>, <c>[MissionState]</c> (always, also before the accept), one <c>[TriggerSave]</c> for each
    /// saved trigger, and <c>[BStoryMissionDone]</c>. A trigger id is <c>FLHash.CreateID</c> of the name as an unsigned
    /// decimal number, the form that HD Edition writes.
    /// </summary>
    /// <exception cref="ArgumentException">The plan cannot start, or it has no story slot.</exception>
    public static IReadOnlyList<(string Header, IReadOnlyList<string> Lines)> MissionPart(StartPlan plan)
    {
        if (!plan.CanStart || plan.Slot is not { } slot)
            throw new ArgumentException("Only a plan that can start has a start save.", nameof(plan));
        var state = new List<string> { Line("mission_accepted", plan.Accepted ? "1" : "0") };
        if (plan.State.Title != 0)
            state.Add(Line("msn_title", Number(plan.State.Title)));
        if (plan.State.Offer != 0)
            state.Add(Line("msn_offer", Number(plan.State.Offer)));
        state.AddRange(plan.State.Scenes.Select(s => Line("rtc", s.File, s.Repeats ? "true" : "false")));
        state.AddRange(plan.State.Ambients.Select(a =>
            Line("ambi_scene", a.File, Number(RoomId(a.Base, a.Room)), Id(a.Base))));
        var sections = new List<(string, IReadOnlyList<string>)>
        {
            ("StoryInfo", [Line("Mission", slot.Controller), Line("MissionNum", Number(plan.Stage)), Line("delta_worth", NoWorth)]),
            ("MissionState", state),
        };
        sections.AddRange(plan.SavedTriggers.Select(t => ("TriggerSave", (IReadOnlyList<string>)[Line("trigger", Id(t))])));
        sections.Add(("BStoryMissionDone", []));
        return sections;
    }

    /// <summary>The short <c>[Player]</c> section of the OpenLancer form: the description, the time, the system, and the
    /// base or the point with the heading as Euler angles in degrees. With a ship, the place lines are followed by
    /// <c>ship_archetype</c>, one <c>equip</c> line for each mounted item and one <c>cargo</c> line for each good in
    /// the hold, in the order of the save of the game.</summary>
    private static (string, IReadOnlyList<string>) PlayerSection(StartPlan plan, StartSavePlace place, DateTime time,
        StartSaveShip? ship)
    {
        var fileTime = time.ToFileTime();
        var lines = new List<string>
        {
            Line("description", Hex(Description(plan))),
            Line("tstamp", Number((uint)(fileTime >> 32)), Number((uint)(fileTime & 0xFFFFFFFF))),
            Line("system", place.System),
        };
        if (place.Base is { } dock)
        {
            lines.Add(Line("base", dock));
        }
        else
        {
            var angles = place.Orientation.GetEulerDegrees();
            lines.Add(Line("pos", Number(place.Position.X), Number(place.Position.Y), Number(place.Position.Z)));
            lines.Add(Line("rotate", Number(angles.X), Number(angles.Y), Number(angles.Z)));
        }
        if (ship != null)
        {
            lines.Add(Line("ship_archetype", ship.Ship));
            lines.AddRange(ship.Equip.Select(e =>
                e.Hardpoint is { Length: > 0 } hardpoint ? Line("equip", e.Item, hardpoint) : Line("equip", e.Item)));
            lines.AddRange(ship.Cargo.Select(c => Line("cargo", c.Item, Number(c.Count))));
        }
        return ("Player", lines);
    }

    /// <summary>The text of sections with the line end of canonical text, an empty line between two sections.</summary>
    private static string Text(IEnumerable<(string Header, IReadOnlyList<string> Lines)> sections)
    {
        var text = new StringBuilder();
        foreach (var (header, lines) in sections)
        {
            if (text.Length > 0)
                text.Append(IniLines.LineEnd);
            text.Append('[').Append(header).Append(']').Append(IniLines.LineEnd);
            foreach (var line in lines)
                text.Append(line).Append(IniLines.LineEnd);
        }
        return text.ToString();
    }

    private static string Line(string key, params string[] values) =>
        IniLines.Line(key, values.Select(v => (ValueBase)new StringValue(v)));

    /// <summary>The game id of a name as an unsigned decimal number.</summary>
    private static string Id(string name) => Number(FLHash.CreateID(name.Trim()));

    /// <summary>The game id of a room of a base. The game makes it from the base and the room name, not from the room
    /// name alone. A save keeps this id in <c>ambi_scene</c>, and the game finds the room of the base by it.</summary>
    internal static uint RoomId(string dock, string room) => FLHash.CreateLocationID(dock.Trim(), room.Trim());

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A coordinate or an angle with at most three decimals, and 0 for a value that rounds to 0.</summary>
    private static string Number(float value)
    {
        var rounded = MathF.Round(value, 3);
        return (rounded == 0 ? 0 : rounded).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
