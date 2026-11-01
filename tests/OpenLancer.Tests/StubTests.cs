using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The stub program of the process tests (<see cref="StubProgram"/>) is next to the tests and answers a plan. The
/// process tests need it. So a missing or broken stub fails here with a clear reason, and not only in a wait of a
/// process test.
/// </summary>
public sealed class StubTests : IDisposable
{
    private readonly TestWorkspace ws = new();

    public void Dispose() => ws.Dispose();

    [Fact]
    public async Task TheStubIsNextToTheTestsAndAnswersAPlan()
    {
        Assert.Equal(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(StubProgram.FilePath));
        Assert.True(File.Exists(StubProgram.FilePath), $"{StubProgram.FilePath} is not next to the tests.");
        var stub = new PlayStub(Path.Combine(ws.Root, "stub"));
        var info = stub.Command(new StubPlan(["a line", "!err an error"], ["started"]));
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.StandardInput.WriteLine("start");
        process.StandardInput.WriteLine("stop");
        process.StandardInput.Flush();
        stub.WaitForInput(0, 2);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(["a line", "started"], (await output).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("an error", (await errors).Trim());
        Assert.Equal(["start", "stop"], stub.Input(0));
        Assert.Equal(process.Id, stub.ProcessId(0));
    }
}
