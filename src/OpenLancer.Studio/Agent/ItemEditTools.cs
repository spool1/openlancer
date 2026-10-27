using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Runtime.Scene;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The generic edit tools of section 7.2 of the MCP1 design. Each tool takes the reference of an item. It calls the
/// command of the command layer that the editor UI calls for the item, through the shared edit call of
/// <see cref="EditTools"/>.
/// </summary>
internal static class ItemEditTools
{
    /// <summary>The keys of the name and of the infocard of a section, in the order that <c>set_text</c> tries
    /// them.</summary>
    private static readonly string[] NameKeys = ["ids_name", "strid_name"];

    private static readonly string[] InfoKeys = ["ids_info"];

    /// <summary>The most characters of the text of <c>set_text</c>. An infocard of the game data can have some thousand
    /// characters.</summary>
    public const int MaxText = 32768;

    public static ToolStep SetFields(ToolCall call) => ToolResult.Of(EditTools.Apply(call, scope =>
    {
        var section = EditTools.Section(call, call.Args.Text("ref")!, "ref");
        var fields = call.Args.Node("fields")!.AsObject().Select(f => KeyValuePair.Create(f.Key, f.Value)).ToList();
        scope.Run(FieldCommands.SetFields(scope.Session, section, fields), [section]);
    }));

    /// <summary>
    /// Sets the text of a text key of a section. Only the named section gets the new text, as the Inspector does when it
    /// does not ask. The result warns about each other section of the item that shows the same text, so the agent can
    /// call the tool on it too.
    /// </summary>
    public static ToolStep SetText(ToolCall call) => ToolResult.Of(EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var section = EditTools.Section(call, call.Args.Text("ref")!, "ref");
        var name = TextKey(session, section, call.Args.Text("field")!);
        // The shared sections keep the old text, so the check reads them before the edit.
        var shared = ItemEdits.SharedText(session, section, name);
        var plan = FieldCommands.SetText(session, section, name, call.Args.Text("text")!);
        if (!plan.CanRun)
        {
            var refusal = EditTools.RefusalOf(session, plan);
            throw new EditException(refusal with { Field = refusal.Kind == EditErrorKind.InvalidArgument ? "field" : "text" });
        }
        scope.Run(plan, [section]);
        if (shared.Count == 0)
            return;
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Warnings = shared.Select(s => new Problem(DiagnosticSeverity.Warning,
                AgentTexts.Error("text_shared", ("ref", ItemRefs.Canonical(session, s)), ("key", name)))
            {
                Ref = ItemRefs.Canonical(session, s),
            }).ToList(),
        });
    }));

    /// <summary>
    /// Gives an item a new nickname with each line that names it. An item of a system calls the rename of the Scene
    /// (decisions D12, D231 and D257). Another item calls the rename of the Database, which changes only an item that the
    /// active mod adds (review finding R4 of MCP1-2). The result gives the old and the new reference.
    /// </summary>
    public static ToolStep RenameItem(ToolCall call) => ToolResult.Of(EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var section = EditTools.Section(call, call.Args.Text("ref")!, "ref");
        var name = call.Args.Text("new_name")!;
        var scene = SceneSections.KindOf(session, section) != null;
        var item = scene ? ScenePlans.RenameTarget(session, section) : section;
        var from = ItemRefs.Canonical(session, item);
        var plan = scene ? SceneCommands.Rename(session, section, name) : DatabaseCommands.Rename(session, item, name);
        scope.Run(plan, [item]);
        var renamed = plan.SelectAfter ?? item with { Id = item.Id with { Key = name.Trim() } };
        scope.Report(new EditResult(EditStatus.Applied)
        {
            Renames = [new ResultRename(from, ItemRefs.Canonical(session, renamed))],
        });
    }));

    /// <summary>
    /// Deletes items of a system with the cascade of decision D81: the children, and the lines that a rule removes. The
    /// other lines that name them show "Missing". The editor has no delete of a data item, so such a reference refuses
    /// (review finding R4 of MCP1-2).
    /// </summary>
    public static ToolStep DeleteItems(ToolCall call) => ToolResult.Of(EditTools.Apply(call, scope =>
    {
        var targets = Targets(call);
        scope.Run(SceneCommands.Delete(scope.Session, targets), targets);
    }));

    /// <summary>
    /// Copies items of a system with their children (decisions D241 and D259 to D262). The copies are at the place of
    /// their items. With an offset, the same batch moves each copy and each copy of a child by the offset (review finding
    /// R6 of MCP1-2). A copy without a position stays as it is.
    /// </summary>
    public static ToolStep DuplicateItems(ToolCall call) => ToolResult.Of(EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var targets = Targets(call);
        var plan = SceneCommands.Duplicate(session, targets);
        scope.Run(plan, targets);
        if (call.Args.Point("offset") is not { } offset || offset == Vector3.Zero)
            return;
        var moves = plan.Adds.Where(s => session.Get(s)?.Lines("pos").Count > 0)
            .Select(s => new TransformMove(s, new TransformChange(Position: offset))).ToList();
        if (moves.Count > 0)
            scope.Run(SceneCommands.Transform(session, moves, TransformMode.Relative), moves.Select(m => m.Item));
    }));

    /// <summary>The sections of the argument <c>refs</c>, each one time. A call names at most the item limit of the
    /// host.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Limit"/> above the limit, or the error of a reference
    /// with the place of the reference in the list as the field.</exception>
    private static IReadOnlyList<SectionRef> Targets(ToolCall call)
    {
        var refs = call.Args.Texts("refs")!;
        var limit = call.Limits.EditItems;
        if (refs.Count > limit)
        {
            throw new EditException(new EditError(EditErrorKind.Limit, "EditItems",
                AgentTexts.Error("limit_items", ("max", limit))) { Field = "refs" });
        }
        return refs.Select((r, i) => EditTools.Section(call, r, $"refs[{i}]")).Distinct().ToList();
    }

    /// <summary>The INI key of the field argument of <c>set_text</c>. <c>name</c> and <c>info</c> give the key of the
    /// name and of the infocard of the section. Another text is the INI key itself.</summary>
    private static string TextKey(EditSession session, SectionRef section, string field)
    {
        var keys = field.ToLowerInvariant() switch
        {
            "name" => NameKeys,
            "info" => InfoKeys,
            _ => null,
        };
        if (keys == null)
            return field;
        var view = session.Get(section);
        return keys.FirstOrDefault(k => session.Schema(section, k) != null || view?.Lines(k).Count > 0) ?? keys[0];
    }
}
