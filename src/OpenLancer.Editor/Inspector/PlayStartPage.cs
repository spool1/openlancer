using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Run;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Play start page of the Inspector (section 8.3 of the M6c design): the start, the story slot, the story stage, the
/// accept, the start place, the ship, the triggers that wait at the start, the warnings, and the setup triggers as
/// read-only raw INI in the closed section "Advanced". A change of the start or of the place changes the play start in
/// the editor settings. It is not data of the mod, so it has no Undo entry.
/// </summary>
internal sealed class PlayStartPage(EditorWindow window, PropertyGrid grid) : IDisposable
{
    private const float HeaderPaddingX = 10;

    private readonly ValuePicker basePicker = new();
    private readonly RawIniEditor setupIni = new(MissionWorkspace.Vocabulary);
    private bool pickBase;
    private (MissionAnalysis Analysis, PlayStartSetting Setting, PlayStartPageModel Model)? cache;

    public void Draw(MissionRef item)
    {
        var missions = window.Missions;
        if (missions.Analysis(item.Script) is not { } analysis)
        {
            PanelStates.Message("This mission script is not in the edit view.", "Select a mission in the Missions tab.");
            return;
        }

        var plays = missions.Plays;
        var setting = plays.PlayStartOf(item.Script);
        if (cache is not var (a, s, model) || !ReferenceEquals(a, analysis) || s != setting)
        {
            model = PlayStartPageModel.Of(analysis, setting);
            cache = (analysis, setting, model);
        }

        Header();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.BeginChild("##play-start-page", Vector2.Zero);
        ImGui.PopStyleVar();
        if (visible)
        {
            Body(analysis, model);
        }

        ImGui.EndChild();
    }

    private static void Header()
    {
        var padding = MathF.Truncate(HeaderPaddingX * ImGuiHelper.Scale);
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight() * 2;
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(EditorTheme.Section));
        ImGui.SetCursorScreenPos(min + new Vector2(padding, MathF.Truncate(ImGui.GetFrameHeight() / 2)));
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        ImGui.Text($"{EditorIcons.Play}  Play start");
        ImGui.PopStyleColor();
        ImGui.SetCursorScreenPos(new Vector2(min.X + width - InfoIcon.Width - padding,
            min.Y + MathF.Truncate((height - ImGui.GetFrameHeight()) / 2)));
        InfoIcon.Draw("play-start-help", PlayStartPageModel.Help);
        ImGui.SetCursorScreenPos(min + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 1));
    }

    private void Body(MissionAnalysis analysis, PlayStartPageModel model)
    {
        var plan = model.Plan;
        var script = model.Script;
        var plays = window.Missions.Plays;
        // The check of the start place reads the game data and the Scene, which change without a new plan.
        var placeProblem = plan.CanStart ? window.StartPlaceProblem(plan.Place) : null;
        if (grid.BeginSection("PlayStart.Start", EditorIcons.Play, "Play start", true, PlayStartPageModel.Help))
        {
            grid.Help(PlayStartPageModel.StartTip);
            grid.Row("Start", () => StartCombo(analysis, model));
            if (plan.Kind == StartKind.Approximate)
            {
                grid.Row("Approximate", () =>
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled("The setup triggers rebuild the state of the path.");
                    ImGui.SameLine();
                    InfoIcon.Draw("approximate-help", PlayStartPageModel.ApproximateHelp);
                });
            }

            if (!plan.CanStart)
            {
                grid.FullRow(() =>
                {
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {StartPlan.RefusalPrefix}{plan.Reason}");
                    ImGui.PopTextWrapPos();
                });
            }

            grid.Help(PlayStartPageModel.SlotTip);
            grid.Text("Story slot", plan.Slot?.Slot ?? "No story slot");
            if (plan.CanStart)
            {
                grid.Help(PlayStartPageModel.StageTip);
                grid.Text("Story stage", model.StageText);
                grid.Help(PlayStartPageModel.AcceptTip);
                grid.Text("Mission accepted", plan.Accepted ? "Yes" : "No");
                grid.Help(PlayStartPageModel.PlaceTip);
                grid.Row("Start place", () => PlaceCombo(script, model));
                if (placeProblem != null)
                {
                    Warning(placeProblem);
                }

                if (model.ShowsSource)
                {
                    grid.Help(PlayStartPageModel.SourceTip);
                    grid.Row("Source of the place", () => PlaceSource(analysis, model));
                }

                var (ship, shipTip) = PlayShips.ShipText(window.Session, ShipPackage());
                grid.Help($"{PlayStartPageModel.ShipTip} {shipTip}");
                grid.Text("Ship", ship);
            }

            grid.EndSection();
        }

        foreach (var warning in plan.Warnings)
        {
            Warning(warning);
        }

        if (grid.BeginSection("PlayStart.Wait", EditorIcons.DotCircle, $"Triggers that wait at the start ({plan.ActiveSet.Count})",
                true, null))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextDisabled(PlayStartPageModel.WaitTip);
            ImGui.PopTextWrapPos();
            foreach (var name in plan.ActiveSet)
            {
                if (ImGui.Selectable($"{RunOverlay.TriggerName(analysis, name)}##wait-{name}") &&
                    analysis.Index.Trigger(name) is { } trigger)
                {
                    SelectTrigger(script, trigger);
                }
            }

            grid.EndSection();
        }

        if (model.SetupText is { } setup && grid.BeginSection("PlayStart.Advanced", EditorIcons.Terminal, "Advanced", false, null))
        {
            ImGui.TextDisabled("Setup lines");
            ImGui.SetItemTooltip(PlayStartPageModel.SetupTip);
            // Read-only raw INI with the colors of the mission scripts (UI rule 11). The plan refuses a setup trigger
            // with an error of the checks, so the lines have no marks.
            var height = (setup.Lines.Count + 2) * ImGui.GetTextLineHeightWithSpacing();
            setupIni.Draw("##setup-lines", setup, [], new Vector2(ImGui.GetContentRegionAvail().X,
                Math.Min(height, 20 * ImGui.GetTextLineHeightWithSpacing())));
            grid.EndSection();
        }

        // Play from the start: the same request as F5 in the Mission tab.
        var block = !plan.CanStart ? StartPlan.RefusalPrefix + plan.Reason : placeProblem;
        grid.FullRow(() =>
        {
            ImGui.BeginDisabled(block != null);
            if (ImGui.Button($"{EditorIcons.Play}  Play from the play start"))
            {
                plays.PlayFromPlayStart(script);
            }

            ImGui.EndDisabled();
            ImGui.SetItemTooltip(block ?? PlayStartPageModel.PlayTip);
        });
    }

    /// <summary>Frees the native editor of the setup lines.</summary>
    public void Dispose() => setupIni.Dispose();

    private void Warning(string text) => grid.FullRow(() =>
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {text}");
        ImGui.PopTextWrapPos();
    });

    private void StartCombo(MissionAnalysis analysis, PlayStartPageModel model)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##start", model.Plan.StartName))
        {
            ImGui.SetItemTooltip(PlayStartPageModel.StartTip);
            return;
        }

        EditorTheme.PushMenuColors();
        foreach (var (start, label) in model.StartChoices(analysis.Index))
        {
            if (ImGui.Selectable(label, start.Equals(model.Setting.Start)))
            {
                window.Missions.Plays.SetPlayStart(model.Script, model.Setting with { Trigger = start.Trigger ?? "" });
            }
        }

        EditorTheme.PopMenuColors();
        ImGui.EndCombo();
    }

    private void PlaceCombo(string script, PlayStartPageModel model)
    {
        var plays = window.Missions.Plays;
        var (place, dock) = model.Setting.PlaceChoice;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##place", model.PlaceText))
        {
            EditorTheme.PushMenuColors();
            if (ImGui.Selectable($"Suggested: {model.SuggestedText}", place == StartPlaceChoice.Suggested))
            {
                plays.SetPlayStart(script, PlayStartSetting.Of(model.Setting.Start, StartPlaceChoice.Suggested, null));
            }

            if (ImGui.Selectable("At base...", place == StartPlaceChoice.Base))
            {
                pickBase = true;
            }

            if (ImGui.Selectable("At the Scene camera", place == StartPlaceChoice.Camera))
            {
                plays.SetPlayStart(script, PlayStartSetting.Of(model.Setting.Start, StartPlaceChoice.Camera, null));
            }

            EditorTheme.PopMenuColors();
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip(PlayStartPageModel.PlaceTip);
        var open = pickBase;
        pickBase = false;
        if (window.Session is not { } session)
        {
            return;
        }

        var picked = basePicker.Draw("##start-base", open, dock ?? "", () =>
            session.Edits.Lists.List("base")?.Entries.Select(e => (e, e.Name ?? e.Value)).ToList() ?? [],
            none: false, ObjectKinds.OfList("base"));
        if (picked is { Length: > 0 })
        {
            plays.SetPlayStart(script, PlayStartSetting.Of(model.Setting.Start, StartPlaceChoice.Base, picked));
        }
    }

    /// <summary>Where the suggested place comes from, and a link that selects the trigger of its line (UI rule 6).</summary>
    private void PlaceSource(MissionAnalysis analysis, PlayStartPageModel model)
    {
        var place = model.Plan.Suggested;
        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled(place.Source);
        ImGui.PopTextWrapPos();
        if (place.Trigger is not { } name || analysis.Index.Trigger(name) is not { } trigger)
        {
            return;
        }

        if (ImGui.Selectable($"Select {RunOverlay.TriggerName(analysis, name)}##place-source"))
        {
            SelectTrigger(model.Script, trigger);
        }

        ImGui.SetItemTooltip("Selects the trigger of the line in the graph.");
    }

    /// <summary>Selects a trigger of the script and frames it in the graph (UI rule 6).</summary>
    private void SelectTrigger(string script, MissionTrigger trigger)
    {
        window.Selection = new MissionRef(script, MissionItemKind.Trigger, trigger.Section.Ref);
        window.Missions.PendingGraphFrame = true;
    }

    /// <summary>The ship package of the Play menu, or null for the default ship.</summary>
    private string? ShipPackage() =>
        window.Session is { } session ? window.Settings.PlayPackageOf(session.Profile.FilePath) : null;
}
