using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Numerics;
using OpenLancer.Editor;
using OpenLancer.Editor.Play;
using OpenLancer.Runtime.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The game process of Play with a stub program (section 16.1 of the M4 design): the job that ends it, the commands on
/// its input, and the events and engine lines of its output on the UI thread.
/// </summary>
public sealed class PlayProcessTests : IDisposable
{
    private readonly TestWorkspace ws = new();
    private readonly QueuedUIThread ui = new();
    private readonly PlayStub stub;

    public PlayProcessTests() => stub = new PlayStub(Path.Combine(ws.Root, "stub"));

    public void Dispose()
    {
        stub.KillAll();
        ws.Dispose();
    }

    /// <summary>
    /// (1) Decision D53: the job ends the game when it closes, also a game that ignores the end of its input. The stub
    /// sleeps 120 s, so only the job can end it within the 30 s of the wait.
    /// </summary>
    [Fact]
    public void DisposeClosesTheJobAndEndsAGameThatIgnoresItsInput()
    {
        int? code = null;
        var process = PlayProcess.Start(stub.Command(new StubPlan([], Sleep: 120)), ui, _ => { }, _ => { },
            (c, _) => code = c);
        stub.ProcessId(0);
        process.CloseInput();
        Assert.False(process.WaitForExit(TimeSpan.FromSeconds(1)));
        Assert.True(stub.Runs(0));

        process.Dispose();

        ui.RunUntil(() => code != null, "the exit of the stub after the job closed");
        Assert.False(stub.Runs(0));
    }

    /// <summary>
    /// (2) Review finding R6: a command to a game that ended gives false and never throws. The exit comes when Windows
    /// gives the exit code of the stub. Windows can close the input pipe of the stub after that, so a write to the pipe
    /// can still succeed. Send must ask the process, not the pipe (see
    /// <see cref="SendToAGameThatEndedReturnsFalseAlsoWhenItsInputTakesTheWrite"/>).
    /// </summary>
    [Fact]
    public void SendToAGameThatEndedReturnsFalse()
    {
        int? code = null;
        using var process = PlayProcess.Start(stub.Command(new StubPlan([], Exit: 0)), ui, _ => { }, _ => { },
            (c, _) => code = c);
        ui.RunUntil(() => code != null, "the exit of the stub");

        Assert.False(process.Send(new PlayCommand(PlayCommandKind.Start)));
        Assert.False(process.Send(new PlayCommand(PlayCommandKind.Stop)));
        process.CloseInput();
        Assert.False(process.Send(new PlayCommand(PlayCommandKind.Stop)));
        process.Kill();
        Assert.Equal(0, code);
    }

    /// <summary>
    /// (2) The race of review finding R6 on each run: the read end of the input stays open after the stub ends, so each
    /// write to the input succeeds. Windows can keep the input pipe of an ended game open in this way for a moment.
    /// Send must still give false, because the game ended.
    /// </summary>
    [Fact]
    public void SendToAGameThatEndedReturnsFalseAlsoWhenItsInputTakesTheWrite()
    {
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out);
        using var readEnd = new AnonymousPipeClientStream(PipeDirection.In, pipe.ClientSafePipeHandle);
        var input = new StreamWriter(pipe);
        int? code = null;
        using var process = PlayProcess.Start(stub.Command(new StubPlan([], Exit: 0)), input, ui, _ => { }, _ => { },
            (c, _) => code = c);
        ui.RunUntil(() => code != null, "the exit of the stub");

        // The input still takes a write after the exit.
        input.WriteLine("probe");
        input.Flush();

        Assert.False(process.Send(new PlayCommand(PlayCommandKind.Start)));
        Assert.False(process.Send(new PlayCommand(PlayCommandKind.Stop)));
        Assert.Equal(0, code);
    }

    [Fact]
    public void SendWritesOneLineForEachCommand()
    {
        int? code = null;
        using var process = PlayProcess.Start(stub.Command(new StubPlan([])), ui, _ => { }, _ => { },
            (c, _) => code = c);

        Assert.True(process.Send(new PlayCommand(PlayCommandKind.Reload, 4)));
        Assert.True(process.Send(new PlayCommand(PlayCommandKind.Start)));
        Assert.True(process.Send(new PlayCommand(PlayCommandKind.Stop)));

        ui.RunUntil(() => code != null, "the exit of the stub at stop");
        Assert.Equal(["reload 4", "start", "stop"], stub.Input(0));
    }

    /// <summary>
    /// (3) An event line becomes an event, and a warning or an error line of the engine log becomes a log line, both on
    /// the UI thread. The other lines are dropped. The exit comes last, with the first lines of the error output.
    /// </summary>
    [Fact]
    public void EventsAndEngineWarningsAndErrorsComeOnTheUIThreadBeforeTheExit()
    {
        var waiting = new PlayEvent.Waiting(2300);
        var spawned = new PlayEvent.Spawned("Br01", null, new Vector3(1, 2, 3), "ge_fighter", "Starflier", 450);
        string[] lines =
        [
            PlayProtocol.Write(waiting),
            "[Warning: 12:00:00] Ini: a warning",
            "[Info: 12:00:00] Game: info",
            "[Debug: 12:00:00] Game: debug",
            "    Channels       = 2",
            "@ol {\"event\":\"broken\"}",
            "[Error: 12:00:00] Ship: an error",
            PlayProtocol.Write(spawned),
            "!err Process terminated. Assertion failed.",
            "!err ",
            "!err    at A.B()",
            "!err second",
            "!err third",
        ];
        var testThread = Environment.CurrentManagedThreadId;
        var got = new List<object>();
        var threads = new List<int>();
        (int Code, string Errors)? exit = null;
        using var process = PlayProcess.Start(stub.Command(new StubPlan(lines, Exit: 7)), ui,
            e =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                got.Add(e);
            },
            line =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                got.Add(line);
            },
            (code, errors) =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                exit = (code, errors);
            });

        // Two events, two log lines and the exit. The stub ended, and nothing ran yet.
        ui.WaitForQueued(5);
        Assert.Empty(got);
        ui.RunUntil(() => exit != null, "the exit of the stub");

        Assert.Equal([waiting, "[Warning: 12:00:00] Ini: a warning", "[Error: 12:00:00] Ship: an error", spawned], got);
        Assert.All(threads, id => Assert.Equal(testThread, id));
        Assert.Equal((7, "Process terminated. Assertion failed. at A.B() second"), exit);
    }

    /// <summary>
    /// A game runs for a long time, and each read of its output blocks its thread. So threads of their own read the
    /// output and wait for the exit, not threads of the thread pool. On a busy computer the pool can have no free thread
    /// for many seconds, and an exit that waits for one comes late. CI failed the 30 s waits of these tests in this way.
    /// </summary>
    [Fact]
    public void TheOutputAndTheExitDoNotWaitForAThreadOfThePool()
    {
        string[] lines = [PlayProtocol.Write(new PlayEvent.Waiting(2300)), "[Warning: 12:00:00] Ini: a warning", "!err an error"];
        var events = 0;
        var logLines = 0;
        (int Code, string Errors)? exit = null;
        using var process = PlayProcess.Start(stub.Command(new StubPlan(lines, Exit: 4)), ui, _ => events++,
            _ => logLines++, (code, errors) => exit = (code, errors));
        ui.RunUntil(() => exit != null, "the exit of the stub");

        Assert.Equal((1, 1, (4, "an error")), (events, logLines, exit));
        Assert.Equal(0, ui.QueuedFromThreadPool);
    }

    [Fact]
    public void TheCommandStartsThePlayProgramNextToTheEditorWithTheArgumentsOfThePlay()
    {
        var args = new PlayArgs(@"C:\profiles\a b.json", "proof", 3, new PlayStart.AtBase("Br01_01_Base"), "le_package");

        var info = PlayProcess.Command(args);

        Assert.Equal(ProgramPaths.Play, info.FileName);
        Assert.Equal(args.ToArguments(), info.ArgumentList);
    }
}
