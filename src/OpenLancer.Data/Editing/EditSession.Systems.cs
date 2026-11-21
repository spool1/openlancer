using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using LibreLancer.Data.Ini;

namespace OpenLancer.Data.Editing;

/// <summary>Reads a section into a value, as the generated <c>TryParse</c> of an upstream section class does.</summary>
/// <returns>True when the parser made the value.</returns>
public delegate bool SectionParser<T>(Section section, [NotNullWhen(true)] out T? value, IniStringPool? pool,
    IniParseProperties? properties) where T : class;

public sealed partial class EditSession
{
    private SystemFiles? systems;
    private RoomFiles? rooms;

    /// <summary>The star system files of the edit view, and <c>universe.ini</c>.</summary>
    internal SystemFiles Systems => systems ??= new SystemFiles(SystemFiles.ReadUniversePath(FreelancerIni), OpenDocument);

    /// <summary>The room files of the edit view, from the bases of <c>universe.ini</c> (decision D135 of the M8b
    /// design).</summary>
    internal RoomFiles Rooms => rooms ??= new RoomFiles(this, () => FreelancerIni);

    /// <summary>True when a <c>[Room]</c> of a base file of the edit view names the file. The editor knows the schema of
    /// a room file, so the Inspector edits its <c>[Room_Info]</c>, <c>[CharacterPlacement]</c> and
    /// <c>[PlayerShipPlacement]</c>.</summary>
    public bool IsRoomFile(string path) => Rooms.Contains(path);

    /// <summary>The install path of the <c>universe.ini</c> that the <c>freelancer.ini</c> of the edit view names, or
    /// null when it names none.</summary>
    public string? UniversePath => Systems.UniversePath;

    /// <summary>
    /// The install path of the file of a system: the <c>file</c> of its <c>[system]</c> in W of <c>universe.ini</c>,
    /// relative to the folder of <c>universe.ini</c>. Null when no <c>[system]</c> or more than one has the nickname,
    /// or when the system has no <c>file</c>.
    /// </summary>
    public string? SystemPath(string systemNickname) => Systems.PathOf(systemNickname);

    /// <summary>The <c>[system]</c> section of a system in W of <c>universe.ini</c>, or null when no section or more
    /// than one has the nickname.</summary>
    public SectionRef? SystemSection(string systemNickname) => Systems.SectionOf(systemNickname);

    /// <summary>True for a <c>[system]</c> section of <c>universe.ini</c>.</summary>
    public bool IsSystemSection(SectionRef section) =>
        UniversePath is { } universe && IO.VfsPath.Comparer.Equals(section.Path, universe) &&
        section.Id.Type.Equals("system", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a <c>[system]</c> section in W of <c>universe.ini</c> names the file, also when the nickname
    /// of that system is ambiguous. The engine reads the objects, zones and lights of a system only from such a
    /// file.</summary>
    public bool IsSystemFile(string path) => Systems.IsSystemFile(path);

    /// <summary>The sections of W of a file, in file order. The list is empty when the edit view has no such INI
    /// file.</summary>
    public IReadOnlyList<SectionRef> Sections(string path) =>
        OpenDocument(path) is { } document
            ? Enumerable.Range(0, document.Working.Count).Select(i => new SectionRef(document.Path, document.IdentityAt(i))).ToList()
            : [];

    /// <summary>
    /// Reads a section of W as the engine reads it, with the generated parser of its upstream class. The parser gets a
    /// copy of the section with which it logs nothing (see <see cref="SectionChecks.ParserCopy"/>). In the copy, a
    /// text placeholder is 0, because an unsaved text has no id. A string in an integer key gives the integer that the
    /// engine reads from the file, for which <see cref="Check"/> gives a warning. The parse reads no other file.
    /// </summary>
    /// <param name="parser">The generated parser of the section class, for example <c>SystemObject.TryParse</c>.</param>
    /// <returns>The value, or null when the edit view has no such section, when the editor does not know its schema,
    /// when <see cref="Check"/> gives an error for it, or when the parser fails.</returns>
    public T? Parse<T>(SectionRef target, SectionParser<T> parser) where T : class
    {
        if (SchemaOf(target) is not { } schema || !TryFind(target, out var document, out var index) ||
            Check(target, schema, document, index).Any(IsError))
            return null;
        var copy = SectionChecks.ParserCopy(schema, document.Working[index], document.IsBinary);
        // Without the properties of a file system, a class that reads a dependent file reads none.
        return parser(copy, out var value, null, IniParseProperties.Empty) ? value : null;
    }
}
