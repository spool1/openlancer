using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing.Lists;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The game data around one cutscene of a session, from the data lists of W (section 12.1 of the M8b design). So the
/// save gate sees the unsaved INI edits too, for example a new costume. The lists of each kind of item are in
/// <see cref="ThnTemplateRules.ListsOf"/>.
/// </summary>
/// <remarks>
/// <para>A template that the room fills in, for example <c>$terrain_dyna_01</c>, gets no state: W has no room
/// substitution. <c>oltool validate</c> checks it with the rooms of the loaded data.</para>
/// <para>A name outside the lists is missing. An entry that the engine cannot use (<see cref="DataLists.Unusable"/>) is
/// missing too, because the engine drops it, for example a costume whose body names no body part
/// (GameItemDb.cs:974-978). An item whose model the engine loads has no model without a <c>DA_archetype</c> line, or
/// when the file of the line does not exist in the edit view (<see cref="ThnTemplateRules.NeedsModel"/>).</para>
/// <para>The checks of a script depend on the game data only through the answers of this class. So the class keeps
/// each answer, and <see cref="Unchanged"/> tells if the lists give the same answers now. An unsaved INI edit that
/// changes an answer then checks the cutscene again, and an edit of another file does not.</para>
/// </remarks>
internal sealed class SessionThnData(EditSession session, string path) : IThnGameData
{
    private readonly Dictionary<(ThnItemKind Kind, string Template), ThnItemState?> answers = [];
    private bool? makesCompounds;

    public ThnItemState? Item(ThnItemKind kind, string template)
    {
        if (!answers.TryGetValue((kind, template), out var state))
            answers[(kind, template)] = state = Answer(kind, template);
        return state;
    }

    /// <summary>The INI lines of the edit view tell when the game plays the script only in space. The session reads
    /// them once for each edit view, and only when a COMPOUND has a problem.</summary>
    public bool MakesCompounds => makesCompounds ??= !OnlyInSpace();

    /// <summary>True when the game data of the session gives each kept answer again.</summary>
    public bool Unchanged() =>
        answers.All(a => Answer(a.Key.Kind, a.Key.Template) == a.Value) &&
        (makesCompounds is not { } known || known == !OnlyInSpace());

    private bool OnlyInSpace() => session.EditViewReferences.OnlyInSpace(path);

    private ThnItemState? Answer(ThnItemKind kind, string template)
    {
        if (template.StartsWith('$') && ThnTemplateRules.RoomFillsIn(kind))
            return null;
        var lists = session.Lists;
        if (lists.List(ThnTemplateRules.ListsOf(kind)) is not { } list)
            return null;
        if (list.Find(template) is not { } entry || lists.Unusable(entry) != null)
            return ThnItemState.Missing;
        return ThnTemplateRules.NeedsModel(kind) && !HasModel(entry) ? ThnItemState.NoModel : ThnItemState.Ready;
    }

    /// <summary>True when the section of an entry names a model file of the edit view in its last
    /// <c>DA_archetype</c> line with a value (GameItemDb.cs:122-138, :2088-2105). An entry without a known section
    /// counts as one with a model, so the check gives no false error.</summary>
    private bool HasModel(ListEntry entry)
    {
        if (session.Lists.SectionOf(entry) is not { } section)
            return true;
        return section.LastOrDefault(e => e.Name.Equals("DA_archetype", StringComparison.OrdinalIgnoreCase) && e.Count > 0)
                   is { } line &&
               line[0].ToString().Trim() is { Length: > 0 } model && session.Lists.FileExists(model);
    }
}
