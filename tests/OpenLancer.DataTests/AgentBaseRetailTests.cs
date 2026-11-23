using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using LibreLancer;
using OpenLancer.Studio.Agent;
using Xunit;

namespace OpenLancer.DataTests;

/// <summary>
/// The read tools and the resource of the bases on the vanilla data (section H.2 of the bases design): each result fits
/// the output schema of its tool and the size limit of a result. The tests count and write the time of each call to the
/// log, without a gate. The frame gate of list_bases and get_base is in <see cref="AgentRetailTests"/>. The tests hold
/// no retail text: the bases of the calls come from the data at run time.
/// </summary>
public sealed class AgentBaseRetailTests(ITestOutputHelper output)
{
    static AgentBaseRetailTests() => AppHandler.ConsoleInit(silent: true);

    /// <summary>The bases of vanilla 1.1 without the three intro bases.</summary>
    private const int Bases = 194;

    /// <summary>
    /// list_bases gives the 194 bases that are not intro bases, in pages. get_base gives each part of the first station
    /// with its seven steps. The catalog of the template bases has the same 194 bases. A base of the game has no error of
    /// the base checks.
    /// </summary>
    [Fact]
    public void TheBaseToolsReadTheVanillaBases()
    {
        using var workspace = new RetailWorkspace(GameDataPaths.RequireBase(), "c4agentbases");
        using var host = HeadlessHost.OpenReadOnly(workspace.ProfilePath, workspace.ModId);
        JsonObject Ok(string tool, string arguments)
        {
            var start = Stopwatch.GetTimestamp();
            var result = AgentTools.Run(host, tool, JsonNode.Parse(arguments)!.AsObject()).Result!;
            output.WriteLine($"{Stopwatch.GetElapsedTime(start).TotalMilliseconds,8:0.00} ms  {tool} {arguments}");
            Assert.False(result.IsError, result.Text);
            Assert.False(result.Limited(host.Limits).IsError, $"{tool}: {result.Bytes} bytes");
            Assert.Empty(OpenLancer.Tests.SchemaCheck.Problems(AgentTools.Find(tool)!.OutputSchema, result.Content));
            return result.Content;
        }

        Ok("list_bases", """{ "limit": 50 }""");
        // The second call reads the caches of the session that the first call filled.
        var page = Ok("list_bases", """{ "limit": 50 }""");
        var stations = Ok("list_bases", """{ "kind": "station", "has_rooms": ["Deck", "Bar"], "limit": 50 }""");
        var station = (string)stations["rows"]![0]!["ref"]!;
        Ok("get_base", $$"""{ "base": "{{station}}" }""");
        var view = Ok("get_base", $$"""{ "base": "{{station}}" }""");
        var start = Stopwatch.GetTimestamp();
        var catalog = JsonNode.Parse(AgentResources.Find("openlancer://catalog/base-templates")!.Read(host))!["templates"]!
            .AsArray();
        output.WriteLine($"{Stopwatch.GetElapsedTime(start).TotalMilliseconds,8:0.00} ms  catalog/base-templates");

        Assert.Equal(Bases, (int)page["total"]!);
        Assert.DoesNotContain(page["rows"]!.AsArray(), r => ((string)r!["nickname"]!).StartsWith("intro", System.StringComparison.OrdinalIgnoreCase));
        Assert.True((int)stations["total"]! > 0);
        Assert.Equal(7, (int)view["completeness"]!["total"]!);
        Assert.NotEmpty(view["rooms"]!.AsArray());
        Assert.NotEmpty(view["characters"]!.AsArray());
        Assert.NotEmpty(view["market"]!.AsArray());
        Assert.DoesNotContain(view["problems"]!.AsArray(), p => (string?)p!["severity"] == "error");
        Assert.Equal(Bases, catalog.Count);
        output.WriteLine($"{station}: {view["rooms"]!.AsArray().Count} rooms, {view["characters"]!.AsArray().Count} " +
                         $"characters, {view["market"]!.AsArray().Count} market lines");
    }
}
