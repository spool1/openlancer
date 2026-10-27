using System;
using System.IO;
using System.Linq;
using ImGuiNET;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Dialogs;

/// <summary>
/// The dialog of a delete or a rename of the Scene (decisions D238 and D242 of the M3 design): what goes too, what lines
/// change, what lines show "Missing", the files that name the item as text, and the other warnings. A refused plan shows
/// the reason and one button, OK.
/// </summary>
internal sealed class PlanDialog : ModalDialog
{
    private ScenePlan? plan;
    private Action? run;
    private string title = "Delete";

    protected override string Title => $"{title}###plan";

    /// <param name="run">Runs the command of the plan when the user clicks Delete or Rename.</param>
    public void Show(ScenePlan plan, Action run)
    {
        this.plan = plan;
        this.run = run;
        title = !plan.CanRun
            ? plan.Action == SceneAction.Delete ? "Cannot delete" : "Cannot rename"
            : plan.Label;
        Open();
    }

    protected override void Body()
    {
        if (plan == null)
        {
            return;
        }

        if (!plan.CanRun)
        {
            foreach (var refusal in plan.Refusals)
            {
                ImGui.TextWrapped(refusal.Message);
            }

            ImGui.Spacing();
            if (Button("OK", primary: true))
            {
                Close();
            }

            return;
        }

        var nickname = plan.Deletes.FirstOrDefault()?.Section.Id.Key ?? plan.SelectAfter?.Id.Key ?? "";
        List("These items go too:", plan.Deletes.Where(d => d.Why == DeleteReason.Cascade)
            .Select(d => $"{d.Section.Id.Key ?? d.Section.Id.ToString()} ({d.Cause}) in {Path.GetFileName(d.Section.Path)}"));
        List("These lines change:", plan.Fixes.Select(f => f.Text));
        List("These lines show Missing:", plan.Missing.Select(m =>
            $"{m.Reference.Section.Id} '{m.Reference.Key}' in {Path.GetFileName(m.Reference.Section.Path)}. {m.Consequence}" +
            (m.BlocksSave ? " The save stops until you fix it." : "")));
        List(plan.Mentions.Select(m => m.Nickname).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                ? $"These files name {plan.Mentions[0].Nickname} as text:"
                : "These files name the items as text:",
            plan.Mentions.Select(m => $"{m.Path} ({m.Count} {(m.Count == 1 ? "time" : "times")})"));
        List("Warnings:", plan.Warnings.Select(w => w.Message));
        if (plan.Action == SceneAction.Delete && plan.Deletes.Count == 1 && plan.Fixes.Count == 0 && plan.Missing.Count == 0)
        {
            ImGui.TextWrapped($"Delete {nickname}?");
        }

        ImGui.Spacing();
        if (Button(plan.Action == SceneAction.Rename ? "Rename" : "Delete", primary: true))
        {
            Close();
            run?.Invoke();
            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    /// <summary>A heading and a bullet for each line, or nothing when there are no lines.</summary>
    private static void List(string heading, System.Collections.Generic.IEnumerable<string> lines)
    {
        var all = lines.ToList();
        if (all.Count == 0)
        {
            return;
        }

        ImGui.TextColored(EditorTheme.Label, heading);
        foreach (var line in all.Take(40))
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped(line);
        }

        if (all.Count > 40)
        {
            ImGui.TextDisabled($"and {all.Count - 40} more");
        }

        ImGui.Spacing();
    }
}
