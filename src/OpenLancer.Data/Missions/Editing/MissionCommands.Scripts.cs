using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>
/// Makes a new mission script: a whole file of the active mod under <c>DATA\MISSIONS</c> with one <c>[Mission]</c>
/// section (decision D680). With a ship build file, the <c>[Mission]</c> section names it, and a file that does not
/// exist becomes a new whole file of the active mod.
/// </summary>
/// <param name="Path">The install path of the new script.</param>
public sealed record CreateScript(string Path) : MissionCommand
{
    /// <summary>The install path of the ship build file, or null.</summary>
    public string? ShipBuildFile { get; init; }

    /// <summary>The sections of the script, or null for one <c>[Mission]</c> section. The inverse of a delete gives
    /// them.</summary>
    public IReadOnlyList<RawSection>? Sections { get; init; }

    /// <summary>The sections of a new ship build file, or null for none. The inverse of a delete gives them.</summary>
    public IReadOnlyList<RawSection>? ShipBuildSections { get; init; }

    protected override string Describe() => $"New script {Path}";

    internal override void Run(MissionDraft d)
    {
        var path = d.NewFilePath(Path);
        var shipPath = ShipBuildFile == null ? null : d.NewFilePath(ShipBuildFile, allowExisting: true);
        var script = d.NewFile(path);
        IReadOnlyList<RawSection> sections = Sections ??
        [
            new RawSection("Mission", shipPath == null
                ? []
                : [new RawEntry("npc_ship_file", [MissionFiles.Display(shipPath)])]),
        ];
        d.SetFile(script, sections.Select(s => MissionDraft.NewSection(d.TypeName(s.Type), d.Entries(script, null, s.Entries)))
            .ToList(), exists: true);
        string? createdShips = null;
        if (shipPath != null && d.Session.OpenDocument(shipPath) is not { State.Exists: true })
        {
            var ships = d.NewFile(shipPath);
            d.SetFile(ships, (ShipBuildSections ?? []).Select(s =>
                MissionDraft.NewSection(d.TypeName(s.Type), d.Entries(ships, null, s.Entries))).ToList(), exists: true);
            createdShips = shipPath;
        }
        var reference = new ScriptRef(path);
        d.Adds.Add(reference.ToString());
        if (createdShips != null)
            d.Adds.Add(new ScriptRef(createdShips).ToString());
        d.Select = reference.ToString();
        d.Inverse(new DeleteScript(reference));
    }
}

/// <summary>
/// Deletes a whole mission file of the active mod: a script, or the ship build file of a script. A file of the game
/// data or of a lower mod cannot go. A whole file of the active mod that takes the place of a file of the lower layer
/// cannot go either, because the game then uses that file again. The ship build file of a deleted script goes with it
/// when that file is a whole file of the active mod that no other script names.
/// </summary>
public sealed record DeleteScript(ScriptRef Script) : MissionCommand
{
    protected override string Describe() => $"Delete script {MissionFiles.Display(Script.Path)}";

    internal override void Run(MissionDraft d)
    {
        var document = d.Session.OpenDocument(Script.Path);
        if (document is not { State.Exists: true } || !document.HoldsScript && !d.Session.IsGuardedMissionFile(document.Path))
            throw d.Refuse(MissionErrors.ScriptNotFoundError(Script.Path));
        if (!document.IsWholeFile)
            throw d.Refuse(MissionErrors.NotWholeFileError(document.Path));
        if (d.Session.LowerLayerHas(document.Path))
            throw d.Refuse(MissionErrors.ReplacesLowerFileError(document.Path));
        var script = document.HoldsScript ? d.Script(document.Path) : d.File(document.Path);
        var sections = script.Working.Select(MissionDraft.RawSection).ToList();
        var shipPath = script.HoldsScript ? d.Session.ShipBuildFileOf(script.Path) : null;
        IReadOnlyList<RawSection>? shipSections = null;
        if (shipPath != null && d.Session.OpenDocument(shipPath) is { IsWholeFile: true, State.Exists: true } ships &&
            !d.OtherScriptNames(script.Path, shipPath) && !d.Session.LowerLayerHas(ships.Path))
        {
            var file = d.File(ships.Path);
            shipSections = file.Working.Select(MissionDraft.RawSection).ToList();
            d.SetFile(file, [], exists: false);
        }
        d.SetFile(script, [], exists: false);
        d.Deletes.Add(new ResultDelete(Script.ToString(), OpenLancer.Data.Editing.Scene.DeleteReason.Target, null));
        if (shipSections != null)
            d.Deletes.Add(new ResultDelete(new ScriptRef(shipPath!).ToString(), OpenLancer.Data.Editing.Scene.DeleteReason.Cascade,
                "npc_ship_file"));
        d.Inverse(new CreateScript(script.Path)
        {
            ShipBuildFile = shipSections != null ? shipPath : null,
            Sections = sections,
            ShipBuildSections = shipSections,
        });
    }
}

/// <summary>
/// Gives a text id of a line own text: the text goes into <c>strings.json</c> of the active mod, and the line gets its
/// <c>$str:</c> placeholder, by the key rules of MOD_FORMAT "New items, renames and text".
/// </summary>
/// <param name="Name">The name of the text id value in the schema.</param>
public sealed record SetLineText(RowRef Row, string Name, string Text) : MissionCommand
{
    protected override string Describe() => $"Set the text of {Name}";

    internal override void Run(MissionDraft d)
    {
        var (document, index, row) = d.Row(Row);
        d.Editable(document, index);
        var section = document.Working[index];
        var entry = section[row];
        var specs = d.SpecsOf(section.Name, entry);
        var spec = specs.FirstOrDefault(s => s.Name.Equals(Name, StringComparison.OrdinalIgnoreCase))
                   ?? throw d.Refuse(MissionErrors.NoSuchValueError(entry.Name, Name, specs.Select(s => s.Name)));
        if (spec.Kind != MissionValueKind.TextId)
            throw d.Refuse(MissionErrors.NotTextIdError(spec.Label));
        if (string.IsNullOrEmpty(Text))
            throw d.Refuse(MissionErrors.ValueKindError("", "a text", spec.Label, "some text"));
        var current = spec.Index < entry.Count ? MissionIndex.Text(entry[spec.Index]) : null;
        var key = current != null && StringPlaceholder.TryParse(current, out var placeholder) &&
                  placeholder.Kind == StringKind.String && !MissionDraft.OtherLineUses(document, index, row, current)
            ? placeholder.Key
            : d.NewTextKey(document, index, entry.Name);
        var oldText = d.Text.Get(StringKind.String, key);
        var inverse = new List<MissionCommand>();
        if (!string.Equals(oldText, Text, StringComparison.Ordinal))
        {
            d.Text = d.Text.With(StringKind.String, key, Text);
            inverse.Add(new SetText(key) { Text = oldText });
        }
        var texts = MissionValues.TextsOf(entry).ToList();
        while (texts.Count < spec.Index)
            texts.Add(d.Gap(specs, texts.Count));
        var value = MissionValues.TextPrefix + key;
        if (spec.Index == texts.Count)
            texts.Add(value);
        else
            texts[spec.Index] = value;
        var old = entry;
        var entries = section.ToList();
        entries[row] = d.NewEntry(document, index, entry.Name, texts, entries.Take(row));
        if (d.SetLines(document, index, entries, inverse: false))
        {
            var reference = MissionDraft.RowOf(document, index, row);
            d.Changes.Add(reference.Reference);
            inverse.Insert(0, new SetLine(reference, MissionDraft.RawLine(old)));
        }
        if (inverse.Count > 0)
            d.Inverse(inverse.Count == 1 ? inverse[0] : new CommandGroup(inverse));
    }
}
