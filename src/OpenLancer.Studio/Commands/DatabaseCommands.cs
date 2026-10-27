using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;

namespace OpenLancer.Studio.Commands;

/// <summary>
/// The commands of the items of the Database (section 4.0.8 of the M6b design). They make a new item from a template,
/// and rename an item that the active mod adds. The Database and the agent tools call them. Each one gives the plan
/// that its caller runs.
/// </summary>
/// <remarks>Each command wraps the edit of today (<see cref="ItemTemplates"/> and <see cref="ItemEdits.Rename"/>) and
/// does not copy its rules. A command never throws for a rule of the edit. It gives a plan that refuses, with the
/// reason. A plan reads the edit view when it is made, so make it and run it at once.</remarks>
public static class DatabaseCommands
{
    /// <summary>
    /// Plans a new ship or new equipment from a template, with its goods, packages and munitions (the "New from
    /// template" of the Database). See <see cref="ItemTemplates.Plan"/>.
    /// </summary>
    /// <param name="template">The <c>[Ship]</c> or the equipment section to copy.</param>
    /// <param name="nickname">The nickname of the new item.</param>
    public static EditPlan CreateFromTemplate(EditSession session, SectionRef template, string nickname) =>
        CreateFromTemplate(ItemTemplates.Plan(session, template, nickname));

    /// <summary>
    /// The plan of a template plan that the Database dialog shows: the sections that it adds, and the new item to select.
    /// A template plan with problems refuses with them. A template plan that the edit view no longer matches refuses
    /// with the reason (see <see cref="ItemTemplates.Build"/>).
    /// </summary>
    public static EditPlan CreateFromTemplate(TemplatePlan plan) => new TemplateEditPlan(plan);

    /// <summary>
    /// Plans a new nickname for an item that the active mod adds, with each line that names it. The tool
    /// <c>rename_item</c> calls it for a data item (section 7.2 of the MCP1 design). See
    /// <see cref="ItemEdits.Rename"/>. A nickname that another section of the same kind of file has refuses with the
    /// detail <see cref="MissionErrors.NameTaken"/>.
    /// </summary>
    /// <remarks>The plan warns about each mission line that names the item and that the edit cannot change. The save
    /// stops at such a line (rule M23).</remarks>
    public static EditPlan Rename(EditSession session, SectionRef item, string newNickname) =>
        new RenamePlan(session, item, newNickname);

    /// <summary>The plan of a template plan.</summary>
    private sealed class TemplateEditPlan : EditPlan
    {
        public TemplateEditPlan(TemplatePlan plan) : base($"New {plan.Nickname} from {plan.Template.Id.Key}")
        {
            if (plan.Problems.Count > 0)
            {
                Refusals = plan.Problems;
                return;
            }

            try
            {
                Command = ItemTemplates.Build(plan);
            }
            catch (InvalidOperationException e)
            {
                Refusals = [Diagnostic.Error(plan.Template.Path, ErrorText.Of(e)) with { Target = plan.Template }];
                return;
            }

            Adds = plan.Copies.Select(c => c.Copy).ToList();
            SelectAfter = plan.Copies.FirstOrDefault(c => c.Source.Equals(plan.Template))?.Copy;
        }
    }

    /// <summary>The plan of a rename of an item.</summary>
    private sealed class RenamePlan : EditPlan
    {
        public RenamePlan(EditSession session, SectionRef item, string newNickname)
            : base($"Rename {item.Id.Key} to {newNickname}")
        {
            var warnings = new List<Diagnostic>();
            try
            {
                Command = ItemEdits.Rename(session, item, newNickname, warnings);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // The rename throws an InvalidOperationException only for a nickname that another section has.
                Refusals =
                [
                    Diagnostic.Error(item.Path, ErrorText.Of(e)) with
                    {
                        Target = item,
                        Rule = e is InvalidOperationException ? MissionErrors.NameTaken : null,
                    },
                ];
                return;
            }

            Changes = [item with { Id = item.Id with { Key = newNickname } }];
            Warnings = warnings;
        }
    }
}
