using System.Collections.Generic;
using OpenLancer.Editor.Editing;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The status frame of a save or a Play that waits for the index of the cutscene uses (#87). The gate blocks the UI
/// thread, so a status line written just before it does not show. The editor writes the status line first, draws one
/// frame with it, and runs the save in the next frame.
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

    [Fact]
    public void TheStatusLineIsSimplifiedTechnicalEnglish()
    {
        Assert.Equal("The editor checks the cutscenes that use the changed items.", GateWait.Status);
        Assert.Empty(SteLint.Problems(GateWait.Status));
    }
}
