using System.Collections.Generic;
using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The status frame of a save or a Play that waits for the index of the cutscene uses (#87). The gate blocks the UI
/// thread, so a status line written just before it does not show. The editor writes the status line first, draws one
/// frame with it, and runs the save in the next frame. A Play request waits in its controller, which asks the hold in
/// each frame.
/// </summary>
public sealed class GateWaitTests
{
    [Fact]
    public void ASaveThatWaitsRunsAfterAFrameWithTheStatusLine()
    {
        var wait = new GateWait();
        var calls = new List<string>();

        wait.Run(true, s => calls.Add(s), () => calls.Add("save"));

        Assert.Equal([GateWait.Status], calls);
        Assert.True(wait.Waiting);
        // The frame that draws the status line.
        wait.Frame();
        Assert.Equal([GateWait.Status], calls);
        wait.Frame();
        Assert.Equal([GateWait.Status, "save"], calls);
        Assert.False(wait.Waiting);
        wait.Frame();
        wait.Frame();
        Assert.Equal([GateWait.Status, "save"], calls);
    }

    [Fact]
    public void ASaveThatDoesNotWaitRunsAtOnceWithoutAStatusLine()
    {
        var wait = new GateWait();
        var calls = new List<string>();

        wait.Run(false, s => calls.Add(s), () => calls.Add("save"));

        Assert.Equal(["save"], calls);
        Assert.False(wait.Waiting);
    }

    /// <summary>A second request before the frame of the first one replaces it, and it waits for a frame of its
    /// own.</summary>
    [Fact]
    public void ASecondRequestBeforeItsFrameReplacesTheFirst()
    {
        var wait = new GateWait();
        var calls = new List<string>();
        wait.Run(true, s => calls.Add(s), () => calls.Add("save"));
        wait.Frame();

        wait.Run(true, s => calls.Add(s), () => calls.Add("play"));
        wait.Frame();
        Assert.Equal([GateWait.Status, GateWait.Status], calls);
        wait.Frame();

        Assert.Equal([GateWait.Status, GateWait.Status, "play"], calls);
    }

    /// <summary>A Play request stays in its controller. The hold writes the status line one time, keeps the request in
    /// the frame that draws the line, and lets it start in the frame after that.</summary>
    [Fact]
    public void APlayRequestThatWaitsStartsAfterAFrameWithTheStatusLine()
    {
        var wait = new GateWait();
        var calls = new List<string>();
        var request = new object();

        Assert.True(wait.Hold(request, true, s => calls.Add(s)));
        Assert.True(wait.Hold(request, true, s => calls.Add(s)));
        // The frame that draws the status line.
        wait.Frame();
        Assert.True(wait.Hold(request, true, s => calls.Add(s)));
        wait.Frame();
        Assert.False(wait.Hold(request, true, s => calls.Add(s)));

        Assert.Equal([GateWait.Status], calls);
    }

    /// <summary>Another request writes the status line again and waits for a frame of its own. A request does not wait
    /// while the gate does not wait.</summary>
    [Fact]
    public void AnotherPlayRequestWaitsForAFrameOfItsOwn()
    {
        var wait = new GateWait();
        var calls = new List<string>();
        var (first, second) = (new object(), new object());
        Assert.True(wait.Hold(first, true, s => calls.Add(s)));
        wait.Frame();

        Assert.True(wait.Hold(second, true, s => calls.Add(s)));
        wait.Frame();
        Assert.True(wait.Hold(second, true, s => calls.Add(s)));
        wait.Frame();
        Assert.False(wait.Hold(second, true, s => calls.Add(s)));
        Assert.False(wait.Hold(first, false, s => calls.Add(s)));

        Assert.Equal([GateWait.Status, GateWait.Status], calls);
    }

    [Fact]
    public void TheStatusLineIsSimplifiedTechnicalEnglish()
    {
        Assert.Equal("The editor checks the cutscenes that use the changed items.", GateWait.Status);
        Assert.Empty(SteLint.Problems(GateWait.Status));
        Assert.Empty(SteLint.Problems(OpenLancer.Editor.Export.ExportDialog.ClosedBeforeStart));
    }

    /// <summary>The Export dialog shows no list of the errors of the save gate, so its text sends the user to the
    /// Console. oltool and the agent tool list the errors after the refusal, so their text stays.</summary>
    [Fact]
    public void TheGateRefusalOfTheExportDialogNamesTheConsole()
    {
        var text = OpenLancer.Editor.Export.ExportDialog.GateRefusal("m", 2);

        Assert.Equal($"{OpenLancer.Data.Export.ExportGate.Refusal("m", 2)} The Console lists each error.", text);
        Assert.DoesNotContain("Console", OpenLancer.Data.Export.ExportGate.Refusal("m", 2));
        Assert.Empty(SteLint.Problems(text));
    }
}
