using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Overlay;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The merge of the item lists (issue #94): <c>ol.append</c> adds single lines of the gate locks, and
/// <c>ol.remove_line</c> removes one line. The lock lists of the two games are synthetic. They copy two facts of the
/// retail files: HD Edition 0.7.1 locks the jump hole from Ku07 to Ku06 for the player and vanilla 1.1 does not, and
/// vanilla 1.1 has NPC lock lines that HD Edition does not have. The other nicknames are invented.
/// </summary>
public sealed class ItemListMergeTests
{
    public const string WorldPath = @"DATA\initialworld.ini";

    /// <summary>The lock lines of a synthetic vanilla 1.1.</summary>
    public static readonly string Vanilla = $"""
        [locked_gates]
        locked_gate = {Hash("Ol01_to_Ol02")}
        locked_gate = {Hash("Ol02_to_Ol01")}
        npc_locked_gate = {Hash("Ku07_to_Ku06_hole")}
        npc_locked_gate = {Hash("Ol05_to_Ol06_hole")}
        npc_locked_gate = {Hash("Ol06_to_Ol05_hole")}

        [Group]
        nickname = ol_grp
        """;

    /// <summary>The lock lines of a synthetic HD Edition 0.7.1: one more player lock, and two NPC lock lines less.</summary>
    public static readonly string Hde = $"""
        [locked_gates]
        locked_gate = {Hash("Ol01_to_Ol02")}
        locked_gate = {Hash("Ku07_to_Ku06_hole")}
        locked_gate = {Hash("Ol02_to_Ol01")}
        npc_locked_gate = {Hash("Ku07_to_Ku06_hole")}

        [Group]
        nickname = ol_grp
        """;

    public static uint Hash(string nickname) => FLHash.CreateID(nickname);

    /// <summary>The hashes of the lines of a name of the merged <c>[locked_gates]</c>, in file order.</summary>
    public static List<uint> Locks(IEnumerable<Section> sections, string name) =>
        sections.Single(s => s.Name == "locked_gates").Where(e => e.Name == name)
            .Select(e => Data.Ini.ItemLists.HashOf(e[0])!.Value).ToList();

    private static ModOverlay Merge(string world, params (string Id, string Patch)[] patches)
    {
        using var ws = new TestWorkspace();
        ws.WriteBase(WorldPath, world);
        return ws.Build(patches.Select(p => ws.Mod(p.Id, new() { [@"patches\" + WorldPath] = p.Patch })).ToArray());
    }

    private static List<Section> World(ModOverlay overlay) => TestWorkspace.Sections(overlay, WorldPath);

    /// <summary>
    /// The patch of a mod for both games with one new lock: on each game, the new lock goes after the last player lock,
    /// and each other lock line of the game stays. The merge gives no message.
    /// </summary>
    [Fact]
    public void AModForBothGamesWithOneNewLockKeepsTheLockLinesOfEachGame()
    {
        var patch = $"[locked_gates]\nol.append = true\nlocked_gate = {Hash("Ol03_to_Ol01")}\n";

        var vanilla = Merge(Vanilla, ("m", patch));
        var hde = Merge(Hde, ("m", patch));

        Assert.Empty(vanilla.Diagnostics);
        Assert.Empty(hde.Diagnostics);
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ol02_to_Ol01"), Hash("Ol03_to_Ol01")], Locks(World(vanilla), "locked_gate"));
        Assert.Equal([Hash("Ku07_to_Ku06_hole"), Hash("Ol05_to_Ol06_hole"), Hash("Ol06_to_Ol05_hole")],
            Locks(World(vanilla), "npc_locked_gate"));
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ku07_to_Ku06_hole"), Hash("Ol02_to_Ol01"), Hash("Ol03_to_Ol01")],
            Locks(World(hde), "locked_gate"));
        Assert.Equal([Hash("Ku07_to_Ku06_hole")], Locks(World(hde), "npc_locked_gate"));
        Assert.Equal(["locked_gate", "locked_gate", "locked_gate", "locked_gate", "npc_locked_gate"],
            World(hde)[0].Select(e => e.Name));
    }

    /// <summary>
    /// A line whose item the target has already stays one line, also in the signed form of the hash. An item list that
    /// the target does not have goes at the end of the section.
    /// </summary>
    [Fact]
    public void AnAddedLineWhoseItemTheTargetHasIsSkipped()
    {
        var signed = unchecked((int)Hash("Ol02_to_Ol01"));
        var world = $"[locked_gates]\nlocked_gate = {Hash("Ol01_to_Ol02")}\nlocked_gate = {Hash("Ol02_to_Ol01")}\n";

        var overlay = Merge(world, ("m", $"""
            [locked_gates]
            ol.append = true
            locked_gate = {signed}
            npc_locked_gate = {Hash("Ol01_to_Ol02")}
            locked_gate = {Hash("Ol03_to_Ol01")}
            """));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ol02_to_Ol01"), Hash("Ol03_to_Ol01")], Locks(World(overlay), "locked_gate"));
        Assert.Equal(["locked_gate", "locked_gate", "locked_gate", "npc_locked_gate"], World(overlay)[0].Select(e => e.Name));
    }

    /// <summary>ol.remove_line removes each line of its name with its item. A removed item that the target does not have
    /// needs no change and gives no message, so a mod for both games can remove a lock of one game.</summary>
    [Fact]
    public void ARemovedLineGoesAndAnItemThatTheTargetDoesNotHaveNeedsNoChange()
    {
        var patch = $"""
            [locked_gates]
            ol.remove_line = locked_gate, {Hash("Ku07_to_Ku06_hole")}
            ol.remove_line = npc_locked_gate, {unchecked((int)Hash("Ku07_to_Ku06_hole"))}
            """;

        var vanilla = Merge(Vanilla, ("m", patch));
        var hde = Merge(Hde, ("m", patch));

        Assert.Empty(vanilla.Diagnostics);
        Assert.Empty(hde.Diagnostics);
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ol02_to_Ol01")], Locks(World(vanilla), "locked_gate"));
        Assert.Equal([Hash("Ol05_to_Ol06_hole"), Hash("Ol06_to_Ol05_hole")], Locks(World(vanilla), "npc_locked_gate"));
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ol02_to_Ol01")], Locks(World(hde), "locked_gate"));
        Assert.Empty(Locks(World(hde), "npc_locked_gate"));
    }

    /// <summary>A patch line of an item list without ol.append replaces all lines of its name, as before the rules of
    /// issue #94. So an old patch keeps its meaning.</summary>
    [Fact]
    public void AFullListPatchKeepsItsMeaning()
    {
        var overlay = Merge(Hde, ("m", $"[locked_gates]\nlocked_gate = {Hash("Ol01_to_Ol02")}\nlocked_gate = {Hash("Ol03_to_Ol01")}\n"));

        Assert.Empty(overlay.Diagnostics);
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ol03_to_Ol01")], Locks(World(overlay), "locked_gate"));
        Assert.Equal([Hash("Ku07_to_Ku06_hole")], Locks(World(overlay), "npc_locked_gate"));
    }

    /// <summary>
    /// Two mods that each add a lock keep both locks, and the merge gives no conflict warning. A later mod that sets all
    /// lines of the name replaces the added lines, so it gets the warning of two mods that change the same entry.
    /// </summary>
    [Fact]
    public void TwoModsThatAddALockKeepBothLocksAndALaterFullListWarns()
    {
        var a = $"[locked_gates]\nol.append = true\nlocked_gate = {Hash("Ol03_to_Ol01")}\n";
        var b = $"[locked_gates]\nol.append = true\nlocked_gate = {Hash("Ol04_to_Ol01")}\n";

        var both = Merge(Vanilla, ("a", a), ("b", b));
        var full = Merge(Vanilla, ("a", a), ("b", b), ("c", $"[locked_gates]\nlocked_gate = {Hash("Ol01_to_Ol02")}\n"));

        Assert.Empty(both.Diagnostics);
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ol02_to_Ol01"), Hash("Ol03_to_Ol01"), Hash("Ol04_to_Ol01")],
            Locks(World(both), "locked_gate"));
        var warning = Assert.Single(full.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("[locked_gates]: 'locked_gate' was also changed by mod 'b'. This mod loads later and wins.",
            warning.Message);
        Assert.Equal([Hash("Ol01_to_Ol02")], Locks(World(full), "locked_gate"));
    }

    /// <summary>The item directives are errors outside a section with item lists, with a directive that adds, deletes
    /// or replaces a section, and with a line that gives no item. The section then stays as it is.</summary>
    [Theory]
    [InlineData("[Group]\nnickname = ol_grp\nol.append = true\nrep = 0.5, ol_grp\n",
        "[Group]: ol.append is only for a [Data] section of a patch of EXE\\freelancer.ini, and for a section with " +
        "item lists: [locked_gates].")]
    [InlineData("[Group]\nnickname = ol_grp\nol.remove_line = rep, 0.5\n",
        "[Group]: ol.remove_line is only for a section with item lists: [locked_gates].")]
    [InlineData("[locked_gates]\nol.new = true\nol.append = true\nlocked_gate = 5\n",
        "[locked_gates]: A section with ol.append or ol.remove_line cannot also have ol.new, ol.delete, ol.rename, " +
        "ol.replace or ol.parent.")]
    [InlineData("[locked_gates]\nol.append = false\nlocked_gate = 5\n",
        "[locked_gates]: ol.append needs one line with the value true.")]
    [InlineData("[locked_gates]\nol.append = true\nol.remove = npc_locked_gate\nlocked_gate = 5\n",
        "[locked_gates]: ol.remove cannot remove the item list 'npc_locked_gate' in a section with ol.append or " +
        "ol.remove_line. Use ol.remove_line for each line.")]
    [InlineData("[locked_gates]\nol.append = true\nlocked_gate = Ol03_to_Ol01\n",
        "[locked_gates]: 'locked_gate = Ol03_to_Ol01': ol.append needs a hash as a decimal number as the item of the line.")]
    [InlineData("[locked_gates]\nol.remove_line = rep, 5\n",
        "[locked_gates]: ol.remove_line = rep, 5: the line needs the name of an item list and then the item. The item " +
        "lists are locked_gate, npc_locked_gate. The item is a hash as a decimal number.")]
    [InlineData("[locked_gates]\nol.remove_line = locked_gate, gate\n",
        "[locked_gates]: ol.remove_line = locked_gate, gate: the item must be a hash as a decimal number.")]
    [InlineData("[locked_gates]\nlocked_gate = 5\nol.remove_line = locked_gate, 6\n",
        "[locked_gates]: the patch sets the lines of 'locked_gate' and also has ol.remove_line for 'locked_gate'. Add " +
        "ol.append = true to add the lines.")]
    [InlineData("[locked_gates]\nol.append = true\nlocked_gate = 5\nol.remove_line = locked_gate, 5\n",
        "[locked_gates]: the patch adds 'locked_gate = 5' and also removes it with ol.remove_line.")]
    public void TheItemDirectivesRefuseOtherSectionsAndBadLines(string patch, string message)
    {
        var overlay = Merge(Vanilla, ("m", patch));

        var error = Assert.Single(overlay.Diagnostics);
        Assert.Equal((DiagnosticSeverity.Error, message), (error.Severity, error.Message));
        Assert.Equal([Hash("Ol01_to_Ol02"), Hash("Ol02_to_Ol01")], Locks(World(overlay), "locked_gate"));
        Assert.Empty(SteLint.Problems(message));
    }
}
