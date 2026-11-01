using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Missions.Prefabs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using OpenLancer.Editor.Ui.Fields;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The Inspector page of a mission selection (section 10 of the M6a design). From the top, it has the header line with
/// the glyph of the kind, the chips and the info icon. Then come the read-only note, the lead lines and the
/// completeness panel. Then come the component sections and the INI block in the raw INI editor. It draws from the page
/// model (<see cref="MissionPageModel"/>), so each page kind has the same frame. The kinds are the mission, a trigger
/// or a line, a prefab instance or kind and the cast. Other kinds are an order list, an objective, a conversation, a
/// random encounter, a label, an act, and the cutscene and base scene links.
/// </summary>
/// <remarks>The page keeps its model and the marks of its INI block until the run of the checks, the selection or a
/// toggle changes. So it does not make them again in each frame. The list fields are read-only in M6a, with the same
/// widget as an editable list field (section 11.1).</remarks>
internal sealed partial class MissionPage(EditorWindow window, PropertyGrid grid) : IDisposable
{
    // The empty states of the mission pages of the Inspector (#56).
    internal static readonly EmptyState NoPrefab = new($"No mod has the prefab '{EmptyState.TextPlace}'.",
        "Select a prefab in the library or in the Assets panel.");
    internal static readonly EmptyState NoScript =
        new("This mission script is not in the edit view.", "Select a mission in the Missions tab.");

    // Sizes in unscaled points.
    private const float HeaderPaddingX = 10;
    private const float IniHeight = 220;

    private readonly RawIniEditor ini = new(MissionWorkspace.Vocabulary);
    private readonly Dictionary<string, ListField> lists = [];
    private (MissionRunResult? Result, MissionAnalysis? Analysis, MissionRef? Item, bool ShowBaseGame, bool HidesOpenLancer,
        string? NoEdit, GraphFile? File, (long Revision, bool Dirty, int Run, int Live, long LiveSecond, bool Shown) Session,
        MissionPageModel? Model) page;
    private (MissionRunResult? Result, string? Key, bool ShowBaseGame, RawText? Text, IReadOnlyList<RawMark> Marks)
        block = (null, null, false, null, []);

    public void Draw(MissionRef item)
    {
        var missions = window.Missions;
        if (missions.Result is not { } result)
        {
            PanelStates.Loading("Checking the mission scripts");
            return;
        }

        // In prefab mode, the script of the selection is the template of the prefab (section 8.6 of the M6b design).
        if (missions.Analysis(item.Script) is not { } analysis)
        {
            if (PrefabRef.OfScript(item.Script) is { } missing)
            {
                PanelStates.Message(NoPrefab.With(missing.Id));
            }
            else
            {
                PanelStates.Message(NoScript);
            }

            return;
        }

        // A gesture whose widget is gone ends, so the history does not wait for it.
        if (gesture != null && !GestureShows())
        {
            EndGesture();
        }

        var model = Model(result, analysis, item, out var changed);
        if (missions.PendingSection is var (script, section) && VfsPath.Comparer.Equals(script, item.Script) &&
            item.Kind == MissionItemKind.Mission)
        {
            missions.PendingSection = null;
            grid.RequestSection(SectionKey(section));
        }
        else if (changed && model.FocusSection is { } focus)
        {
            grid.RequestSection(SectionKey(focus));
        }

        Header(model);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.BeginChild("##mission-page", Vector2.Zero);
        ImGui.PopStyleVar();
        if (visible)
        {
            var padding = MathF.Truncate(HeaderPaddingX * ImGuiHelper.Scale);
            ImGui.Indent(padding);
            // The read-only note shows only while the session takes no edits (section 8.1 of the M6b design).
            if (missions.ReadOnlyNote is { } note)
            {
                ImGui.TextDisabled(note);
            }
            else if (model.ReadOnly is { } reason)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextDisabled(reason);
                ImGui.PopTextWrapPos();
            }
            else if (model.Base is { } change)
            {
                BaseBanner(change);
            }

            foreach (var field in model.Lead)
            {
                LeadLine(field);
            }

            if (model.Completeness is { } report)
            {
                CompletenessPanel.Draw("completeness", report, missions.ShowBaseGame, GoTo,
                    missions.ReadOnlyNote == null ? step => Fixes(analysis, step) : null);
            }

            ImGui.Unindent(padding);
            foreach (var pageSection in model.Sections)
            {
                Section(model, pageSection);
            }

            ImGui.Indent(padding);
            AddAction(model);
            ImGui.Unindent(padding);
            Ini(result, analysis, model, wholeScript: item.Kind == MissionItemKind.Mission);
            PrefabFile(model);
        }

        ImGui.EndChild();
    }

    /// <summary>Why the INI block of a template in prefab mode takes no edits.</summary>
    internal const string TemplateIniNote =
        "This block shows the lines of the prefab with the roles as names. Change the lines in the fields or in the graph. " +
        "The Prefab page shows the text of the prefab file.";

    /// <summary>The key of a section of the page in the settings and in a section request.</summary>
    private static string SectionKey(string id) => $"Mission.{id}";

    // True while the banner of a changed base shows the two sets of lines.
    private bool compareBase;

    /// <summary>
    /// The banner of a section whose base changed after the edit of the active mod (sections 6.3 and 8.11 of the M6b
    /// design). "Compare" shows the lines of the edit and of the new base side by side. "Keep my lines" accepts the new
    /// base, and "Use the new base" removes the edit of the section. Each button is one undo entry.
    /// </summary>
    private void BaseBanner(BaseChange change)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle} {BaseChange.Text}");
        ImGui.PopTextWrapPos();
        if (ImGui.SmallButton("Compare##base"))
        {
            compareBase = !compareBase;
        }

        ImGui.SetItemTooltip(BaseChange.CompareTip);
        ImGui.SameLine();
        if (ImGui.SmallButton("Keep my lines##base"))
        {
            Run(change.Keep);
        }

        ImGui.SetItemTooltip(BaseChange.KeepTip);
        ImGui.SameLine();
        if (ImGui.SmallButton("Use the new base##base"))
        {
            Run(change.Revert);
        }

        ImGui.SetItemTooltip(BaseChange.RevertTip);
        if (!compareBase || !ImGui.BeginTable("##base-compare", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            return;
        }

        ImGui.TableSetupColumn("Your lines");
        ImGui.TableSetupColumn("The new base");
        ImGui.TableHeadersRow();
        for (var i = 0; i < Math.Max(change.Mine.Count, change.Base.Count); i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            CompareLine(change.Mine, change.Base, i);
            ImGui.TableNextColumn();
            CompareLine(change.Base, change.Mine, i);
        }

        ImGui.EndTable();
    }

    /// <summary>A line of the compare of a changed base. A line that differs from the line of the other side shows in
    /// the accent color.</summary>
    private static void CompareLine(IReadOnlyList<string> lines, IReadOnlyList<string> other, int index)
    {
        var text = index < lines.Count ? lines[index] : "";
        if (index < other.Count && other[index] == text)
        {
            ImGui.TextDisabled(text);
        }
        else
        {
            ImGui.TextColored(EditorTheme.AccentText, text);
        }
    }

    /// <summary>The model of the page, made again only when the run, the selection or a toggle changes.</summary>
    /// <param name="changed">True when the page made a new model for another item.</param>
    private MissionPageModel Model(MissionRunResult result, MissionAnalysis analysis, MissionRef item, out bool changed)
    {
        var session = window.Session!;
        var showBaseGame = window.Missions.ShowBaseGame;
        var hidesOpenLancer = session.Edits.HidesOpenLancer;
        var noEdit = window.Missions.NoEditReason(item.Script);
        // The graph file holds the acts and the records of the prefab instances, so a change of it makes the page again.
        // A save takes the blue bar of an override away, so a save makes the page again too.
        var file = window.Missions.GraphOf(item.Script);
        // A change of the dry run or of the live view, and a change of the view that shows it, make the section "Run" of
        // the Trigger page again (section 4.5 of the M6c design). The states of the live view also change with the time,
        // for example "Ran" for one second and the stall, so the page follows each second of the live view.
        var run = window.Missions.ShownRun(item.Script);
        var liveSecond = run?.Live == true ? (long)window.Missions.Live.Now : 0;
        var state = (session.Edits.Revision, session.Edits.IsDirty, window.Missions.DryRuns.Version,
            window.Missions.Live.Version, liveSecond, run != null);
        changed = !Equals(page.Item, item);
        if (page.Model is { } known && ReferenceEquals(page.Result, result) && ReferenceEquals(page.Analysis, analysis) &&
            !changed && page.ShowBaseGame == showBaseGame && page.HidesOpenLancer == hidesOpenLancer &&
            page.NoEdit == noEdit && ReferenceEquals(page.File, file) && page.Session == state)
        {
            return known;
        }

        var prefab = PrefabRef.OfScript(item.Script);
        var context = new PageContext(result, analysis, showBaseGame, hidesOpenLancer)
        {
            Text = id => session.Name(id),
            CommFile = CommFile,
            SystemName = session.SystemName,
            NameOf = NameOf,
            // A section of a template has the reason of its prefab only.
            ReadOnlyOf = section => window.Missions.NoEditReason(section.Path) ??
                                    (PrefabRef.OfScript(section.Path) == null ? session.Edits.Missions.ReadOnlyReason(section) : null),
            ScriptReadOnly = window.Missions.NoEditReason,
            SessionReadOnly = window.Missions.ReadOnlyNote,
            UnsavedOverrides = session.Edits.Missions.UnsavedOverrides,
            FreePrefabId = session.Edits.Missions.FreePrefabId,
            Prefab = prefab != null ? session.Edits.Missions.PrefabMode(prefab) : null,
            Acts = prefab == null ? window.Missions.ActsOf(analysis) : null,
            ChangedOf = session.Edits.Missions.ChangedLines,
            BaseOf = session.Edits.Missions.ChangedBase,
            SharedBuildFile = session.Edits.Missions.IsSharedBuildFile,
            Run = run,
            RunNow = run?.Live == true ? window.Missions.Live.Now : 0,
        };
        var model = MissionPageModel.For(context, item);
        page = (result, analysis, item, showBaseGame, hidesOpenLancer, noEdit, file, state, model);
        if (changed)
        {
            // The widgets of another item start again, and an open gesture of the item before ends.
            lists.Clear();
            ForgetWidgets();
            compareBase = false;
        }
        else
        {
            // The new model has the values of the last edit.
            ForgetNumbers();
        }

        return model;
    }

    /// <summary>The header line: the glyph of the kind, the title, the kind as a dim label and the chips. The page menu
    /// and the info icon are at the right end.</summary>
    private void Header(MissionPageModel model)
    {
        var padding = MathF.Truncate(HeaderPaddingX * ImGuiHelper.Scale);
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight() * 2;
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, height), ImGui.GetColorU32(EditorTheme.Section));
        ImGui.SetCursorScreenPos(min + new Vector2(padding, MathF.Truncate(ImGui.GetFrameHeight() / 2)));
        ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.TextBright);
        ImGui.Text($"{model.Glyph}  {model.Title}");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled(model.Kind);
        if (model.HeaderChips.Count > 0)
        {
            ImGui.SameLine();
            Chip.Flow(model.HeaderChips.Select(c => new ChipItem(c, ChipLook.Grey, MissionPageModel.TriggerChipTip(c))).ToList());
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X + width - 2 * InfoIcon.Width - padding,
            min.Y + MathF.Truncate((height - ImGui.GetFrameHeight()) / 2)));
        PageMenu(model);
        ImGui.SetCursorScreenPos(new Vector2(min.X + width - InfoIcon.Width - padding,
            min.Y + MathF.Truncate((height - ImGui.GetFrameHeight()) / 2)));
        InfoIcon.Draw("header", model.Help);
        ImGui.SetCursorScreenPos(min + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 1));
    }

    /// <summary>A line under the read-only note, for example "Part of: Spawn wave t_spawn" with a link.</summary>
    private void LeadLine(PageField field)
    {
        ImGui.TextDisabled($"{field.Label}:");
        ImGui.SameLine();
        if (field.Link is { } link)
        {
            if (ImGui.TextLink($"{field.Value}##lead-{field.Label}"))
            {
                GoTo(link);
            }
        }
        else
        {
            ImGui.Text(field.Value);
        }

        if (field.Help != null)
        {
            ImGui.SetItemTooltip(field.Help);
        }
    }

    private void Section(MissionPageModel model, PageSection section)
    {
        var add = section.AddGroup is { } group && model.PageTrigger is { } trigger && window.Missions.ReadOnlyNote == null
            ? (EditorIcons.Plus, $"Add an action of the group {section.Title}.", (Action)(() => AddToGroup(trigger, group)))
            : ((char, string, Action)?)null;
        if (!grid.BeginSection(SectionKey(section.Id), section.Glyph, section.Title, !section.Advanced, section.Help, add))
        {
            return;
        }

        for (var i = 0; i < section.Fields.Count; i++)
        {
            Field(section, i, section.Fields[i]);
        }

        foreach (var note in section.Notes)
        {
            grid.NoteRow(note);
        }

        SectionActions(section);
        grid.EndSection();
    }

    /// <summary>
    /// One field: a problem, a heading, a list field, chips, a reference or the parts of a value. Else it is a link, a
    /// struck or missing value, or a text. A value field uses the read-only state of the widget of an editable field.
    /// So M6b only turns on the edit (section 10.1 of the M6a design).
    /// </summary>
    private void Field(PageSection section, int index, PageField field)
    {
        if (field.Help != null && !field.Heading)
        {
            grid.Help(field.Help, field.Hard);
        }

        var id = $"{section.Id}.{index}";
        var editable = window.Missions.ReadOnlyNote == null;
        // The label of a value that the active mod sets gets the blue bar (section 8.1 of the M6b design, UI rule 7). An
        // override of a prefab instance is bold with the violet bar (section 8.6).
        if (field.Changed)
        {
            grid.MarkNextChanged();
        }

        if (field.Override)
        {
            grid.MarkNextOverride();
        }

        // A field with an edit binding that the session can change draws its edit widget, with its menu after it
        // (section 8.4 of the M6b design). Else it uses the read-only state of the widget.
        if (field is { Heading: false, List: null, Edit: not null } && EditField(id, field))
        {
            return;
        }

        if (field.Severity != null && field.Link is LineLink)
        {
            Problem(field);
        }
        else if (field.Actions.Count > 0 && editable)
        {
            // A row with a menu: the value or the link, and the menu button after it.
            grid.Row(field.Label, () =>
            {
                ImGui.AlignTextToFramePadding();
                if (field.Link is { } link)
                {
                    if (ImGui.TextLink($"{field.Value}##link-{id}"))
                    {
                        GoTo(link);
                    }
                }
                else if (field.Struck)
                {
                    StruckOrMissing(field);
                }
                else
                {
                    ImGui.Text(field.Value);
                }

                RowMenu(id, field.Actions);
            });
        }
        else if (field.Heading)
        {
            Heading(field);
        }
        else if (field.List is { } list)
        {
            // The list field has its own label and rows, so it spans both columns of the section.
            ListEdit(id, field, list);
        }
        else if (field.Chips.Count > 0)
        {
            grid.Row(field.Label, () =>
            {
                var clicked = Chip.Flow(field.Chips.Select(c => new ChipItem(c.Text, ChipLook.Grey, c.Tooltip,
                    c.Link != null)).ToList());
                if (clicked >= 0 && field.Chips[clicked].Link is { } link)
                {
                    GoTo(link);
                }
            });
        }
        else if (field.Reference is { } reference)
        {
            var link = field.Link;
            grid.ReferenceTo(field.Label, new ObjectKind(reference.Kind, reference.Glyph, EditorTheme.Label),
                reference.Nickname, reference.Name, link != null ? () => GoTo(link) : null, field.Missing);
        }
        else if (field.Parts.Count > 0)
        {
            grid.Parts(field.Label, field.Parts);
        }
        else if (field.Struck || field.Missing)
        {
            grid.Row(field.Label, () => StruckOrMissing(field));
        }
        else if (field.Link is { } link)
        {
            if (grid.Link(field.Label, field.Value, field.Help ?? "Go to the item."))
            {
                GoTo(link);
            }
        }
        else if (field.Severity is { } severity)
        {
            grid.Row(field.Label, () => ImGui.TextColored(
                severity == DiagnosticSeverity.Error ? EditorTheme.Error : EditorTheme.Warning, field.Value));
        }
        else
        {
            grid.Text(field.Label, field.Value);
        }
    }

    /// <summary>A heading row, for example "Line 3: Spawn ship", with a link to the line when it has one. A heading
    /// without a link shows its help text in its tooltip.</summary>
    private void Heading(PageField field)
    {
        if (field.Link is { } link)
        {
            if (grid.Link(field.Label, field.Value, field.Help ?? "Select the line."))
            {
                GoTo(link);
            }

            return;
        }

        grid.SubHeading($"{field.Label}: {field.Value}");
        if (field.Help != null)
        {
            ImGui.SetItemTooltip(field.Help);
        }
    }

    /// <summary>A value that the game ignores, struck through, or a name that does not resolve, with the "Missing" look
    /// of M21.</summary>
    private static void StruckOrMissing(PageField field)
    {
        if (field.Missing)
        {
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.ExclamationTriangle} {field.Value}");
            return;
        }

        ImGui.TextDisabled(field.Value);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var y = MathF.Round((min.Y + max.Y) / 2);
        ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, y), new Vector2(max.X, y), ImGui.GetColorU32(EditorTheme.TextDim));
    }

    /// <summary>A row of the Problems section: the glyph of the severity, or the lock glyph of a base-game problem, the
    /// message, and "Go to". As in the Console, only the glyph has the severity color, and a base-game problem is muted
    /// (decision D512).</summary>
    private void Problem(PageField field)
    {
        grid.Row(field.Label, () =>
        {
            var (glyph, color) = field.Muted ? (EditorIcons.Lock, EditorTheme.TextDim)
                : field.Severity == DiagnosticSeverity.Error ? (EditorIcons.TimesCircle, EditorTheme.Error)
                : (EditorIcons.ExclamationTriangle, EditorTheme.Warning);
            ImGui.TextColored(color, glyph.ToString());
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, field.Muted ? EditorTheme.TextDim : EditorTheme.Text);
            ImGui.TextWrapped(field.Value);
            ImGui.PopStyleColor();
            if (field.Link is { } link && ImGui.SmallButton("Go to"))
            {
                GoTo(link);
            }

            ImGui.SetItemTooltip("Open the mission at the line in the Mission tab.");
            // The fixes of the problem (section 8.7 of the M6b design). Each fix is one undo entry.
            if (window.Missions.ReadOnlyNote == null && field.Actions.Count > 0)
            {
                CompletenessPanel.FixButtons(field.Actions.Select(a => (a.Label, (Action)(() => Run(a.Batch)))).ToList());
            }
        });
    }

    /// <summary>True while the user edits the INI block or drags a field.</summary>
    public bool IsEditing => ini.IsEditing || gesture != null;

    /// <summary>True while the rename popup or quick add of the page is open. An agent call waits for it (section 5.5,
    /// item 3 of the MCP1 design).</summary>
    public bool InGesture => rename.IsOpen || quickAdd.IsOpen;

    /// <summary>Ends an open edit of the INI block and an open drag of a field, for example before a save.</summary>
    public void EndEdit()
    {
        ini.EndEdit();
        EndGesture();
    }

    /// <summary>The INI block of the sections of the page, with the marks of the checks (section 10.1 item 7 of the M6a
    /// design). The user can edit it while the session takes edits (section 8.8 of the M6b design). The Mission page
    /// also has a button that opens the whole script. A page with several sections, for example a prefab instance,
    /// shows them all in one block that starts closed.</summary>
    /// <param name="wholeScript">True for the Mission page, which has the button for the whole script.</param>
    private void Ini(MissionRunResult result, MissionAnalysis analysis, MissionPageModel model, bool wholeScript)
    {
        var targets = model.IniSections.Count > 0 ? model.IniSections
            : model.Ini is { } one ? [one]
            : [];
        var sections = targets.Select(t => analysis.Document.Find(t)).OfType<MissionSection>().ToList();
        if (sections.Count == 0)
        {
            ini.EndEdit();
            return;
        }

        if (!grid.BeginSection(SectionKey(model.IniSections.Count > 0 ? "iniAll" : "ini"), EditorIcons.Terminal, model.IniTitle,
                model.IniSections.Count == 0, MissionSchema.Retail.Help["rawIni"]))
        {
            // A closed block ends its open edit.
            ini.EndEdit();
            return;
        }

        grid.EndSection();
        var showBaseGame = window.Missions.ShowBaseGame;
        var key = string.Join("|", sections.Select(s => s.Ref.ToString()));
        if (block.Text is not { } text || !ReferenceEquals(block.Result, result) || block.Key != key ||
            block.ShowBaseGame != showBaseGame)
        {
            // The block keeps the identity of each section in its file, so a repeated nickname finds its marks.
            text = RawText.Canonical(sections[0].Ref.Path, sections.Select(s => s.Section).ToList(),
                ids: sections.Select(s => s.Ref.Id).ToList());
            var refs = sections.Select(s => s.Ref).ToHashSet();
            block = (result, key, showBaseGame, text, IniLint.WithNotes(
                IniLint.Marks(text, analysis.Problems.Where(p => p.Target != null && refs.Contains(p.Target)), showBaseGame),
                PrefabLinks.MemberLines(text, analysis)));
        }

        var height = MathF.Min(IniHeight * ImGuiHelper.Scale * (sections.Count > 1 ? 2 : 1),
            (text.Lines.Count + 2) * ImGui.GetTextLineHeightWithSpacing());
        var path = sections[0].Ref.Path;
        var ids = sections.Select(s => s.Ref.Id).ToList();
        // The INI block of a template in prefab mode shows its lines in the role form. The prefab file is the text that the
        // editor writes, so the block is read-only. The Prefab page shows the text of the file.
        var readOnly = PrefabRef.OfScript(path) != null ? TemplateIniNote : window.Missions.NoEditReason(path);
        ini.DrawEditable("##ini", key, text, block.Marks, new Vector2(ImGui.GetContentRegionAvail().X, height),
            readOnly, (start, end) => window.Missions.CommitRaw(path, ids, start, end), window.Missions.Draft(path, ids));
        if (wholeScript && ImGui.Button("Open the whole script"))
        {
            window.Missions.ShowRaw = true;
            window.Missions.OpenMission(analysis.Script.Path, show: true);
        }
    }

    /// <summary>The text of the prefab file of the Prefab page, read-only, with a mark on each line with a problem (UI rule
    /// 11). A prefab mode edit changes the file through the commands, so the text does not take edits.</summary>
    private void PrefabFile(MissionPageModel model)
    {
        if (model.PrefabFile is not { } file)
        {
            return;
        }

        if (!grid.BeginSection(SectionKey(MissionPageModel.FileSection), EditorIcons.Terminal, "Prefab file (read-only)", false,
                MissionSchema.Retail.Help["rawIni"]))
        {
            return;
        }

        grid.EndSection();
        ImGui.TextDisabled(file.Path);
        var height = MathF.Min(IniHeight * 2 * ImGuiHelper.Scale, (file.Text.Lines.Count + 2) * ImGui.GetTextLineHeightWithSpacing());
        ini.Draw("##prefab-file", file.Text, file.Marks, new Vector2(ImGui.GetContentRegionAvail().X, height));
    }

    /// <summary>Goes to the item of a link: a mission selection, a line of a problem, a section, a system or a
    /// cutscene. A bundle row does its action.</summary>
    private void GoTo(object link)
    {
        var missions = window.Missions;
        switch (link)
        {
            case MissionRef item:
                window.Selection = item;
                break;
            case RawLineLink raw:
                missions.ShowRaw = true;
                missions.OpenMission(raw.Target.Path, show: true);
                window.Selection = MissionRef.OfSection(raw.Target, raw.Entry);
                missions.PendingLine = (raw.Target, raw.Entry);
                break;
            case LineLink line:
                missions.GoTo(line.Target, line.Entry);
                break;
            case SectionRef target:
                // A section outside the mission scripts, for example a ship build, shows its own page.
                if (!missions.GoTo(target, -1))
                {
                    window.Selection = target;
                }

                break;
            case SystemLink system when window.Session?.GameData?.Items.Systems.Get(system.Nickname) is { } found:
                window.OpenSystem(found);
                break;
            case CutsceneLink cutscene:
                missions.OpenCutscene(cutscene.Value);
                break;
            case PrefabModeLink or PrefabAssetLink or SavePrefabLink:
                missions.Follow(link);
                break;
            case BundleLink { Row.Action: MissionBundleAction.ShowSection, Row.Section: { } id }:
                grid.RequestSection(SectionKey(id));
                break;
            case BundleLink { Row.Action: MissionBundleAction.OpenCutscene, Row.Files: [var file, ..] }:
                missions.OpenCutscene(file);
                break;
            case BundleLink bundle:
                missions.OpenMission(bundle.Script, show: true);
                break;
        }
    }

    /// <summary>The fixes of a step of the completeness panel: one batch of <see cref="ApplyFix"/> for each fix.</summary>
    private IReadOnlyList<(string Label, Action Run)> Fixes(MissionAnalysis analysis, CompletenessStep step) =>
        step.Fix is { } subject
            ? MissionPageModel.FixActions(analysis, subject).Select(a => (a.Label, (Action)(() => Run(a.Batch)))).ToList()
            : [];

    /// <summary>The voices file of a comm line, from the comm line list of the edit view, or null.</summary>
    private string? CommFile(string line) =>
        window.Session?.Edits.Lists.List("comm_line")?.Find(line)?.Section?.Path is { } path
            ? MissionFiles.Display(path)
            : null;

    /// <summary>The name text of an item of the game data: a system, a base, a faction or a ship archetype. Null for
    /// another kind or an item that the game data does not have.</summary>
    private string? NameOf(MissionValueKind kind, string nickname)
    {
        if (window.Session is not { GameData: { } data } session)
        {
            return null;
        }

        object? item = kind switch
        {
            MissionValueKind.System => data.Items.Systems.Get(nickname),
            MissionValueKind.Base => data.Items.Bases.Get(nickname),
            MissionValueKind.Faction => data.Items.Factions.Get(nickname),
            MissionValueKind.ShipArch => data.Items.Ships.Get(nickname),
            _ => null,
        };
        return item != null ? session.ItemName(item) : null;
    }

    /// <summary>Forgets the model and the texts of the profile.</summary>
    public void Reset()
    {
        ForgetWidgets();
        page = default;
        block = (null, null, false, null, []);
        lists.Clear();
        compareBase = false;
    }

    public void Dispose() => ini.Dispose();
}
