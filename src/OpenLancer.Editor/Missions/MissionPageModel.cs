using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Fields;
using OpenLancer.Data.IO;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Ui;
using static OpenLancer.Editor.Missions.MissionCommandRefs;

namespace OpenLancer.Editor.Missions;

/// <summary>A chip of a page field, for example a system of a mission.</summary>
/// <param name="Link">The item that a click on the chip goes to, or null for a chip without a click.</param>
internal sealed record PageChip(string Text, string Tooltip, object? Link);

/// <summary>A link to a line of a mission script: the Mission tab opens the mission at the line.</summary>
/// <param name="Entry">The index of the line in the section, or -1 for the whole section.</param>
internal sealed record LineLink(SectionRef Target, int Entry);

/// <summary>A link to a system: the Scene opens the system.</summary>
internal sealed record SystemLink(string Nickname);

/// <summary>A link to a bundle row of a mission: a click does what the row does in the Missions tab.</summary>
internal sealed record BundleLink(string Script, MissionBundleRow Row);

/// <summary>A link to a space cutscene: a click opens it in the Cutscene tab. The value is the path of the line.</summary>
internal sealed record CutsceneLink(string Value);

/// <summary>A value of a page field that names an item of the script or of the game data (section 10.1 of the M6a
/// design). The page draws it with the reference widget of the Inspector, read-only in M6a.</summary>
/// <param name="Nickname">The name as the line gives it.</param>
/// <param name="Name">The name text of the item, or null.</param>
/// <param name="Glyph">The glyph of the kind of the item.</param>
/// <param name="Kind">The kind of the item in plain words, for example "Faction".</param>
internal sealed record PageReference(string Nickname, string? Name, char Glyph, string Kind);

/// <summary>A link to a line in the "Raw INI" view of the whole script (the row menu item "Show in raw INI").</summary>
/// <param name="Entry">The index of the line in the section.</param>
internal sealed record RawLineLink(SectionRef Target, int Entry);

/// <summary>The widget of an editable page field (section 8.4 of the M6b design).</summary>
internal enum EditWidget
{
    /// <summary>A name of the script or of the game data: the list picker with the "Missing" and "None" rows.</summary>
    Picker,

    /// <summary>A word of a closed set, or a condition of the field "Event": a dropdown.</summary>
    Words,

    /// <summary>A number: a drag field (M20).</summary>
    Number,

    /// <summary>A whole number: a drag field of whole numbers.</summary>
    WholeNumber,

    /// <summary>Three numbers, with "Pick in Scene" for a position.</summary>
    Position,

    /// <summary>A rotation: four numbers.</summary>
    Rotation,

    Bool,

    /// <summary>A text id: a number, or own text that the editor writes into <c>strings.json</c>.</summary>
    TextId,

    Text,

    /// <summary>A list widget (section 8.5 of the M6b design).</summary>
    List,

    /// <summary>An "Add" button with a picker: a pick adds an item to the rows above it, for example a trigger to
    /// "Started by" or a member to a label.</summary>
    AddPicker,
}

/// <summary>
/// The edit binding of a page field (section 8.4 of the M6b design): the widget kind, the value and the functions that
/// make the command of a new value. The page draws the widget, and one gesture of the widget gives one batch. The
/// binding makes no change itself, so the tests check the batches.
/// </summary>
/// <param name="Label">The label of the history entry of an edit, for example "Set Delay".</param>
internal sealed record FieldEdit(EditWidget Widget, string Label)
{
    /// <summary>The value now: one value, the numbers of a position joined with ", ", or the key of the condition of the
    /// field "Event".</summary>
    public string Value { get; init; } = "";

    /// <summary>The spec of the value, or null.</summary>
    public MissionValueSpec? Spec { get; init; }

    /// <summary>The ids of the data lists of a picker, joined with "+", or null.</summary>
    public string? List { get; init; }

    /// <summary>The file that scopes the mission lists of a picker: the script.</summary>
    public string? Scope { get; init; }

    /// <summary>True when the picker offers "None", which writes <see cref="NoneWord"/>.</summary>
    public string? NoneWord { get; init; }

    /// <summary>The choices of a dropdown: the value, its plain label and its group, or null.</summary>
    public IReadOnlyList<(string Value, string Label, string? Group)> Choices { get; init; } = [];

    /// <summary>Makes the batch of a new value.</summary>
    public Func<MissionValue, MissionBatch?>? Make { get; init; }

    /// <summary>Makes the batch of own text of a text id: the text goes into the text of the mod.</summary>
    public Func<string, MissionBatch?>? MakeText { get; init; }

    /// <summary>Makes the batch of a list after one gesture of the list widget.</summary>
    public Func<ListFieldModel, MissionBatch?>? MakeList { get; init; }

    /// <summary>Makes the batch that adds the picked item of an <see cref="EditWidget.AddPicker"/> field.</summary>
    public Func<string, MissionBatch?>? Add { get; init; }

    /// <summary>True for a position that "Pick in Scene" can set.</summary>
    public bool PickInScene { get; init; }

    /// <summary>The system of the item of a position, or null. "Pick in Scene" takes a point only while the Scene shows
    /// this system.</summary>
    public string? PickSystem { get; init; }

    /// <summary>True when an edit can need a confirmation, for example a change of a ship build of a shared file. The
    /// widget then runs one batch at the end of its gesture, so the dialog can ask (section 4.0.5 of the M6b
    /// design).</summary>
    public bool Confirms { get; init; }

    /// <summary>Why the field cannot change, or null. The widget then has its read-only state.</summary>
    public string? ReadOnly { get; init; }

    /// <summary>A note under the widget, for example why the value names no file, or null.</summary>
    public string? Note { get; init; }

    /// <summary>The severity of <see cref="Note"/> when the note is a problem of the checks, or null for a plain
    /// note.</summary>
    public DiagnosticSeverity? NoteSeverity { get; init; }

    /// <summary>The notes of the choices of a dropdown by their value, for example the note of the shared ship build
    /// file. The dropdown shows a note under its choice and in the tooltip of the choice.</summary>
    public IReadOnlyDictionary<string, string> ChoiceNotes { get; init; } = new Dictionary<string, string>();

    /// <summary>The item "New file..." at the end of a dropdown, or null.</summary>
    public NewFileItem? NewFile { get; init; }
}

/// <summary>
/// The item "New file..." of a dropdown (#18). A popup asks for a name, and shows the install path and the live refusal
/// of the name. "Create" runs the batch. The functions make no change, so the tests check them.
/// </summary>
/// <param name="Title">The title of the popup, for example "New ship build file".</param>
/// <param name="Tooltip">The tooltip of the item in the dropdown.</param>
/// <param name="Note">A dim note of the popup, for example what happens to the old file.</param>
/// <param name="DefaultName">The name in the field when the popup opens.</param>
/// <param name="PathOf">Gives the install path of a name.</param>
/// <param name="Refusal">Gives why "Create" cannot make the file of a name, or null.</param>
/// <param name="Make">Makes the batch of "Create" for a name.</param>
internal sealed record NewFileItem(string Title, string Tooltip, string Note, string DefaultName, Func<string, string> PathOf,
    Func<string, string?> Refusal, Func<string, MissionBatch> Make)
{
    /// <summary>The label of the item in the dropdown.</summary>
    public const string Label = "New file...";
}

/// <summary>An item of a row menu or a page menu, for example "Move up". It runs a batch, or goes to a link. An item
/// without a batch and a link is a heading of the menu, for example "Overrides (2)".</summary>
internal sealed record PageAction(string Label, MissionBatch? Batch, object? Link = null, string? Tooltip = null)
{
    /// <summary>True for a heading of the menu.</summary>
    public bool IsHeading => Batch == null && Link == null;
}

/// <summary>One field of a page section: a plain name and a value, with an optional help text and an item to go
/// to.</summary>
/// <param name="Help">The help text of the field, or null.</param>
/// <param name="Link">The item that a click on the field selects, or null.</param>
/// <param name="Muted">True for a base-game problem that shows muted, with the lock glyph.</param>
/// <param name="Severity">The severity of a problem, or null for a field that is not a problem.</param>
internal sealed record PageField(string Label, string Value, string? Help = null, object? Link = null, bool Muted = false,
    DiagnosticSeverity? Severity = null)
{
    /// <summary>The chips of the field in place of its value, for example the systems of a mission.</summary>
    public IReadOnlyList<PageChip> Chips { get; init; } = [];

    /// <summary>True for a field that the schema marks as hard to understand. Its label gets the help glyph (section
    /// 11.3 of the M6a design).</summary>
    public bool Hard { get; init; }

    /// <summary>The list of a list field in place of the value, or null (section 11.1 of the M6a design). The page shows
    /// it read-only in M6a.</summary>
    public ListFieldModel? List { get; init; }

    /// <summary>True for a heading row of the lines that follow it, for example "Line 3: Spawn ship".</summary>
    public bool Heading { get; init; }

    /// <summary>True for a line that the game ignores, for example a condition before the last one. The value is struck
    /// through.</summary>
    public bool Struck { get; init; }

    /// <summary>True for a name that does not resolve. The value has the "Missing" look of M21.</summary>
    public bool Missing { get; init; }

    /// <summary>The item that the value names, or null. The page draws it with the reference widget.</summary>
    public PageReference? Reference { get; init; }

    /// <summary>The named parts of a value of several numbers, for example the X, Y and Z of a position. The page draws
    /// one field for each part. Empty for another value.</summary>
    public IReadOnlyList<(string Label, string Value)> Parts { get; init; } = [];

    /// <summary>The edit binding of the field, or null for a field that does not change (section 8.4 of the M6b
    /// design).</summary>
    public FieldEdit? Edit { get; init; }

    /// <summary>True when the active mod sets the value, also with an edit that is not saved. The label gets the blue bar
    /// (section 8.1 of the M6b design, UI rule 7).</summary>
    public bool Changed { get; init; }

    /// <summary>True for a field of a linked prefab instance that overrides the prefab, or a removed member. The label is
    /// bold and gets the violet bar. With <see cref="Changed"/>, the override is not saved, and the blue bar comes
    /// beside the violet bar (section 8.6 of the M6b design, UI rule 7).</summary>
    public bool Override { get; init; }

    /// <summary>The items of the row menu of the field, for example "Move up" of an action line.</summary>
    public IReadOnlyList<PageAction> Actions { get; init; } = [];
}

/// <summary>
/// The banner of a section whose base changed after the edit of the active mod (sections 6.3 and 8.11 of the M6b design):
/// the lines of the new base and the lines of the edit for "Compare", and the batches of "Keep my lines" and "Use the new
/// base".
/// </summary>
/// <param name="Base">The lines of the new base, each as "key = values".</param>
/// <param name="Mine">The lines of the edit, each as "key = values".</param>
internal sealed record BaseChange(IReadOnlyList<string> Base, IReadOnlyList<string> Mine, MissionBatch Keep, MissionBatch Revert)
{
    public const string Text = "The base of this section changed after your edit.";
    public const string CompareTip = "Show your lines and the lines of the new base side by side.";
    public const string KeepTip = "Keep your lines and accept the new base.";
    public const string RevertTip = "Remove your edit of this section.";
}

/// <summary>One component section of a mission page (section 10.1 of the M6a design).</summary>
/// <param name="Id">The id of the section, for the open state in the settings.</param>
/// <param name="Glyph">The glyph of the header.</param>
/// <param name="Notes">Lines under the fields, for example the special rules of a slot.</param>
/// <param name="Advanced">True for the closed Advanced section.</param>
internal sealed record PageSection(string Id, string Title, char Glyph, HelpText? Help, IReadOnlyList<PageField> Fields,
    IReadOnlyList<string> Notes, bool Advanced = false)
{
    /// <summary>The action group of the "+" in the header of an action section of the Trigger page, or null.</summary>
    public string? AddGroup { get; init; }

    /// <summary>The items of the menu of the section, for example "This ship only" of a ship build.</summary>
    public IReadOnlyList<PageAction> Actions { get; init; } = [];
}

/// <summary>
/// The model of a mission page of the Inspector (section 10.1 of the M6a design): the header, the read-only note, the
/// completeness report, the component sections and the INI block. The page draws from it. The model makes no ImGui
/// call, so the tests check it (review finding RV12). The queries of <see cref="MissionSummary"/> give its data.
/// </summary>
/// <param name="Title">The title of the header line, for example "M04: {title text}".</param>
/// <param name="Kind">The kind label of the header line, for example "Mission".</param>
/// <param name="Glyph">The glyph of the kind in the header line.</param>
/// <param name="Help">The text of the info icon of the header line.</param>
/// <param name="Completeness">The completeness report, or null for an item without parts.</param>
/// <param name="Ini">The section of the INI block, or null.</param>
internal sealed partial record MissionPageModel(string Title, string Kind, char Glyph, HelpText Help,
    CompletenessReport? Completeness, IReadOnlyList<PageSection> Sections, SectionRef? Ini)
{
    /// <summary>The read-only reason of a field without an edit binding, for example a list field of a prefab instance,
    /// and of each field of a page model without an edit session. The INI block of the page and the raw view take the
    /// edits of such a field (section 8.8 of the M6b design).</summary>
    public const string FieldNote = "Change this value in the INI block of the page.";

    /// <summary>The value of a field of a key that the section does not have.</summary>
    public const string NotSet = "Not set";

    /// <summary>The section of an id, or null.</summary>
    public PageSection? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    /// <summary>The lines under the header, before the completeness panel, for example "Part of".</summary>
    public IReadOnlyList<PageField> Lead { get; init; } = [];

    /// <summary>The chips of the header, for example "Start" and "Repeats" of a trigger.</summary>
    public IReadOnlyList<string> HeaderChips { get; init; } = [];

    /// <summary>The sections of the INI block when it shows more than one, for example all triggers of a prefab
    /// instance. Empty for the one section of <see cref="Ini"/>.</summary>
    public IReadOnlyList<SectionRef> IniSections { get; init; } = [];

    /// <summary>The title of the INI block: "INI", "INI (all triggers)" or "INI (all sections)".</summary>
    public string IniTitle { get; init; } = "INI";

    /// <summary>The id of a section that the page opens and scrolls to, for example the group of a selected line, or
    /// null.</summary>
    public string? FocusSection { get; init; }

    /// <summary>The trigger of a Trigger page: the button "Add action" and the "+" of the groups add to it.</summary>
    public MissionTrigger? PageTrigger { get; init; }

    /// <summary>Why the item of the page cannot change, or null. The page shows it under the header (section 8.1 of the
    /// M6b design).</summary>
    public string? ReadOnly { get; init; }

    /// <summary>The items of the page menu, for example "Duplicate" and "Delete" of a mission ship.</summary>
    public IReadOnlyList<PageAction> PageActions { get; init; } = [];

    /// <summary>The text of the prefab file of the Prefab page, read-only, with the marks of its problems, or null (UI
    /// rule 11).</summary>
    public PrefabFileView? PrefabFile { get; init; }

    /// <summary>Makes the batch of "Rename" of the page menu, or null for an item without a rename.</summary>
    public Func<string, MissionBatch?>? Rename { get; init; }

    /// <summary>The banner of a section whose base changed after the edit, or null (section 6.3 of the M6b
    /// design).</summary>
    public BaseChange? Base { get; init; }

    /// <summary>The Mission page (section 10.2 of the M6a design). The context gives the texts, the voices files, the
    /// names of the systems, the read-only reasons, the marks of the active mod and the ship build files.</summary>
    public static MissionPageModel Mission(PageContext context)
    {
        var (result, analysis, text, showBaseGame) = (context.Result, context.Analysis, context.Text, context.ShowBaseGame);
        var schema = MissionSchema.Retail;
        var index = analysis.Index;
        var slot = analysis.Script.Slot;
        var title = slot != null
            ? $"{slot.Slot}: {Text(text, slot.TitleId) ?? MissionFiles.Display(analysis.Script.Path)}"
            : MissionFiles.Display(analysis.Script.Path);
        var sections = new List<PageSection>();

        if (slot != null)
        {
            var columns = MissionsBrowserModel.Columns.ToDictionary(c => c.Name, c => c.Tooltip);
            sections.Add(new PageSection("slot", "Story slot", EditorIcons.Route, schema.Help["storySlot"],
            [
                new("Slot", slot.Slot, columns["Slot"]),
                new("Script", MissionFiles.Display(analysis.Script.Path), columns["Mission script"]),
                new("Start stage", MissionSummary.Stage(slot.StartStage), columns["Start stage"]),
                new("Accept stage", MissionSummary.Stage(slot.AcceptStage), columns["Accept stage"]),
                new("Next mission", MissionSummary.NextText(slot.Next),
                    $"{columns["Next mission"]} {MissionSummary.NextTooltip(slot.Next)}"),
                new("Title text", Text(text, slot.TitleId) is { } titleText ? $"{titleText} ({slot.TitleId})" : Number(slot.TitleId),
                    schema.Notes["titleText"]),
            ], slot.Notes.Select(n => schema.SlotNotes.GetValueOrDefault(n) ?? n).ToList()));
        }

        sections.Add(Overview(analysis, context.SystemName));
        sections.Add(Problems(analysis, analysis.Problems, showBaseGame));
        if (!context.HidesOpenLancer)
            sections.Add(OpenLancer(index));
        sections.Add(VoiceLines(index, context.CommFile));
        sections.Add(Texts(index, text));
        var header = analysis.Document.OfKind(MissionSectionKind.Mission).FirstOrDefault();
        var files = MissionSummary.Bundle(result, analysis).Select(b => new PageField(b.Name, b.Value, FileTooltip(b),
            b.ActionPhrase != null ? new BundleLink(analysis.Script.Path, b) : null)).ToList();
        if (header != null && context.Editable(header.Ref))
            files.Insert(0, ShipBuildFileField(context, header));
        sections.Add(new PageSection("files", "Files", EditorIcons.Folder, schema.Help["missionFiles"], files, []));
        var advanced = new List<PageField>();
        // The keys that the game does not use (decision D426). Their help text says where the titles come from.
        foreach (var key in schema.Section("Mission")?.Keys.Where(k => k.Unused) ?? [])
        {
            if (header?.Section.FirstOrDefault(e => MissionIndex.Is(e.Name, key.Key)) is { } line)
                advanced.Add(new PageField(key.Label, string.Join(", ", line.Select(MissionIndex.Text)), key.Help));
        }
        if (advanced.Count > 0)
            sections.Add(new PageSection("advanced", "Advanced", EditorIcons.Cog, null, advanced, [], Advanced: true));

        return new MissionPageModel(title, "Mission", EditorIcons.Route, schema.Help["mission"],
            MissionCompleteness.OfMission(analysis), sections, header?.Ref);
    }

    /// <summary>The note of the shared ship build file in the field "Ship build file".</summary>
    internal const string SharedShipFileNote = "Encounters and bases use the builds of this file too.";

    /// <summary>The note of a value of the field "Ship build file" that names no file.</summary>
    internal const string NoShipFileNote =
        "No file has this path. The editor makes the file when you add the first ship build of this mission.";

    /// <summary>The end of the tooltip of the field "Ship build file", and the note of its popup "New file...".</summary>
    internal const string KeepsBuildsNote =
        "A pick or a new file changes only this line. The builds of the old file do not move.";

    /// <summary>
    /// The field "Ship build file" of the Mission page (section 8.4 of the M6b design, #18): the <c>npc_ship_file</c>
    /// line of <c>[Mission]</c>. A new ship build of the script goes into this file, so a new build needs it. The
    /// dropdown lists the files that the line can name, and "New file..." makes an empty file in the folder of the
    /// script. The note under the field gives the problem of the checks at the line. Else it tells about a value without
    /// a file or about the shared file.
    /// </summary>
    private static PageField ShipBuildFileField(PageContext context, MissionSection header)
    {
        var script = context.Analysis.Script.Path;
        var key = MissionSchema.Retail.Section("Mission")?.Key("npc_ship_file");
        var row = header.Section.ToList().FindIndex(e => MissionIndex.Is(e.Name, "npc_ship_file") && e.Count > 0);
        var value = row >= 0 ? MissionIndex.Text(header.Section[row][0]) : "";
        var files = context.ShipBuildFiles();
        var current = files.FirstOrDefault(f => SameFile(f.Value, value));
        var problem = row < 0 ? null
            : BaseGameMessages.Shown(context.Analysis.Problems, context.ShowBaseGame)
                .FirstOrDefault(p => Equals(p.Target, header.Ref) && p.EntryIndex == row);
        const string label = "Ship build file";
        string PathOf(string name) => MissionEditor.NewShipFilePath(script, name);
        return new PageField(label, value.Length > 0 ? value : NotSet, $"{key?.Help} {KeepsBuildsNote}".TrimStart())
        {
            Changed = context.Changed(header, "npc_ship_file"),
            Edit = new FieldEdit(EditWidget.Words, $"Set {label}")
            {
                Value = current?.Value ?? value,
                Choices = files.Select(f => (f.Value, f.Value, (string?)null)).ToList(),
                ChoiceNotes = files.Where(f => f.Shared)
                    .ToDictionary(f => f.Value, _ => SharedShipFileNote, StringComparer.OrdinalIgnoreCase),
                Note = problem?.Message ?? (value.Length == 0 ? null
                    : current == null ? NoShipFileNote
                    : current.Shared ? SharedShipFileNote
                    : null),
                NoteSeverity = problem?.Severity,
                Make = picked => picked.Scalar is { Length: > 0 } path && !SameFile(path, value)
                    ? new MissionBatch($"Set {label}", [new SetItemKey(ItemOf(header), "npc_ship_file",
                        [LineSpec.Raw("npc_ship_file", path)])])
                    : null,
                NewFile = new NewFileItem("New ship build file",
                    "Make a new empty ship build file in the folder of the script, and name it in this line.",
                    KeepsBuildsNote, context.FreeShipFileName(script), PathOf,
                    name => context.NewShipFileRefusal(script, PathOf(name)),
                    name => new MissionBatch("New ship build file", [new NewShipBuildFile(new ScriptRef(script), PathOf(name))])),
            },
        };
    }

    /// <summary>True when two values of an <c>npc_ship_file</c> line name the same file below the data folder.</summary>
    private static bool SameFile(string a, string b) =>
        MissionFiles.RelativeOf(a) is { } left && MissionFiles.RelativeOf(b) is { } right && VfsPath.Comparer.Equals(left, right);

    /// <summary>The tooltip of a row of the Files section: what it holds, and what a click does.</summary>
    private static string FileTooltip(MissionBundleRow row) =>
        row.ActionPhrase is { } action ? $"{row.Tooltip}\nClick to {action}." : row.Tooltip;

    /// <summary>The page of another section of a mission, for example <c>[MetaBehavior]</c>. It has the header, the
    /// read-only note, the problems of its section and its INI block. Each other kind has its own page (see
    /// <see cref="For"/>).</summary>
    public static MissionPageModel Item(MissionAnalysis analysis, MissionRef item, bool showBaseGame)
    {
        var schema = MissionSchema.Retail;
        var section = item.Section is { } target ? analysis.Document.Find(target) : null;
        var (kind, glyph, help) = KindOf(item.Kind, item.Key, schema);
        var title = section?.Nickname ?? section?.Ref.Id.ToString() ?? item.Key ?? MissionFiles.Display(item.Script);
        if (item.Row >= 0 && section != null && item.Row < section.Section.Count)
            title = $"{title}: {section.Section[item.Row].Name}";
        var problems = analysis.Problems.Where(p => section != null && Equals(p.Target, section.Ref) &&
                                                    (item.Row < 0 || p.EntryIndex == item.Row || p.EntryIndex < 0)).ToList();
        var sections = new List<PageSection> { Problems(analysis, problems, showBaseGame) };
        return new MissionPageModel(title, kind, glyph, help, null, sections, section?.Ref);
    }

    /// <summary>The kind label, the glyph and the help text of an item kind (section 10.1 of the M6a design).</summary>
    /// <param name="key">The name of an item without a section, for example the id of a behavior prefab.</param>
    internal static (string Kind, char Glyph, HelpText Help) KindOf(MissionItemKind kind, string? key, MissionSchema schema) =>
        kind switch
        {
            MissionItemKind.Act when key == MissionActs.NotStartedKey => ("Frame", EditorIcons.EyeSlash, schema.Help["notStarted"]),
            MissionItemKind.Mission => ("Mission", EditorIcons.Route, schema.Help["mission"]),
            MissionItemKind.Trigger or MissionItemKind.Row => ("Trigger", EditorIcons.Bolt, schema.Help["trigger"]),
            MissionItemKind.Ship => ("Mission ship", EditorIcons.SpaceShuttle, schema.Help["missionShip"]),
            MissionItemKind.Formation => ("Formation", EditorIcons.Users, schema.Help["formation"]),
            MissionItemKind.Solar => ("Solar", EditorIcons.Satellite, schema.Help["solar"]),
            MissionItemKind.Loot => ("Loot", EditorIcons.Box, schema.Help["loot"]),
            MissionItemKind.OrderList => ("Order list", EditorIcons.List, schema.Help["orderList"]),
            MissionItemKind.Objective => ("Objective", EditorIcons.Bullseye, schema.Help["objective"]),
            MissionItemKind.Conversation => ("Conversation", EditorIcons.VolumeUp, schema.Help["conversation"]),
            MissionItemKind.Npc => ("NPC", EditorIcons.User, schema.Help["npc"]),
            MissionItemKind.Label => ("Label", EditorIcons.Tag, schema.Help["label"]),
            MissionItemKind.RandomEncounter => ("Random encounter", EditorIcons.Crosshairs, schema.Help["randomEncounter"]),
            MissionItemKind.Act => ("Act", EditorIcons.Film, schema.Help["act"]),
            MissionItemKind.PrefabInstance or MissionItemKind.PrefabKind => ("Behavior prefab", EditorIcons.Cubes,
                MissionPrefabs.Retail.Definitions.FirstOrDefault(d => key != null &&
                    key.Split('|')[0].Equals(d.Id, StringComparison.OrdinalIgnoreCase))?.Help ?? schema.Help["behaviorPrefab"]),
            _ => ("Mission section", EditorIcons.Th, schema.Help["otherSection"]),
        };

    private static PageSection Overview(MissionAnalysis analysis, Func<string, string?>? systemName)
    {
        var index = analysis.Index;
        var fields = new List<PageField>
        {
            new("Triggers", Number(index.Triggers.Count)),
            new("Mission ships", Number(index.Document.OfKind(MissionSectionKind.Ship).Count())),
            new("Objectives", Number(index.Document.OfKind(MissionSectionKind.Objective).Count())),
            new("Conversations", Number(index.Document.OfKind(MissionSectionKind.Conversation).Count())),
        };
        if (analysis.Prefabs is { } prefabs)
        {
            foreach (var group in prefabs.Instances.GroupBy(i => i.Definition).OrderBy(g => g.Key.Order))
                fields.Add(new PageField(group.Key.Name, Number(group.Count()), group.Key.Help.WhatItIs));
        }
        var systems = MissionSummary.Systems(index).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (systems.Count > 0)
        {
            fields.Add(new PageField("Systems", string.Join(", ", systems), MissionSchema.Retail.Notes["missionSystems"])
            {
                Chips = systems.Select(nickname => SystemChip(nickname, systemName)).ToList(),
            });
        }
        foreach (var start in index.StartTriggers)
            fields.Add(new PageField("Start trigger", start.Nickname, start.Condition?.Sentence(),
                new MissionRef(index.Document.Path, MissionItemKind.Trigger, start.Section.Ref)));
        return new PageSection("overview", "Overview", EditorIcons.Th, MissionSchema.Retail.Help["overview"], fields, []);
    }

    /// <summary>The chip of a system: its name, and a link that opens it in the Scene. A system that the game data does
    /// not have shows its nickname, without a link.</summary>
    private static PageChip SystemChip(string nickname, Func<string, string?>? systemName)
    {
        if (systemName?.Invoke(nickname) is not { } name)
            return new PageChip(nickname, $"{nickname}. The game data has no system with this name.", null);
        var text = name.Length > 0 ? name : nickname;
        return new PageChip(text, $"{text} ({nickname}). Click to open the system in the Scene.", new SystemLink(nickname));
    }

    /// <summary>The Problems section (section 10.2 of the M6a design): each problem that shows, with its place, its
    /// message and "Go to". A base-game problem shows muted with the lock glyph while the base-game messages show
    /// (decision D512).</summary>
    private static PageSection Problems(MissionAnalysis analysis, IReadOnlyList<Diagnostic> problems, bool showBaseGame) =>
        new("problems", "Problems", EditorIcons.ExclamationTriangle, MissionSchema.Retail.Help["problems"],
            BaseGameMessages.Shown(problems, showBaseGame).Select(p => new PageField(PlaceOf(analysis, p), p.Message,
                p.Origin?.Text, p.Target is { } t ? new LineLink(t, p.EntryIndex) : null, p.Origin?.IsBaseGame == true,
                p.Severity)
            {
                Actions = p.Subject is { } subject ? FixActions(analysis, subject) : [],
            }).ToList(),
            BaseGameMessages.HiddenLine(BaseGameMessages.Hidden(problems, showBaseGame)) is { } line ? [line] : []);

    /// <summary>The fixes of a problem or of a step by its subject, each as a <see cref="ApplyFix"/> batch (section 8.7
    /// of the M6b design). A fix is one command and one undo entry.</summary>
    internal static IReadOnlyList<PageAction> FixActions(MissionAnalysis analysis, string subject) =>
        MissionFixes.OfSubject(analysis, subject)
            .Select(f => new PageAction(f.Label, new MissionBatch($"Fix: {f.Label}",
                [new ApplyFix(new ScriptRef(analysis.Script.Path), subject, f.Id)]), Tooltip: MissionFixes.ButtonTip))
            .ToList();

    /// <summary>The place of a problem in plain words, for example "Trigger t_start, line 3", or "Mission" for a
    /// problem of the whole script.</summary>
    internal static string PlaceOf(MissionAnalysis analysis, Diagnostic problem)
    {
        if (problem.Target is not { } target || analysis.Document.Find(target) is not { } section)
            return "Mission";
        var kind = KindOf(MissionRef.KindOf(section.Kind), null, MissionSchema.Retail).Kind;
        var name = section.Nickname ?? section.Ref.Id.ToString();
        return problem.EntryIndex >= 0
            ? string.Create(CultureInfo.InvariantCulture, $"{kind} {name}, line {problem.EntryIndex + 1}")
            : $"{kind} {name}";
    }

    private static PageSection OpenLancer(MissionIndex index)
    {
        var report = MissionSummary.OpenLancer(index);
        return new PageSection("openlancer", "OpenLancer", EditorIcons.Rocket, MissionSchema.Retail.Help["openLancer"],
            report.Gaps.Select(g => new PageField(g.Name, g.Text)).ToList(),
            report.HasGaps ? [] : ["OpenLancer runs all lines of this mission."]);
    }

    private static PageSection VoiceLines(MissionIndex index, Func<string, string?> commFile)
    {
        var fields = MissionSummary.CommLines(index).Select(n => commFile(n) is { } file
            ? new PageField(n, file)
            : new PageField(n, "Missing", MissionSchema.Retail.Notes["missingCommLine"],
                Severity: DiagnosticSeverity.Error)).ToList();
        return new PageSection("voice", "Voice lines", EditorIcons.VolumeUp, MissionSchema.Retail.Help["voiceLines"],
            fields, fields.Count == 0 ? ["The script names no comm line."] : []);
    }

    /// <summary>The text ids of a script, grouped as titles, offers, objectives, fail texts and log lines.</summary>
    private static PageSection Texts(MissionIndex index, Func<int, string> text)
    {
        var fields = MissionSummary.TextIds(index).Select(t => new PageField(t.Group,
            int.TryParse(t.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && Text(text, n) is { } value
                ? $"{value} ({t.Id})"
                : t.Id)).ToList();
        return new PageSection("text", "Text", EditorIcons.Pen, MissionSchema.Retail.Help["missionText"], fields,
            fields.Count == 0 ? ["The script names no text id."] : []);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(Func<int, string> text, int id) => text(id) is { Length: > 0 } value ? value : null;
}
