using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Bases;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The component checks (layer 3 of roadmap section 5.4, section 5.5 of the M3 design). They give problems of a section
/// with a target, a key, a severity and a consequence, as the list checks do. A zone nickname that another zone of any
/// system has, also by FLHash, is an error: retail drops one of the two zones (decision D240). The errors of the
/// universe checks, of the jump connection checks and of the trade lane checks also stop the save (see
/// <see cref="UniverseChecks"/>, <see cref="ConnectionChecks"/> and <see cref="LaneChecks"/>). The checks of a base,
/// of the sections of its base file and of <c>mBases.ini</c>, and of an object that docks with a base of another system
/// come from <see cref="BaseChecks"/>. The gate reads their errors in its own layer. The other checks are warnings until
/// a retail comparison proves an effect (decision D254).
/// </summary>
internal static class ComponentChecks
{
    /// <summary>The problems of a section of a system file, of a <c>[Base]</c> or a <c>[system]</c> of
    /// <c>universe.ini</c>, of a section of a base file or of <c>mBases.ini</c>, or of a <c>[locked_gates]</c>
    /// section.</summary>
    /// <param name="onlyActiveMod">True to check the section only when the active mod adds or changes it. The retail
    /// tests check each section.</param>
    public static IReadOnlyList<Diagnostic> Check(EditSession session, SectionRef section, bool onlyActiveMod = true)
    {
        if (session.Get(section) is not { } view || onlyActiveMod && !view.IsChanged)
            return [];
        var result = new List<Diagnostic>();
        if (IsUniverse(session, section))
        {
            if (SceneSections.Is(section.Id.Type, "Base"))
                result.AddRange(BaseChecks.Check(session, section));
            result.AddRange(UniverseChecks.Check(session, section));
            return result;
        }
        if (BaseChecks.IsBasePart(session, section))
            return BaseChecks.Check(session, section);
        result.AddRange(ConnectionChecks.Check(session, section));
        result.AddRange(LaneChecks.Check(session, section));
        switch (SceneSections.KindOf(session, section))
        {
            case SceneKind.Zone:
                if (session.Nicknames.Collisions(zones: true).FirstOrDefault(g => g.Any(n => n.Section.Equals(section))) is { } zones)
                {
                    foreach (var other in zones.Where(n => !n.Section.Equals(section)))
                        result.Add(Problem(DiagnosticSeverity.Error, section, "nickname",
                            $"the zone {other.Name} in {SystemOf(session, other.Section)} has the same nickname or the same " +
                            "FLHash. The game drops one of the two zones. Give the zone another nickname."));
                }
                break;
            case SceneKind.Object:
                if (session.Nicknames.Collisions(zones: false).FirstOrDefault(g => g.Any(n => n.Section.Equals(section))) is { } objects)
                {
                    foreach (var other in objects.Where(n => !n.Section.Equals(section)))
                        result.Add(Problem(DiagnosticSeverity.Warning, section, "nickname",
                            $"the object {other.Name} in {SystemOf(session, other.Section)} has the same nickname or the " +
                            "same FLHash. The game can confuse the two objects."));
                }
                if (SceneSections.Last(view, "dock_with") != null && !ObjectComponents.HasDockingSphere(session, view) &&
                    SceneSections.Last(view, "archetype") is { } archetype)
                    result.Add(Problem(DiagnosticSeverity.Warning, section, "dock_with",
                        $"The player cannot dock here: the archetype {archetype} has no docking sphere."));
                result.AddRange(BaseChecks.Check(session, section));
                break;
        }
        return result;
    }

    /// <summary>
    /// The errors of layer 3 for the save gate: the zone nickname collisions of the zones that the active mod adds or
    /// changes in the system files that it changes, and the errors of the jump connections and of the trade lanes of
    /// these files. Then the errors of the universe checks of the <c>[system]</c> sections that the active mod adds or
    /// changes, when it changes <c>universe.ini</c>.
    /// </summary>
    public static IEnumerable<Diagnostic> SaveErrors(EditSession session)
    {
        var paths = session.ActiveChangedPaths();
        var result = new List<Diagnostic>();
        foreach (var path in paths.Where(session.IsSystemFile).Distinct(VfsPath.Comparer))
        {
            foreach (var section in session.Sections(path).Where(s => SceneSections.Is(s.Id.Type, SceneSections.ZoneType)))
                result.AddRange(Check(session, section).Where(p => p.Severity == DiagnosticSeverity.Error));
            result.AddRange(ConnectionChecks.SaveErrors(session, path));
            result.AddRange(LaneChecks.SaveErrors(session, path));
        }
        if (session.UniversePath is { } universe && paths.Contains(universe, VfsPath.Comparer))
            result.AddRange(UniverseChecks.SaveErrors(session));
        return result;
    }

    /// <summary>True for a <c>[Base]</c> or a <c>[system]</c> of <c>universe.ini</c>.</summary>
    private static bool IsUniverse(EditSession session, SectionRef section) =>
        (SceneSections.Is(section.Id.Type, "Base") || SceneSections.Is(section.Id.Type, "system")) &&
        session.UniversePath is { } universe && VfsPath.Comparer.Equals(section.Path, universe);

    /// <summary>The system of a base of the list of the bases, or null.</summary>
    internal static string? SystemOfBase(EditSession session, string baseName) =>
        session.Lists.List("base")?.Find(baseName)?.Section is { } section ? session.SystemOfBase(section) : null;

    private static string SystemOf(EditSession session, SectionRef section) =>
        session.SystemNicknameOf(section.Path) ?? SceneSections.FileName(section.Path);

    private static Diagnostic Problem(DiagnosticSeverity severity, SectionRef section, string key, string message) =>
        new(severity, section.Path, $"{section.Id.Key ?? section.Id.ToString()}: {message}") { Target = section, Key = key };
}
