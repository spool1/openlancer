using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>What one start of the stub does.</summary>
/// <param name="Lines">The lines that the stub writes at its start. A line that starts with <c>!err </c> goes to the
/// error output without the prefix.</param>
/// <param name="StartLines">The lines that the stub writes when it reads <c>start</c>.</param>
/// <param name="Exit">An exit code. The stub then exits after its lines and reads no input.</param>
/// <param name="IgnoreStop">True: the stub does not end at <c>stop</c>, only at the end of its input.</param>
/// <param name="Sleep">Seconds that the stub waits before it ends. It writes nothing and reads no input.</param>
/// <param name="ReloadLines">The lines that the stub writes when it reads <c>reload n</c>: the first line at the first
/// reload, the second line at the second reload, and the last line at each later reload. A line can hold more lines,
/// each after <see cref="NextLine"/>. Without them, the stub does not answer a reload.</param>
/// <param name="RestartLines">The lines that the stub writes when it reads <c>restart n</c> (section 5.8 of the M6c
/// design), in the same way as <paramref name="ReloadLines"/>. Without them, the stub does not answer a
/// restart.</param>
internal sealed record StubPlan(
    string[] Lines,
    string[]? StartLines = null,
    int? Exit = null,
    bool IgnoreStop = false,
    int Sleep = 0,
    string[]? ReloadLines = null,
    string[]? RestartLines = null)
{
    /// <summary>The character between two lines of one reload in <see cref="ReloadLines"/>.</summary>
    public const char NextLine = '\u001f';
}

/// <summary>
/// A stub of the play program (section 16.1 of the M4 design): the stub program in play mode (see
/// <see cref="StubProgram"/>). It writes its process id and each input line to a record file, and <c>&lt;eof&gt;</c> at
/// the end of its input. It answers <c>start</c>, each <c>reload n</c> and each <c>restart n</c> with the lines of its
/// plan. It ends at <c>stop</c> or at the end of its input.
/// </summary>
internal sealed class PlayStub
{
    private readonly string folder;
    private readonly List<string> records = [];

    public PlayStub(string folder)
    {
        this.folder = folder;
        Directory.CreateDirectory(folder);
    }

    /// <summary>The number of starts of the stub.</summary>
    public int Starts => records.Count;

    /// <summary>The program and the arguments of one start of the stub.</summary>
    public ProcessStartInfo Command(StubPlan plan)
    {
        var n = records.Count;
        var record = Path.Combine(folder, $"record{n}.txt");
        records.Add(record);
        var info = new ProcessStartInfo(StubProgram.FilePath) { ArgumentList = { "play", "--record", record } };
        if (plan.Lines.Length > 0)
        {
            info.ArgumentList.Add("--lines");
            info.ArgumentList.Add(LinesFile($"lines{n}.txt", plan.Lines));
        }

        if (plan.StartLines is { Length: > 0 } startLines)
        {
            info.ArgumentList.Add("--start-lines");
            info.ArgumentList.Add(LinesFile($"start{n}.txt", startLines));
        }

        if (plan.Exit is { } exit)
        {
            info.ArgumentList.Add("--exit");
            info.ArgumentList.Add(exit.ToString());
        }

        if (plan.IgnoreStop)
        {
            info.ArgumentList.Add("--ignore-stop");
        }

        if (plan.Sleep > 0)
        {
            info.ArgumentList.Add("--sleep");
            info.ArgumentList.Add(plan.Sleep.ToString());
        }

        if (plan.ReloadLines is { Length: > 0 } reloadLines)
        {
            info.ArgumentList.Add("--reload-lines");
            info.ArgumentList.Add(LinesFile($"reload{n}.txt", reloadLines));
        }

        if (plan.RestartLines is { Length: > 0 } restartLines)
        {
            info.ArgumentList.Add("--restart-lines");
            info.ArgumentList.Add(LinesFile($"restart{n}.txt", restartLines));
        }

        return info;
    }

    /// <summary>The input lines that a start of the stub read, and <c>&lt;eof&gt;</c> at the end of its input.</summary>
    public IReadOnlyList<string> Input(int start) => Read(start).Where(l => !l.StartsWith("pid ")).ToList();

    /// <summary>Waits until a start of the stub read the count of input lines.</summary>
    public void WaitForInput(int start, int count)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Input(start).Count < count)
        {
            Assert.True(DateTime.UtcNow < end, $"The stub did not read {count} input lines in 30 s. It read " +
                                               $"[{string.Join(", ", Input(start))}], and it runs: {Runs(start)}.");
            Thread.Sleep(20);
        }
    }

    /// <summary>The process id of a start of the stub. It waits until the stub wrote it.</summary>
    public int ProcessId(int start)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (Read(start).FirstOrDefault(l => l.StartsWith("pid ")) is { } line)
            {
                return int.Parse(line["pid ".Length..]);
            }

            Assert.True(DateTime.UtcNow < end, "The stub did not start in 30 s.");
            Thread.Sleep(20);
        }
    }

    /// <summary>True while the process of a start of the stub runs.</summary>
    public bool Runs(int start)
    {
        try
        {
            using var process = Process.GetProcessById(ProcessId(start));
            return IsStub(process, start);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Kills the processes of the stub that still run, after a test that failed. The tests started them.</summary>
    public void KillAll()
    {
        for (var start = 0; start < records.Count; start++)
        {
            if (!File.Exists(records[start]))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(ProcessId(start));
                if (IsStub(process, start))
                {
                    process.Kill();
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // It ended now.
            }
        }
    }

    /// <summary>
    /// True when the process is the running stub of the start. Windows soon gives the id of an ended process to a new
    /// process, also to a stub of a test that runs in parallel. The stub started before it wrote its record file, and
    /// a process that got its id later started after that. So the start time tells the two apart.
    /// </summary>
    private bool IsStub(Process process, int start) =>
        !process.HasExited && process.StartTime.ToUniversalTime() <= File.GetCreationTimeUtc(records[start]);

    private string[] Read(int start)
    {
        try
        {
            using var stream = new FileStream(records[start], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))
                .ToArray();
        }
        catch (IOException)
        {
            // The stub did not write the file yet, or it writes the file now.
            return [];
        }
    }

    private string LinesFile(string name, string[] lines)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllLines(path, lines);
        return path;
    }
}
