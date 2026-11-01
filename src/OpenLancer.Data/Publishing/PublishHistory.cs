using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using OpenLancer.Data.Export;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Publishing;

/// <summary>A package of an export in the history.</summary>
/// <param name="Target">The target id, for example <c>hde-0.7.1</c>.</param>
/// <param name="Form">The form: <c>dropin</c>, <c>flmm</c> or <c>modfolder</c> (see <see cref="PackageForms"/>).</param>
/// <param name="File">The file name of the package in the folder of the export.</param>
/// <param name="Sha256">The SHA-256 of the package file.</param>
/// <param name="GameFiles">The guard digest of the package (decision D589).</param>
public sealed record HistoryPackage(string Target, string Form, string File, string Sha256, string GameFiles);

/// <summary>An export in the history.</summary>
/// <param name="OpenLancer">The informational version of OpenLancer that made the export.</param>
/// <param name="Content">The content fingerprint of the mod at the export.</param>
/// <param name="Page">The page fingerprint of the mod at the export.</param>
/// <param name="Folder">The full path of the output folder.</param>
public sealed record HistoryExport(string Version, DateTime Time, string OpenLancer, string Content, string Page,
    string Folder, IReadOnlyList<HistoryPackage> Packages)
{
    /// <summary>The time of the export that replaced this export, or null.</summary>
    public DateTime? ReplacedBy { get; init; }
}

/// <summary>The mark "published" of a version.</summary>
/// <param name="Page">The link of the Nexus page, or empty.</param>
public sealed record HistoryMark(string Version, DateTime Time, string Page);

/// <summary>A test play in the real game (decision D569).</summary>
/// <param name="Target">The target of the game, or null for a manual mark.</param>
/// <param name="Alone">True when the play installed this mod alone.</param>
/// <param name="Manual">True for "Mark as tested".</param>
public sealed record HistoryPlay(DateTime Time, string? Target, string Content, bool Alone, int Seconds, bool Manual);

/// <summary>The last check of a target (decision D572).</summary>
/// <param name="Base">The base of the check: <c>&lt;root&gt;|&lt;build&gt;</c>, or empty for the target
/// OpenLancer.</param>
public sealed record HistoryCheck(string Target, DateTime Time, string Content, string Base, int Errors, int Warnings);

/// <summary>Where a history lives and which mod it belongs to.</summary>
/// <param name="Folder">The folder <c>&lt;workspace&gt;\exports\&lt;mod id&gt;</c>.</param>
/// <param name="GameRoots">The game folders that the history folder must not overlap.</param>
public sealed record HistoryLocation(string Folder, string ModId, string ModFolder, IReadOnlyCollection<string> GameRoots);

/// <summary>The result of a write of the history.</summary>
/// <param name="Messages">The warnings for the Console: a busy lock, a bad file that moved, a refused folder.</param>
public sealed record HistoryWrite(bool Written, IReadOnlyList<string> Messages);

/// <summary>How a read of the history file ended.</summary>
public enum HistoryLoadState
{
    /// <summary>The file loaded, or it does not exist and the history is empty.</summary>
    Loaded,

    /// <summary>The file does not parse, or a record in it is not complete. The next record of a play, a check or a
    /// mark renames the file.</summary>
    Unreadable,

    /// <summary>The file has another format version. OpenLancer does not write it.</summary>
    OtherVersion,
}

/// <summary>The result of a read of the history.</summary>
/// <param name="History">The history. It is empty when the file does not load.</param>
/// <param name="Problem">The problem of the read, or null.</param>
public sealed record HistoryLoad(PublishHistory History, string? Problem)
{
    public HistoryLoadState State { get; init; }

    /// <summary>The full path of the history file.</summary>
    public string Path { get; init; } = "";

    /// <summary>The reason why the same-version guard cannot run, or null. Without the history, the guard cannot
    /// protect a published version, so an export stops with this reason (decision D568).</summary>
    public string? GuardRefusal => State switch
    {
        HistoryLoadState.Unreadable => PublishMessages.GuardUnreadable(Path),
        HistoryLoadState.OtherVersion => PublishMessages.GuardOtherVersion(Path, History.FormatVersion),
        _ => null,
    };
}

/// <summary>The case of the same-version guard (decision D568).</summary>
public enum GuardKind
{
    /// <summary>No conflict. The export can write.</summary>
    None,

    /// <summary>The version has the mark "published". Only "Export as next" or Cancel.</summary>
    Published,

    /// <summary>The version was exported with other game files.</summary>
    Exported,
}

/// <summary>The result of the same-version guard.</summary>
/// <param name="Earlier">The newest conflicting export, or null.</param>
/// <param name="Missing">The targets and forms of the conflicting exports that the new export does not have, for
/// example "HD Edition 0.7.1 (FLMM package)". Replace needs an empty list.</param>
public sealed record GuardResult(GuardKind Kind, string Version, HistoryExport? Earlier, IReadOnlyList<string> Missing)
{
    public static GuardResult None(string version) => new(GuardKind.None, version, null, []);

    /// <summary>True when "Replace version" is available.</summary>
    public bool CanReplace => Kind == GuardKind.Exported && Missing.Count == 0;

    /// <summary>The title of the question.</summary>
    public string Title => Kind == GuardKind.Published ? $"Version {Version} is on Nexus" : $"Version {Version} exists";

    /// <summary>The text of the question, or null without a conflict.</summary>
    public string? Text => Kind switch
    {
        GuardKind.Published => PublishMessages.GuardPublished(Version),
        GuardKind.Exported => PublishMessages.GuardExported(Version, Earlier!.Time),
        _ => null,
    };

    /// <summary>The tooltip of the disabled Replace button, or null when Replace is available.</summary>
    public string? PartialText => Kind == GuardKind.Exported && Missing.Count > 0
        ? PublishMessages.GuardPartialReplace(Version, Earlier!.Time, string.Join(", ", Missing))
        : null;
}

/// <summary>What a record of an export did (decision D590).</summary>
public enum RecordKind
{
    /// <summary>The history got a new entry.</summary>
    Added,

    /// <summary>The packages are the same as in an earlier export of the version. The history did not change.</summary>
    Same,

    /// <summary>Only the texts changed. The history got a new entry, and the old entry got <c>replacedBy</c>.</summary>
    TextsOnly,

    /// <summary>The user replaced the version. The old entry got <c>replacedBy</c>.</summary>
    Replaced,

    /// <summary>The packages are the same as in an earlier export of the version, but the content or the page
    /// fingerprint changed, for example a tag of the Nexus page. The history got a new entry, and the old entry got
    /// <c>replacedBy</c>.</summary>
    PageOnly,
}

/// <summary>The result of a record of an export.</summary>
/// <param name="Earlier">The earlier export that the record compared with, or null.</param>
/// <param name="Published">True when the version has the mark "published".</param>
public sealed record RecordResult(RecordKind Kind, HistoryExport? Earlier, bool Published)
{
    /// <summary>The Console lines of the result: the texts of decision D590.</summary>
    public IEnumerable<string> Messages(string version)
    {
        if (Kind is RecordKind.Same or RecordKind.PageOnly)
            yield return PublishMessages.SamePackages(version, Earlier!.Time);
        if (Kind == RecordKind.TextsOnly)
        {
            yield return PublishMessages.TextsOnly(version, Earlier!.Time);
            if (Published)
                yield return PublishMessages.TextsOnlyPublished(version);
        }
    }
}

/// <summary>The state of the files of an export in the history.</summary>
public enum ExportFilesState
{
    InPlace,
    NotFound,
    OtherFiles,
    Replaced,
}

/// <summary>The names of the package forms in the history.</summary>
public static class PackageForms
{
    public const string DropIn = "dropin";
    public const string Flmm = "flmm";
    public const string ModFolder = "modfolder";

    public static string Of(PackageForm form) => form switch
    {
        PackageForm.DropIn => DropIn,
        PackageForm.Flmm => Flmm,
        _ => ModFolder,
    };

    /// <summary>The name of a form for a person, for example "drop-in zip".</summary>
    public static string Name(string form) => form switch
    {
        DropIn => "drop-in zip",
        Flmm => "FLMM package",
        _ => "mod folder zip",
    };
}

/// <summary>
/// The publish history of a mod (decision D565 and section 8.7 of the M11c design):
/// <c>&lt;workspace&gt;\exports\&lt;mod id&gt;\publish-history.json</c>. It records each export with its version,
/// targets, forms, file names and hashes, the test plays, the last checks, and the versions with the mark "published".
/// </summary>
/// <remarks>
/// <para>The file is local and never in the mod folder. Each read-change-write holds
/// <c>publish-history.lock</c>, so that the editor and <c>oltool</c> do not lose an entry of the other (finding R25).
/// A file that does not load moves to <c>publish-history.bad.json</c> at the next write, or to a numbered name when
/// that file exists. An export does not write while the file does not load, because the same-version guard needs the
/// history.</para>
/// <para>The history is the one place for the time of an export. No package and no text of an export holds a
/// date.</para>
/// </remarks>
public sealed class PublishHistory
{
    public const string FileName = "publish-history.json";
    public const string LockName = "publish-history.lock";
    public const string BadName = "publish-history.bad.json";
    public const int CurrentFormatVersion = 1;

    /// <summary>The number of plays that the history keeps.</summary>
    public const int MaxPlays = 50;

    /// <summary>The time that a write waits for the lock of another program.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Keep the '+' of the OpenLancer version and the characters of the paths as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // A record without a field, or with null in a field that must have a value, does not load. The guard then
        // stops the export, and does not compare with a part of the history.
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string ModId { get; set; } = "";

    /// <summary>The full path of the mod folder of the first record. Two mods with the same id in two folders share
    /// one history, and the window warns.</summary>
    public string ModFolder { get; set; } = "";

    public List<HistoryExport> Exports { get; set; } = [];

    public List<HistoryMark> Published { get; set; } = [];

    public List<HistoryPlay> Plays { get; set; } = [];

    public List<HistoryCheck> Checks { get; set; } = [];

    /// <summary>The informational version of OpenLancer, with the commit when the build has it.</summary>
    public static string OpenLancerVersion => BuildInfo.Informational;

    /// <summary>The history folder of a mod: <c>&lt;workspace&gt;\exports\&lt;mod id&gt;</c>.</summary>
    public static string FolderOf(string workspace, string modId) =>
        Path.Combine(Path.GetFullPath(workspace), "exports", modId);

    /// <summary>Reads the history of a folder without a write. A missing file gives an empty history. A file that does
    /// not load gives an empty history, a problem and its state.</summary>
    public static HistoryLoad Load(string folder, string modId)
    {
        var path = Path.GetFullPath(Path.Combine(folder, FileName));
        if (!File.Exists(path))
            return new HistoryLoad(new PublishHistory { ModId = modId }, null) { Path = path };
        try
        {
            var bytes = SharedFile.ReadAllBytes(path);
            // Read the format version first: a file of another version can have other records.
            if (FormatVersionOf(bytes) is { } version && version != CurrentFormatVersion)
                return new HistoryLoad(new PublishHistory { ModId = modId, FormatVersion = version },
                    PublishMessages.HistoryVersion(path, version)) { State = HistoryLoadState.OtherVersion, Path = path };
            if (JsonSerializer.Deserialize<PublishHistory>(bytes, JsonOptions) is { } history && history.IsComplete())
                return new HistoryLoad(history, null) { Path = path };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The file does not load.
        }
        return new HistoryLoad(new PublishHistory { ModId = modId }, PublishMessages.HistoryUnreadable(path))
        {
            State = HistoryLoadState.Unreadable,
            Path = path,
        };
    }

    /// <summary>
    /// Changes the history under its lock: reads the file, runs the change and writes the file. The write refuses a
    /// folder in a game folder or in the mod folder. A held lock gives no record after <paramref name="wait"/>.
    /// </summary>
    /// <param name="record">The name of the record for the busy text, for example "export" or "play".</param>
    /// <param name="wait">The time to wait for the lock, or null for <see cref="LockWait"/>.</param>
    public static HistoryWrite Update(HistoryLocation location, string record, Action<PublishHistory> change,
        TimeSpan? wait = null)
    {
        var folder = Path.GetFullPath(location.Folder);
        if (ModExport.HistoryFolderRefusal(folder, location.ModFolder, location.GameRoots) is { } refusal)
            return new HistoryWrite(false, [refusal]);
        var messages = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);
            using var held = TakeLock(Path.Combine(folder, LockName), wait ?? LockWait);
            if (held == null)
                return new HistoryWrite(false, [PublishMessages.HistoryBusy(record)]);
            var path = Path.Combine(folder, FileName);
            var load = Load(folder, location.ModId);
            var history = load.History;
            // A file of another format version stays for the OpenLancer that wrote it.
            if (load.State == HistoryLoadState.OtherVersion)
                return new HistoryWrite(false, [load.Problem!]);
            if (load.State == HistoryLoadState.Unreadable)
            {
                // An earlier bad file stays, so the move never replaces a file.
                var bad = Path.Combine(folder, ModWorkspace.FreeName(Path.GetFileNameWithoutExtension(BadName),
                    name => File.Exists(Path.Combine(folder, name + ".json"))) + ".json");
                File.Move(path, bad);
                messages.Add(PublishMessages.HistoryBad(path, bad));
            }
            if (history.ModId.Length == 0)
                history.ModId = location.ModId;
            if (history.ModFolder.Length == 0)
                history.ModFolder = Path.GetFullPath(location.ModFolder);
            change(history);
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(history, JsonOptions));
            return new HistoryWrite(true, messages);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            messages.Add($"OpenLancer did not record this {record} in the publish history: {e.Message}");
            return new HistoryWrite(false, messages);
        }
    }

    /// <summary>The warning when the history belongs to a mod in another folder, or null.</summary>
    public string? FolderWarning(string modFolder) =>
        ModFolder.Length > 0 && !GameFolders.Same(ModFolder, modFolder)
            ? PublishMessages.HistoryOfAnotherFolder(ModFolder)
            : null;

    /// <summary>The newest export, or null.</summary>
    [JsonIgnore]
    public HistoryExport? Newest => Exports.MaxBy(e => e.Time);

    /// <summary>The versions of the exports, for "Increase version".</summary>
    [JsonIgnore]
    public IEnumerable<string> Versions => Exports.Select(e => e.Version);

    /// <summary>True when the version has the mark "published". The comparison ignores the letter case.</summary>
    public bool IsPublished(string version) => Published.Any(m => ModVersion.TextComparer.Equals(m.Version, version));

    /// <summary>The exports of a version without <c>replacedBy</c>, newest first.</summary>
    public IEnumerable<HistoryExport> ValidExports(string version) => Exports
        .Where(e => e.ReplacedBy == null && ModVersion.TextComparer.Equals(e.Version, version))
        .OrderByDescending(e => e.Time);

    /// <summary>
    /// The same-version guard (decision D568): an export of the same version without <c>replacedBy</c> that has a
    /// package of the same target and form with another <c>gameFiles</c> digest. An export of other content with a
    /// package of another target is a conflict too, because the version would then hold two contents. A difference of
    /// the texts only is no conflict. The Export dialog asks after <c>Prepare</c> and before <c>Write</c>.
    /// </summary>
    public GuardResult Conflict(PreparedExport prepared) => Conflict(prepared.Version, prepared.Content, prepared.Digests);

    /// <summary>The same-version guard of the packages of a new export.</summary>
    /// <param name="content">The content fingerprint of the new export.</param>
    /// <param name="packages">The target, the form and the digest of each package of the new export.</param>
    public GuardResult Conflict(string version, string content,
        IReadOnlyCollection<(string Target, string Form, string GameFiles)> packages)
    {
        var conflicts = ValidExports(version).Where(e => e.Packages.Any(old =>
            packages.Any(p => p.Target == old.Target && p.Form == old.Form && p.GameFiles != old.GameFiles) ||
            e.Content != content && packages.All(p => p.Target != old.Target))).ToList();
        if (conflicts.Count == 0)
            return GuardResult.None(version);
        var missing = conflicts.SelectMany(e => e.Packages)
            .Where(old => !packages.Any(p => p.Target == old.Target && p.Form == old.Form))
            .Select(old => $"{ExportTarget.Find(old.Target)?.Name ?? old.Target} ({PackageForms.Name(old.Form)})")
            .Distinct().ToList();
        return new GuardResult(IsPublished(version) ? GuardKind.Published : GuardKind.Exported, version, conflicts[0],
            missing);
    }

    /// <summary>
    /// Records an export (decisions D568 and D590). The history gets no entry when each package has the SHA-256 of a
    /// package of an export of the version, and that export has the same fingerprints. Otherwise the history gets the
    /// entry. Each earlier export of the version whose targets and forms the new export covers gets
    /// <c>replacedBy</c>.
    /// </summary>
    /// <remarks>A change of a text that only <c>nexus-page.txt</c> holds, for example a tag, changes the page
    /// fingerprint and not the packages. It adds an entry, so that the checklist and the chips see the current
    /// texts.</remarks>
    /// <param name="replace">True when the user picked "Replace version".</param>
    public RecordResult RecordExport(HistoryExport entry, bool replace = false)
    {
        var earlier = ValidExports(entry.Version).ToList();
        var published = IsPublished(entry.Version);
        var matching = entry.Packages.Select(p => earlier.FirstOrDefault(e => e.Packages.Any(old =>
            old.Target == p.Target && old.Form == p.Form && old.Sha256 == p.Sha256))).ToList();
        var samePackages = entry.Packages.Count > 0 && matching.All(m => m != null);
        if (samePackages && matching.All(m => m!.Content == entry.Content && m.Page == entry.Page))
            return new RecordResult(RecordKind.Same, matching[0], published);

        var textsOnly = earlier.FirstOrDefault(e => e.Packages.Any(old => entry.Packages.Any(p =>
            old.Target == p.Target && old.Form == p.Form && old.GameFiles == p.GameFiles && old.Sha256 != p.Sha256)));
        for (var i = 0; i < Exports.Count; i++)
        {
            var old = Exports[i];
            if (old.ReplacedBy == null && ModVersion.TextComparer.Equals(old.Version, entry.Version) &&
                old.Packages.All(o => entry.Packages.Any(p => p.Target == o.Target && p.Form == o.Form)))
                Exports[i] = old with { ReplacedBy = entry.Time };
        }
        Exports.Add(entry);
        if (samePackages)
            return new RecordResult(RecordKind.PageOnly, matching[0], published);
        if (replace)
            return new RecordResult(RecordKind.Replaced, earlier.FirstOrDefault(), published);
        return textsOnly != null
            ? new RecordResult(RecordKind.TextsOnly, textsOnly, published)
            : new RecordResult(RecordKind.Added, null, published);
    }

    /// <summary>Gives a version the mark "published". A mark of the same version gets the new time and link.</summary>
    public void Mark(string version, DateTime time, string page)
    {
        Unmark(version);
        Published.Add(new HistoryMark(version, time, page));
    }

    public void Unmark(string version) => Published.RemoveAll(m => ModVersion.TextComparer.Equals(m.Version, version));

    /// <summary>Adds a play, and keeps the last <see cref="MaxPlays"/> plays.</summary>
    public void AddPlay(HistoryPlay play)
    {
        Plays.Add(play);
        if (Plays.Count > MaxPlays)
            Plays.RemoveRange(0, Plays.Count - MaxPlays);
    }

    /// <summary>Keeps the check as the last check of its target.</summary>
    public void SetCheck(HistoryCheck check)
    {
        Checks.RemoveAll(c => c.Target == check.Target);
        Checks.Add(check);
    }

    /// <summary>Keeps the result of the checks of each target of an export or a check as the last check of the target
    /// (decision D572).</summary>
    /// <param name="content">The content fingerprint of the checked mod.</param>
    public void SetChecks(IEnumerable<TargetCheck> checks, string content, DateTime time)
    {
        foreach (var check in checks)
            SetCheck(new HistoryCheck(check.Target.Id, time, content, check.Base, check.Errors, check.Warnings));
    }

    /// <summary>The last check of a target, or null.</summary>
    public HistoryCheck? CheckOf(string target) => Checks.LastOrDefault(c => c.Target == target);

    /// <summary>
    /// The valid packages of the exports of a version into a folder (section 9.3 of the M11c design): the export has no
    /// <c>replacedBy</c>, its content is the current content, and the file exists with the recorded SHA-256. The
    /// Nexus page of a new export lists them too.
    /// </summary>
    public IReadOnlyList<ExportedPackage> ValidPackages(string version, string folder, string content)
    {
        var result = new List<ExportedPackage>();
        foreach (var entry in ValidExports(version).Where(e => e.Content == content && GameFolders.Same(e.Folder, folder)))
        {
            foreach (var package in entry.Packages)
            {
                var path = Path.Combine(folder, package.File);
                if (ExportTarget.Find(package.Target) is not { } target || result.Any(r => r.FileName == package.File) ||
                    !File.Exists(path) || DataDigest.HashFile(path) != package.Sha256)
                    continue;
                result.Add(new ExportedPackage(target, package.File, path)
                {
                    Form = package.Form switch
                    {
                        PackageForms.DropIn => PackageForm.DropIn,
                        PackageForms.Flmm => PackageForm.Flmm,
                        _ => PackageForm.ModFolder,
                    },
                    Sha256 = package.Sha256,
                    GameFiles = package.GameFiles,
                });
            }
        }
        return result;
    }

    /// <summary>The state of the files of an export, for the History tab.</summary>
    public static ExportFilesState StateOf(HistoryExport entry)
    {
        if (entry.ReplacedBy != null)
            return ExportFilesState.Replaced;
        foreach (var package in entry.Packages)
        {
            var path = Path.Combine(entry.Folder, package.File);
            if (!File.Exists(path))
                return ExportFilesState.NotFound;
            if (DataDigest.HashFile(path) != package.Sha256)
                return ExportFilesState.OtherFiles;
        }
        return ExportFilesState.InPlace;
    }

    /// <summary>The history entry of a written export, with the packages that the export wrote.</summary>
    public static HistoryExport EntryOf(ExportResult result, DateTime time) => new(
        result.Version, time, OpenLancerVersion, result.Content, result.Page, result.OutputFolder,
        [.. result.Packages.Where(p => p.Path != null && p.Sha256 != null).Select(p =>
            new HistoryPackage(p.Target.Id, PackageForms.Of(p.Form), p.FileName, p.Sha256!, p.GameFiles ?? ""))]);

    /// <summary>True when no list holds null. The reader refuses a missing or null field itself, but not a null item of
    /// a list.</summary>
    private bool IsComplete() =>
        Exports.All(e => e != null && e.Packages.All(p => p != null)) && Published.All(m => m != null) &&
        Plays.All(p => p != null) && Checks.All(c => c != null);

    /// <summary>The value of <c>formatVersion</c>, or null when the bytes do not parse or the field is not a
    /// number.</summary>
    private static int? FormatVersionOf(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("formatVersion", out var field) && field.TryGetInt32(out var version)
                ? version
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Takes the lock file with no shared access, or returns null after the wait.</summary>
    private static FileStream? TakeLock(string path, TimeSpan wait)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (watch.Elapsed >= wait)
                    return null;
                Thread.Sleep(50);
            }
        }
    }
}
