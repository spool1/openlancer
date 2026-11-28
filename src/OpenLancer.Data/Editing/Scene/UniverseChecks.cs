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
/// systems with one nickname, and a <c>file</c> that names no file. The other checks are warnings. The list checks
/// report two different system nicknames with one FLHash, as for each hash list.
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
            return System(session, section, view, warnings: true).ToList();
        return SceneSections.Is(section.Id.Type, "Base") ? Base(session, section, view).ToList() : [];
    }

    /// <summary>The other sections whose problems of <see cref="Check"/> read a section: for an object of a system file
    /// with a <c>goto</c> line, the <c>[system]</c> that each such line names. The check of that <c>[system]</c> tells
    /// if a jump leads to it. Empty for another section.</summary>
    public static IReadOnlyList<SectionRef> Linked(EditSession session, SectionRef section)
    {
        if (SceneSections.KindOf(session, section) != SceneKind.Object || session.Get(section) is not { } view)
            return [];
        return view.Lines("goto").Where(l => l.Values.Count > 0)
            .Select(l => session.SystemSection(l.Values[0].ToString().Trim())).OfType<SectionRef>().ToList();
    }

    /// <summary>The errors of the <c>[system]</c> sections that the active mod adds or changes, for the save gate. The
    /// call skips the warnings, so a save does not read the jump connections of each system file.</summary>
    public static IEnumerable<Diagnostic> SaveErrors(EditSession session)
    {
        if (session.UniversePath is not { } universe)
            return [];
        return session.Sections(universe)
            .Where(s => SceneSections.Is(s.Id.Type, "system"))
            .Select(s => (Section: s, View: session.Get(s)))
            .Where(s => s.View is { IsChanged: true })
            .SelectMany(s => System(session, s.Section, s.View!, warnings: false))
            .Where(p => p.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    /// <summary>The problems of a <c>[system]</c>.</summary>
    /// <param name="warnings">False to skip the checks that give only warnings.</param>
    private static IEnumerable<Diagnostic> System(EditSession session, SectionRef section, SectionView view, bool warnings)
    {
        if (section.Id.Key is not { } nickname)
            yield break;
        var entries = view.Entries;
        var fileAt = IndexOf(entries, "file");
        if (fileAt >= 0 && fileAt < IndexOf(entries, "nickname"))
            yield return Error(session, section, "file",
                $"The [system] {nickname} has its file line before its nickname line. The game then loads the system under " +
                "another id. In a text editor, put the nickname line of this [system] before its file line. Then open " +
                "the profile again.");

        var others = Systems(session).Where(s => !s.Section.Equals(section)).ToList();
        // Two different names with one FLHash are a collision of the hash list "system". The list checks report it, so
        // this check reports only one name in two sections.
        foreach (var other in others.Where(o => o.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
            yield return Error(session, section, "nickname",
                (other.Nickname == nickname
                    ? $"Two [system] sections have the nickname {nickname}."
                    : $"The system {nickname} has the same nickname as {other.Nickname}. The game ignores the letter case " +
                      "of a nickname.") +
                " The game drops one of the two systems. Give one system another nickname.");

        var value = FileValue(view);
        var path = value == null ? null : SystemFiles.FilePath(section.Path, value);
        if (value != null && (path == null || !session.HasFile(path)))
            yield return Error(session, section, "file",
                $"The system {nickname} names the file {value}, and the mod has no such file. The game loads the system " +
                "without its objects and zones, and the game can crash.");
        if (!warnings)
            yield break;

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

        if (!session.Routes.Graph.Reached(nickname))
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
                $"[Base] {name} comes after the [system] of {system}. The game does not link the base to its station. In a " +
                "text editor, move the [Base] before the [system] in the file of its mod. Then open the profile again.");
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
