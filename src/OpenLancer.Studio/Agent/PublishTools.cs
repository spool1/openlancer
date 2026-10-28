using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;

namespace OpenLancer.Studio.Agent;

/// <summary>A step of the publish checklist in the result of <c>publish_checklist</c>.</summary>
/// <param name="Missing">What is missing and why it is necessary. Empty when the step is complete.</param>
internal sealed record ChecklistStepRow(int Number, string Name, bool Complete, IReadOnlyList<string> Missing);

/// <summary>The result of <c>publish_checklist</c>: the steps of the checklist of the Mods window, as <c>oltool
/// publish --json</c> gives them.</summary>
/// <param name="Mod">The id of the active mod.</param>
/// <param name="Name">The display name of the mod.</param>
/// <param name="Version">The version of the mod.</param>
/// <param name="Complete">The number of complete steps.</param>
/// <param name="Total">The number of steps.</param>
/// <param name="Recommended">The recommended items. They do not count as steps.</param>
internal sealed record ChecklistView(string Mod, string Name, string Version, int Complete, int Total,
    IReadOnlyList<ChecklistStepRow> Steps, IReadOnlyList<string> Recommended)
{
    /// <summary>The problems of the reads of <c>publish.json</c> and of the publish history.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>A note about what the checklist reads, or null.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The tools <c>publish_checklist</c> and <c>export_mod</c> of section 7.6 of the MCP1 design. Both read the saved files
/// of the active mod, not the edit view. They use the builders of the Mods window, the Export dialog and oltool (review
/// finding R12 of MCP1-2). So the agent gets the same steps and the same packages.
/// </summary>
internal static class PublishTools
{
    /// <summary>
    /// The publish checklist of the active mod (<c>PublishChecklist.Of</c>). The session part loads the mod and the
    /// linked games. The tail reads the history and the fingerprints of the files of the mod on a worker, as the Export
    /// dialog does.
    /// </summary>
    public static ToolStep Checklist(ToolCall call)
    {
        var session = call.Session;
        var mod = SavedMod(session);
        var games = call.Host.Games(mod.Manifest);
        var history = ModExport.DefaultFolder(session.Profile.FilePath, mod.Id);
        var note = session.IsDirty ? AgentTexts.Error("checklist_unsaved") : null;
        return ToolStep.Then(cancel => Task.Run(() =>
        {
            ChecklistInput input;
            IReadOnlyList<string> problems;
            try
            {
                input = ChecklistInput.Read(mod, history, games.Reports, games.PlayTargets, out problems);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new EditException(new EditError(EditErrorKind.Refused, "ModNotRead",
                    AgentTexts.Error("mod_not_read", ("reason", e.Message))));
            }
            var checklist = PublishChecklist.Of(input);
            return ToolResult.Of(new ChecklistView(mod.Id, mod.Manifest.Name, mod.Manifest.Version, checklist.CompleteCount,
                checklist.Steps.Count,
                checklist.Steps.Select(s => new ChecklistStepRow(s.Number, s.Name, s.Complete, s.Missing)).ToList(),
                checklist.Recommended.Select(r => r.Text).ToList())
            {
                Warnings = problems,
                Note = note,
            });
        }, cancel));
    }

    /// <summary>The active mod as its saved files give it: the export and the checklist read the saved
    /// files.</summary>
    /// <exception cref="EditException"><see cref="EditErrorKind.Refused"/>: the session has no active mod, or its
    /// <c>mod.json</c> does not load.</exception>
    private static Mod SavedMod(EditSession session)
    {
        if (session.ActiveMod is not { } active)
            throw new EditException(new EditError(EditErrorKind.Refused, "NoActiveMod", AgentTexts.Error("no_active_mod")));
        try
        {
            return Mod.Load(active.Folder);
        }
        catch (ModLoadException e)
        {
            throw new EditException(new EditError(EditErrorKind.Refused, "ModNotRead",
                AgentTexts.Error("mod_not_read", ("reason", e.Message))));
        }
    }
}
