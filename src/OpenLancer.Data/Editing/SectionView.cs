using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Ini;
using OpenLancer.Data.Strings;

namespace OpenLancer.Data.Editing;

/// <summary>Where the lines of a name in the edit view come from.</summary>
public enum ValueOrigin
{
    /// <summary>The base data.</summary>
    Base,

    /// <summary>A mod that loads before the active mod.</summary>
    LowerMod,

    /// <summary>The active mod: its saved patch or an edit.</summary>
    ActiveMod,
}

/// <summary>One line of a section: the entry name and its values.</summary>
public sealed record EntryView(string Name, IReadOnlyList<ValueBase> Values);

/// <summary>What the engine reads from the lines of one key of a section type.</summary>
/// <param name="Name">The entry name, for example <c>hit_pts</c>.</param>
/// <param name="ClrType">The type that the engine makes from one line, for example <see cref="float"/>,
/// <see cref="System.Numerics.Vector3"/>, an enum or <c>float[]</c>. Null for a key that upstream code reads
/// itself (an entry handler), for example <c>addon</c> of a <c>[Good]</c>.</param>
/// <param name="Multiline">True when the engine reads each line of the key. Otherwise the last line wins, and the
/// engine logs a warning for each line after the first.</param>
/// <param name="Required">True when the engine drops the section without the key.</param>
/// <param name="MinComponents">The number of values that a line needs. The engine ignores a line with fewer values.
/// A <see cref="System.Numerics.Vector3"/> line can also be the one value <c>0</c>.</param>
/// <param name="IsKeyEntry">True for the key entry that a patch finds the section by, for example <c>nickname</c>.</param>
/// <param name="TextKind">The kind of text that a numeric value of the key gives, or null when the key holds no
/// text id.</param>
public sealed record KeySchema(string Name, Type? ClrType, bool Multiline, bool Required, int MinComponents,
    bool IsKeyEntry, StringKind? TextKind)
{
    /// <summary>
    /// True when a line can have no value, for example <c>separable</c> of a <c>[CollisionGroup]</c>. The engine
    /// reads a <see cref="bool"/> line without a value as true, and a line with a value as that value. Without a
    /// line, the field keeps its default.
    /// </summary>
    public bool Presence { get; init; }

    /// <summary>True when a <see cref="System.Numerics.Vector2"/> line can be the one value <c>max</c>. The engine then
    /// reads (-1, max).</summary>
    public bool MinMax { get; init; }

    /// <summary>True when a color holds values from 0 to 1. Otherwise its values go from 0 to 255.</summary>
    public bool FloatColor { get; init; }

    /// <summary>How the engine reads a <see cref="System.Numerics.Vector3"/> line with one or two values.</summary>
    public Vec3Mode Mode { get; init; }

    /// <summary>
    /// The upstream field that the key sets, or null for a key that an entry handler reads. Two keys with one field
    /// are two names of one value, for example <c>ambient_color</c> and <c>ambient</c> of an <c>[Object]</c>. Each
    /// line of either name sets the field, so the last line of the two names wins.
    /// </summary>
    public string? Field { get; init; }

    /// <summary>False when the retail game never reads the key, for example <c>lane_id</c> of a zone. The checks skip
    /// the lines of such a key (decision D513 of the console filter design).</summary>
    public bool RetailReads { get; init; } = true;
}

/// <summary>What an edit, an undo or a redo changed.</summary>
/// <param name="Sections">The sections that changed. A deleted or renamed section has its old reference too.</param>
/// <param name="Structural">True when a section was added, deleted or renamed, or when the change does not list its
/// sections. A restore of unsaved edits lists no section.</param>
/// <param name="Text">True when the text of the active mod changed. A change of the text alone lists no section, also
/// when a section shows that text through a placeholder.</param>
/// <param name="Cutscenes">The install paths of the cutscene scripts that changed, in normal form (see
/// <c>VfsPath.Normalize</c>). A change of a cutscene alone lists no section.</param>
public sealed record EditChange(IReadOnlyList<SectionRef> Sections, bool Structural, bool Text,
    IReadOnlyList<string> Cutscenes)
{
    /// <summary>
    /// The sections that got a new key, as (old reference, new reference), in order. An undo gives the pairs in the other
    /// direction and in reverse order. The scene uses them to keep the item of a renamed section (milestone M3).
    /// </summary>
    public IReadOnlyList<(SectionRef From, SectionRef To)> Renames { get; init; } = [];

    /// <summary>The editor files of the active mod that changed, by their path in the mod folder, for example
    /// <c>graphs\DATA\MISSIONS\M02\m02.ini</c> (section 6.8 of the M6b design). A change of an editor file alone lists
    /// no section.</summary>
    public IReadOnlyList<string> ModFiles { get; init; } = [];

    /// <summary>The install paths of the INI files that the change set holds, in normal form, also a file whose W did
    /// not change in the end. A restore of unsaved edits lists its files here and no section. The mission editor gives
    /// the new script versions from it (section 4.2 of the M6b design).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Who made the entry of the edit history: the user, an agent or the restore of the unsaved edits. A step of
    /// a transaction has the origin of its transaction. The marks of the agent edits read it (section 8.5 of the MCP1
    /// design).</summary>
    public EditOrigin Origin { get; init; } = EditOrigin.User;

    /// <summary>True when the change takes the entry back: an undo, or the cancel of a transaction. Then
    /// <see cref="Renames"/> go in the other direction.</summary>
    public bool Undone { get; init; }
}

/// <summary>The result of <see cref="EditSession.Save"/>.</summary>
/// <param name="Saved">True when the save wrote its files, so the session has no unsaved edits now. False when the
/// save wrote nothing.</param>
/// <param name="Written">The files that the save wrote.</param>
/// <param name="Deleted">The files that the save deleted.</param>
/// <param name="Problems">The problems. When the save wrote nothing, they tell why. After a save, they can hold
/// warnings, and errors of the merge that the saved files give and that the edit view did not have before. The checks
/// of the save do not find each merge error, for example a mod with more text entries than the text DLL holds.</param>
/// <param name="ChangedOnDisk">The files of the active mod that something outside the editor changed since the
/// session read them. The save wrote nothing. Save again with <c>overwrite</c> to replace them.</param>
public sealed record SaveResult(
    bool Saved,
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<Diagnostic> Problems,
    IReadOnlyList<string> ChangedOnDisk)
{
    /// <summary>
    /// True when the save is decided but a file did not move into place. The save journal holds the save, and the
    /// next save or the next open completes it first. Until then, the files of the mod are a mix of the old and the
    /// new files. <see cref="Problems"/> has a warning that tells why.
    /// </summary>
    public bool Incomplete { get; init; }
}

/// <summary>A read-only view of one section of the edit view.</summary>
public sealed class SectionView
{
    internal SectionView(Section section, string? readOnlyReason, bool isNew, bool isChanged)
    {
        Type = section.Name;
        Entries = section.Select(e => new EntryView(e.Name, e.ToArray())).ToList();
        ReadOnlyReason = readOnlyReason;
        IsNew = isNew;
        IsChanged = isChanged;
    }

    /// <summary>The section name, for example <c>Good</c>.</summary>
    public string Type { get; }

    /// <summary>The lines in file order.</summary>
    public IReadOnlyList<EntryView> Entries { get; }

    /// <summary>Why the editor cannot change the section, or null when it can.</summary>
    public string? ReadOnlyReason { get; }

    /// <summary>True when the active mod adds the section: the lower layer does not have it.</summary>
    public bool IsNew { get; }

    /// <summary>True when the active mod adds or changes the section, with its saved patch or with an edit.</summary>
    public bool IsChanged { get; }

    /// <summary>The lines of one name, in file order. The name compares without letter case.</summary>
    public IReadOnlyList<EntryView> Lines(string name) =>
        Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// True when the name belongs to a group of names whose lines belong together, for example <c>encounter</c>,
    /// <c>faction</c> and <c>faction_weight</c> of a <c>[Zone]</c>. The engine attaches each line of a later name of
    /// the group to the last line before it. So an edit of the lines of one name alone can give lines to another
    /// owner.
    /// </summary>
    public bool IsLinked(string name) =>
        SectionKeys.LinkedGroups(Type).Any(g => g.Contains(name, StringComparer.OrdinalIgnoreCase));
}
