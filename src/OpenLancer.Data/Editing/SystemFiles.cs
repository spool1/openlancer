using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema.Universe;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Editing;

/// <summary>
/// The star system files of the edit view: the path of <c>universe.ini</c>, and the file of each <c>[system]</c>
/// section in it. The map reads W of <c>universe.ini</c>, so it shows the edits.
/// </summary>
/// <remarks>
/// <para>The engine reads <c>universe.ini</c> from the last <c>universe</c> entry of the <c>[Data]</c> sections of
/// <c>freelancer.ini</c>, in the folder of <c>data path</c>. A <c>[system]</c> names its file with <c>file</c>,
/// relative to the folder of <c>universe.ini</c>. The engine reads only the last <c>file</c> line.</para>
/// <para>The map keeps the systems until W of <c>universe.ini</c> changes. A W state cannot change, so the map compares
/// the section list by reference.</para>
/// <para>A nickname that two <c>[system]</c> sections share is ambiguous, because the engine uses only one of them.
/// The map then gives no section and no file for it.</para>
/// </remarks>
internal sealed class SystemFiles
{
    /// <summary>
    /// The section classes of <c>universe.ini</c>. The engine reads the file without a <c>[ParsedIni]</c> class
    /// (upstream <c>UniverseIni</c>), and gives each section to the generated parser of its class.
    /// </summary>
    private static readonly Dictionary<string, Type> UniverseClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = typeof(StarSystem),
        ["base"] = typeof(Base),
    };

    private readonly Func<string, EditDocument?> open;
    private IReadOnlyList<Section>? indexed;
    private Dictionary<string, List<SystemEntry>> byNickname = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> files = new(VfsPath.Comparer);
    private List<string> ordered = [];

    /// <param name="universePath">The install path of <c>universe.ini</c>, or null when <c>freelancer.ini</c> names
    /// none.</param>
    /// <param name="open">Gives the document of an install path, or null when the edit view has no such file.</param>
    public SystemFiles(string? universePath, Func<string, EditDocument?> open)
    {
        UniversePath = universePath == null ? null : VfsPath.Normalize(universePath);
        this.open = open;
    }

    /// <summary>The install path of <c>universe.ini</c>, or null.</summary>
    public string? UniversePath { get; }

    /// <summary>The folder of <c>universe.ini</c>, as an install path, or null. A <c>[system] file</c> line names its
    /// file relative to this folder (see <see cref="ContentPaths.UniverseValue"/>).</summary>
    public string? Folder => UniversePath == null ? null : VfsPath.Parent(UniversePath);

    /// <summary>The install path of <c>universe.ini</c> that <c>freelancer.ini</c> names, or null.</summary>
    public static string? ReadUniversePath(IReadOnlyList<Section> freelancerIni) =>
        DataPaths.Entries(freelancerIni)
            .Where(e => e.Name.Equals("universe", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Path)
            .LastOrDefault();

    /// <summary>The value of the last <c>file</c> line of a <c>[system]</c> section that has a value, or null. The
    /// engine reads only that line.</summary>
    public static string? FileValue(Section system) =>
        system.LastOrDefault(e => e.Name.Equals("file", StringComparison.OrdinalIgnoreCase) && e.Count > 0)?[0].ToString();

    /// <summary>The install path of the file that a <c>[system] file</c> value names, or null when the path leaves the
    /// install root. The game joins the folder of <c>universe.ini</c> and the value, so a <c>..</c> part goes up from
    /// that folder.</summary>
    /// <param name="universePath">The install path of <c>universe.ini</c>.</param>
    public static string? FilePath(string universePath, string fileValue) =>
        VfsPath.Normalize($@"{VfsPath.Parent(universePath)}\{fileValue}");

    /// <summary>The install path of the file of a system, or null when no <c>[system]</c> or more than one has the
    /// nickname, or when the system has no <c>file</c>.</summary>
    public string? PathOf(string nickname) => One(nickname)?.Path;

    /// <summary>The <c>[system]</c> section of a system, or null when no section or more than one has the
    /// nickname.</summary>
    public SectionRef? SectionOf(string nickname) => One(nickname)?.Section;

    /// <summary>The nickname of the first <c>[system]</c> section that names a file, or null.</summary>
    public string? NicknameOf(string path)
    {
        Refresh();
        var normal = VfsPath.Normalize(path);
        return byNickname.Values.SelectMany(s => s)
            .Where(s => s.Path != null && VfsPath.Comparer.Equals(s.Path, normal))
            .Select(s => s.Section.Id.Key)
            .FirstOrDefault();
    }

    /// <summary>The files that the <c>[system]</c> sections name, in the order of the sections, without
    /// repeats.</summary>
    public IReadOnlyList<string> Files
    {
        get
        {
            Refresh();
            return ordered;
        }
    }

    /// <summary>True when a <c>[system]</c> section names the file, also when its nickname is ambiguous.</summary>
    public bool IsSystemFile(string path)
    {
        Refresh();
        return VfsPath.Normalize(path) is { } normal && files.Contains(normal);
    }

    /// <summary>The schema of a section type of <c>universe.ini</c> or of a star system file, or null for another
    /// file.</summary>
    public SectionSchema? SchemaOf(string path, string type)
    {
        if (UniversePath != null && VfsPath.Comparer.Equals(VfsPath.Normalize(path), UniversePath))
            return UniverseClasses.TryGetValue(type, out var section) ? SchemaCatalog.SectionClass(section, type) : null;
        return IsSystemFile(path) ? SchemaCatalog.Section(typeof(StarSystem), type) : null;
    }

    private SystemEntry? One(string nickname)
    {
        Refresh();
        return byNickname.TryGetValue(nickname, out var systems) && systems is [var one] ? one : null;
    }

    /// <summary>Reads the <c>[system]</c> sections again when W of <c>universe.ini</c> changed since the last
    /// call.</summary>
    private void Refresh()
    {
        var document = UniversePath == null ? null : open(UniversePath);
        if (ReferenceEquals(document?.Working, indexed))
            return;
        indexed = document?.Working;
        byNickname = new Dictionary<string, List<SystemEntry>>(StringComparer.OrdinalIgnoreCase);
        files = new HashSet<string>(VfsPath.Comparer);
        ordered = [];
        if (document == null)
            return;
        for (var i = 0; i < document.Working.Count; i++)
        {
            var id = document.IdentityAt(i);
            // A [system] without exactly one nickname line has no key, so nothing can find it.
            if (!id.Type.Equals("system", StringComparison.OrdinalIgnoreCase) || id.Key == null)
                continue;
            // The path of the document has the letter case of the files.
            var path = FileValue(document.Working[i]) is { } file ? FilePath(document.Path, file) : null;
            if (path != null && files.Add(path))
                ordered.Add(path);
            (byNickname.TryGetValue(id.Key, out var named) ? named : byNickname[id.Key] = [])
                .Add(new SystemEntry(new SectionRef(document.Path, id), path));
        }
    }

    /// <param name="Path">The install path of the file of the system, or null.</param>
    private sealed record SystemEntry(SectionRef Section, string? Path);
}
