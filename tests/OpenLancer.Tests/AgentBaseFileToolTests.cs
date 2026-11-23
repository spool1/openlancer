using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Studio.Agent;
using Xunit;
using static OpenLancer.Tests.AgentCalls;

namespace OpenLancer.Tests;

/// <summary>
/// The agent rules of mBases.ini and news.ini (section C.3 of the bases design). They are below DATA\MISSIONS, but they
/// hold the bar data and the news of the bases. So they are in the tool group Scene, and their changes are no mission
/// changes. The generic edit tools change the sections of mBases.ini with the field checks and the list rules.
/// </summary>
public sealed class AgentBaseFileToolTests
{
    [Fact]
    public void MBasesAndNewsAreInTheGroupScene()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var npc = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC", "ol01_01_npc_1");

        Assert.Equal(ToolGroup.Scene, EditTools.GroupOf(session, npc));
        Assert.Equal(ToolGroup.Scene, EditTools.GroupOf(session,
            new SectionRef(@"data\missions\NEWS.INI", new SectionIdentity("NewsItem", null))));
        Assert.Equal(ToolGroup.Missions, EditTools.GroupOf(session,
            new SectionRef(@"DATA\MISSIONS\M01A\m01a.ini", new SectionIdentity("Trigger", "t1"))));
        Assert.Equal(ToolGroup.Scene, EditTools.GroupOf(session, BaseWorkspace.Section(session, BaseWorkspace.Ol01Path, "Object", "Ol01_01")));
        Assert.Null(EditTools.GroupOf(session, BaseWorkspace.Section(session, BaseWorkspace.StationPath, "BaseInfo")));
    }

    /// <summary>A change of mBases.ini is not a change of a mission script, so the question of a plan does not name it as
    /// mission lines.</summary>
    [Fact]
    public void AChangeOfMBasesIsNoMissionChangeInTheQuestion()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var npc = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC", "ol01_01_npc_1");
        var plan = new CommandPlan(EditCommand.Group("Rename x", []), [npc]) { NeedsConfirmation = true };

        Assert.Equal("The edit 'Rename x' changes more than its targets.", EditTools.ConfirmationOf(session, plan));
    }

    /// <summary>
    /// set_fields on a bar character runs the field checks and the list rule of its head, and the result gives the new
    /// problem. A host that hides the tool group Missions still edits mBases.ini. A host that hides the group Scene
    /// refuses it.
    /// </summary>
    [Fact]
    public void SetFieldsOnABarCharacterRunsTheChecksAlsoWhenMissionsAreHidden()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        host.Hidden.Add(ToolGroup.Missions);
        var npc = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC", "ol01_01_npc_1");
        var reference = Json(ItemRefs.Canonical(session, npc));

        var broken = Ok(host, "set_fields", $$"""{ "ref": {{reference}}, "fields": { "head": "no_head" } }""");
        var wrongType = Error(host, "set_fields", $$"""{ "ref": {{reference}}, "fields": { "individual_name": "$ifc:x" } }""");

        Assert.Equal("applied", (string?)broken["status"]);
        Assert.Equal("no_head", session.Get(npc)!.Lines("head").Single().Values[0].ToString());
        var problem = Assert.Single(broken["new_problems"]!.AsArray())!;
        Assert.Contains("no_head", (string?)problem["message"]);
        Assert.Equal("error", (string?)problem["severity"]);
        // The field checks know that individual_name holds a string id.
        Assert.Equal("invalid_value", wrongType.Code);

        host.Hidden.Add(ToolGroup.Scene);
        Assert.Equal(("not_available", "GroupHidden"),
            Error(host, "set_fields", $$"""{ "ref": {{reference}}, "fields": { "head": "ol_head" } }""") is var (code, detail, _)
                ? (code, detail)
                : default);
    }

    /// <summary>set_text on a bar character writes a text of the mod into individual_name in the same undo step.</summary>
    [Fact]
    public void SetTextGivesABarCharacterANameOfTheMod()
    {
        using var ws = BaseWorkspace.Create();
        var session = ws.Session("act");
        var host = new TestAgentHost(session);
        var npc = BaseWorkspace.MBasePart(session, BaseWorkspace.Station, "GF_NPC", "ol01_01_npc_1");
        var reference = Json(ItemRefs.Canonical(session, npc));

        var result = Ok(host, "set_text", $$"""
            { "ref": {{reference}}, "field": "individual_name", "text": "Ol Guild\nBar Patron\nAda Vance" }
            """);

        Assert.Equal("applied", (string?)result["status"]);
        var value = session.Get(npc)!.Lines("individual_name").Single().Values[0].ToString();
        Assert.True(Data.Strings.StringPlaceholder.TryParse(value, out var placeholder));
        Assert.Equal(Data.Strings.StringKind.String, placeholder.Kind);
        Assert.Equal("Ol Guild\nBar Patron\nAda Vance", session.Text(placeholder));
        session.Undo();
        Assert.Equal("1102", session.Get(npc)!.Lines("individual_name").Single().Values[0].ToString());
    }
}
