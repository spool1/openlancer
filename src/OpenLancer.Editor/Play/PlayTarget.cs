using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;

namespace OpenLancer.Editor.Play;

/// <summary>
/// A linked game that Play in the game can start (section 8.6 of the M11 design). It has a game program, and its report
/// gives a retail target. HD Edition 0.7.1, Freelancer 1.0 and 1.1, and a source build with the retail INI files are
/// play targets.
/// </summary>
internal sealed record PlayTarget(GameReport Game)
{
    /// <summary>The game root.</summary>
    public string Root => Game.Root!;

    /// <summary>The name in the menu and the status bar, for example "HD Edition 0.7.1" or "source build".</summary>
    public string Name => Game.Kind == GameKind.SourceBuild ? "source build" : Game.ShortName;

    /// <summary>The Play cell of Freelancer 1.0 in the README table "Supported games".</summary>
    public const string PatchFirst = "Patch the game to 1.1 first. The 1.0 program does not start on Windows 10 and 11.";

    /// <summary>What the user must know before Play in the game starts this game, or null. The program of Freelancer
    /// 1.0 does not start on Windows 10 and 11, so its note is the Play cell of 1.0 in the README.</summary>
    public string? Note => Game is { Kind: GameKind.Vanilla, Version: "1.0" } ? PatchFirst : null;

    /// <summary>True when the program folder of the game has <c>FLServer.exe</c>, so Host test server can run.</summary>
    public bool HasServer =>
        File.Exists(Path.Combine(Root, Game.Layout == GameLayout.SourceBuild ? "bin" : "EXE", TestServer.ProgramName));

    /// <summary>The play targets of the checks of the linked games, in the order of the links.</summary>
    public static IReadOnlyList<PlayTarget> Of(IEnumerable<GameReport> games) =>
        games.Where(g => g is { IsGame: true, Root: not null, ExePath: not null } &&
                         ExportTarget.Find(g.Target ?? "") is { IsRetail: true })
            .Select(g => new PlayTarget(g)).ToList();
}
