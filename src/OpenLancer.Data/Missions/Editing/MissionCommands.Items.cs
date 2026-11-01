using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.IO;
using DeleteReason = OpenLancer.Data.Editing.Scene.DeleteReason;

namespace OpenLancer.Data.Missions.Editing;

/// <summary>The pilot and the voice of a new <c>[NPC]</c> of a mission ship (MSD section 3.6).</summary>
public sealed record PilotSpec
{
    public string? Faction { get; init; }
    public string? Voice { get; init; }
    public string? Head { get; init; }
    public string? Body { get; init; }
    public IReadOnlyList<string> Accessories { get; init; } = [];

    /// <summary>The text id of the name of the pilot: a number, or <c>$str:&lt;key&gt;</c>.</summary>
    public MissionValue? NameText { get; init; }
}

/// <summary>A new ship build, an <c>[NPCShipArch]</c> of the ship build file of the mission (MSD section 3.6).</summary>
public sealed record ShipBuildSpec(string Archetype, string Loadout)
{
    public string? Pilot { get; init; }
    public string? StateGraph { get; init; }
    public string? Level { get; init; }
    public IReadOnlyList<string> NpcClasses { get; init; } = [];
}

/// <summary>A place near an object: the <c>rel_pos</c> line of a ship or a formation.</summary>
public sealed record NearSpec(string Anchor)
{
    /// <summary>The heading in degrees.</summary>
    public double Heading { get; init; }

    /// <summary>The range in meters, or null.</summary>
    public double? Range { get; init; }
}

/// <summary>Where a change of a loadout of a mission ship goes (decision D670).</summary>
public enum LoadoutScope
{
    /// <summary>The ship build, for each ship that uses it.</summary>
    Shared,

    /// <summary>A copy of the ship build that only this ship uses.</summary>
    ThisShip,
}

/// <summary>
/// Adds a mission ship as one undo entry (section 4.9 of the M6b design): an <c>[MsnShip]</c> at the end of the script,
/// a new <c>[NPC]</c> in the script, and a new <c>[NPCShipArch]</c> in the ship build file of the mission. An existing
/// <c>[NPC]</c> or ship build can take the place of a new one.
/// </summary>
public sealed record AddMissionShip(ScriptRef Script) : MissionCommand
{
    public string? Nickname { get; init; }

    /// <summary>The nickname of an existing <c>[NPC]</c> of the script, or null for a new one.</summary>
    public string? Npc { get; init; }

    /// <summary>The pilot and the voice of a new <c>[NPC]</c>.</summary>
    public PilotSpec? Pilot { get; init; }

    /// <summary>The nickname of an existing ship build, or null for a new one.</summary>
    public string? Build { get; init; }

    /// <summary>A new ship build.</summary>
    public ShipBuildSpec? NewBuild { get; init; }

    /// <summary>The start position: three numbers.</summary>
    public MissionValue? Position { get; init; }

    /// <summary>The rotation: four numbers.</summary>
    public MissionValue? Rotation { get; init; }

    /// <summary>A place near an object, in place of a position.</summary>
    public NearSpec? Near { get; init; }

    public string? System { get; init; }

    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>The order list that the ship starts with, or null.</summary>
    public string? Orders { get; init; }

    protected override string Describe() => $"Add mission ship {Nickname}".TrimEnd();

    internal override void Run(MissionDraft d)
    {
        var script = d.Script(Script);
        var stem = global::System.IO.Path.GetFileNameWithoutExtension(script.Path).ToLowerInvariant();
        var created = new List<(EditDocument Document, string Type, string Name)>();
        IEnumerable<MissionCommand> deleteFile = [];
        // An existing [NPC] names its build, so only a new [NPC] needs a build.
        var buildName = Build?.Trim();
        if (buildName == null && Npc == null)
        {
            var spec = NewBuild ?? throw d.Refuse(MissionErrors.ValueMissingError("Ship build") with
            {
                Message = "A mission ship needs a ship build. Give an existing ship build or a new one.",
            });
            var file = d.ShipBuildFile(script, create: true) ?? throw d.Refuse(MissionErrors.NoShipBuildFileError());
            buildName = d.NewName(file, "NPCShipArch", null, stem);
            var lines = new List<LineSpec>
            {
                LineSpec.Of("nickname", ("nickname", buildName)),
                LineSpec.Of("ship_archetype", ("ship_archetype", spec.Archetype)),
                LineSpec.Of("loadout", ("loadout", spec.Loadout)),
            };
            if (spec.Level != null)
                lines.Add(LineSpec.Of("level", ("level", spec.Level)));
            if (spec.Pilot != null)
                lines.Add(LineSpec.Of("pilot", ("pilot", spec.Pilot)));
            if (spec.StateGraph != null)
                lines.Add(LineSpec.Of("state_graph", ("state_graph", spec.StateGraph)));
            if (spec.NpcClasses.Count > 0)
                lines.Add(LineSpec.Of("npc_class", ("npc_class", MissionValue.List(spec.NpcClasses.Select(MissionValue.Of)))));
            d.AddSectionAt(file, "NPCShipArch", lines);
            created.Add((file, "NPCShipArch", buildName));
            deleteFile = d.DeleteIfCreated(file).ToList();
        }
        var npcName = Npc?.Trim();
        if (npcName == null)
        {
            var pilot = Pilot ?? new PilotSpec();
            npcName = d.NewName(script, "NPC", null);
            var lines = new List<LineSpec> { LineSpec.Of("nickname", ("nickname", npcName)) };
            if (pilot.Head != null && pilot.Body != null)
            {
                var costume = new List<(string, MissionValue)> { ("head", pilot.Head), ("body", pilot.Body) };
                if (pilot.Accessories.Count > 0)
                    costume.Add(("accessories", MissionValue.List(pilot.Accessories.Select(MissionValue.Of))));
                lines.Add(LineSpec.Of("space_costume", [.. costume]));
            }
            if (pilot.Voice != null)
                lines.Add(LineSpec.Of("voice", ("voice", pilot.Voice)));
            lines.Add(LineSpec.Of("npc_ship_arch", ("npc_ship_arch", buildName!)));
            if (pilot.Faction != null)
                lines.Add(LineSpec.Of("affiliation", ("affiliation", pilot.Faction)));
            if (pilot.NameText is { } text)
                lines.Add(LineSpec.Of("individual_name", ("individual_name", text)));
            d.AddSectionAt(script, "NPC", lines);
            created.Add((script, "NPC", npcName));
        }
        var shipName = d.NewName(script, "MsnShip", Nickname);
        var ship = new List<LineSpec> { LineSpec.Of("nickname", ("nickname", shipName)) };
        if (System != null)
            ship.Add(LineSpec.Of("system", ("system", System)));
        ship.Add(LineSpec.Of("npc", ("npc", npcName)));
        ship.AddRange(Labels.Select(l => LineSpec.Of("label", ("label", l))));
        if (Near is { } near)
        {
            var rel = new List<(string, MissionValue)> { ("heading", near.Heading), ("anchor", near.Anchor) };
            if (near.Range is { } range)
                rel.Add(("range", range));
            ship.Add(LineSpec.Of("rel_pos", [.. rel]));
        }
        else if (Position is { } position)
            ship.Add(LineSpec.Of("position", ("position", position)));
        if (Rotation is { } rotation)
            ship.Add(LineSpec.Of("orientation", ("orientation", rotation)));
        if (Orders != null)
            ship.Add(LineSpec.Of("init_objectives", ("init_objectives", Orders)));
        d.AddSectionAt(script, "MsnShip", ship);
        created.Add((script, "MsnShip", shipName));
        d.Select = d.RefOf(script, script.Working.Count - 1);
        // The inverse removes the new parts only, the ship first. Then it deletes a ship build file that the command
        // made.
        var inverse = created.AsEnumerable().Reverse()
            .Select(c => (MissionCommand)new RemoveSection(ItemRef.Of(c.Document.Path, c.Type, c.Name))).Concat(deleteFile)
            .ToList();
        d.Inverse(new CommandGroup(inverse));
    }
}

/// <summary>
/// Removes a mission ship (decision D669): its <c>[MsnShip]</c>, its <c>[NPC]</c> when no other ship or formation names
/// it, and its ship build when no other <c>[NPC]</c> names it and the build is in the ship build file of this mission.
/// Lines that name the ship stay "Missing".
/// </summary>
public sealed record RemoveMissionShip(ItemRef Ship) : MissionCommand
{
    protected override string Describe() => $"Delete mission ship {Ship.Section.Key}";

    internal override void Run(MissionDraft d)
    {
        var (script, index) = d.Item(Ship);
        var ship = script.Working[index];
        if (!MissionIndex.Is(ship.Name, "MsnShip"))
            throw d.Refuse(MissionErrors.NotAnItemError(Ship.ToString(), "a mission ship"));
        // A delete of a member of a linked prefab instance is a removed-member override (decision D676 c).
        d.Editable(script, index, members: true);
        var inverses = new List<MissionCommand>();
        var npcName = ItemFirst(ship, "npc");
        d.RemoveSection(script, index, ReferencesMode.Keep, DeleteReason.Target, null);
        inverses.AddRange(d.PopInverse());
        if (npcName != null && MissionDraft.IndexOfKey(script, "NPC", npcName) is >= 0 and var npcIndex &&
            !script.Working.Any(s => (MissionIndex.Is(s.Name, "MsnShip") || MissionIndex.Is(s.Name, "MsnFormation")) &&
                                      MissionIndex.Is(ItemFirst(s, "npc"), npcName)))
        {
            d.Editable(script, npcIndex);
            var buildName = ItemFirst(script.Working[npcIndex], "npc_ship_arch");
            d.RemoveSection(script, npcIndex, ReferencesMode.Keep, DeleteReason.Cascade, $"npc = {npcName}");
            inverses.AddRange(d.PopInverse());
            if (buildName != null && !script.Working.Any(s => MissionIndex.Is(s.Name, "NPC") &&
                                                              MissionIndex.Is(ItemFirst(s, "npc_ship_arch"), buildName)) &&
                d.ShipBuildFile(script, create: false) is { } file && MissionDraft.IndexOfKey(file, "NPCShipArch", buildName) is >= 0 and var buildIndex)
            {
                d.Editable(file, buildIndex);
                d.RemoveSection(file, buildIndex, ReferencesMode.Keep, DeleteReason.Cascade, $"npc_ship_arch = {buildName}");
                inverses.AddRange(d.PopInverse());
            }
        }
        inverses.Reverse();
        d.Inverse(inverses.Count == 1 ? inverses[0] : new CommandGroup(inverses));
    }

    internal static string? ItemFirst(Section section, string key) => ItemIndex.First(section, key)?.Trim();
}

/// <summary>Places a mission ship: its <c>position</c>, <c>orientation</c> and <c>system</c> lines, or its
/// <c>rel_pos</c> line near an object.</summary>
public sealed record PlaceMissionShip(ItemRef Ship) : MissionCommand
{
    public MissionValue? Position { get; init; }
    public MissionValue? Rotation { get; init; }
    public string? System { get; init; }
    public NearSpec? Near { get; init; }

    protected override string Describe() => $"Place {Ship.Section.Key}";

    internal override void Run(MissionDraft d)
    {
        var (script, index) = d.Item(Ship);
        var type = script.Working[index].Name;
        if (!MissionIndex.Is(type, "MsnShip") && !MissionIndex.Is(type, "MsnFormation"))
            throw d.Refuse(MissionErrors.NotAnItemError(Ship.ToString(), "a mission ship or a formation"));
        d.Editable(script, index);
        var entries = script.Working[index].ToList();
        if (Near is { } near)
        {
            var values = new List<(string, MissionValue)> { ("heading", near.Heading), ("anchor", near.Anchor) };
            if (near.Range is { } range)
                values.Add(("range", range));
            entries = d.WithKey(script, index, entries, "rel_pos", [LineSpec.Of("rel_pos", [.. values])]);
            entries = d.WithKey(script, index, entries, "position", []);
        }
        else if (Position is { } position)
        {
            entries = d.WithKey(script, index, entries, "position", [LineSpec.Of("position", ("position", position))]);
            entries = d.WithKey(script, index, entries, "rel_pos", []);
        }
        if (Rotation is { } rotation)
            entries = d.WithKey(script, index, entries, "orientation", [LineSpec.Of("orientation", ("orientation", rotation))]);
        if (System != null)
            entries = d.WithKey(script, index, entries, "system", [LineSpec.Of("system", ("system", System))]);
        d.SetLines(script, index, entries);
    }
}

/// <summary>
/// Sets the loadout of a mission ship (decision D670). Shared: the <c>loadout</c> line of the ship build, for each ship
/// that uses the build. This ship: a copy of the build with a made name in the ship build file of the mission, and the
/// ship names the copy. The <c>[NPC]</c> gets a copy too when other ships use it.
/// </summary>
public sealed record SetShipLoadout(ItemRef Ship, string Loadout, LoadoutScope Scope) : MissionCommand
{
    protected override string Describe() => $"Set the loadout of {Ship.Section.Key}";

    internal override void Run(MissionDraft d)
    {
        var (script, index) = d.Item(Ship);
        var ship = script.Working[index];
        if (!MissionIndex.Is(ship.Name, "MsnShip"))
            throw d.Refuse(MissionErrors.NotAnItemError(Ship.ToString(), "a mission ship"));
        var npcName = RemoveMissionShip.ItemFirst(ship, "npc") ??
                      throw d.Refuse(MissionErrors.ItemNotFoundError(script.Path, "NPC", "(none)"));
        var npcIndex = MissionDraft.IndexOfKey(script, "NPC", npcName);
        if (npcIndex < 0)
            throw d.Refuse(MissionErrors.ItemNotFoundError(script.Path, "NPC", npcName));
        var buildName = RemoveMissionShip.ItemFirst(script.Working[npcIndex], "npc_ship_arch") ??
                        throw d.Refuse(MissionErrors.ItemNotFoundError(script.Path, "ship build", "(none)"));
        var (buildFile, buildIndex, _) = d.FindBuild(script, buildName);
        var loadout = d.Build("NPCShipArch", LinePlace.Section, LineSpec.Of("loadout", ("loadout", Loadout)));
        if (Scope == LoadoutScope.Shared)
        {
            // A build of a shared file needs a confirmation (SharedBuildChange).
            (buildFile, buildIndex) = d.Item(MissionDraft.ItemOf(buildFile, buildIndex), lines: true);
            d.Editable(buildFile, buildIndex);
            var entries = d.WithKey(buildFile, buildIndex, buildFile.Working[buildIndex].ToList(), "loadout",
                [LineSpec.Raw(loadout.Key, [.. loadout.Texts])]);
            d.SetLines(buildFile, buildIndex, entries);
            return;
        }
        var inverses = new List<MissionCommand>();
        var file = d.ShipBuildFile(script, create: true) ?? throw d.Refuse(MissionErrors.NoShipBuildFileError());
        var stem = global::System.IO.Path.GetFileNameWithoutExtension(script.Path).ToLowerInvariant();
        var copyName = d.NewName(file, "NPCShipArch", null, stem);
        var copy = buildFile.Working[buildIndex].ToList();
        copy = d.WithKey(buildFile, buildIndex, copy, "nickname", [LineSpec.Raw("nickname", copyName)], key: true);
        copy = d.WithKey(buildFile, buildIndex, copy, "loadout", [LineSpec.Raw(loadout.Key, [.. loadout.Texts])]);
        d.Insert(file, file.Working.Count, MissionDraft.NewSection(buildFile.Working[buildIndex].Name, copy));
        d.Adds.Add(d.RefOf(file, file.Working.Count - 1));
        // The inverse removes the copy, then deletes a ship build file that the command made.
        inverses.AddRange(d.DeleteIfCreated(file));
        inverses.Add(new RemoveSection(ItemRef.Of(file.Path, "NPCShipArch", copyName)));
        var users = script.Working.Count(s => (MissionIndex.Is(s.Name, "MsnShip") || MissionIndex.Is(s.Name, "MsnFormation")) &&
                                              MissionIndex.Is(RemoveMissionShip.ItemFirst(s, "npc"), npcName));
        if (users > 1)
        {
            var npcCopyName = d.NewName(script, "NPC", null);
            var npc = script.Working[npcIndex].ToList();
            npc = d.WithKey(script, npcIndex, npc, "nickname", [LineSpec.Raw("nickname", npcCopyName)], key: true);
            npc = d.WithKey(script, npcIndex, npc, "npc_ship_arch", [LineSpec.Raw("npc_ship_arch", copyName)]);
            d.Insert(script, script.Working.Count, MissionDraft.NewSection(script.Working[npcIndex].Name, npc));
            d.Adds.Add(d.RefOf(script, script.Working.Count - 1));
            inverses.Add(new RemoveSection(ItemRef.Of(script.Path, "NPC", npcCopyName)));
            var (shipDoc, shipIndex) = d.Item(Ship);
            d.Editable(shipDoc, shipIndex);
            var shipLines = d.WithKey(shipDoc, shipIndex, shipDoc.Working[shipIndex].ToList(), "npc",
                [LineSpec.Raw("npc", npcCopyName)]);
            d.SetLines(shipDoc, shipIndex, shipLines);
            inverses.AddRange(d.PopInverse());
        }
        else
        {
            d.Editable(script, npcIndex);
            var npc = d.WithKey(script, npcIndex, script.Working[npcIndex].ToList(), "npc_ship_arch",
                [LineSpec.Raw("npc_ship_arch", copyName)]);
            d.SetLines(script, npcIndex, npc);
            inverses.AddRange(d.PopInverse());
        }
        inverses.Reverse();
        d.Inverse(new CommandGroup(inverses));
    }
}

/// <summary>Adds an item to a mission file: a formation, a solar, a loot, an order list, an objective, a conversation,
/// an NPC, a random encounter or a ship build. A ship build goes into the ship build file.</summary>
/// <param name="ItemKind">The section type, for example <c>MsnFormation</c>, or a kind word such as <c>formation</c>,
/// <c>order_list</c> or <c>ship_build</c>.</param>
public sealed record AddItem(ScriptRef Script, string ItemKind) : MissionCommand
{
    public string? Nickname { get; init; }
    public IReadOnlyList<LineSpec> Lines { get; init; } = [];

    protected override string Describe() => $"Add {ItemKind} {Nickname}".TrimEnd();

    internal override void Run(MissionDraft d)
    {
        var type = MissionDraft.TypeOfKind(ItemKind) ?? throw d.Refuse(MissionErrors.NotAKeyError(ItemKind, "mission file") with
        {
            Message = $"'{ItemKind}' is not a kind of mission item. Use formation, solar, loot, order_list, objective, " +
                      "conversation, npc, random_encounter or ship_build.",
        });
        var script = d.Script(Script);
        var file = MissionIndex.Is(type, "NPCShipArch")
            ? d.ShipBuildFile(script, create: true) ?? throw d.Refuse(MissionErrors.NoShipBuildFileError())
            : script;
        var stem = global::System.IO.Path.GetFileNameWithoutExtension(script.Path).ToLowerInvariant();
        var name = d.NewName(file, type, Nickname, stem);
        var lines = new List<LineSpec> { LineSpec.Raw("nickname", name) };
        lines.AddRange(Lines.Where(l => !l.Key.Equals("nickname", StringComparison.OrdinalIgnoreCase)));
        d.AddSectionAt(file, type, lines);
        var item = ItemRef.Of(file.Path, type, name);
        d.Select = d.RefOf(file, file.Working.Count - 1);
        // The inverse also deletes a ship build file that the command made.
        var inverse = d.DeleteIfCreated(file).Prepend(new RemoveItem(item)).ToList();
        d.Inverse(inverse.Count == 1 ? inverse[0] : new CommandGroup(inverse));
    }
}

/// <summary>Removes an item of a mission file. Lines that name it stay "Missing". An <c>[NPC]</c> that a ship names,
/// and a ship build that an <c>[NPC]</c> names, refuse the delete (RequireComponent).</summary>
public sealed record RemoveItem(ItemRef Item) : MissionCommand
{
    protected override string Describe() => $"Delete {Item.Section.Key ?? Item.Section.Type}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Item);
        d.Editable(document, index, members: true);
        var section = document.Working[index];
        var name = SectionIdentity.KeyOf(section);
        if (name != null && MissionIndex.Is(section.Name, "NPC"))
        {
            var users = document.Working.Count(s => (MissionIndex.Is(s.Name, "MsnShip") || MissionIndex.Is(s.Name, "MsnFormation")) &&
                                                     MissionIndex.Is(RemoveMissionShip.ItemFirst(s, "npc"), name));
            if (users > 0)
                throw d.Refuse(MissionErrors.RequiredByError("NPC", name, users, users == 1 ? "ship" : "ships"));
        }
        if (name != null && MissionIndex.Is(section.Name, "NPCShipArch"))
        {
            var users = d.NpcsNaming(name);
            if (users > 0)
                throw d.Refuse(MissionErrors.RequiredByError("ship build", name, users, users == 1 ? "NPC" : "NPCs"));
        }
        d.RemoveSection(document, index, ReferencesMode.Keep, DeleteReason.Target, null);
    }
}

/// <summary>Copies an item with a new name to the end of its file. A copy of a mission ship names the same
/// <c>[NPC]</c>. Lines that name the original do not change.</summary>
public sealed record DuplicateItem(ItemRef Item) : MissionCommand
{
    public string? NewName { get; init; }

    protected override string Describe() => $"Duplicate {Item.Section.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Item);
        // A copy of a member of a linked prefab instance refuses: duplicate the instance (section 4.9 of the M6b design).
        d.RefuseMember(document, index);
        var section = document.Working[index];
        var keyEntry = d.KeyEntryOf(section.Name) ??
                       throw d.Refuse(MissionErrors.NotAnItemError(Item.ToString(), "an item with a nickname"));
        var name = d.NewName(document, section.Name, NewName);
        var entries = d.WithKey(document, index, section.ToList(), keyEntry, [LineSpec.Raw(keyEntry, name)], key: true);
        d.Insert(document, document.Working.Count, MissionDraft.NewSection(section.Name, entries));
        var copy = ItemRef.Of(document.Path, section.Name, name);
        var reference = d.RefOf(document, document.Working.Count - 1);
        d.Adds.Add(reference);
        d.Select = reference;
        d.Inverse(new RemoveItem(copy));
    }
}

/// <summary>
/// Gives an item a new name (section 4.10 of the M6b design): the key line, and with <see cref="ChangeReferences"/>, true
/// by default, each line that names it by the reference kinds of the schema. A name of a script changes only in its script. A ship
/// build changes in each script whose <c>[NPC]</c> names it. A ship build of a shared file refuses
/// (<c>SharedBuildRename</c>).
/// </summary>
public sealed record RenameItem(ItemRef Item, string NewName) : MissionCommand
{
    /// <summary>True changes each line that names the item. It is true by default. False keeps the lines, and they show
    /// "Missing".</summary>
    [System.ComponentModel.DefaultValue(true)]
    public bool ChangeReferences { get; init; } = true;

    protected override string Describe() => $"Rename {Item.Section.Key} to {NewName}";

    internal override void Run(MissionDraft d)
    {
        // Random encounters can use a build of a shared file, so a mission cannot rename it (review finding RB2).
        if (MissionIndex.Is(Item.Section.Type, "NPCShipArch") && d.IsSharedBuildFile(Item.Script))
            throw d.Refuse(MissionErrors.SharedBuildRenameError(Item.Section.Key ?? Item.Section.ToString()));
        var (document, index) = d.Item(Item);
        d.Editable(document, index);
        var section = document.Working[index];
        if (MissionIndex.Is(section.Name, "Trigger"))
        {
            d.RunAsThis(new RenameTrigger(new TriggerRef(document.Path, TriggerRules.Nickname(section)!), NewName)
            {
                ChangeReferences = ChangeReferences,
            });
            return;
        }
        var keyEntry = d.KeyEntryOf(section.Name) ??
                       throw d.Refuse(MissionErrors.NotAnItemError(Item.ToString(), "an item with a nickname"));
        var old = SectionIdentity.KeyOf(section) ??
                  throw d.Refuse(MissionErrors.NotAnItemError(Item.ToString(), "an item with one nickname"));
        var name = NewName.Trim();
        if (string.Equals(old, name, StringComparison.Ordinal))
            return;
        var isBuild = MissionIndex.Is(section.Name, "NPCShipArch");
        var baseName = document.BaseSection(document.IdentityAt(index)) is { } b ? SectionIdentity.KeyOf(b) : null;
        d.CheckRename(document, index, section.Name, name, skipForm: !ChangeReferences ||
                                                                    string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase));
        var before = d.RefOf(document, index);
        var inverse = new List<MissionCommand>();
        var kinds = MissionLinks.KindsOf(section.Name);
        var files = isBuild ? d.ScriptsNamingBuild(old) : [document];
        foreach (var file in files)
        {
            var lines = MissionLinks.Naming(d.Schema, file.Working, kinds, old)
                .Where(l => !(ReferenceEquals(file, document) && l.Section == index)).ToList();
            if (!isBuild && kinds.Contains(MissionValueKind.Object))
                lines = lines.Where(l => MissionDraft.ObjectNamesItem(file, l, section.Name)).ToList();
            if (ChangeReferences)
            {
                foreach (var group in lines.GroupBy(l => l.Section))
                {
                    if (file.ReadOnlyReasonAt(group.Key) != null)
                        continue;
                    d.ChangeValues(file, group.Key, group.Select(l => (l.Row, l.Value)).ToList(), name, inverse);
                }
                // The values of the linked prefab instances follow, so their members show no hand edit (rule P01).
                if (file.HoldsScript)
                    d.RenameInRecords(file, kinds, old, name);
            }
            else
                d.MarkMissing(file, lines.Select(l => (l.Section, l.Row)).Distinct().ToList(), old,
                    "The game ignores this line, because no item has this name now.");
        }
        var entries = d.WithKey(document, index, section.ToList(), keyEntry, [LineSpec.Raw(keyEntry, name)], key: true);
        d.SetLines(document, index, entries, inverse: false);
        var renamed = MissionDraft.ItemOf(document, index);
        d.Renames.Add(new ResultRename(before, d.RefOf(document, index)));
        d.GoneNames.Add(old);
        d.Select = d.RefOf(document, index);
        // The inverse gives the lines that the rename changed their old name first, then the item its old name. So the
        // rename back finds no line that names the old name, and reports no false "Missing" line.
        inverse.Reverse();
        inverse.Add(new RenameItem(renamed, old) { ChangeReferences = false });
        d.Inverse(inverse.Count == 1 ? inverse[0] : new CommandGroup(inverse));
    }
}

/// <summary>Sets all lines of one key of an item, in their place (section 4.9 of the M6b design). A ship key of a ship
/// or a formation goes before the first <c>formation</c> or <c>ship</c> line (rule M09).</summary>
public sealed record SetItemKey(ItemRef Item, string Key, IReadOnlyList<LineSpec> Lines) : MissionCommand
{
    protected override string Describe() => $"Set {Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Item, lines: true);
        d.Editable(document, index);
        var section = document.Working[index];
        var (key, _) = d.KeyOf(section.Name, Key);
        if (d.IsKeyEntry(section, key))
            throw d.Refuse(MissionErrors.KeyLineError(key));
        var entries = d.WithKey(document, index, section.ToList(), key,
            Lines.Select(l => l with { Key = key }).ToList());
        d.SetLines(document, index, entries);
    }
}

/// <summary>Sets the <c>ship</c> lines of a formation in order (section 4.9 of the M6b design). A ship section that comes
/// later in the file moves before the formation (rule M08).</summary>
public sealed record SetFormationMembers(ItemRef Formation, IReadOnlyList<string> Ships) : MissionCommand
{
    protected override string Describe() => $"Set the members of {Formation.Section.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Formation);
        if (!MissionIndex.Is(document.Working[index].Name, "MsnFormation"))
            throw d.Refuse(MissionErrors.NotAnItemError(Formation.ToString(), "a formation"));
        d.Editable(document, index);
        var inverses = new List<MissionCommand>();
        // A later ship moves before the formation, so the formation takes it (MSD section 2.6 item 2).
        foreach (var ship in Ships.Select(s => s.Trim()))
        {
            var (_, at) = d.Item(Formation);
            var shipIndex = MissionDraft.IndexOfKey(document, "MsnShip", ship);
            if (shipIndex > at)
                inverses.AddRange(d.RunInner(new MoveSection(ItemRef.Of(document.Path, "MsnShip", ship))
                {
                    Before = MissionDraft.ItemOf(document, at),
                }));
        }
        var (_, formation) = d.Item(Formation);
        var entries = d.WithKey(document, formation, document.Working[formation].ToList(), "ship",
            Ships.Select(s => LineSpec.Of("ship", ("ship", s.Trim()))).ToList());
        d.SetLines(document, formation, entries);
        inverses.AddRange(d.PopInverse());
        inverses.Reverse();
        d.Inverse(inverses.Count == 1 ? inverses[0] : new CommandGroup(inverses));
    }
}

/// <summary>Adds a <c>label</c> line to a ship, a formation, a solar or a loot, before the first <c>formation</c> or
/// <c>ship</c> line (rule M09).</summary>
public sealed record AddLabel(ItemRef Item, string Label) : MissionCommand
{
    protected override string Describe() => $"Add label {Label}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Item);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        var labels = entries.Where(e => MissionIndex.Is(e.Name, "label"))
            .Select(e => LineSpec.Raw("label", [.. MissionValues.TextsOf(e)])).ToList();
        if (labels.Any(l => l.Texts!.Count > 0 && MissionIndex.Is(l.Texts[0], Label.Trim())))
            return;
        labels.Add(LineSpec.Of("label", ("label", Label.Trim())));
        d.SetLines(document, index, d.WithKey(document, index, entries, "label", labels));
    }
}

/// <summary>Removes each <c>label</c> line with a label from an item.</summary>
public sealed record RemoveLabel(ItemRef Item, string Label) : MissionCommand
{
    protected override string Describe() => $"Remove label {Label}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Item);
        d.Editable(document, index);
        var entries = document.Working[index].ToList();
        var kept = entries.Where(e => !(MissionIndex.Is(e.Name, "label") && e.Count > 0 &&
                                        MissionIndex.Is(MissionIndex.Text(e[0]), Label.Trim()))).ToList();
        d.SetLines(document, index, kept);
    }
}

/// <summary>Moves a ship or a formation section before another section of its file, or to the end. The place of
/// another section type has no effect in the game.</summary>
public sealed record MoveSection(ItemRef Section) : MissionCommand
{
    /// <summary>The section that the section goes before, or null for the end of the file.</summary>
    public ItemRef? Before { get; init; }

    protected override string Describe() => $"Move {Section.Section.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(Section);
        var type = document.Working[index].Name;
        if (!MissionIndex.Is(type, "MsnShip") && !MissionIndex.Is(type, "MsnFormation"))
            throw d.Refuse(MissionErrors.NotMovableError(type));
        d.Editable(document, index);
        var target = Before == null ? document.Working.Count : d.Item(Before).Index;
        if (target == index || target == index + 1)
            return;
        var next = index + 1 < document.Working.Count ? MissionDraft.ItemOf(document, index + 1) : null;
        var section = d.Remove(document, index);
        var at = target > index ? target - 1 : target;
        d.Insert(document, at, section);
        var moved = MissionDraft.ItemOf(document, at);
        d.Changes.Add(d.RefOf(document, at));
        d.Inverse(new MoveSection(moved) { Before = next });
    }
}

/// <summary>Adds an order line of one of the 23 directives to an order list, before a row or at the end.</summary>
public sealed record AddOrder(ItemRef OrderList, LineSpec Line) : MissionCommand
{
    public int? Before { get; init; }

    protected override string Describe() => $"Add order {Line.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(OrderList);
        if (!MissionIndex.Is(document.Working[index].Name, "ObjList"))
            throw d.Refuse(MissionErrors.NotAnItemError(OrderList.ToString(), "an order list"));
        d.Editable(document, index);
        var line = d.Build("ObjList", LinePlace.Order, Line);
        var entries = document.Working[index].ToList();
        var at = d.Place(document, index, Before, entries.Count);
        if (at <= TriggerRules.NicknameLine(entries))
            throw d.Refuse(MissionErrors.BadPlaceError() with
            {
                Message = "An order cannot go before the nickname line. Pick a place after the nickname line.",
            });
        entries.Insert(at, d.NewEntry(document, index, line.Key, line.Texts, entries.Take(at)));
        d.SetLines(document, index, entries, inverse: false);
        var row = MissionDraft.RowOf(document, index, at);
        d.Adds.Add(row.Reference);
        d.Inverse(new RemoveLine(row));
    }
}

/// <summary>Adds a waypoint to an order list: a <c>GotoVec</c> line with the point. The other values take the defaults
/// of the schema.</summary>
public sealed record AddWaypoint(ItemRef OrderList, MissionValue Position) : MissionCommand
{
    public int? Before { get; init; }

    protected override string Describe() => "Add waypoint";

    internal override void Run(MissionDraft d) =>
        d.RunAsThis(new AddOrder(OrderList, LineSpec.Of("GotoVec", ("point", Position))) { Before = Before });
}

/// <summary>Sets all order lines of an order list in order. The other lines keep their places. The list widget of the
/// order list page uses it.</summary>
public sealed record SetOrders(ItemRef OrderList, IReadOnlyList<LineSpec> Lines) : MissionCommand
{
    protected override string Describe() => $"Set the orders of {OrderList.Section.Key}";

    internal override void Run(MissionDraft d)
    {
        var (document, index) = d.Item(OrderList);
        if (!MissionIndex.Is(document.Working[index].Name, "ObjList"))
            throw d.Refuse(MissionErrors.NotAnItemError(OrderList.ToString(), "an order list"));
        d.Editable(document, index);
        var built = Lines.Select(l => d.Build("ObjList", LinePlace.Order, l)).ToList();
        var old = document.Working[index].ToList();
        var first = old.FindIndex(e => d.Schema.Directive(e.Name) != null);
        var kept = old.Where(e => d.Schema.Directive(e.Name) == null).ToList();
        var at = first < 0 ? kept.Count : old.Take(first).Count(e => d.Schema.Directive(e.Name) == null);
        var orders = new List<Entry>();
        foreach (var line in built)
            orders.Add(d.NewEntry(document, index, line.Key, line.Texts, kept.Take(at).Concat(orders)));
        kept.InsertRange(at, orders);
        d.SetLines(document, index, kept);
    }
}
