using System;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Editor.Play;
using Xunit;
using static OpenLancer.Tests.PlayGame;

namespace OpenLancer.Tests;

/// <summary>The local test server of a play copy (section 8.7 of the M11 design), with fake programs and a fake
/// clock. No test starts FLServer.</summary>
public sealed class TestServerTests : IAsyncDisposable
{
    private readonly GamePlayRig rig = new();

    public ValueTask DisposeAsync() => rig.DisposeAsync();

    /// <summary>Opens a profile with a goods patch and hosts the test server until FLServer starts.</summary>
    private async Task<FakeGameProcess> HostAsync()
    {
        rig.Open(rig.Game.Export.GoodsMod(GamePlayRig.GoodsPatch));
        rig.Plays.HostServer(rig.Target);
        await rig.RunAsync(() => rig.Starts.Count == 1, "the start of the server");
        return rig.Starts[0].Process;
    }

    [Fact]
    public async Task TheServerStartsWithSlashC()
    {
        await HostAsync();

        var info = rig.Starts[0].Info;
        Assert.Equal(rig.Game.CopyPath(@"EXE\FLServer.exe"), info.FileName);
        Assert.Equal(["/c"], info.ArgumentList);
        Assert.Equal(rig.Game.CopyPath("EXE"), info.WorkingDirectory);
        Assert.Equal("Test server: Freelancer 1.0", rig.Plays.ServerStatus);
        Assert.Null(rig.Plays.GameStatus);
        Assert.Contains(TestServer.Started, rig.Console);
        Assert.Contains(TestServer.Characters, rig.Console);
        Assert.Contains("price = 5600", System.IO.File.ReadAllText(rig.Game.CopyPath(ExportGame.GoodsPath)));
    }

    [Fact]
    public async Task NoServerProgramGivesTheText()
    {
        await using var hd = new GamePlayRig(hd: true);
        hd.Game.Game.Delete(@"EXE\FLServer.exe");
        Assert.False(hd.Target.HasServer);
        hd.Open(hd.Game.Export.GoodsMod(GamePlayRig.GoodsPatch));

        hd.Plays.HostServer(hd.Target);
        hd.Plays.Update(false, false);

        Assert.Contains($"Play did not start. {TestServer.Missing}", hd.Console);
        Assert.Empty(hd.Starts);
    }

    [Fact]
    public async Task JoinNeedsARunningServer()
    {
        rig.Plays.JoinServer();
        Assert.Contains("Play did not start. Host a test server first.", rig.Console);
        Assert.Empty(rig.Starts);

        await HostAsync();
        rig.Plays.JoinServer();

        Assert.Equal(2, rig.Starts.Count);
        Assert.Equal(rig.Game.CopyPath(ProgramPath), rig.Starts[1].Info.FileName);
        Assert.Equal(rig.Game.CopyPath("EXE"), rig.Starts[1].Info.WorkingDirectory);
        Assert.Equal("Playing in Freelancer 1.0 (play copy)", rig.Plays.GameStatus);
        Assert.Equal("The game of the play copy runs.", rig.Plays.JoinBlock);
        // The game exits first. The play copy keeps its lock for the server.
        rig.Starts[1].Exit(0);
        await rig.RunAsync(() => !rig.Plays.Checking, "the check after the exit of the game");
        Assert.True(rig.Plays.ServerRuns);
        Assert.Throws<OpenLancer.Data.PlayCopies.PlayCopyException>(() =>
            OpenLancer.Data.PlayCopies.PlayCopy.Open(rig.Game.CopyFolder));
        rig.Starts[0].Exit(0);
        await rig.RunAsync(() => !rig.Plays.Checking, "the check after the exit of the server");
        Assert.Contains("The test server closed.", rig.Console);
    }

    [Fact]
    public async Task StopWaitsBeforeItEndsTheServer()
    {
        var server = await HostAsync();

        rig.Plays.StopServer();

        Assert.Equal(1, server.Closes);
        Assert.False(server.Killed);
        Assert.True(rig.Plays.ServerForceStop);
        Assert.Equal("Stopping the test server", rig.Plays.ServerStatus);
        Assert.Contains(TestServer.Stopping, rig.Console);
        rig.Now += TestServer.StopLimit - TimeSpan.FromSeconds(1);
        rig.Plays.Update(false, false);
        Assert.False(server.Killed);
        rig.Now += TimeSpan.FromSeconds(1);
        rig.Plays.Update(false, false);
        Assert.True(server.Killed);
        Assert.Contains("The test server did not stop in 60 s, so the editor ended it.", rig.Console);
    }

    [Fact]
    public async Task ForceStopEndsTheServerAtOnce()
    {
        var server = await HostAsync();

        rig.Plays.StopServer();
        rig.Plays.StopServer();

        Assert.True(server.Killed);
        Assert.Equal(1, server.Closes);
        Assert.Contains("The editor ended the test server.", rig.Console);
    }

    [Fact]
    public async Task TheCloseOfTheEditorAsksAndWaitsForTheServer()
    {
        Assert.True(rig.Plays.CloseRequested(() => throw new InvalidOperationException("No server runs.")));
        var server = await HostAsync();
        var closed = 0;

        Assert.False(rig.Plays.CloseRequested(() => closed++));

        Assert.Equal(TestServer.CloseQuestion, rig.Host.Asked!.Value.Message);
        Assert.Equal(["Stop and close", "Cancel"], rig.Host.Asked.Value.Choices.Select(c => c.Label));
        rig.Host.Asked.Value.Choices[0].Action();
        Assert.Equal(1, server.Closes);
        Assert.Equal(0, closed);
        rig.Starts[0].Exit(0);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task PlayInTheGameWaitsForTheServer()
    {
        await HostAsync();

        rig.Plays.Play(rig.Target);

        Assert.Contains("Play did not start. The test server of the play copy runs. Stop it first.", rig.Console);
        Assert.Single(rig.Starts);
    }
}
