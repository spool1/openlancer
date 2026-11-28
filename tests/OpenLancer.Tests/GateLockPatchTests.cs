using System.Linq;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using Xunit;
using static OpenLancer.Tests.ItemListMergeTests;

namespace OpenLancer.Tests;

/// <summary>
/// The patch of the gate locks that the lock edits write (issue #94). An edit writes only the lock lines that it adds
/// and removes against the lower layer. So the patch of a mod for vanilla 1.1 and HD Edition 0.7.1 keeps the lock
/// lines of each game. The two lock lists are synthetic, as in <see cref="ItemListMergeTests"/>.
/// </summary>
public sealed class GateLockPatchTests
{
    private const string Groups = "[Group]\nnickname = li_n_grp\nrep = 0.5, li_p_grp\n[Group]\nnickname = li_p_grp\n" +
                                  "[Group]\nnickname = br_n_grp\n";

    /// <summary>The lock lines of a synthetic vanilla 1.1: the locks of the Li01 and Li02 gates of the workspace, and an
    /// NPC lock line that names no object.</summary>
    private static readonly string VanillaLocks = Groups + $"""
        [locked_gates]
        locked_gate = {Hash("Li01_to_Li02")}
        npc_locked_gate = {Hash("Li02_to_Li01")}
        npc_locked_gate = {Hash("Li01_to_Li02")}
        npc_locked_gate = {Hash("Ol05_to_Ol06_hole")}
        """;

    /// <summary>The lock lines of a synthetic HD Edition 0.7.1: the player lock of the hole from Ku07 to Ku06 is more,
    /// and the NPC lock line that names no object is not there.</summary>
    private static readonly string HdeLocks = Groups + $"""
        [locked_gates]
        locked_gate = {Hash("Li01_to_Li02")}
        locked_gate = {Hash("Ku07_to_Ku06_hole")}
        npc_locked_gate = {Hash("Li02_to_Li01")}
        npc_locked_gate = {Hash("Li01_to_Li02")}
        npc_locked_gate = {Hash("Ku07_to_Ku06_hole")}
        """;

    private static readonly SectionRef Group = new(ListWorkspace.GroupsPath, new SectionIdentity("Group", "li_n_grp"));

    private static TestWorkspace VanillaWorkspace(string? patch = null)
    {
        var ws = ConnectionWorkspace.Create();
        ws.WriteBase(ListWorkspace.GroupsPath, VanillaLocks);
        if (patch != null)
            ws.Mod("act", new() { [@"patches\" + ListWorkspace.GroupsPath] = patch });
        return ws;
    }

    private static void Run(EditSession session, ConnectionPlan plan)
    {
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
    }

    private static void Save(EditSession session)
    {
        var result = session.Save();
        Assert.True(result.Saved, string.Join("\n", result.Problems));
    }

    /// <summary>The <c>[locked_gates]</c> section of the patch of the gate lock file, with LF line ends.</summary>
    private static string LockPatch(TestWorkspace ws)
    {
        var text = ws.ReadModPatch(ListWorkspace.GroupsPath).Replace("\r\n", "\n");
        var start = text.IndexOf("[locked_gates]", System.StringComparison.Ordinal);
        Assert.True(start >= 0, text);
        var end = text.IndexOf("\n[", start + 1, System.StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..(end + 1)];
    }

    /// <summary>The merge of the saved mod on the lock lists of another game: the hashes of the lines of each name.</summary>
    private static (uint[] Player, uint[] Npc) MergedOn(TestWorkspace ws, string locks)
    {
        ws.WriteBase(ListWorkspace.GroupsPath, locks);
        var overlay = ws.Build(Data.Mods.Mod.Load(ws.ModFolder("act")));
        Assert.DoesNotContain(overlay.Diagnostics, d => d.Message.Contains("locked_gate"));
        var sections = TestWorkspace.Sections(overlay, ListWorkspace.GroupsPath);
        return ([.. Locks(sections, "locked_gate")], [.. Locks(sections, "npc_locked_gate")]);
    }

    /// <summary>
    /// A new player lock of a mod for both games, made on a vanilla 1.1 profile, writes one added line. On HD Edition,
    /// the merge keeps the player lock of the hole from Ku07 to Ku06 and adds the new lock.
    /// </summary>
    [Fact]
    public void ANewLockOfAModForBothGamesWritesOnlyItsOwnLine()
    {
        using var ws = VanillaWorkspace();
        var session = ws.Session("act");

        Run(session, ConnectionPlans.SetLock(session, ConnectionWorkspace.Li03("Object", "Li03_gate"), JumpLock.Player));
        Save(session);

        Assert.Equal($"[locked_gates]\nol.append = true\nlocked_gate = {Hash("Li03_gate")}\n", LockPatch(ws));
        var (player, npc) = MergedOn(ws, HdeLocks);
        Assert.Equal([Hash("Li01_to_Li02"), Hash("Ku07_to_Ku06_hole"), Hash("Li03_gate")], player);
        Assert.Equal([Hash("Li02_to_Li01"), Hash("Li01_to_Li02"), Hash("Ku07_to_Ku06_hole")], npc);
    }

    /// <summary>
    /// A player lock of a gate pair of the game adds the player lock of one end, and removes the NPC locks of both ends
    /// with one line each. The other lock lines of each game stay.
    /// </summary>
    [Fact]
    public void ALockOfAPairOfTheGameAddsAndRemovesSingleLines()
    {
        using var ws = VanillaWorkspace();
        var session = ws.Session("act");

        Run(session, ConnectionPlans.SetLock(session, SceneWorkspace.Li02("Object", "Li02_to_Li01"), JumpLock.Player));
        Save(session);

        Assert.Equal($"""
            [locked_gates]
            ol.append = true
            locked_gate = {Hash("Li02_to_Li01")}
            ol.remove_line = npc_locked_gate, {Hash("Li02_to_Li01")}
            ol.remove_line = npc_locked_gate, {Hash("Li01_to_Li02")}

            """.Replace("\r\n", "\n"), LockPatch(ws));
        var (player, npc) = MergedOn(ws, HdeLocks);
        Assert.Equal([Hash("Li01_to_Li02"), Hash("Ku07_to_Ku06_hole"), Hash("Li02_to_Li01")], player);
        Assert.Equal([Hash("Ku07_to_Ku06_hole")], npc);
    }

    /// <summary>
    /// A rename of a gate of the game removes the lock lines of the old nickname and adds the lines of the new one. A
    /// delete of a gate pair of the game removes the lock lines of its ends. Neither edit writes the other lock lines.
    /// </summary>
    [Fact]
    public void ARenameAndADeleteOfAGateWriteSingleLines()
    {
        using var ws = VanillaWorkspace();
        var session = ws.Session("act");

        session.Execute(ScenePlans.Rename(session, SceneWorkspace.Object("Li01_to_Li02"), "Li01_gate").Command!);
        Save(session);

        Assert.Equal($"""
            [locked_gates]
            ol.append = true
            locked_gate = {Hash("Li01_gate")}
            npc_locked_gate = {Hash("Li01_gate")}
            ol.remove_line = locked_gate, {Hash("Li01_to_Li02")}
            ol.remove_line = npc_locked_gate, {Hash("Li01_to_Li02")}

            """.Replace("\r\n", "\n"), LockPatch(ws));
        var delete = ScenePlans.Delete(session, [SceneWorkspace.Object("Li01_gate")]);
        Assert.True(delete.CanRun, string.Join("\n", delete.Refusals.Select(r => r.Message)));
        session.Execute(delete.Command!);
        Save(session);
        Assert.Equal($"""
            [locked_gates]
            ol.remove_line = locked_gate, {Hash("Li01_to_Li02")}
            ol.remove_line = npc_locked_gate, {Hash("Li02_to_Li01")}
            ol.remove_line = npc_locked_gate, {Hash("Li01_to_Li02")}

            """.Replace("\r\n", "\n"), LockPatch(ws));
        var (player, npc) = MergedOn(ws, HdeLocks);
        Assert.Equal([Hash("Ku07_to_Ku06_hole")], player);
        Assert.Equal([Hash("Ku07_to_Ku06_hole")], npc);
    }

    /// <summary>
    /// A loaded patch with all lock lines of a name, the form before issue #94, keeps its text when the save changes
    /// another section of the file. The next lock edit writes the lines that the lists add and remove against the lower
    /// layer. The loaded line of the added lock keeps its comment.
    /// </summary>
    [Fact]
    public void AnOldFullListChangesToTheNewFormAtTheNextLockEdit()
    {
        var old = $"[locked_gates]\nlocked_gate = {Hash("Li01_to_Li02")}\nlocked_gate = {Hash("Li03_gate")} ; the new gate\n";
        using var ws = VanillaWorkspace(old);
        var session = ws.Session("act");

        session.Execute(EditCommand.SetLinesFromText(Group, "rep", ["0.25, li_p_grp"]));
        Save(session);
        Assert.Equal(old, LockPatch(ws));

        Run(session, ConnectionPlans.SetLock(session, ConnectionWorkspace.Li03("Object", "Li03_hole"), JumpLock.Npc));
        Save(session);
        Assert.Equal($"""
            [locked_gates]
            ol.append = true
            locked_gate = {Hash("Li03_gate")} ; the new gate
            npc_locked_gate = {Hash("Li03_hole")}

            """.Replace("\r\n", "\n"), LockPatch(ws));
        var (player, npc) = MergedOn(ws, HdeLocks);
        Assert.Equal([Hash("Li01_to_Li02"), Hash("Ku07_to_Ku06_hole"), Hash("Li03_gate")], player);
        Assert.Equal([Hash("Li02_to_Li01"), Hash("Li01_to_Li02"), Hash("Ku07_to_Ku06_hole"), Hash("Li03_hole")], npc);
    }

    /// <summary>
    /// A loaded patch of the new form keeps its lines when the save changes another section, also the added line whose
    /// item the lower layer has, and the removed line whose item the lower layer does not have. An edit of one item list
    /// writes that list against the lower layer. The other list keeps its loaded lines.
    /// </summary>
    [Fact]
    public void ALoadedNewFormKeepsItsLinesUntilAnEditOfItsList()
    {
        var loaded = $"""
            [locked_gates]
            ol.append = true
            locked_gate = {Hash("Li01_to_Li02")}
            locked_gate = {Hash("Li03_gate")}
            ol.remove_line = npc_locked_gate, {Hash("Ol09_to_Ol01")}

            """.Replace("\r\n", "\n");
        using var ws = VanillaWorkspace(loaded);
        var session = ws.Session("act");
        Assert.Equal([Hash("Li01_to_Li02"), Hash("Li03_gate")], ConnectionWorkspace.LockLines(session, "locked_gate"));

        session.Execute(EditCommand.SetLinesFromText(Group, "rep", ["0.25, li_p_grp"]));
        Save(session);
        Assert.Equal(loaded, LockPatch(ws));

        Run(session, ConnectionPlans.SetLock(session, ConnectionWorkspace.Li03("Object", "Li03_hole"), JumpLock.Npc));
        Save(session);
        Assert.Equal($"""
            [locked_gates]
            ol.append = true
            locked_gate = {Hash("Li01_to_Li02")}
            locked_gate = {Hash("Li03_gate")}
            npc_locked_gate = {Hash("Li03_hole")}

            """.Replace("\r\n", "\n"), LockPatch(ws));
    }

    /// <summary>A lock edit and its undo leave no lock section in the patch. The edit view and the merge of the saved
    /// patch on the same game give the same lock lines.</summary>
    [Fact]
    public void AnUndoneLockEditWritesNoLockLines()
    {
        using var ws = VanillaWorkspace();
        var session = ws.Session("act");

        Run(session, ConnectionPlans.SetLock(session, ConnectionWorkspace.Li03("Object", "Li03_gate"), JumpLock.Both));
        session.Undo();
        Run(session, ConnectionPlans.SetLock(session, ConnectionWorkspace.Li03("Object", "Li03_hole"), JumpLock.Npc));
        Save(session);

        Assert.Equal($"[locked_gates]\nol.append = true\nnpc_locked_gate = {Hash("Li03_hole")}\n", LockPatch(ws));
        var edited = (ConnectionWorkspace.LockLines(session, "locked_gate"), ConnectionWorkspace.LockLines(session, "npc_locked_gate"));
        var (player, npc) = MergedOn(ws, VanillaLocks);
        Assert.Equal(edited.Item1, player);
        Assert.Equal(edited.Item2, npc);
    }
}
