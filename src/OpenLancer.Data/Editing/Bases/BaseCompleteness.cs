using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.Editing.Scene;

namespace OpenLancer.Data.Editing.Bases;

/// <summary>
/// The completeness of a base (AGENTS.md UI rule 12, section C.1 of the bases design). Seven steps go from its entry
/// in <c>universe.ini</c> to the object that docks with it. Each step reads W and the base checks (see
/// <see cref="BaseChecks"/>), so a missing step shows the text of its check. The base page and the agent tool of a base
/// show the same report.
/// </summary>
public static class BaseCompleteness
{
    /// <summary>The subject of the fix of the market step: the buy-only lines of the ship packages that the base does not
    /// list (see <see cref="MarketPlans.ShipLines"/>).</summary>
    public const string ShipLinesFix = "ship-lines";

    private const string EntryReason =
        "The game knows a base only by its [Base] in universe.ini. The entry names the system and the base file.";

    private const string FileReason = "The game reads the rooms and the start room of the base from its base file.";

    private const string StartRoomReason = "The player lands in the start room after a dock.";

    private const string RoomsReason =
        "Each room needs a room file that exists, and each door of a room must lead to a room of the base.";

    private const string CharactersReason =
        "The bar characters and the dealers come from the [MBase] of the base in mBases.ini.";

    private const string MarketReason = "The dealers of the base sell the goods of its market lines.";

    private const string DockReason =
        "The player docks at an object whose dock_with line names the base. The game links the base to an object with " +
        "its base line and its name.";

    /// <summary>The report of a base, or null when no <c>[Base]</c> of W has the nickname.</summary>
    public static CompletenessReport? Of(EditSession session, string baseNickname)
    {
        if (session.Base(baseNickname) is not { } view)
            return null;
        var problems = BaseChecks.Steps(session, view.Nickname);
        var hidden = 0;

        CompletenessStep Step(string name, BaseStep step, string reason, string? missing = null, object? goTo = null)
        {
            var found = problems.Where(p => p.Step == step).Select(p => p.Problem).ToList();
            if (step == BaseStep.Entry)
                found.InsertRange(0, EntryProblems(session, view.Entry));
            if (step == BaseStep.StartRoom && view.Info is { } info)
                found.AddRange(session.Lists.Problems(info).Select(p => p.Diagnostic)
                    .Where(p => SceneSections.Is(p.Key ?? "", "start_room")));
            if (found.Count == 0 && missing == null)
                return new CompletenessStep(name, CompletenessState.Done, reason);
            var first = found.FirstOrDefault(p => p.Severity == DiagnosticSeverity.Error) ?? found.FirstOrDefault();
            // A market that the active mod changes is not a market of the base game, also when its problem names the
            // [Base].
            var changedMarket = step == BaseStep.Market && view.Market.Any(r => r.Origin == ValueOrigin.ActiveMod);
            var state = BaseGame(session, view, found) && !changedMarket ? CompletenessState.Hidden : CompletenessState.Missing;
            if (state == CompletenessState.Hidden)
                hidden += found.Count == 0 ? 1 : found.Count;
            // A problem of the [Base] goes to the place of the step, for example the system of a missing dock object.
            var target = first?.Target is { } at && !(goTo != null && at.Equals(view.Entry)) ? at : goTo;
            return new CompletenessStep(name, state, reason, first?.Message ?? missing, target,
                IsError: found.Any(p => p.Severity == DiagnosticSeverity.Error));
        }

        var steps = new List<CompletenessStep>
        {
            Step("Universe entry", BaseStep.Entry, EntryReason,
                view.System == null ? $"The [Base] of {view.Nickname} has no system line." : null, view.Entry),
            Step("Base file", BaseStep.File, FileReason),
            Step("Start room", BaseStep.StartRoom, StartRoomReason,
                view.Info == null ? $"{view.Nickname} has no [BaseInfo] with a start room." : null, view.Entry),
            Step("Rooms", BaseStep.Rooms, RoomsReason,
                view.Info != null && view.Rooms.Count == 0 ? $"The base file of {view.Nickname} has no [Room]." : null,
                view.Info != null && view.Rooms.Count == 0 ? view.Info : null),
            Step("Characters", BaseStep.Characters, CharactersReason,
                view.MBase == null ? $"{view.Nickname} has no [MBase]. Its bar has no characters." : null, view.Entry),
            Market(Step("Market", BaseStep.Market, MarketReason), session, view),
            Step("Dock object", BaseStep.DockObject, DockReason, null,
                view.System is { } system ? session.SystemSection(system) : null),
        };
        return new CompletenessReport("Base", steps) { HiddenMessages = hidden };
    }

    /// <summary>The market step with the fix of the ship lines, when the base misses them.</summary>
    private static CompletenessStep Market(CompletenessStep step, EditSession session, BaseView view) =>
        step.State == CompletenessState.Missing && MarketPlans.MissingShipLines(session, view).Count > 0
            ? step with { Fix = ShipLinesFix }
            : step;

    /// <summary>The problems of the field checks, the list checks and the universe checks of the <c>[Base]</c>.</summary>
    private static IEnumerable<Diagnostic> EntryProblems(EditSession session, SectionRef entry) =>
        session.Check(entry).Concat(session.Lists.Problems(entry).Select(p => p.Diagnostic))
            .Concat(UniverseChecks.Check(session, entry))
            .Select(p => p with { Target = p.Target ?? entry });

    /// <summary>True when the base game misses a step too (decision D551). The base comes from the base game, and no
    /// mod changes a section of the problems of the step.</summary>
    private static bool BaseGame(EditSession session, BaseView view, IReadOnlyList<Diagnostic> problems) =>
        view.Origin == ValueOrigin.Base && Unchanged(session, view.Entry) &&
        problems.All(p => p.Target is not { } target || Unchanged(session, target));

    /// <summary>True when no mod changes a section, also with an unsaved edit.</summary>
    private static bool Unchanged(EditSession session, SectionRef section) =>
        !session.ModChanges(section) && session.Get(section) is not { IsChanged: true };
}
