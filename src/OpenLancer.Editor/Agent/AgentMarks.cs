using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;

namespace OpenLancer.Editor.Agent;

/// <summary>
/// The marks of the agent edits (section 8.5 of the MCP1 design, decision D718). The Scene draws a pulse ring around each
/// item of an agent entry for 4 seconds, and the Hierarchy shows the robot glyph on each row that an agent entry changed
/// since the last save. The marks follow <see cref="EditSession.Changed"/>: an undo of the entry removes them, a redo
/// puts them back, and a save clears them. The blue mark of an unsaved edit stays as it is.
/// </summary>
/// <param name="clock">The time now. The tests give a fake clock.</param>
internal sealed class AgentMarks(Func<DateTime> clock)
{
    /// <summary>How long the pulse of an agent edit runs.</summary>
    public static readonly TimeSpan PulseTime = TimeSpan.FromSeconds(4);

    /// <summary>The start of the pulse of each section.</summary>
    private readonly Dictionary<SectionRef, DateTime> pulses = [];

    private EditSession? session;

    /// <summary>Follows the changes of the session of the open profile. Call it in each frame before the agent calls run.
    /// A new session starts without pulses.</summary>
    public void Track(EditSession? edits)
    {
        if (ReferenceEquals(edits, session))
            return;
        if (session != null)
            session.Changed -= OnChanged;
        session = edits;
        pulses.Clear();
        if (session != null)
            session.Changed += OnChanged;
    }

    /// <summary>The sections whose pulse runs now, with the time since the start of each pulse. A pulse ends after
    /// <see cref="PulseTime"/>, at an undo of its entry and at a save.</summary>
    public IReadOnlyList<(SectionRef Section, TimeSpan Age)> Pulses()
    {
        if (pulses.Count == 0)
            return [];
        var now = clock();
        var marked = session?.AgentSections;
        foreach (var (section, start) in pulses.ToList())
        {
            if (now - start >= PulseTime || marked?.Contains(section) != true)
                pulses.Remove(section);
        }
        return pulses.Select(p => (p.Key, now - p.Value)).ToList();
    }

    /// <summary>True when an agent entry changed the section after the last save, so its Hierarchy row shows the robot
    /// glyph.</summary>
    public bool Marked(SectionRef section) => session?.AgentSections.Contains(section) == true;

    private void OnChanged(EditChange change)
    {
        if (change.Origin != EditOrigin.Agent)
            return;
        if (change.Undone)
        {
            foreach (var section in change.Sections)
                pulses.Remove(section);
            return;
        }
        var now = clock();
        foreach (var section in change.Sections)
            pulses[section] = now;
    }
}
