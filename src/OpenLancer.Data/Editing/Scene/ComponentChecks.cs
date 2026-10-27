using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing.Scene;

/// <summary>
/// The component checks (layer 3 of roadmap section 5.4, section 5.5 of the M3 design). They give problems of a section
/// with a target, a key, a severity and a consequence, as the list checks do. Only a zone nickname that another zone of
/// any system has, also by FLHash, is an error: retail drops one of the two zones (decision D240). The other checks are
/// warnings until a retail comparison proves an effect (decision D254).
/// </summary>
internal static class ComponentChecks
{
    /// <summary>The problems of a section of a system file, or of a <c>[Base]</c> of <c>universe.ini</c>.</summary>
    /// <param name="onlyActiveMod">True to check the section only when the active mod adds or changes it. The retail
    /// tests check each section.</param>
    public static IReadOnlyList<Diagnostic> Check(EditSession session, SectionRef section, bool onlyActiveMod = true)
    {
        if (session.Get(section) is not { } view || onlyActiveMod && !view.IsChanged)
            return [];
        var result = new List<Diagnostic>();
        if (IsBase(session, section))
        {
            result.AddRange(BaseChecks(session, section, view));
            return result;
        }
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
                if (SceneSections.Last(view, "dock_with") is { } dock)
                {
                    if (!ObjectComponents.HasDockingSphere(session, view) && SceneSections.Last(view, "archetype") is { } archetype)
                        result.Add(Problem(DiagnosticSeverity.Warning, section, "dock_with",
                            $"The player cannot dock here: the archetype {archetype} has no docking sphere."));
                    if (SystemOfBase(session, dock) is { } system && session.SystemPath(system) is { } path &&
                        !VfsPath.Comparer.Equals(path, section.Path))
                        result.Add(Problem(DiagnosticSeverity.Warning, section, "dock_with",
                            $"The base {dock} is in {system}. After a launch, the game puts the player in {system}."));
                }
                break;
        }
        return result;
    }

    /// <summary>
    /// The errors of layer 3 for the save gate: the zone nickname collisions of the zones that the active mod adds or
    /// changes in the system files that it changes.
    /// </summary>
    public static IEnumerable<Diagnostic> SaveErrors(EditSession session)
    {
        var changed = session.ActiveChangedPaths().Where(session.IsSystemFile).ToHashSet(VfsPath.Comparer);
        if (changed.Count == 0)
            return [];
        var result = new List<Diagnostic>();
        foreach (var path in changed)
        {
            foreach (var section in session.Sections(path).Where(s => SceneSections.Is(s.Id.Type, SceneSections.ZoneType)))
                result.AddRange(Check(session, section).Where(p => p.Severity == DiagnosticSeverity.Error));
        }
        return result;
    }

    /// <summary>
    /// The warnings of a <c>[Base]</c> (decision D254): no object of its system docks with it, or no object of its system
    /// has <c>base</c> with it. The game then puts the player at the center of the system after a launch, or the
    /// navigation map does not list the base.
    /// </summary>
    private static IEnumerable<Diagnostic> BaseChecks(EditSession session, SectionRef section, SectionView view)
    {
        if (section.Id.Key is not { } name || SceneSections.Last(view, "system") is not { } system ||
            session.SystemPath(system) is not { } path)
            yield break;
        var references = session.Lists.ReferencesTo("base", name);
        if (!references.Any(r => SceneSections.Is(r.Key, "dock_with") && VfsPath.Comparer.Equals(r.Section.Path, path)))
            yield return Problem(DiagnosticSeverity.Warning, section, "system",
                $"No object in {system} docks with {name}. The game puts the player at the center of {system} after a launch.");
        if (!references.Any(r => SceneSections.Is(r.Key, "base") && VfsPath.Comparer.Equals(r.Section.Path, path)))
            yield return Problem(DiagnosticSeverity.Warning, section, "system",
                $"No object in {system} has 'base = {name}'. The navigation map does not list the base, and its rooms show " +
                "no planet.");
    }

    /// <summary>True for a <c>[Base]</c> of <c>universe.ini</c>.</summary>
    private static bool IsBase(EditSession session, SectionRef section) =>
        SceneSections.Is(section.Id.Type, "Base") && session.UniversePath is { } universe &&
        VfsPath.Comparer.Equals(section.Path, universe);

    /// <summary>The system of a base of the list of the bases, or null.</summary>
    internal static string? SystemOfBase(EditSession session, string baseName) =>
        session.Lists.List("base")?.Find(baseName)?.Section is { } section ? session.SystemOfBase(section) : null;

    private static string SystemOf(EditSession session, SectionRef section) =>
        session.SystemNicknameOf(section.Path) ?? SceneSections.FileName(section.Path);

    private static Diagnostic Problem(DiagnosticSeverity severity, SectionRef section, string key, string message) =>
        new(severity, section.Path, $"{section.Id.Key ?? section.Id.ToString()}: {message}") { Target = section, Key = key };
}
