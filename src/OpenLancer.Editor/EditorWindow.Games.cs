using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Logs;

namespace OpenLancer.Editor;

/// <summary>
/// The game link (milestone M11a): the linked game folders, the first-run screen, the Game link window, New profile and
/// a folder that the user drags to the window. The members of <see cref="IGameLinkHost"/> are explicit, so the public
/// surface of the window does not grow.
/// </summary>
internal sealed partial class EditorWindow : IGameLinkHost
{
    private readonly GameLinkWindow gameLinkWindow;
    private readonly NewProfileDialog newProfileDialog;

    // True when the start met the conditions of the first-run screen (decision D193).
    private bool firstRunAtStart;

    // The profile that did not open because its base folder is missing. A relink of its game opens it.
    private string? missingBaseProfile;

    /// <summary>The linked game folders and their checks.</summary>
    public GameLinks Links { get; }

    /// <summary>The missing base folder of the last profile that did not open, or null. The Welcome screen then offers
    /// the Game link window.</summary>
    public string? OpenErrorMissingBase { get; private set; }

    /// <summary>True while the first-run screen replaces the card of the Welcome page: the start met its conditions, the
    /// user did not skip it, and no game is linked.</summary>
    public bool ShowsFirstRun => firstRunAtStart && !welcome.FirstRun.Skipped && Links.Games.Count == 0;

    /// <summary>Shows the Game link window.</summary>
    public void ShowGameLink() => gameLinkWindow.Open();

    /// <summary>Shows the New profile dialog for one of the games.</summary>
    public void ShowNewProfile(IReadOnlyList<GameReport> games) => newProfileDialog.Show(games);

    /// <summary>The reports of the linked games that the editor can use as a base, in the order of the links.</summary>
    public IReadOnlyList<GameReport> LinkedGameReports() =>
        Links.Games.Select(g => Links.ReportOf(g.Path)).OfType<GameReport>().Where(r => r.IsGame).ToList();

    /// <summary>The linked game that is the base of a retail target, or null. The save gate merges the mission patches
    /// of the active mod on it (section 6.12 of the M6b design).</summary>
    private GameBase? TargetGameOf(string target) =>
        ExportTarget.Find(target) is { } found && found.BaseIn(LinkedGameReports()) is { Root: { } root }
            ? GameBase.Standard(root)
            : null;

    IReadOnlyList<string> IGameLinkHost.KnownProfiles()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return ProfileFiles(Settings.WorkspaceOrDefault).Concat(Settings.RecentProfiles.Select(r => r.Path))
            .Distinct(comparer).ToList();
    }

    string? IGameLinkHost.ProfileToReopen => Session?.Profile.FilePath ?? missingBaseProfile;

    void IGameLinkHost.Ask(string title, string message, IReadOnlyList<Choice> choices) =>
        Choice.Show(title, message, choices);

    /// <summary>A file or a folder that the user drags to the window. It goes to the UI thread, because the event can
    /// come from the event loop of the platform.</summary>
    protected override void OnDrop(string? file)
    {
        if (file != null && !options.Bench)
        {
            QueueUIThread(() => Dropped(file));
        }
    }

    /// <summary>
    /// A drop checks the folder: on the first-run screen it goes to the check of that screen. On a card of the Game link
    /// window it relinks that game. Else the Game link window opens and links the folder. A drop never opens or closes a
    /// profile.
    /// </summary>
    private void Dropped(string path)
    {
        if (DialogOpen)
        {
            Log.Add(ConsoleSeverity.Warning, GameLinks.Source, "Close the dialog. Then drag the folder again.");
            return;
        }

        if (Session == null && ShowsFirstRun)
        {
            welcome.FirstRun.Check(path);
            return;
        }

        if (gameLinkWindow.CardAt(ImGui.GetMousePos()) is { } root)
        {
            Links.Relink(root, path);
            return;
        }

        gameLinkWindow.Link(path);
    }
}
