using OpenLancer.Data.Editing;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Tests;

/// <summary>The editor for a <see cref="CommandRunner"/> without a window: a session, the reason why the editor takes no
/// edits, and a Console.</summary>
internal sealed class FakeCommandHost(EditSession? session, string? noEditReason = null) : ICommandHost
{
    public EditSession? Session { get; set; } = session;

    public string? NoEditReason { get; set; } = noEditReason;

    public ConsoleLog Log { get; } = new();

    /// <summary>A runner over a session.</summary>
    public static CommandRunner Runner(EditSession? session, string? noEditReason = null) =>
        new(new FakeCommandHost(session, noEditReason));
}
