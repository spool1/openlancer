using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>The edits of the lanes of a delete (see <see cref="LaneParts.Relink"/>).</summary>
/// <param name="Commands">The commands that relink the rings, move the end names and move the lane zones.</param>
/// <param name="Changes">The rings and the zones that the commands change.</param>
/// <param name="Fixes">What each command does, for the result.</param>
/// <param name="Lines">The link lines that the commands write again, as the ring and the key in lowercase. Such a line
/// names a ring that goes, but it does not refuse the delete.</param>
internal sealed record LaneRelinks(List<EditCommand> Commands, List<SectionRef> Changes, List<PlannedFix> Fixes,
    List<Diagnostic> Warnings, List<Diagnostic> Refusals, HashSet<(SectionRef Ring, string Key)> Lines);

/// <summary>
/// The parts of the trade lanes of a delete (see <see cref="ScenePlans.Delete(EditSession, IReadOnlyList{SectionRef})"/>).
/// A lane is one item, and its rings are its parts, so a delete of a ring relinks the lane. When a lane keeps one ring
/// or none, the whole lane goes with its lane zone. The rings of a file that goes with its system get no parts.
/// </summary>
internal sealed class LaneParts(EditSession session)
{
    /// <summary>
    /// The rings and the lane zones that go with the lanes of a delete and that it does not take yet: the last ring of
    /// a lane that keeps one ring, and the lane zone of a lane that keeps one ring or none. A lane zone that the editor
    /// cannot delete stays, and <paramref name="warnings"/> gets the reason.
    /// </summary>
    /// <param name="going">The sections that the delete reads so far.</param>
    /// <param name="queued">The sections that the delete takes, also before it reads them.</param>
    /// <param name="gone">The sections that go with a file that the caller deletes.</param>
    public List<(SectionRef Section, string Cause)> Wholes(IReadOnlySet<SectionRef> going, IReadOnlySet<SectionRef> queued,
        IReadOnlySet<SectionRef> gone, List<Diagnostic> warnings)
    {
        var result = new List<(SectionRef, string)>();
        foreach (var (ring, lane) in Lanes(going, gone))
        {
            var remaining = lane.Rings.Where(r => !going.Contains(r)).ToList();
            if (remaining.Count > 1)
                continue;
            var cause = $"the trade lane of {ring.Id.Key}";
            result.AddRange(remaining.Where(r => !queued.Contains(r)).Select(r => (r, cause)));
            if (LanePlans.ZoneOf(session, lane) is not { } zone || queued.Contains(zone))
                continue;
            if (session.Get(zone)?.ReadOnlyReason is { } reason)
                warnings.Add(Diagnostic.Warning(zone.Path, $"The lane zone {zone.Id.Key} stays. The editor cannot delete it: " +
                                                           $"{reason} Lane traffic still spawns at that place.") with
                {
                    Target = zone,
                });
            else
                result.Add((zone, cause));
        }
        return result;
    }

    /// <summary>
    /// The edits of the lanes that keep two or more rings after a delete. Each link line of a ring that stays and that
    /// names a ring that goes names the next ring that stays, or goes when no ring stays on that side. In a lane that
    /// goes in a circle, the delete cuts the circle at the first ring that goes. An end name moves from a removed end
    /// ring to the new end ring. The lane zone follows the new ends.
    /// </summary>
    public LaneRelinks Relink(IReadOnlySet<SectionRef> going, IReadOnlySet<SectionRef> gone)
    {
        var result = new LaneRelinks([], [], [], [], [], []);
        var rule = session.Lists.RuleData.Builders.Lane;
        foreach (var (_, lane) in Lanes(going, gone))
        {
            var removed = lane.Rings.Where(going.Contains).ToList();
            List<SectionRef> remaining;
            if (lane.Loops)
            {
                var cut = lane.Rings.ToList().FindIndex(going.Contains);
                remaining = [.. lane.Rings.Skip(cut + 1).Concat(lane.Rings.Take(cut)).Where(r => !going.Contains(r))];
            }
            else
                remaining = [.. lane.Rings.Where(r => !going.Contains(r))];
            if (remaining.Count < 2)
                continue;
            var names = removed.Select(r => r.Id.Key!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < remaining.Count; i++)
            {
                var ring = remaining[i];
                var view = session.Get(ring)!;
                foreach (var (key, wanted) in new[]
                         {
                             ("prev_ring", i > 0 ? remaining[i - 1] : null),
                             ("next_ring", i < remaining.Count - 1 ? remaining[i + 1] : null),
                         })
                {
                    if (SceneSections.Last(view, key) is not { } current || !names.Contains(current))
                        continue;
                    if (view.ReadOnlyReason is { } reason)
                    {
                        result.Refusals.Add(Diagnostic.Error(ring.Path, $"{ring.Id.Key} links to {current} in its trade lane, " +
                                                                        $"and the editor cannot change {ring.Id.Key}: {reason}") with
                        {
                            Target = ring, Key = key,
                        });
                        continue;
                    }
                    result.Commands.Add(wanted == null
                        ? EditCommand.RemoveName(ring, key)
                        : EditCommand.SetLinesFromText(ring, key, [wanted.Id.Key!]));
                    result.Lines.Add((ring, key));
                    result.Changes.Add(ring);
                    result.Fixes.Add(new PlannedFix(ring, key, wanted == null
                        ? $"{SceneSections.FileName(ring.Path)}: the {key} line of {ring.Id.Key} goes"
                        : $"{SceneSections.FileName(ring.Path)}: the {key} line of {ring.Id.Key} names {wanted.Id.Key}"));
                }
            }
            if (lane.Loops)
                continue;
            MoveName(lane.Rings[0], remaining[0], going, result);
            MoveName(lane.Rings[^1], remaining[^1], going, result);
            if (rule != null && (going.Contains(lane.Rings[0]) || going.Contains(lane.Rings[^1])) &&
                LanePlans.ZoneOf(session, lane) is { } zone && !going.Contains(zone) &&
                LanePlans.PositionOf(session, remaining[0]) is { } first &&
                LanePlans.PositionOf(session, remaining[^1]) is { } last)
            {
                var old = LanePlans.PositionOf(session, lane.Rings[0]) is { } a && LanePlans.PositionOf(session, lane.Rings[^1]) is { } b
                    ? System.Numerics.Vector3.Distance(a, b)
                    : (float?)null;
                var commands = LanePlans.Follow(session, zone, first, last, old, rule, result.Changes, result.Warnings);
                if (commands.Count > 0)
                {
                    result.Commands.AddRange(commands);
                    result.Fixes.Add(new PlannedFix(zone, "size",
                        $"{SceneSections.FileName(zone.Path)}: the lane zone {zone.Id.Key} follows the new ends of its lane"));
                }
            }
        }
        result.Changes.Clear();
        result.Changes.AddRange(result.Fixes.Select(f => f.Section).Distinct());
        return result;
    }

    /// <summary>Moves the end name of a removed end ring to the new end ring, when the new end ring has no name.</summary>
    private void MoveName(SectionRef end, SectionRef newEnd, IReadOnlySet<SectionRef> going, LaneRelinks result)
    {
        if (!going.Contains(end) || session.Get(end)?.Lines("tradelane_space_name").LastOrDefault(l => l.Values.Count > 0) is not
            { } name || session.Get(newEnd) is not { } view || view.Lines("tradelane_space_name").Count > 0)
            return;
        if (view.ReadOnlyReason is { } reason)
        {
            result.Warnings.Add(Diagnostic.Warning(newEnd.Path, $"{newEnd.Id.Key} is the new end of its trade lane, but the " +
                                                                $"editor cannot give it the end name: {reason} The HUD shows " +
                                                                "no lane label.") with { Target = newEnd });
            return;
        }
        result.Commands.Add(EditCommand.SetLinesFromText(newEnd, "tradelane_space_name",
            [string.Join(", ", name.Values.Select(v => v.ToString()!.Trim()))]));
        result.Fixes.Add(new PlannedFix(newEnd, "tradelane_space_name",
            $"{SceneSections.FileName(newEnd.Path)}: {newEnd.Id.Key} gets the end name of {end.Id.Key}"));
    }

    /// <summary>The lanes of the rings that go, once each, with the first ring of each lane that goes. A ring that goes
    /// with its file has no lane part.</summary>
    private IEnumerable<(SectionRef Ring, LaneChain Lane)> Lanes(IReadOnlySet<SectionRef> going, IReadOnlySet<SectionRef> gone)
    {
        var seen = new HashSet<SectionRef>();
        foreach (var section in going)
        {
            if (gone.Contains(section) || seen.Contains(section) || LanePlans.RingView(session, section) is null ||
                LanePlans.LaneOf(session, section) is not { } lane)
                continue;
            seen.UnionWith(lane.Rings);
            yield return (section, lane);
        }
    }
}
