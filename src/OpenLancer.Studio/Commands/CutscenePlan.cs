using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Thn;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The plan of a command of a cutscene script (see <see cref="CutsceneCommands"/>): the script, the elements that the
/// command changes and deletes, and the number of elements that it adds. The editor selects the new elements after the
/// run (decision D249), and the agent tools give the item references of the result from the plan.
/// </summary>
/// <remarks>
/// <para>The ids of new elements exist only after the run. <see cref="ThnEdits.AddEntity"/> and
/// <see cref="ThnEdits.AddEvent"/> add each element at the end of its list, so <see cref="Added"/> gives the last
/// elements of W after the run.</para>
/// <para>The lists of <see cref="EditPlan"/> name INI sections, so a cutscene plan leaves them empty.</para>
/// </remarks>
public sealed class CutscenePlan : EditPlan
{
    private CutscenePlan(string label, string path) : base(label)
    {
        Path = path;
    }

    /// <summary>The install path of the script, in the form that the session stores.</summary>
    public string Path { get; }

    /// <summary>The elements that the command changes and keeps. Each element is in the list one time, and its
    /// reference has <see cref="Path"/>.</summary>
    public IReadOnlyList<ThnRef> Changed { get; private init; } = [];

    /// <summary>The elements that the command deletes, each one time, with <see cref="Path"/>.</summary>
    public IReadOnlyList<ThnRef> Deleted { get; private init; } = [];

    /// <summary>The number of entities that the command adds.</summary>
    public int NewEntities { get; private init; }

    /// <summary>The number of events that the command adds.</summary>
    public int NewEvents { get; private init; }

    /// <summary>
    /// The new elements after the run: the last <see cref="NewEntities"/> entities of W, then the last
    /// <see cref="NewEvents"/> events of W. Call it right after the run, before the next edit of the script.
    /// </summary>
    public IReadOnlyList<ThnRef> Added(EditSession session)
    {
        if ((NewEntities == 0 && NewEvents == 0) || session.Cutscene(Path) is not { } working)
            return [];
        return Last(working, ThnElementKind.Entity, NewEntities).Concat(Last(working, ThnElementKind.Event, NewEvents))
            .ToList();
    }

    /// <summary>
    /// The plan of a session command that <paramref name="make"/> makes now. A session command that cannot be made
    /// throws an <see cref="ArgumentException"/> or an <see cref="InvalidOperationException"/>. The plan then refuses
    /// with the reason, so a command of the layer never throws for a rule of the edit.
    /// </summary>
    /// <param name="label">The label of the entry of the edit history.</param>
    /// <param name="path">The install path of the script, in the form that the session stores.</param>
    /// <param name="changed">The elements of the script that the command changes and keeps, or null. They can repeat,
    /// and each path can name the script in another form.</param>
    /// <param name="deleted">The elements of the script that the command deletes, or null, as for
    /// <paramref name="changed"/>.</param>
    /// <param name="newEntities">The number of entities that the command adds at the end of the entities.</param>
    /// <param name="newEvents">The number of events that the command adds at the end of the events.</param>
    /// <param name="needsConfirmation">True when the editor asks before it runs the command (see
    /// <see cref="EditPlan.NeedsConfirmation"/>).</param>
    internal static CutscenePlan Of(string label, string path, Func<EditCommand> make,
        IReadOnlyList<ThnRef>? changed = null, IReadOnlyList<ThnRef>? deleted = null, int newEntities = 0,
        int newEvents = 0, bool needsConfirmation = false)
    {
        EditCommand command;
        try
        {
            command = make();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return Refused(label, path, ErrorText.Of(e));
        }

        return new CutscenePlan(label, path)
        {
            Command = command,
            Changed = InScript(path, changed),
            Deleted = InScript(path, deleted),
            NewEntities = newEntities,
            NewEvents = newEvents,
            NeedsConfirmation = needsConfirmation,
        };
    }

    /// <summary>A plan that cannot run, with the reason.</summary>
    /// <param name="label">The label of the edit that cannot run.</param>
    /// <param name="path">The install path of the script.</param>
    /// <param name="message">Why the edit cannot run, in the strict form of Simplified Technical English.</param>
    /// <param name="detail">The detail code of the error of the refusal (see <see cref="EditError.Detail"/>), or null
    /// for <see cref="EditPlan.PlanRefused"/>.</param>
    internal static CutscenePlan Refused(string label, string path, string message, string? detail = null) =>
        new(label, path) { Refusals = [Diagnostic.Error(path, message) with { Rule = detail }] };

    /// <summary>The references of elements of the script, each one time, with the path of the plan. A plan changes one
    /// script, so each element is in it.</summary>
    private static IReadOnlyList<ThnRef> InScript(string path, IReadOnlyList<ThnRef>? elements) =>
        elements == null ? [] : elements.Select(e => e with { Path = path }).Distinct().ToList();

    /// <summary>The references of the last elements of a kind of W.</summary>
    private IEnumerable<ThnRef> Last(ThnScriptModel working, ThnElementKind kind, int count)
    {
        var elements = working.Elements(kind);
        return elements.Skip(Math.Max(0, elements.Count - count)).Select(e => new ThnRef(Path, kind, e.Id));
    }
}
