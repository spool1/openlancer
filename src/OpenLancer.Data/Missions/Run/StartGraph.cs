using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using LibreLancer.Data;

namespace OpenLancer.Data.Missions.Run;

/// <summary>
/// The links and the trigger names of a script that the start plans read: the starts, the checkpoints, the story path
/// and the replay path of each trigger, and the names that the setup triggers cannot use. An index does not change after
/// its build, so the plans of one index share one graph. The Play buttons of the graph view make a plan for each
/// trigger, so each plan must not read all links and names of the script again.
/// </summary>
internal sealed class StartGraph
{
    private static readonly ConditionalWeakTable<MissionIndex, StartGraph> Graphs = new();

    private readonly Dictionary<MissionTrigger, MissionTrigger?> storyParents;
    private readonly Dictionary<MissionTrigger, int> depths = [];
    private readonly Dictionary<MissionTrigger, MissionTrigger?> replayParents;
    private readonly HashSet<MissionTrigger> checkpointSet;
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> ids = [];

    private StartGraph(MissionIndex index)
    {
        Starts = Next(index, MissionLinkKind.Start);
        StartsAndLoads = Next(index, MissionLinkKind.Start, MissionLinkKind.Load);
        Checkpoints = DryRun.Checkpoints(index);
        checkpointSet = [.. Checkpoints];
        storyParents = Search(index.StartTriggers, StartsAndLoads, depths);
        replayParents = Search(index.StartTriggers.Concat(Checkpoints), Starts, []);
        foreach (var trigger in index.Triggers)
        {
            names.Add(trigger.Nickname);
            ids.Add(FLHash.CreateID(trigger.Nickname));
        }
    }

    /// <summary>The triggers that each trigger starts (<c>Act_ActTrig</c>). See <see cref="Next"/>.</summary>
    public ILookup<MissionTrigger, MissionTrigger> Starts { get; }

    /// <summary>The triggers that each trigger starts or saves (<c>Act_ActTrig</c> and <c>Act_Save</c>).</summary>
    public ILookup<MissionTrigger, MissionTrigger> StartsAndLoads { get; }

    /// <summary>The restore triggers of the checkpoints, in file order (see <see cref="DryRun.Checkpoints"/>).</summary>
    public IReadOnlyList<MissionTrigger> Checkpoints { get; }

    /// <summary>The graph of an index. The first call makes it.</summary>
    public static StartGraph Of(MissionIndex index) => Graphs.GetValue(index, i => new StartGraph(i));

    public bool IsCheckpoint(MissionTrigger trigger) => checkpointSet.Contains(trigger);

    /// <summary>True when a trigger of the script has the name, without letter case, or its game id (TR4).</summary>
    public bool IsTaken(string name) => names.Contains(name) || ids.Contains(FLHash.CreateID(name));

    /// <summary>The story path of a trigger (see <see cref="StartPlan.StoryPath"/>), or an empty list.</summary>
    public IReadOnlyList<MissionTrigger> StoryPath(MissionTrigger trigger) => PathOf(storyParents, trigger);

    /// <summary>The length of the story path of a trigger, or -1 when no start trigger reaches it.</summary>
    public int Depth(MissionTrigger trigger) => depths.GetValueOrDefault(trigger, -1);

    /// <summary>The replay path of a trigger: the shortest path of starts from a start trigger or from a restore trigger,
    /// in that order of the roots. Empty when no root reaches the trigger.</summary>
    public IReadOnlyList<MissionTrigger> ReplayPath(MissionTrigger trigger) => PathOf(replayParents, trigger);

    /// <summary>The triggers that each trigger reaches through links of some kinds. A duplicate trigger has no link. A
    /// start that a stop after it cancels in the same trigger is not a link, because the game does not start its target
    /// (TR9).</summary>
    private static ILookup<MissionTrigger, MissionTrigger> Next(MissionIndex index, params MissionLinkKind[] kinds)
    {
        var cancelled = index.Cancels.Where(c => c.Target != null).Select(c => (c.Source, c.Target!)).ToHashSet();
        return index.Links.Where(l => kinds.Contains(l.Kind) && l.Target is { IsDuplicate: false } && !l.Source.IsDuplicate &&
                                      !(l.Kind == MissionLinkKind.Start && cancelled.Contains((l.Source, l.Target))))
            .ToLookup(l => l.Source, l => l.Target!);
    }

    /// <summary>A search in width from the roots, in the order of the roots. The parent of each trigger gives its
    /// shortest path from the first root that reaches it. A search that stops at a target gives the same parents for
    /// the triggers that it reaches, so one search gives the path of each trigger.</summary>
    /// <param name="depths">Gets the length of the path of each trigger that the search reaches.</param>
    private static Dictionary<MissionTrigger, MissionTrigger?> Search(IEnumerable<MissionTrigger> roots,
        ILookup<MissionTrigger, MissionTrigger> next, Dictionary<MissionTrigger, int> depths)
    {
        var parent = new Dictionary<MissionTrigger, MissionTrigger?>();
        var queue = new Queue<MissionTrigger>();
        foreach (var root in roots.Where(r => !r.IsDuplicate))
        {
            if (!parent.TryAdd(root, null))
                continue;
            depths[root] = 0;
            queue.Enqueue(root);
        }
        while (queue.Count > 0)
        {
            var trigger = queue.Dequeue();
            foreach (var child in next[trigger])
            {
                if (!parent.TryAdd(child, trigger))
                    continue;
                depths[child] = depths[trigger] + 1;
                queue.Enqueue(child);
            }
        }
        return parent;
    }

    private static List<MissionTrigger> PathOf(Dictionary<MissionTrigger, MissionTrigger?> parents, MissionTrigger target)
    {
        if (!parents.ContainsKey(target))
            return [];
        var path = new List<MissionTrigger>();
        for (MissionTrigger? at = target; at != null; at = parents[at])
            path.Add(at);
        path.Reverse();
        return path;
    }
}
