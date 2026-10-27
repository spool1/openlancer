using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>
/// The data tools of section 7.3 of the MCP1 design that are not in a system: <c>create_from_template</c>. Each tool
/// calls the command of the Database of the editor (see <see cref="DatabaseCommands"/>), in the shared edit call of
/// <see cref="EditTools"/>.
/// </summary>
internal static class DataEditTools
{
    /// <summary>The words of <c>kind</c> of <c>create_from_template</c>, with the data list of each kind.</summary>
    public static readonly IReadOnlyList<string> TemplateKinds = ["ship", "equipment"];

    /// <summary>The detail code of a template that is not an item of the kind of the request.</summary>
    public const string TemplateKind = "TemplateKind";

    /// <summary>
    /// Makes a new ship or new equipment as a copy of a template, with its goods, packages and munitions. The Database
    /// calls this "New from template". The new item is an item of the active mod, so <c>rename_item</c> can rename it
    /// later.
    /// </summary>
    public static ToolStep CreateFromTemplate(ToolCall call) => EditTools.Apply(call, scope =>
    {
        var session = scope.Session;
        var args = call.Args;
        var list = args.Text("kind")!;
        var kind = list == "ship" ? ItemFileKind.Ships : ItemFileKind.Equipment;
        var text = args.Text("template")!;
        var template = EditTools.At("template", () =>
        {
            if (!text.Contains(':'))
            {
                return session.Find(kind, text) ?? throw new EditException(new EditError(EditErrorKind.NotFound,
                    ItemRefs.NotFound, AgentTexts.Error("template_none", ("list", list), ("template", text)) +
                                       ItemRefs.Hint(text, session.Lists.List(list)?.Entries.Select(e => e.Value) ?? []))
                {
                    Target = text,
                });
            }
            // No nickname holds a ':' (section 5.6 of the MCP1 design), so a text with one is a reference.
            var section = EditTools.Section(call, text, "template");
            if (section.Id.Key is not { } key || !section.Equals(session.Find(kind, key)))
            {
                throw new EditException(new EditError(EditErrorKind.InvalidArgument, TemplateKind,
                    AgentTexts.Error("template_kind", ("ref", text), ("list", list))) { Target = text });
            }
            return section;
        });
        var plan = DatabaseCommands.CreateFromTemplate(session, template, args.Text("nickname")!.Trim());
        scope.Run(plan);
    });
}
