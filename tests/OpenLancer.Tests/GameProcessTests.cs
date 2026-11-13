using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenLancer.Editor.Play;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The process of a play copy (section 8.6 of the M11 design, decision D313) with a stub program. The stub is the stub
/// program of the tests in game mode (see <see cref="StubProgram"/>), without a window. It writes its working folder,
/// its arguments and its process id, then waits or exits. No test starts a game program or FLServer.
/// </summary>
public sealed class GameProcessTests : IAsyncDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "openlancer-tests", Guid.NewGuid().ToString("N"));
    private readonly QueuedUIThread ui = new();

    public GameProcessTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "EXE"));
    }

    /// <summary>Deletes the folder of the test. Windows can hold the working folder of an ended stub for a short
    /// time, so the delete tries again for up to 10 s.</summary>
    public async ValueTask DisposeAsync()
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Directory.Exists(folder))
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow > end)
                    return;
                await Task.Delay(100);
            }
        }
    }

    private string RecordPath => Path.Combine(folder, "record.txt");

    /// <summary>The start of the stub in the EXE folder of the test, without a window.</summary>
    private ProcessStartInfo Stub(bool wait, int exit = 0)
    {
        var info = new ProcessStartInfo(StubProgram.FilePath)
        {
            WorkingDirectory = Path.Combine(folder, "EXE"),
            CreateNoWindow = true,
            ArgumentList = { "game", "--record", RecordPath, "--exit", exit.ToString() },
        };
        if (wait)
            info.ArgumentList.Add("--wait");
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(TestServer.SettingsSwitch);
        return info;
    }

    /// <summary>The lines of the record of the stub, by their first word. It waits until the stub wrote them.</summary>
    private async Task<string> RecordAsync(string key)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                if (File.Exists(RecordPath) && File.ReadAllLines(RecordPath) is { Length: 3 } lines)
                    return lines.Single(l => l.StartsWith(key + " "))[(key.Length + 1)..];
            }
            catch (IOException)
            {
                // The stub writes the file now.
            }

            Assert.True(DateTime.UtcNow < end, "The stub did not write its record in 30 s.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static bool Runs(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task TheProgramStartsInItsFolderAndGivesItsExitOnTheUiThread()
    {
        int? code = null;

        using var process = GameProcess.Start(Stub(wait: false, exit: 3), ui, c => code = c);
        await ui.RunUntilAsync(() => code != null, "the exit of the stub");

        Assert.Equal(3, code);
        Assert.Equal(Path.Combine(folder, "EXE"), await RecordAsync("cwd"));
        Assert.Equal("/c", await RecordAsync("args"));
    }

    /// <summary>A game runs for a long time. So a thread of its own waits for the exit, not a thread of the thread pool.
    /// On a busy computer the pool can have no free thread for many seconds, and an exit that waits for one comes
    /// late.</summary>
    [Fact]
    public async Task TheExitDoesNotWaitForAThreadOfThePool()
    {
        int? code = null;

        using var process = GameProcess.Start(Stub(wait: false, exit: 5), ui, c => code = c);
        await ui.RunUntilAsync(() => code != null, "the exit of the stub");

        Assert.Equal(5, code);
        Assert.Equal(0, ui.QueuedFromThreadPool);
    }

    [Fact]
    public async Task KillEndsTheProgramAndAWindowlessProgramHasNoWindowToClose()
    {
        int? code = null;
        using var process = GameProcess.Start(Stub(wait: true), ui, c => code = c);
        var id = int.Parse(await RecordAsync("pid"));

        Assert.False(process.CloseMainWindow());
        process.Kill();
        await ui.RunUntilAsync(() => code != null, "the exit of the stub");

        Assert.False(Runs(id));
    }

    [Fact]
    public async Task TheEndOfTheEditorEndsTheProgram()
    {
        var process = GameProcess.Start(Stub(wait: true), ui, _ => { });
        var id = int.Parse(await RecordAsync("pid"));
        Assert.True(Runs(id));

        // The editor disposes the process when it closes. The job then ends the program.
        process.Dispose();

        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Runs(id))
        {
            Assert.True(DateTime.UtcNow < end, "The stub still runs 30 s after the end of its job.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
