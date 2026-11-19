using System.Globalization;
using System.IO;

namespace OpenLancer.Runtime.Play;

/// <summary>
/// The folder of the files that a play writes (decision D41): <c>&lt;profile folder&gt;\&lt;profile name&gt;.play\</c>, next
/// to the deploy folder of the profile. It holds the game config <c>lancer.ini</c>, the save folder <c>saves\</c> and
/// the capture folder <c>captures\</c>.
/// </summary>
public static class PlayFolder
{
    private const string Suffix = ".play";
    private const string ConfigName = "lancer.ini";
    private const string SavesName = "saves";
    private const string StartName = "start";
    private const string CapturesName = "captures";

    /// <param name="profilePath">The path of the profile file.</param>
    public static string Of(string profilePath)
    {
        var full = Path.GetFullPath(profilePath);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + Suffix);
    }

    /// <summary>The game config of the play. The in-game options write it.</summary>
    public static string ConfigPath(string profilePath) => Path.Combine(Of(profilePath), ConfigName);

    /// <summary>The save folder of the game server: the saves of the player, <c>AutoSave.fl</c> and
    /// <c>scripts\</c>.</summary>
    public static string SavesPath(string profilePath) => Path.Combine(Of(profilePath), SavesName);

    /// <summary>The start save of a mission play (section 5.6 of the M6c design): <c>start\start.fl</c> in the play
    /// folder.</summary>
    public static string StartSavePath(string profilePath) => Path.Combine(Of(profilePath), StartName, "start.fl");

    /// <summary>The test layer of a mission play (section 5.4 of the M6c design): <c>start\layer</c> in the play folder.
    /// The play loads it after the mods of the play.</summary>
    public static string TestLayerPath(string profilePath) => Path.Combine(Of(profilePath), StartName, "layer");

    /// <summary>The folder of the images of the agent tool <c>play_capture</c>: <c>captures\</c> in the play folder. The
    /// game writes each image there, and the editor reads it from there only and deletes it after the read.</summary>
    public static string CapturesPath(string profilePath) => Path.Combine(Of(profilePath), CapturesName);

    /// <summary>The name of the PNG file of a capture request in <see cref="CapturesPath"/>, for example
    /// <c>capture-3.png</c>.</summary>
    /// <param name="request">The number of the request (<see cref="PlayCommand.Request"/>).</param>
    public static string CaptureName(int request) => $"capture-{request.ToString(CultureInfo.InvariantCulture)}.png";

    /// <summary>
    /// Makes the play folder and its save folder when they do not exist. Call it before the game config and the
    /// server use them, because neither makes its folder, and a write error on the server thread ends the process.
    /// </summary>
    public static void Create(string profilePath) => Directory.CreateDirectory(SavesPath(profilePath));
}
