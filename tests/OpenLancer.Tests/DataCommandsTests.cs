using System;
using System.Collections.Generic;
using System.Linq;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Studio.Commands;
using Xunit;
using static OpenLancer.Tests.ItemWorkspace;

namespace OpenLancer.Tests;

/// <summary>
/// The market commands (section 4.0.8 of the M6b design). The "Markets" section of the Inspector runs them through the
/// command runner. Each one gives the same W and the same entry as the market edit of today.
/// </summary>
public sealed class MarketCommandsTests
{
    private static readonly MarketValue Maximum = MarketEdits.Fields.Single(f => f.Index == MarketLine.MaximumIndex);

    [Fact]
    public void EachMarketCommandGivesTheLinesOfToday()
    {
        var values = MarketEdits.DefaultValues("equipment");
        DataParity.Assert(MarketPath, s => s.Execute(MarketEdits.AddToBase(s, "eng1", "b1", values)),
            s => MarketCommands.AddToBase(s, "eng1", "b1", values));
        DataParity.Assert(MarketPath, s => s.Execute(MarketEdits.SetValue(s, Added(s), Maximum, "20")),
            s => MarketCommands.SetValue(s, Added(s), Maximum, "20"), Sell);
        DataParity.Assert(MarketPath, s => s.Execute(MarketEdits.SetTransaction(s, Added(s), MarketTransaction.SellOnly)),
            s => MarketCommands.SetTransaction(s, Added(s), MarketTransaction.SellOnly), Sell);
        DataParity.Assert(MarketPath, s => s.Execute(MarketEdits.SetLine(s, Added(s), null)),
            s => MarketCommands.SetLine(s, Added(s), null), Sell);
    }

    /// <summary>A market edit that the rules refuse gives a plan that refuses with the reason, and changes
    /// nothing.</summary>
    [Fact]
    public void ARefusedMarketEditGivesTheReason()
    {
        using var ws = Create();
        var session = ws.Session("act");
        var lower = session.MarketLines("gun1").Single(l => l.Base == "b1");

        Assert.Equal("The base b1 already sells gun1.",
            Assert.Single(MarketCommands.AddToBase(session, "gun1", "b1", "0, -1, 10, 10, 0, 1").Refusals).Message);
        Assert.StartsWith("A patch cannot change a market line of a lower layer.",
            Assert.Single(MarketCommands.SetLine(session, lower, null).Refusals).Message);
        Sell(session);
        var refused = MarketCommands.SetValue(session, Added(session), Maximum, "many");
        Assert.Equal("Maximum stock must be a whole number, for example 3.", Assert.Single(refused.Refusals).Message);
        Assert.Equal(Added(session).Section, refused.Refusals[0].Target);
    }

    /// <summary>The line of eng1 at b1 that the active mod adds.</summary>
    private static MarketLine Added(EditSession session) => session.MarketLines("eng1").Single(l => l.Base == "b1");

    /// <summary>Makes b1 sell eng1, as the start of an edit of the line.</summary>
    private static void Sell(EditSession session) =>
        session.Execute(MarketEdits.AddToBase(session, "eng1", "b1", MarketEdits.DefaultValues("equipment")));
}

/// <summary>
/// The Database commands (section 4.0.8 of the M6b design): a new item from a template and the rename of an item of
/// the active mod. Each one gives the same W and the same entry as the edit of today.
/// </summary>
public sealed class DatabaseCommandsTests
{
    [Fact]
    public void ANewItemFromATemplateGivesTheSectionsOfToday()
    {
        DataParity.Assert(WeaponsPath, s => s.Execute(ItemTemplates.Build(ItemTemplates.Plan(s, Gun("gun1"), "gun2"))),
            s => DatabaseCommands.CreateFromTemplate(s, Gun("gun1"), "gun2"), null, WeaponGoodsPath);

        using var ws = Create();
        var session = ws.Session("act");
        var template = ItemTemplates.Plan(session, Gun("gun1"), "gun2");
        var plan = DatabaseCommands.CreateFromTemplate(template);
        Assert.Equal("New gun2 from gun1", plan.Label);
        Assert.Equal(template.Copies.Select(c => c.Copy), plan.Adds);
        Assert.Equal(Gun("gun2"), plan.SelectAfter);
    }

    /// <summary>A template plan with problems refuses with them. A template plan of an older edit view refuses with
    /// the reason, so it never runs on a newer state.</summary>
    [Fact]
    public void ATemplatePlanWithProblemsOrOfAnOlderEditViewRefuses()
    {
        using var ws = Create();
        var session = ws.Session("act");

        var taken = DatabaseCommands.CreateFromTemplate(session, Gun("gun1"), "gun1");
        Assert.False(taken.CanRun);
        Assert.Contains("gun1", taken.Refusals[0].Message);

        var old = ItemTemplates.Plan(session, Gun("gun1"), "gun2");
        session.Execute(EditCommand.SetLinesFromText(Gun("gun1"), "ids_info", ["302"]));
        var stale = DatabaseCommands.CreateFromTemplate(old);
        Assert.Contains("the edit view changed after the plan of gun2", Assert.Single(stale.Refusals).Message);
        Assert.Equal(Gun("gun1"), stale.Refusals[0].Target);
    }

    /// <summary>The rename of an item of the active mod gives the W of <see cref="ItemEdits.Rename"/>: the item, its good
    /// and each line that names it. A nickname that another section has refuses with the detail NameTaken.</summary>
    [Fact]
    public void ARenameOfAnItemGivesTheLinesOfToday()
    {
        static void New(EditSession s) => s.Execute(ItemTemplates.Build(ItemTemplates.Plan(s, Gun("gun1"), "gun2")));
        DataParity.Assert(WeaponsPath, s => s.Execute(ItemEdits.Rename(s, Gun("gun2"), "gun3")),
            s => DatabaseCommands.Rename(s, Gun("gun2"), "gun3"), New, WeaponGoodsPath);

        using var ws = Create();
        var session = ws.Session("act");
        New(session);
        var plan = DatabaseCommands.Rename(session, Gun("gun2"), "gun3");
        Assert.Equal("Rename gun2 to gun3", plan.Label);
        Assert.Equal([Gun("gun3")], plan.Changes);

        var taken = DatabaseCommands.Rename(session, Gun("gun2"), "gun1");
        var error = Assert.Single(taken.Report(session).Errors);
        Assert.Equal(MissionErrors.NameTaken, error.Detail);
        Assert.Equal("Another section of the equipment files has the nickname gun1.", error.Message);
        Assert.False(DatabaseCommands.Rename(session, Ship("s1"), "bad name;").CanRun);
    }
}

/// <summary>The parity check of the data commands: the edit of today and the command through the runner.</summary>
internal static class DataParity
{
    /// <summary>
    /// The edit of today and the command through the command runner give the same W of the files. Both give one entry
    /// with the same label, and the runner gives the edit origin User and writes nothing to the Console.
    /// </summary>
    /// <param name="start">An edit before both, or null.</param>
    public static void Assert(string path, Action<EditSession> today, Func<EditSession, EditPlan> command,
        Action<EditSession>? start = null, params string[] paths)
    {
        using var a = Create();
        var expected = a.Session("act");
        start?.Invoke(expected);
        today(expected);

        using var b = Create();
        var session = b.Session("act");
        start?.Invoke(session);
        var host = new FakeCommandHost(session);
        var result = new CommandRunner(host).Run(command);

        Xunit.Assert.Equal(EditStatus.Applied, result.Status);
        foreach (var file in paths.Prepend(path))
            Xunit.Assert.Equal(W(expected, file), W(session, file));
        Xunit.Assert.Equal(expected.UndoLabel, session.UndoLabel);
        Xunit.Assert.Equal(EditOrigin.User, session.UndoOrigin);
        Xunit.Assert.Empty(host.Log.Entries.Where(e => e.Severity == ConsoleSeverity.Error));
    }

    /// <summary>Each section of a file of W with its lines, in file order.</summary>
    private static string[] W(EditSession session, string path) =>
        session.Sections(path).SelectMany(s => session.Get(s)!.Entries
            .Select(e => $"{e.Name} = {FieldEdit.FormatLine(e.Values)}").Prepend($"[{s.Id}]")).ToArray();
}
