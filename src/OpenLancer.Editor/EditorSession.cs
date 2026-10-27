using System;
using System.Collections.Generic;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Resources;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Mods;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor;

/// <summary>
/// One open profile: its edit session, its resources, and the engine data that loads from the edit view: the base
/// data with the mods up to the active mod (decision D2).
/// </summary>
internal sealed class EditorSession : IDisposable
{
    private readonly Dictionary<int, string> infoTexts = [];

    /// <summary>Why the edits of the active mod need the allowance of the user, or null (see
    /// <see cref="RepoMods"/>).</summary>
    private readonly string? repoReason;

    /// <param name="repoRoot">The OpenLancer repo that holds the active mod, or null (see <see cref="RepoCheck"/>).</param>
    /// <param name="ui">The UI thread, which ends each load of the engine data (see <see cref="Loads"/>).</param>
    public EditorSession(EditSession edits, GameResourceManager resources, string? repoRoot, IUIThread ui)
    {
        Edits = edits;
        Resources = resources;
        RepoRoot = repoRoot;
        repoReason = repoRoot != null && edits.ActiveMod is { } mod ? RepoMods.Reason(mod) : null;
        Loads = new DataLoads(ui);
        Texts = new ItemTexts(edits, Name, InfoText);
        edits.Changed += _ => Touch();
    }

    public EditSession Edits { get; }

    /// <summary>The names and other texts of items, as the edit view gives them.</summary>
    public ItemTexts Texts { get; }

    public ModProfile Profile => Edits.Profile;

    public GameResourceManager Resources { get; }

    /// <summary>The engine data. It is null until the background load ends, and stays null when it fails.</summary>
    public GameDataManager? GameData { get; private set; }

    /// <summary>
    /// The loads of the engine data: the first load and a reload after each save that changed files. Each load uses
    /// <see cref="Resources"/>, so one load runs at a time, and the session must not be disposed before a load ends.
    /// </summary>
    public DataLoads Loads { get; }

    /// <summary>True while the first load of the engine data runs in the background.</summary>
    public bool IsLoading => Loads.IsLoading;

    /// <summary>True while the engine data loads again after a save.</summary>
    public bool IsReloading => Loads.IsReloading;

    /// <summary>True while a load or a reload runs. The profile cannot close and no model loads until it ends.</summary>
    public bool IsBusy => Loads.IsBusy;

    /// <summary>A number that changes each time the edit view or the text of the active mod changes.</summary>
    public int Version { get; private set; }

    /// <summary>The number of saves of this session that changed files.</summary>
    public int Saves { get; private set; }

    /// <summary>The OpenLancer repo that holds the active mod, or null.</summary>
    public string? RepoRoot { get; }

    /// <summary>True after the user allowed the edits of a mod in the OpenLancer repo for this session (decision
    /// D5).</summary>
    public bool RepoEditsAllowed { get; set; }

    /// <summary>True when the active mod is in the OpenLancer repo and the user did not allow its edits for this
    /// session yet.</summary>
    public bool NeedsRepoConfirmation => repoReason != null && !RepoEditsAllowed;

    /// <summary>The reason of <see cref="RepoMods.Reason"/> while the user did not allow the edits of the active mod
    /// for this session, else null. The caller adds where the user allows the edits.</summary>
    public string? PendingRepoReason => NeedsRepoConfirmation ? repoReason : null;

    /// <summary>True when the recovery file is older than the edits (decision D8).</summary>
    public bool RecoveryOutdated { get; private set; }

    /// <summary>The time of the last edit, undo or redo.</summary>
    public DateTime LastChange { get; private set; }

    /// <summary>Why the editor takes no edits now, or null.</summary>
    public string? NoEditReason =>
        Edits.ReadOnlyReason ?? (PendingRepoReason is { } repo ? repo + " Allow its edits for this session first." : null);

    public bool CanEdit => NoEditReason == null;

    /// <summary>Notes a change of the edit view or the text.</summary>
    public void Touch()
    {
        Version++;
        RecoveryOutdated = true;
        LastChange = DateTime.UtcNow;
    }

    /// <summary>Notes that the recovery file now has the edits, or that no recovery file is needed.</summary>
    public void RecoveryWritten() => RecoveryOutdated = false;

    /// <summary>
    /// Notes a save. The save makes W again from the saved files, so the views of the edit view change. Returns the
    /// number of the save, or 0 when it changed no file.
    /// </summary>
    public int Saved(bool changedFiles)
    {
        Version++;
        RecoveryOutdated = false;
        return changedFiles ? ++Saves : 0;
    }

    /// <summary>Keeps the data of the first load. Call it on the UI thread when the load ends, with the data, or with
    /// null when the load failed.</summary>
    public void LoadEnded(GameDataManager? data) => GameData = data;

    /// <summary>Keeps the data of a reload. Call it on the UI thread when the reload ends. A null result keeps the old
    /// data.</summary>
    public void ReloadEnded(GameDataManager? data)
    {
        if (data != null)
        {
            GameData = data;
            infoTexts.Clear();
        }
    }

    /// <summary>
    /// The text of a string resource, or an empty string when the id is not above 0 or is unknown. A text with only
    /// white space counts as no text, so that the item shows its nickname.
    /// </summary>
    public string Name(int ids)
    {
        var text = ids <= 0 || GameData == null ? null : GameData.Items.Ini.Infocards.GetStringResource(ids);
        return string.IsNullOrWhiteSpace(text) ? "" : text;
    }

    /// <summary>
    /// The plain text of an infocard, or an empty string when the id is not above 0 or is unknown. The infocard XML is
    /// parsed once for each id.
    /// </summary>
    public string InfoText(int ids)
    {
        if (ids <= 0 || GameData == null)
        {
            return "";
        }

        if (!infoTexts.TryGetValue(ids, out var text))
        {
            var xml = GameData.Items.Ini.Infocards.GetXmlResource(ids);
            text = xml == null ? "" : ItemTexts.PlainText(xml);
            infoTexts[ids] = text;
        }

        return text;
    }

    /// <summary>
    /// The display name of an item, or an empty string when it has no name. The name of a scene item, a system or a
    /// section comes from W (see <see cref="ItemTexts"/>).
    /// </summary>
    public string ItemName(object? item) => Texts.NameOf(item);

    /// <summary>The name of a system of the game data, or an empty string for a system without a name. Null when the
    /// game data has no system with the nickname.</summary>
    public string? SystemName(string nickname) =>
        GameData?.Items.Systems.Get(nickname) is { } system ? ItemName(system) : null;

    /// <summary>The name of a system of the game data, or its nickname when it has no name or the game data does not
    /// have it.</summary>
    public string SystemLabel(string nickname) => SystemName(nickname) is { Length: > 0 } name ? name : nickname;

    /// <summary>The text of a text key of an item, for example <c>tradelane_space_name</c>, or an empty string (see
    /// <see cref="ItemTexts"/>).</summary>
    public string TextOf(object? item, string key) => Texts.TextOf(item, key);

    /// <summary>
    /// The name of a section of the edit view: the text of its <c>ids_name</c>, from the text of the active mod or from
    /// the game data. An empty string when it has no name.
    /// </summary>
    public string SectionName(SectionRef section) => Texts.NameOf(section);

    /// <summary>The name of a section: the text of its <c>ids_name</c>, or an empty string.</summary>
    public string NameOf(SectionView view) => Texts.NameOf(view);

    /// <summary>Frees the resources and releases the lock of the active mod (see <see cref="EditSession"/>).</summary>
    public void Dispose()
    {
        Resources.Dispose();
        Edits.Dispose();
    }
}
