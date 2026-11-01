using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace OpenLancer.TestStub;

/// <summary>
/// A stub of the programs that the editor starts. The process tests of OpenLancer.Tests start it in place of a game.
/// The first argument selects the mode.
/// </summary>
/// <remarks>
/// <para><c>play --record &lt;file&gt;</c> is a stub of the play program (PlayStub in OpenLancer.Tests). It writes its
/// process id and each input line to the record, and <c>&lt;eof&gt;</c> at the end of its input. It writes the lines of
/// <c>--lines</c> at its start, and the lines of <c>--start-lines</c> when it reads <c>start</c>. A line that starts
/// with <c>!err </c> goes to the error output without the prefix. It answers each <c>reload n</c> and each
/// <c>restart n</c> with the lines of <c>--reload-lines</c> and <c>--restart-lines</c>: the first line at the first
/// command, the second line at the second command, and the last line at each later command. The character U+001F
/// separates the output lines of one answer. It ends at <c>stop</c> or at the end of its input. <c>--ignore-stop</c>
/// makes it end only at the end of its input. <c>--exit &lt;code&gt;</c> makes it exit after its start lines, and
/// <c>--sleep &lt;seconds&gt;</c> makes it wait and exit with no output.</para>
/// <para><c>game --record &lt;file&gt; [--exit &lt;code&gt;] [--wait] -- &lt;arguments&gt;</c> is a stub of a program
/// of a play copy (GameProcessTests). It writes its working folder, the arguments after <c>--</c> and its process id to
/// the record in one step. Then it waits 60 s when it has <c>--wait</c>, and exits with the code.</para>
/// </remarks>
internal static class Program
{
    private const char NextLine = '\u001f';

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("play" or "game"))
        {
            Console.Error.WriteLine("Use: OpenLancer.TestStub play|game --record <file> [options]");
            return 2;
        }

        var options = Options.Read(args);
        return args[0] == "play" ? Play(options) : Game(options);
    }

    private static int Play(Options options)
    {
        var record = options.Record;
        WriteRecord(record, $"pid {Environment.ProcessId}");
        if (options.Number("--sleep") is { } sleep and > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(sleep));
            return 0;
        }

        WriteLines(options.Lines("--lines"));
        if (options.Number("--exit") is { } exit)
        {
            return exit;
        }

        var startLines = options.Lines("--start-lines");
        var reloads = new Answers(options.Lines("--reload-lines"));
        var restarts = new Answers(options.Lines("--restart-lines"));
        var ignoreStop = options.Has("--ignore-stop");
        while (Console.In.ReadLine() is { } line)
        {
            WriteRecord(record, line);
            if (line == "start")
            {
                WriteLines(startLines);
            }
            else if (line.StartsWith("reload ", StringComparison.Ordinal))
            {
                WriteLines(reloads.Next());
            }
            else if (line.StartsWith("restart ", StringComparison.Ordinal))
            {
                WriteLines(restarts.Next());
            }
            else if (line == "stop" && !ignoreStop)
            {
                return 0;
            }
        }

        WriteRecord(record, "<eof>");
        return 0;
    }

    private static int Game(Options options)
    {
        // The test reads the record when it has three lines. A move of a full file gives it no half record.
        var text = $"cwd {Directory.GetCurrentDirectory()}\nargs {string.Join(' ', options.Rest)}\npid {Environment.ProcessId}";
        var part = options.Record + ".part";
        File.WriteAllText(part, text, Utf8);
        File.Move(part, options.Record);
        if (options.Has("--wait"))
        {
            Thread.Sleep(TimeSpan.FromSeconds(60));
        }

        return options.Number("--exit") ?? 0;
    }

    /// <summary>Adds a line to the record. The test reads the record while the stub writes it, so both share the
    /// file.</summary>
    private static void WriteRecord(string record, string line)
    {
        using var stream = new FileStream(record, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Utf8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteLines(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("!err ", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(line["!err ".Length..]);
            }
            else
            {
                Console.Out.WriteLine(line);
            }
        }

        Console.Out.Flush();
        Console.Error.Flush();
    }

    /// <summary>The answers to a command that comes more than one time: the first line at the first command, the
    /// second line at the second command, and the last line at each later command. No lines give no answer.</summary>
    private sealed class Answers(string[] lines)
    {
        private int count;

        public string[] Next() =>
            lines.Length == 0 ? [] : lines[Math.Min(count++, lines.Length - 1)].Split(NextLine);
    }

    /// <summary>The options after the mode. A switch has no value. The arguments after <c>--</c> are the rest.</summary>
    private sealed class Options
    {
        private static readonly HashSet<string> Switches = ["--ignore-stop", "--wait"];

        private readonly Dictionary<string, string> values = [];
        private readonly HashSet<string> switches = [];

        public List<string> Rest { get; } = [];

        public string Record => values.TryGetValue("--record", out var record)
            ? record
            : throw new ArgumentException("The stub needs --record <file>.");

        public static Options Read(string[] args)
        {
            var options = new Options();
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--")
                {
                    options.Rest.AddRange(args[(i + 1)..]);
                    break;
                }

                if (Switches.Contains(args[i]))
                {
                    options.switches.Add(args[i]);
                }
                else if (i + 1 < args.Length)
                {
                    options.values[args[i]] = args[++i];
                }
                else
                {
                    throw new ArgumentException($"The option {args[i]} has no value.");
                }
            }

            return options;
        }

        public bool Has(string name) => switches.Contains(name);

        public int? Number(string name) => values.TryGetValue(name, out var value) ? int.Parse(value) : null;

        /// <summary>The lines of the file of the option, or no lines without the option.</summary>
        public string[] Lines(string name) => values.TryGetValue(name, out var path) ? File.ReadAllLines(path) : [];
    }
}
