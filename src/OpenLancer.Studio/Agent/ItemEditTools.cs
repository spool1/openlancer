using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
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
