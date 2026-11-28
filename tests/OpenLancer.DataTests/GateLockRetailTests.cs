using System;
using System.IO;
using System.Linq;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.Ini;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Export;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;
using OpenLancer.Runtime.Strings;
using OpenLancer.Runtime.Thn;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// Retail proof of issue #94: a lock of a mod for both games, made on vanilla 1.1, writes only its own lock lines. The
/// merge on HD Edition 0.7.1 keeps the lock lines of HD Edition, and the FLMM package changes only the lock lines of the
/// mod. The test needs <c>OPENLANCER_HDE</c>, a copy of an HD Edition install, and only reads it.
/// </summary>
public sealed class GateLockRetailTests(ITestOutputHelper output)
{
    static GateLockRetailTests() => AppHandler.ConsoleInit(silent: true);

    private const string WorldPath = @"DATA\initialworld.ini";

    [Fact]
    public void ALockOfAVanillaGatePairKeepsTheLockLinesOfEachGame()
    {
        var hde = GameDataPaths.RequireHde();
        var vanilla = GameDataPaths.RequireBase();
        using var workspace = new RetailWorkspace(vanilla, "ollock");
        var session = workspace.Open();
        var graph = session.Routes.Graph;
        var connection = graph.Connections.First(c => c.Kind == ConnectionKind.Gate && c is { PlayerLocked: false, NpcLocked: false } &&
                                                      graph.PartnerOf(c) is { PlayerLocked: false, NpcLocked: false });
        var partner = graph.PartnerOf(connection)!;
        var gate = new SectionRef(graph.System(connection.System)!.File!, new SectionIdentity("Object", connection.Object));
        output.WriteLine($"Lock of {connection.Object} and {partner.Object}");

        var plan = ConnectionPlans.SetLock(session, gate, JumpLock.Player);
        Assert.True(plan.CanRun, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        session.Execute(plan.Command!);
        var saved = session.Save();
        Assert.True(saved.Saved, string.Join("\n", saved.Problems));

        var added = new[] { FLHash.CreateID(connection.Object), FLHash.CreateID(partner.Object) };
        var patch = Directory.EnumerateFiles(Path.Combine(workspace.ModFolder, Mod.PatchesFolder), "*.ini", SearchOption.AllDirectories)
            .Single(f => Path.GetFileName(f).Equals("initialworld.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"[locked_gates]\r\nol.append = true\r\nlocked_gate = {added[0]}\r\nlocked_gate = {added[1]}\r\n",
            File.ReadAllText(patch));

        var mod = Mod.Load(workspace.ModFolder);
        foreach (var (root, target) in new[] { (vanilla, ExportTarget.Vanilla), (hde, ExportTarget.HdEdition) })
        {
            var files = TargetBuild.Run(GameBase.Standard(root), [mod], ModExport.DllName(mod.Id),
                DllStringResourceWriter.Instance, ThornEvaluator.Instance, keepPlaceholders: true);
            var game = Locks(File.ReadAllBytes(Path.Combine(root, WorldPath)));
            var merged = Locks(files.File(WorldPath)!.Bytes);
            Assert.Equal([.. game.Player, .. added], merged.Player);
            Assert.Equal(game.Npc, merged.Npc);

            var script = FlmmScript.Build(files, mod, FlmmHeader.Of(PublishTexts.Of(mod.Manifest), target), probe: false);
            foreach (var problem in script.Problems)
                output.WriteLine($"{target.Id}: {problem}");
            Assert.DoesNotContain(script.Problems, p => p.Message.Contains("initialworld", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(script.Files, f => f.Path.EndsWith("initialworld.ini", StringComparison.OrdinalIgnoreCase));
            Assert.Contains($"<source>\r\nlocked_gate = {added[0]}\r\nlocked_gate = {added[1]}\r\n</source>", script.Xml);
        }
        Assert.Contains(FLHash.CreateID("Ku07_to_Ku06_hole"), Locks(File.ReadAllBytes(Path.Combine(hde, WorldPath))).Player);
    }

    /// <summary>The hashes of the player locks and of the NPC locks of an <c>InitialWorld.ini</c>, in file order.</summary>
    private static (uint[] Player, uint[] Npc) Locks(byte[] bytes)
    {
        var sections = IniDocument.Parse(WorldPath, bytes).Sections.Where(s => s.Name == "locked_gates").ToList();
        uint[] Of(string name) => [.. sections.SelectMany(s => s).Where(e => e.Name == name).Select(e => ItemLists.HashOf(e[0])!.Value)];
        return (Of("locked_gate"), Of("npc_locked_gate"));
    }
}
