using System;
using System.IO;

namespace OpenLancer.Tests;

/// <summary>
/// The stub program of the process tests (the project OpenLancer.TestStub). The test project references it, so the
/// build puts it next to the tests. The editor build puts the play program next to the editor in the same way. Its doc
/// comment tells the modes and the options.
/// </summary>
/// <remarks>The tests used PowerShell scripts before. On a busy computer, PowerShell can need many seconds before the
/// first line of a script runs. A compiled stub starts fast.</remarks>
internal static class StubProgram
{
    /// <summary>The file of the stub program next to the tests.</summary>
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "OpenLancer.TestStub.exe" : "OpenLancer.TestStub");
}
