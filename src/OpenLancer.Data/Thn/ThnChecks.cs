using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;

namespace OpenLancer.Data.Thn;

/// <summary>A collection of the game data that the engine reads when it builds an entity of a scene.</summary>
public enum ThnItemKind
{
    /// <summary>A solar archetype (GameDataManager.cs:372-376).</summary>
    Archetype,

    /// <summary>A ship (ThnScriptInstance.cs:129-134).</summary>
    Ship,

    /// <summary>A room of petalDB (GameDataManager.cs:394-397).</summary>
    Room,

    /// <summary>A cart of petalDB (GameDataManager.cs:389-392).</summary>
    Cart,

    /// <summary>An equipment (ThnScriptInstance.cs:144-147).</summary>
    Equipment,

    /// <summary>An asteroid (ThnScriptInstance.cs:148-151).</summary>
    Asteroid,

    /// <summary>A costume (ThnScriptInstance.cs:260-264).</summary>
    Costume,

    /// <summary>A prop of petalDB (GameDataManager.cs:377-386).</summary>
    Prop,

    /// <summary>An effect or a visual effect (ThnScriptInstance.cs:192-196).</summary>
    Effect,

    /// <summary>A sound (ThnScriptInstance.cs:282-288).</summary>
    Sound,

    /// <summary>A clip of the animation files, which a START_MOTION event on a character plays (StartMotionEvent.cs:34,
    /// AnimationComponent.cs:192-203). The engine finds it by the name as it is, without letter case.</summary>
    Animation,
}

/// <summary>What the game data has for the template of an entity. The states go from the worst to the best.</summary>
public enum ThnItemState
{
    /// <summary>The game data has no item with the nickname.</summary>
    Missing,

    /// <summary>The item has no model: no model file, or a costume without a body. Only the kinds of
    /// <see cref="ThnTemplateRules.NeedsModel"/> and a costume have this state.</summary>
    NoModel,

    /// <summary>The item has a model.</summary>
    Ready,
}

/// <summary>The game data around one script: the items that the engine reads when it builds the entities of a scene
/// (ThnScriptInstance.cs:106-281), the clips of the animation files, and how the game plays the script.
/// OpenLancer.Runtime implements it with the data of a view.</summary>
public interface IThnGameData
{
    /// <summary>
    /// What the game data has for the template of an entity, or null when the game data did not load or cannot tell. A
    /// room fills in some templates, for example <c>$terrain_dyna_01</c>, with the terrain of its base
    /// (ThnScriptInstance.cs:106-109, <see cref="ThnTemplateRules.RoomFillsIn"/>). When more than one room plays the
    /// script, the result is the worst state of the filled-in templates.
    /// </summary>
    ThnItemState? Item(ThnItemKind kind, string template);

    /// <summary>
    /// Why the item of a template has the state that <see cref="Item"/> gives, as a clause that ends with a period, or
    /// null. The checks add it to the message. Examples are a costume that the lists have but the engine drops, because
    /// its body names no body part (Missing), and an equipment of a section type that gets no model (NoModel).
    /// </summary>
    string? Reason(ThnItemKind kind, string template) => null;

    /// <summary>
    /// False when the game makes no COMPOUND of the script. This is so when the game plays the script only in space: the
    /// space scene uses the objects of the mission in their place (Cutscene.cs:101-113, ThnScriptInstance.cs:113).
    /// </summary>
    bool MakesCompounds { get; }
}

/// <summary>The problems of one walk of a script (see <see cref="ThnChecks.Check"/>).</summary>
/// <param name="Errors">The values that stop the engine.</param>
/// <param name="NameProblems">The templates that name no item and the clips that the game does not have, where the
/// engine does not stop.</param>
public sealed record ThnCheckResult(IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> NameProblems);

/// <summary>
/// The error checks of a cutscene script (section 9 of the M8 design). An error is a value that makes the engine throw
/// while it reads the script (ThnScript.cs:122-487, ThnEvent.cs:43-90 and the event classes), while it builds the
/// scene (ThnScriptInstance.cs:63-293), or when an event reads its targets. The cutscene then does not play. A name
/// problem is a template that names no item of the game data, or a clip of a character that the game does not have,
/// where the engine does not stop but the scene shows less, for example an entity without a model (section 12.1 of the
/// M8b design).
/// </summary>
/// <remarks>
/// <para>The engine reads the keys of an entity of each type, and the props of an event by the type of the event.
/// <see cref="ThnKeyRow.EngineRead"/> tells how the engine reads each key.</para>
/// <para>The scene does not make an entity that a later entity with the same name hides (ThnScript.cs:132-136), or a
/// REFERENCE entity (ThnScriptInstance.cs:70-71). So the checks of the build do not apply to them. For example, 15
/// retail COMPOUNDs are REFERENCE entities with a template and no category, and the category switch refuses such a
/// COMPOUND (ThnScriptInstance.cs:122). An event target with the name of such an entity names an object of another
/// script of the scene, for example of the set script of a room.</para>
/// <para>A DEFORMABLE with <c>userprops.Actor</c> loads its costume only when no room NPC or RTC character plays the
/// actor (ThnScriptInstance.cs:257-264). So a missing costume is an error only for a DEFORMABLE without an Actor.</para>
/// <para>The game makes no COMPOUND of a script that it plays only in space (see
/// <see cref="IThnGameData.MakesCompounds"/>), so the checks of a COMPOUND do not apply there.</para>
/// </remarks>
public static class ThnChecks
{
    private const string EntityGroup = "EntityTypes";
    private const string EventGroup = "EventTypes";
    private const string UserProps = "userprops";

    /// <summary>The longest value text in a message.</summary>
    private const int ShownLength = 60;

    /// <summary>The error of a START_MOTION event on a character without <c>props.animation</c>.</summary>
    internal const string MotionNeedsClip = "The key is missing. The engine needs it for a START_MOTION on a DEFORMABLE.";

    /// <summary>The keys of an entity. The engine reads them from an entity of each type (ThnScript.cs:253-487).</summary>
    private static readonly ImmutableArray<ThnKeyRow> EntityRows =
        [.. ThnSchema.Rows.Where(r => r.Element == ThnElementKind.Entity).DistinctBy(r => r.Path.Text, StringComparer.Ordinal)];

    /// <summary>
    /// The number of targets that an event of a type reads, where it does not check the number first. SET_CAMERA
    /// (SetCameraEvent.cs:17-30), ATTACH_ENTITY (AttachEntityEvent.cs:77-83) and START_PATH_ANIMATION
    /// (StartPathAnimationEvent.cs:34-35) read two. START_SPATIAL_PROP_ANIM and CONNECT_HARDPOINTS check the number,
    /// and the other types read no target.
    /// </summary>
    private static readonly FrozenDictionary<string, int> TargetsRead = new Dictionary<string, int>
    {
        ["SET_CAMERA"] = 2, ["ATTACH_ENTITY"] = 2, ["START_PATH_ANIMATION"] = 2, ["START_SOUND"] = 1,
        ["START_AUDIO_PROP_ANIM"] = 1, ["START_LIGHT_PROP_ANIM"] = 1, ["START_CAMERA_PROP_ANIM"] = 1, ["START_MOTION"] = 1,
        ["START_IK"] = 1, ["START_PSYS"] = 1, ["START_PSYS_PROP_ANIM"] = 1, ["START_FLR_HEIGHT_ANIM"] = 1,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The event types that have no class in the engine (ThnScript.cs:155-199).</summary>
    private static readonly string[] NoClass = ["UNDEFINED_EVENT", "USER_EVENT"];

    /// <summary>
    /// The errors of a script: each value that stops the engine, with its element and its path. A message names an
    /// element by its entity name or its event key, not by its place. So a problem keeps its message when an edit adds
    /// or deletes another element, and <see cref="Diagnostic.NewIn"/> can compare two versions of a script. The
    /// <see cref="Diagnostic.Subject"/> of a problem is the path of the value and the text, without the element. So a
    /// compare by the element can find the problem of an element after a rename.
    /// </summary>
    /// <param name="path">The install path of the script, for the source and the place of each problem.</param>
    /// <param name="globals">The globals of the script, as the evaluator gives them.</param>
    /// <param name="evaluator">Reads the text of a motion path, and gives the THN names.</param>
    /// <param name="data">The game data around the script, or null. Without it, the checks of the templates and the
    /// costumes do not run, and the checks of a COMPOUND run as for a script that a room plays.</param>
    public static IReadOnlyList<Diagnostic> Errors(string path, ThnTable globals, IThnEvaluator evaluator,
        IThnGameData? data = null) =>
        Check(path, globals, evaluator, data).Errors;

    /// <summary>
    /// The name problems of a script: each template that names no item of the game data, and each clip of a character
    /// that the game does not have, where the engine does not stop. A message names the element as in
    /// <see cref="Errors"/>, and it tells what the scene does without the item. Without game data, the result is
    /// empty.
    /// </summary>
    public static IReadOnlyList<Diagnostic> NameProblems(string path, ThnTable globals, IThnEvaluator evaluator,
        IThnGameData? data) =>
        Check(path, globals, evaluator, data).NameProblems;

    /// <summary>The errors and the name problems of a script, from one walk of the script (see <see cref="Errors"/> and
    /// <see cref="NameProblems"/>).</summary>
    public static ThnCheckResult Check(string path, ThnTable globals, IThnEvaluator evaluator, IThnGameData? data = null) =>
        new Checker(path, evaluator, data).Run(globals);

    /// <summary>An element of the <c>entities</c> or the <c>events</c> table.</summary>
    /// <param name="Index">The place in the list, from 0, or -1 for an element in the record part of the table.</param>
    /// <param name="Label">The element in a message: the entity name, the event key, or the place.</param>
    /// <param name="Key">The key text of the element for its <see cref="ThnLocation"/>, or null.</param>
    private sealed record Element(ThnElementKind Kind, ThnTable Table, int Index, string Label, string? Key);

    private sealed class Checker(string path, IThnEvaluator evaluator, IThnGameData? data)
    {
        private readonly IReadOnlyDictionary<string, ThnSymbolInfo> symbols = evaluator.Symbols;
        private readonly List<Diagnostic> errors = [];
        private readonly List<Diagnostic> names = [];

        // The tables of the entities, in the order of the engine, for the rule of the clips.
        private IReadOnlyList<ThnTable> entityTables = [];

        public ThnCheckResult Run(ThnTable globals)
        {
            // ThnScript.cs:124-125, :141.
            if (globals[ThnScriptModel.DurationGlobal] is not ThnNumber)
                Global(ThnScriptModel.DurationGlobal, globals, "a number");
            var entities = Elements(globals, ThnElementKind.Entity, ThnScriptModel.EntitiesGlobal);
            entityTables = entities.Select(e => e.Table).ToList();
            var events = Elements(globals, ThnElementKind.Event, ThnScriptModel.EventsGlobal);

            var broken = new HashSet<Element>(ReferenceEqualityComparer.Instance);
            foreach (var entity in entities)
            {
                if (!EntityReads(entity))
                    broken.Add(entity);
            }
            // The engine keeps the last entity of each name (ThnScript.cs:107, :132-136), and the scene does not make a
            // REFERENCE entity (ThnScriptInstance.cs:70-71).
            var named = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (ThnScriptModel.NameOf(entity.Table) is { } name)
                    named[name] = entity;
            }
            var made = named.Where(n => !broken.Contains(n.Value) && !ThnFlags.IsReference(n.Value.Table, symbols))
                .ToDictionary(n => n.Key, n => n.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var entity in made.Values)
                Build(entity);
            foreach (var ev in events)
                EventReads(ev, made);
            return new ThnCheckResult(errors, names);
        }

        /// <summary>The elements of a global, in the order of the engine: the list and then the record part
        /// (ThornTable.cs:392-409). Reports a global that is not a table, and an element that is not a table.</summary>
        private List<Element> Elements(ThnTable globals, ThnElementKind kind, string global)
        {
            var result = new List<Element>();
            if (globals[global] is not ThnTable table)
            {
                Global(global, globals, "a table");
                return result;
            }
            var items = table.List.Select((v, i) => (Value: v, Index: i, Place: ThnValues.ItemPath(global, i + 1)))
                .Concat(table.Record.Select(f => (f.Value, Index: -1, Place: ThnValues.FieldPath(global, f.Key))));
            foreach (var (value, index, place) in items)
            {
                if (value is ThnTable element)
                    result.Add(new Element(kind, element, index, Label(kind, element, place), ThnLocation.KeyOf(kind, element)));
                else
                    Add(new Element(kind, ThnTable.Empty, index, place, null), null, Value(value, "a table"));
            }
            return result;
        }

        /// <summary>Checks the keys that the engine reads from an entity. Returns false when a key stops the
        /// engine.</summary>
        private bool EntityReads(Element entity)
        {
            var before = errors.Count;
            foreach (var row in EntityRows)
                Key(entity, entity.Table, row, row.Path.Keys);
            // The engine gives the position of an entity with lightprops to its light (ThnScript.cs:433-436).
            if (entity.Table["lightprops"] is ThnTable && !IsVector(Get(entity.Table["spatialprops"], "pos")))
                Add(entity, "spatialprops.pos", "The value is not a list of three numbers. An entity with lightprops needs it.");
            return errors.Count == before;
        }

        /// <summary>Checks the slots of an event (ThnEvent.cs:43-65, :79-90, ThnScript.cs:155-199), the props that its
        /// type reads, and the targets that its type reads.</summary>
        /// <param name="made">The entities that the scene makes, by name.</param>
        private void EventReads(Element ev, Dictionary<string, Element> made)
        {
            var list = ev.Table.List;
            if (list.Length < 1 || list[0] is not ThnNumber)
                Add(ev, "time", Slot(list, 0, "a number"));
            string? type = null;
            if (list.Length < 2 || !TryName(list[1], EventGroup, out type))
                Add(ev, "type", Slot(list, 1, "a name of EventTypes or a number"));
            else if (type == null || NoClass.Contains(type, StringComparer.Ordinal))
            {
                Add(ev, "type", $"The value is {Shown(list[1])}. The engine has no event of this type.");
                type = null;
            }
            var targets = list.Length > 2 ? list[2] as ThnTable : null;
            if (targets == null)
                Add(ev, "targets", Slot(list, 2, "a table"));
            else if (targets.List.FirstOrDefault(t => t is not ThnText) is { } target)
                Add(ev, "targets", $"A target is {Shown(target)}, but the engine reads a text for each target.");
            var props = list.Length > ThnElementPaths.PropsSlot ? list[ThnElementPaths.PropsSlot] as ThnTable : null;
            if (list.Length > ThnElementPaths.PropsSlot)
            {
                if (props == null)
                    Add(ev, "props", Slot(list, ThnElementPaths.PropsSlot, "a table"));
                else if (type != null)
                {
                    foreach (var row in ThnSchema.RowsOf(ThnElementKind.Event, type))
                        Key(ev, props, row, row.Path.Keys[1..]);
                }
            }
            if (type == null || targets == null)
                return;
            var count = TargetsRead.GetValueOrDefault(type);
            if (targets.List.Length < count)
            {
                Add(ev, "targets", $"The event has {targets.List.Length} targets, but the engine reads {count} for {type}.");
                return;
            }
            if (type == "START_MOTION")
                Motion(ev, targets, props);
            // A target that names no entity that this script makes can be an object of another script of the scene, for
            // example of the set script of a room.
            if (type is not ("SET_CAMERA" or "START_PATH_ANIMATION") || targets.List[1] is not ThnText second ||
                !made.TryGetValue(second.Value, out var entity))
                return;
            // The camera update reads the camera props of the second target (Cutscene.cs:492-497, ThnCamera.cs:71).
            if (type == "SET_CAMERA" && TryName(entity.Table["type"], EntityGroup, out var entityType) && entityType != "CAMERA")
            {
                Add(ev, "targets", $"The second target is {entity.Label}, a {entityType ?? "type that the engine does not know"}. " +
                                   "SET_CAMERA needs a CAMERA there.");
            }
            // The path animation reads the path of the second target (StartPathAnimationEvent.cs:35-37,
            // ThnSceneObject.cs:218). An entity gets a path only from pathprops.path_data (ThnScript.cs:476-484).
            if (type == "START_PATH_ANIMATION" && ThnMotionPath.DataOf(entity.Table) == null)
            {
                Add(ev, "targets", $"The second target is {entity.Label}, which has no pathprops.path_data. " +
                                   "START_PATH_ANIMATION needs a path there.");
            }
        }

        /// <summary>
        /// A START_MOTION event on a character plays a clip of the animation files (StartMotionEvent.cs:34,
        /// AnimationComponent.cs:192-203). The engine finds the clip by the name as it is, and a character without the
        /// clip does not move. An empty clip names no clip either. The check applies only where the rule of the clip
        /// list applies (<see cref="ThnTemplateRules.PlaysClips"/>), so a COMPOUND, which plays the scripts of its own
        /// model, gets no check.
        /// </summary>
        /// <remarks>Without the key, the event has no clip, and the engine throws when it looks for the clip
        /// (StartMotionEvent.cs:34, AnimationComponent.cs:194). No code catches it (ThnScriptInstance.cs:335). The
        /// character of a DEFORMABLE has the animations (ThnScriptInstance.cs:266-270), and so does the room NPC that
        /// plays its Actor. So the missing key is an error.</remarks>
        private void Motion(Element ev, ThnTable targets, ThnTable? props)
        {
            // A props slot that is not a table has its own error.
            if (props == null && ev.Table.List.Length > ThnElementPaths.PropsSlot ||
                targets.List[0] is not ThnText first ||
                !ThnTemplateRules.PlaysClips(entityTables, [first.Value], symbols))
                return;
            var value = props?["animation"];
            if (value == null)
            {
                Add(ev, ThnTemplateRules.AnimationPath, MotionNeedsClip);
                return;
            }
            // A value that is not a text has its own error. No clip has an empty name.
            if (data == null || value is not ThnText { Value: var clip } ||
                data.Item(ThnItemKind.Animation, clip) != ThnItemState.Missing)
                return;
            Add(ev, ThnTemplateRules.AnimationPath, ThnTemplateRules.ClipProblem(clip), name: true);
        }

        /// <summary>Checks one key of a row in its root: the record of an entity or the props of an event.</summary>
        /// <param name="keys">The keys of the row path from the root.</param>
        private void Key(Element element, ThnTable root, ThnKeyRow row, string[] keys)
        {
            ThnValue? container = root;
            foreach (var key in keys[..^1])
            {
                container = Get(container, key);
                // A missing record has no keys, and a record that is not a table has its own error.
                if (container is not ThnTable)
                    return;
            }
            var parent = (ThnTable)container!;
            var inUserProps = element.Kind == ThnElementKind.Entity && keys is [UserProps, _];
            var value = Get(parent, keys[^1], inUserProps && !row.CaseSensitive);
            if (value == null)
            {
                if (row.Required)
                    Add(element, row.Path.Text, "The key is missing. The engine needs it.");
                return;
            }
            if (row.ReadWith is { } with && Get(parent, with, inUserProps) == null ||
                row.ReadWithout is { } without && Get(parent, without, inUserProps) != null)
                return;
            if (Problem(row, value) is { } problem)
                Add(element, row.Path.Text, problem);
        }

        /// <summary>Why the engine cannot read a value of a key, or null when it can.</summary>
        private string? Problem(ThnKeyRow row, ThnValue value)
        {
            switch (row.EngineRead)
            {
                case ThnRead.Path when value is ThnText text:
                    return ThnMotionPath.Problem(text.Value, evaluator) is { } problem
                        ? $"The value is not a path that the engine reads. {problem}"
                        : null;
                case ThnRead.Matrix:
                    // ThnTypes.cs:80-92.
                    return "The engine cannot read this key, because it reads the rows of a matrix from index 0. Use q_orient.";
            }
            var expected = row.EngineRead switch
            {
                ThnRead.Any => null,
                ThnRead.Number => value is ThnNumber ? null : "a number",
                ThnRead.Text or ThnRead.Path => value is ThnText ? null : "a text",
                ThnRead.Table => value is ThnTable ? null : "a table",
                ThnRead.ToNumber => IsNumber(value) ? null : "a number",
                ThnRead.ToBool => value is ThnBool or ThnNumber or ThnText { Value: "Y" or "N" or "y" or "n" } ? null : "Y, N or a number",
                ThnRead.ToName => TryName(value, row.Group, out _) ? null : $"a name of {row.Group} or a number",
                ThnRead.ToAxis => IsAxis(value) ? null : "an axis name or a list of three numbers",
                ThnRead.ToVector => IsVector(value) ? null : "a list of three numbers",
                ThnRead.ToQuaternion => HasNumbers(value, 4) ? null : "a list of four numbers { w, x, y, z }",
                ThnRead.Rotation => value is ThnTable { List: { Length: >= 3 } rows } && rows.Take(3).All(IsVector)
                    ? null
                    : "three lists of three numbers",
                ThnRead.Flags => ThnFlags.CanRead(value, symbols) ? null : "a number or a flag name",
                ThnRead.AxisRotation => value is ThnTable { List: [ThnNumber, ..] turn } &&
                                        (turn.Length < 2 || IsAxis(turn[1]))
                    ? null
                    : "a list of degrees and an axis",
                ThnRead.CurvePoints => value is ThnTable { List: var points } && points.All(p => HasNumbers(p, 4))
                    ? null
                    : "a list of points of four numbers",
                _ => throw new InvalidOperationException($"The read {row.EngineRead} has no check."),
            };
            return expected == null ? null : Value(value, expected);
        }

        /// <summary>Checks an entity that the scene makes (ThnScriptInstance.cs:63-293).</summary>
        private void Build(Element entity)
        {
            var table = entity.Table;
            if (!TryName(table["type"], EntityGroup, out var type))
                return;
            switch (type)
            {
                case "LIGHT" when table["lightprops"] == null:
                    // ThnScriptInstance.cs:215-221.
                    Add(entity, "lightprops", "The key is missing. The engine needs it for a LIGHT.");
                    break;
                case "SCENE" when !IsVector(table["ambient"]):
                    // ThnScriptInstance.cs:206-213.
                    Add(entity, "ambient", "The value is not a list of three numbers. The engine needs it for a SCENE.");
                    break;
                // The game data reads the INI files to know how the game plays the script, so the check asks only
                // for a COMPOUND with a problem.
                case "COMPOUND" when Compound(table) is { } problem && data?.MakesCompounds != false:
                    Add(entity, problem.Value, problem.Message, problem.Name);
                    break;
                case "DEFORMABLE":
                    Deformable(entity);
                    break;
                case "PSYS":
                    Psys(entity);
                    break;
            }
        }

        /// <summary>The problem of a COMPOUND that the scene makes, or null. A COMPOUND with a template needs a known
        /// category, and a template of that category (ThnScriptInstance.cs:113-160). An empty template gives an object
        /// without a model.</summary>
        /// <returns>The problem, with <c>Name</c> true for a name problem.</returns>
        private (string Value, string Message, bool Name)? Compound(ThnTable entity)
        {
            if (entity["template_name"] is not ThnText { Value.Length: > 0 } template ||
                template.Value.Equals(ThnTemplateRules.PlayerShip, StringComparison.OrdinalIgnoreCase))
                return null;
            if (ThnTemplateRules.UserProp(entity, "category") is not ThnText category)
                return ("userprops.category", "The key is missing. The engine needs it for a COMPOUND with a template.", false);
            var categories = ThnTemplateRules.Categories;
            if (!categories.TryGetValue(category.Value, out var rule))
            {
                return ("userprops.category", $"The value is {Shown(category)}, but the engine knows only the categories " +
                                              $"{string.Join(", ", categories.Keys.Order(StringComparer.Ordinal))}.", false);
            }
            return data?.Item(rule.Kind, template.Value) switch
            {
                ThnItemState.NoModel => ("template_name", NoModel(rule, template), false),
                ThnItemState.Missing when rule.MissingStops => ("template_name", Absent(rule, template), false),
                ThnItemState.Missing => ("template_name", $"{Absent(rule, template)} {rule.Consequence}", true),
                _ => null,
            };
        }

        /// <summary>The message of a template that names no item: the game data has no such item, or the engine cannot
        /// use it (<see cref="IThnGameData.Reason"/>).</summary>
        /// <param name="missing">The text when the game data has no such item, or null for the usual text.</param>
        private string Absent(ThnTemplateRule rule, ThnText template, string? missing = null) =>
            data?.Reason(rule.Kind, template.Value) is { } reason
                ? $"The {rule.What} {Shown(template)} is not usable: {reason}"
                : missing ?? $"The game data has no {rule.What} {Shown(template)}.";

        /// <summary>The message of an item without a model, with the reason when the game data tells it.</summary>
        private string NoModel(ThnTemplateRule rule, ThnText template) =>
            data?.Reason(rule.Kind, template.Value) is { } reason
                ? $"The {rule.What} {Shown(template)} has no model: {reason}"
                : $"The {rule.What} {Shown(template)} has no model.";

        /// <summary>A PSYS gets the effect of its template, or no effect (ThnScriptInstance.cs:192-205). The engine
        /// gives the template <c>PlayerShipEngines</c> to the engine of the player ship (ThnScriptInstance.cs:98-104),
        /// and an empty template gives no effect.</summary>
        private void Psys(Element entity)
        {
            if (entity.Table["template_name"] is not ThnText { Value.Length: > 0 } template ||
                template.Value.Equals(ThnTemplateRules.PlayerShipEngines, StringComparison.OrdinalIgnoreCase))
                return;
            var rule = ThnTemplateRules.Effect;
            if (data?.Item(rule.Kind, template.Value) == ThnItemState.Missing)
                Add(entity, "template_name", $"{Absent(rule, template)} {rule.Consequence}", name: true);
        }

        /// <summary>A DEFORMABLE without an Actor loads its costume (ThnScriptInstance.cs:255-264). The engine reads an
        /// Actor with <c>ToString</c>, so only a missing or an empty Actor loads the costume.</summary>
        private void Deformable(Element entity)
        {
            if (!ThnTemplateRules.LoadsCostume(entity.Table))
                return;
            if (entity.Table["template_name"] is not ThnText template)
            {
                Add(entity, "template_name", "The key is missing. A DEFORMABLE without userprops.Actor needs a costume.");
                return;
            }
            var rule = ThnTemplateRules.Costume;
            var state = data?.Item(rule.Kind, template.Value);
            if (state == ThnItemState.Missing)
                Add(entity, "template_name", Absent(rule, template, $"The costume {Shown(template)} is not in the game data.") +
                                             " A DEFORMABLE without userprops.Actor needs it.");
            else if (state == ThnItemState.NoModel)
                Add(entity, "template_name", $"The costume {Shown(template)} has no body. A DEFORMABLE without " +
                                             "userprops.Actor needs it.");
        }

        /// <summary>
        /// The name of a value that the engine converts to a THN name of a group (ThnTypes.cs:41-78): a name of the
        /// group, a number that the integer type of the group can hold, or a text with a THN name of a group with the
        /// same integer type. The engine then reads the number of the name as a number of the group.
        /// </summary>
        /// <param name="name">The name of the group with the number, or null when the group has no name with it.</param>
        /// <returns>False when the engine cannot convert the value.</returns>
        private bool TryName(ThnValue? value, string? group, out string? name)
        {
            name = null;
            var storage = symbols.Values.FirstOrDefault(s => s.Group == group)?.Storage;
            long? number = value switch
            {
                ThnSymbol symbol when Info(symbol.Name) is { } info && info.Group == group => info.Value,
                // The engine casts the number to an int, and the int to the integer type of the enum (ThnTypes.cs:72-77).
                ThnNumber n when storage == null || Holds(storage.Value, (int)n.Value) => (int)n.Value,
                ThnText text when storage != null && Info(text.Value) is { } info && info.Storage == storage => info.Value,
                _ => null,
            };
            if (number == null)
                return false;
            name = symbols.FirstOrDefault(s => s.Value.Group == group && s.Value.Value == number).Key;
            return true;
        }

        private ThnSymbolInfo? Info(string name) => symbols.GetValueOrDefault(name);

        /// <summary>An axis as <c>ConvertAxis</c> reads it (ThnTypes.cs:24-38).</summary>
        private bool IsAxis(ThnValue value) => value is ThnTable ? IsVector(value) : TryName(value, ThnSchema.AxisGroup, out _);

        /// <summary>A problem of a global: missing, or a value of another kind.</summary>
        private void Global(string key, ThnTable globals, string expected) =>
            Add(null, key, globals[key] is { } value
                ? Value(value, expected)
                : $"The global is missing. The engine reads {expected} there.");

        /// <param name="element">The element, or null for a problem of the whole script.</param>
        /// <param name="value">The path of the value in the element, or a global name, or null.</param>
        /// <param name="name">True for a name problem, false for an error.</param>
        private void Add(Element? element, string? value, string message, bool name = false)
        {
            var place = element is { Index: >= 0 } placed
                ? new ThnLocation(path, placed.Kind, placed.Index, value, placed.Key)
                : new ThnLocation(path);
            var subject = string.Join(", ", new[] { element?.Label, value }.OfType<string>());
            (name ? names : errors).Add(Diagnostic.Error(path, subject.Length == 0 ? message : $"{subject}: {message}") with
            {
                Cutscene = place,
                Subject = value == null ? message : $"{value}: {message}",
            });
        }
    }

    /// <summary>The label of an element in a message: <c>entity "name"</c>, <c>event { time, TYPE, { targets } }</c>, or
    /// the place when the element has no name or no key.</summary>
    private static string Label(ThnElementKind kind, ThnTable element, string place)
    {
        if (kind == ThnElementKind.Entity)
            return ThnScriptModel.NameOf(element) is { } name ? $"entity {ThnWriter.Value(new ThnText(name))}" : place;
        return ThnKeys.Match(kind, element) is { } match ? $"event {ThnKeys.Text(match)}" : place;
    }

    /// <summary>The value of a key in a table, or null. In the user props of an entity, the engine finds a key without
    /// letter case, and the last key with the name wins (ThnScript.cs:305-314).</summary>
    private static ThnValue? Get(ThnValue? table, string key, bool userProps = false) => table switch
    {
        ThnTable t when userProps => t.Record.LastOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value,
        ThnTable t => t[key],
        _ => null,
    };

    /// <summary>A number, or a text that holds a number, as <c>float.Parse</c> reads it (ThnTypes.cs:48-53).</summary>
    private static bool IsNumber(ThnValue value) =>
        value is ThnNumber || value is ThnText text && float.TryParse(text.Value,
            NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);

    /// <summary>A list whose first three items are numbers (ThornTable.cs:412-445).</summary>
    private static bool IsVector(ThnValue? value) => HasNumbers(value, 3);

    /// <summary>True when an integer type holds a number. The engine converts with <c>Convert.ChangeType</c>, which
    /// throws for a number out of range (ThnTypes.cs:77).</summary>
    private static bool Holds(TypeCode storage, long number) => storage switch
    {
        TypeCode.Byte => number is >= byte.MinValue and <= byte.MaxValue,
        TypeCode.SByte => number is >= sbyte.MinValue and <= sbyte.MaxValue,
        TypeCode.Int16 => number is >= short.MinValue and <= short.MaxValue,
        TypeCode.UInt16 => number is >= ushort.MinValue and <= ushort.MaxValue,
        TypeCode.UInt32 or TypeCode.UInt64 => number >= 0,
        _ => true,
    };

    private static bool HasNumbers(ThnValue? value, int count) =>
        value is ThnTable { List: var items } && items.Length >= count && items.Take(count).All(i => i is ThnNumber);

    /// <summary>The message of a value of another kind.</summary>
    private static string Value(ThnValue value, string expected) =>
        $"The value is {Shown(value)}, but the engine reads {expected} there.";

    /// <summary>The message of an event slot that is missing or holds a value of another kind.</summary>
    private static string Slot(ImmutableArray<ThnValue> list, int slot, string expected) => slot < list.Length
        ? Value(list[slot], expected)
        : $"The value is missing. The engine reads {expected} there.";

    /// <summary>A value as Lua text for a message, cut to <see cref="ShownLength"/> characters.</summary>
    internal static string Shown(ThnValue value)
    {
        var text = ThnWriter.Value(value);
        return text.Length <= ShownLength ? text : text[..(ShownLength - 3)] + "...";
    }
}
