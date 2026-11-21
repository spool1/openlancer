using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LibreLancer.Data;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The checks of the <c>[system]</c> and <c>[Base]</c> sections of <c>universe.ini</c>. Three errors stop the save,
/// because the game loads a wrong system or no system content: a <c>file</c> line before the <c>nickname</c> line, two
/// systems with one nickname or one FLHash, and a <c>file</c> that names no file. The other checks are warnings.
/// </summary>
/// <remarks>
/// <para>Retail facts: the game reads the system file at the <c>file</c> line, with the nickname that the section set
/// before that line. The id of a system is the FLHash of its nickname, and the game drops a second system with the same
/// id. The game joins the folder of <c>universe.ini</c> and the <c>file</c> value. When that file does not open, the
/// system has no objects and no zones, and the game can crash. The nav map shows a system at <c>pos</c> on a grid of 0
/// to 15. An object links to its base only when the game read the <c>[Base]</c> before the <c>[system]</c> of the
/// system.</para>
/// <para>An error comes only from a line of the active mod. When the line of the key comes from the game or a lower
/// mod, the problem is a warning, so it does not stop the save.</para>
/// </remarks>
internal static class UniverseChecks
{
    /// <summary>The problems of a <c>[system]</c> or a <c>[Base]</c> of <c>universe.ini</c>, also of the lower layer.
    /// Empty for another section.</summary>
    public static IReadOnlyList<Diagnostic> Check(EditSession session, SectionRef section)
    {
        if (session.UniversePath is not { } universe || !VfsPath.Comparer.Equals(section.Path, universe) ||
            session.Get(section) is not { } view)
            return [];
        if (SceneSections.Is(section.Id.Type, "system"))
            return System(session, section, view).ToList();
        return SceneSections.Is(section.Id.Type, "Base") ? Base(session, section, view).ToList() : [];
    }

    /// <summary>The problems of a <c>[system]</c>.</summary>
    private static IEnumerable<Diagnostic> System(EditSession session, SectionRef section, SectionView view)
    {
        if (section.Id.Key is not { } nickname)
            yield break;
        var entries = view.Entries;
        var fileAt = IndexOf(entries, "file");
        if (fileAt >= 0 && fileAt < IndexOf(entries, "nickname"))
            yield return Error(session, section, "file",
                $"The [system] {nickname} has its file line before its nickname line. The game then loads the system under " +
                "another id. Move the nickname line up.");

        var others = Systems(session).Where(s => !s.Section.Equals(section)).ToList();
        var hash = FLHash.CreateID(nickname);
        foreach (var other in others.Where(o => FLHash.CreateID(o.Nickname) == hash))
            yield return Error(session, section, "nickname",
                $"The system {nickname} has the same nickname or the same FLHash as {other.Nickname}. The game drops one of " +
                "the two systems. Give one system another nickname.");

        var value = FileValue(view);
        var path = value == null ? null : SystemFiles.FilePath(section.Path, value);
        if (value != null && (path == null || !session.HasFile(path)))
            yield return Error(session, section, "file",
                $"The system {nickname} names the file {value}, and the mod has no such file. The game loads the system " +
                "without its objects and zones, and the game can crash.");

        if (MapPos(view) is { } pos)
        {
            if (pos.X is < 0 or > SystemPlans.MapMax || pos.Y is < 0 or > SystemPlans.MapMax)
                yield return Warning(section, "pos",
                    $"The nav map shows the system {nickname} off the map. Use a position from 0 to {SystemPlans.MapMax} " +
                    "on each axis.");
            foreach (var other in others.Where(o => o.MapPos == pos))
                yield return Warning(section, "pos",
                    $"The systems {nickname} and {other.Nickname} are at the same place on the nav map. Their stars overlap.");
        }

        if (!session.Routes.Graph.Connections.Any(c => c.TargetSystem.Equals(nickname, StringComparison.OrdinalIgnoreCase) &&
                                                       !c.System.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
            yield return Warning(section, "nickname",
                $"No jump gate or jump hole leads to {nickname}. The player can only go there with a mission or a new game " +
                "start.");

        if (path != null)
        {
            foreach (var other in others.Where(o => o.File != null && VfsPath.Comparer.Equals(o.File, path)))
                yield return Warning(section, "file",
                    $"The systems {nickname} and {other.Nickname} use the same file {value}. The game loads its objects twice.");
        }
    }

    /// <summary>The problems of a <c>[Base]</c>: it comes after the <c>[system]</c> of its system.</summary>
    private static IEnumerable<Diagnostic> Base(EditSession session, SectionRef section, SectionView view)
    {
        if (section.Id.Key is not { } name || SceneSections.Last(view, "system") is not { } system)
            yield break;
        var sections = session.Sections(section.Path);
        var hash = FLHash.CreateID(system);
        var at = sections.ToList().FindIndex(s => SceneSections.Is(s.Id.Type, "system") && s.Id.Key != null &&
                                                  FLHash.CreateID(s.Id.Key) == hash);
        if (at >= 0 && sections.ToList().IndexOf(section) > at)
            yield return Warning(section, "system",
                $"[Base] {name} comes after the [system] of {system}. The game does not link the base to its station. Move " +
                "the [Base] before the [system].");
    }

    /// <summary>One <c>[system]</c> of W of <c>universe.ini</c>.</summary>
    /// <param name="File">The install path of its file, or null.</param>
    private sealed record SystemLine(SectionRef Section, string Nickname, string? File, Vector2? MapPos);

    /// <summary>The <c>[system]</c> sections with a nickname of W of <c>universe.ini</c>, in file order.</summary>
    private static IEnumerable<SystemLine> Systems(EditSession session)
    {
        if (session.UniversePath is not { } universe)
            yield break;
        foreach (var section in session.Sections(universe))
        {
            if (!SceneSections.Is(section.Id.Type, "system") || section.Id.Key is not { } nickname ||
                session.Get(section) is not { } view)
                continue;
            var value = FileValue(view);
            yield return new SystemLine(section, nickname, value == null ? null : SystemFiles.FilePath(section.Path, value),
                MapPos(view));
        }
    }

    /// <summary>The value of the last <c>file</c> line with a value, or null. The engine reads only that line.</summary>
    private static string? FileValue(SectionView view) =>
        view.Lines("file").LastOrDefault(l => l.Values.Count > 0)?.Values[0].ToString();

    /// <summary>The place of the last <c>pos</c> line with two numbers, or null.</summary>
    private static Vector2? MapPos(SectionView view) =>
        view.Lines("pos").LastOrDefault() is { Values: [var x, var y, ..] } && x.TryToSingle(out var px) &&
        y.TryToSingle(out var py)
            ? new Vector2(px, py)
            : null;

    private static int IndexOf(IReadOnlyList<EntryView> entries, string name)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>An error when the line of the key comes from the active mod, else a warning.</summary>
    private static Diagnostic Error(EditSession session, SectionRef section, string key, string message) =>
        session.Origin(section, key).Origin == ValueOrigin.ActiveMod
            ? new Diagnostic(DiagnosticSeverity.Error, section.Path, message) { Target = section, Key = key }
            : Warning(section, key, message);

    private static Diagnostic Warning(SectionRef section, string key, string message) =>
        new(DiagnosticSeverity.Warning, section.Path, message) { Target = section, Key = key };
}
