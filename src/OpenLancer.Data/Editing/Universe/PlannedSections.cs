using System;
using System.Collections.Generic;
using LibreLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Universe;

/// <summary>
/// The sections of the files that a plan changes, as they are after its command, while the plan does not run yet. A
/// plan of the jump connections gives its route changes with them, also inside a batch of the agent, where the plan
/// cannot run its command to read the result. Each file starts as W has it.
/// </summary>
internal sealed class PlannedSections(EditSession session)
{
    private readonly Dictionary<string, List<Section>> files = new(VfsPath.Comparer);

    /// <summary>Adds a section at the end of a file.</summary>
    public void Add(string path, Section section) => File(path).Add(section);

    /// <summary>Puts a section in the place of a section of W. A section that the file does not have goes at the end
    /// of the file.</summary>
    public void Replace(SectionRef target, Section section)
    {
        var list = File(target.Path);
        var at = Array.IndexOf(SectionIdentity.Identify(list), target.Id);
        if (at >= 0)
            list[at] = section;
        else
            list.Add(section);
    }

    /// <summary>The route tables of W with the planned files.</summary>
    public RouteTables Routes() => session.RoutesWith(files);

    private List<Section> File(string path)
    {
        if (!files.TryGetValue(path, out var list))
            files[path] = list = [.. session.WorkingSections(path) ?? []];
        return list;
    }
}
