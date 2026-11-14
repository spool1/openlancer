using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
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
/// when the file of the line does not exist in the edit view (<see cref="ThnTemplateRules.NeedsModel"/>). An equipment
/// of a section type that gets no model has no model, and the engine makes no item of some section types
/// (<see cref="ThnTemplateRules.EquipmentModel"/>). <see cref="Reason"/> tells why for each such entry.</para>
/// <para>Each such answer also tells if the active mod causes it: the active mod removes the name, makes the entry
/// unusable, or adds or changes the section of the item. <see cref="Lower"/> gives no state for such an answer.</para>
/// <para>The checks of a script depend on the game data only through the answers of this class. So the class keeps
/// each answer, and <see cref="Unchanged"/> tells if the lists give the same answers now. An unsaved INI edit that
/// changes an answer then checks the cutscene again, and an edit of another file does not.</para>
/// </remarks>
internal sealed class SessionThnData : IThnGameData
{
    private readonly EditSession session;
    private readonly string path;
    private readonly Dictionary<(ThnItemKind Kind, string Template), Answer> answers = [];
    private bool? makesCompounds;

    public SessionThnData(EditSession session, string path)
    {
        this.session = session;
        this.path = path;
        Lower = new LowerData(this);
    }

    public ThnItemState? Item(ThnItemKind kind, string template) => Get(kind, template).State;

    public string? Reason(ThnItemKind kind, string template) => Get(kind, template).Reason;

    /// <summary>The INI lines of the edit view tell when the game plays the script only in space. The session reads
    /// them once for each edit view, and only when a COMPOUND has a problem.</summary>
    public bool MakesCompounds => makesCompounds ??= !OnlyInSpace();

    /// <summary>
    /// The game data for the check of B: the same answers, but no state for an answer that the active mod causes. The
    /// lower data does not have such a problem, so a problem of W that it causes stays new. Examples are a costume that
    /// the active mod deletes or renames, and an equipment whose <c>DA_archetype</c> line the active mod removes.
    /// </summary>
    public IThnGameData Lower { get; }

    /// <summary>True when the game data of the session gives each kept answer again.</summary>
    public bool Unchanged() =>
        answers.All(a => Compute(a.Key.Kind, a.Key.Template) == a.Value) &&
        (makesCompounds is not { } known || known == !OnlyInSpace());

    private Answer Get(ThnItemKind kind, string template)
    {
        if (!answers.TryGetValue((kind, template), out var answer))
            answers[(kind, template)] = answer = Compute(kind, template);
        return answer;
    }

    private bool OnlyInSpace() => session.EditViewReferences.OnlyInSpace(path);

    private Answer Compute(ThnItemKind kind, string template)
    {
        if (template.StartsWith('$') && ThnTemplateRules.RoomFillsIn(kind))
            return default;
        var lists = session.Lists;
        var ids = ThnTemplateRules.ListsOf(kind);
        if (lists.List(ids) is not { } list)
            return default;
        if (list.Find(template) is not { } entry)
            return new Answer(ThnItemState.Missing, ids.Any(id => lists.RemovedOf(id, null, template) != null), null);
        if (lists.Unusable(entry) is { } unusable)
            return new Answer(ThnItemState.Missing, unusable.ByActiveMod, unusable.Reason);
        if (!ThnTemplateRules.NeedsModel(kind) || lists.SectionOf(entry) is not { } section)
            return new Answer(ThnItemState.Ready, false, null);
        var (state, reason) = Model(kind, section);
        return new Answer(state, state != ThnItemState.Ready && lists.ChangedByActiveMod(entry), reason);
    }

    /// <summary>The model of the section of an item, as the engine gives it: the file of the last <c>DA_archetype</c>
    /// line with a value, when the file exists in the edit view (GameItemDb.cs:122-138, :2088-2105). An equipment also
    /// needs a section type that gets a model.</summary>
    private (ThnItemState State, string? Reason) Model(ThnItemKind kind, Section section)
    {
        if (kind == ThnItemKind.Equipment)
        {
            switch (ThnTemplateRules.EquipmentModel(section.Name, Value(section, "motor") != null))
            {
                case ThnItemState.Missing:
                    return (ThnItemState.Missing, $"the game makes no equipment from [{section.Name}] sections.");
                case ThnItemState.NoModel when section.Name.Equals("Munition", StringComparison.OrdinalIgnoreCase):
                    return (ThnItemState.NoModel, "the game gives a model to a [Munition] only when it has a motor line.");
                case ThnItemState.NoModel:
                    return (ThnItemState.NoModel, $"the game makes the [{section.Name}] items without a model.");
            }
        }
        if (Value(section, "DA_archetype") is not { } model)
            return (ThnItemState.NoModel, $"its [{section.Name}] section has no DA_archetype line.");
        return session.Lists.FileExists(model)
            ? (ThnItemState.Ready, null)
            : (ThnItemState.NoModel, $"the model file '{model}' does not exist.");
    }

    /// <summary>The first value of the last line of a key with a value, or null.</summary>
    private static string? Value(Section section, string key) =>
        section.LastOrDefault(e => e.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Count > 0) is { } line &&
        line[0].ToString().Trim() is { Length: > 0 } value
            ? value
            : null;

    /// <summary>The answer for one template: its state, or null when the data cannot tell, whether the active mod causes
    /// a state other than Ready, and the reason of that state.</summary>
    private readonly record struct Answer(ThnItemState? State, bool ByActiveMod, string? Reason);

    /// <summary>The answers of the session data without the answers that the active mod causes (see
    /// <see cref="Lower"/>).</summary>
    private sealed class LowerData(SessionThnData data) : IThnGameData
    {
        public ThnItemState? Item(ThnItemKind kind, string template) =>
            data.Get(kind, template) is { ByActiveMod: false } answer ? answer.State : null;

        public string? Reason(ThnItemKind kind, string template) =>
            data.Get(kind, template) is { ByActiveMod: false } answer ? answer.Reason : null;

        public bool MakesCompounds => data.MakesCompounds;
    }
}
