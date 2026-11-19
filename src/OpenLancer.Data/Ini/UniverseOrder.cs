using System;
using System.Collections.Generic;
using LibreLancer.Data.Ini;
using OpenLancer.Data.IO;

namespace OpenLancer.Data.Ini;

/// <summary>
/// The place of a new <c>[Base]</c> in <c>universe.ini</c>: directly before the first <c>[system]</c>. A new section of
/// another type goes at the end of the file, as in each other file.
/// </summary>
/// <remarks>
/// <para>The game reads <c>universe.ini</c> from the start. It links an object of a system file to its base only when
/// the game read the <c>[Base]</c> before the <c>[system]</c> of that system. The retail files put each
/// <c>[Base]</c> before the first <c>[system]</c>. A <c>[Base]</c> at the end of the file thus has no station in the
/// game.</para>
/// <para>The edit (<c>EditCommand.AddSection</c>), the merge (<see cref="IniPatcher"/>) and the patch writer use this
/// one rule. So W and each merged file agree. The merged files are the overlay of F5, the target build of Ctrl+F5, the
/// play copies, the drop-in zip and the W of a patch that the editor opens again. The rule needs no directive, so a
/// patch keeps <c>ol.new</c> and the mod format does not change.</para>
/// </remarks>
internal static class UniverseOrder
{
    /// <summary>The install path of the file of the rule. The retail games and HD Edition 0.7.1 name this file in the
    /// <c>universe</c> line of <c>freelancer.ini</c>.</summary>
    public const string UniversePath = @"DATA\UNIVERSE\universe.ini";

    private const string BaseType = "Base";
    private const string SystemType = "system";

    /// <summary>True for the install path of <c>universe.ini</c>, in any form.</summary>
    public static bool IsUniverse(string path) =>
        VfsPath.Comparer.Equals(VfsPath.Normalize(path) ?? path, UniversePath);

    /// <summary>True for a <c>[Base]</c> section.</summary>
    public static bool IsBase(Section section) => IsBase(section.Name);

    /// <summary>The index at which a new section of a type goes in the sections of a file: the first <c>[system]</c>
    /// for a <c>[Base]</c> of <c>universe.ini</c>, else the end of the file. A <c>universe.ini</c> without a
    /// <c>[system]</c> gets the new <c>[Base]</c> at the end.</summary>
    /// <param name="path">The install path of the file.</param>
    /// <param name="sections">The sections of the file.</param>
    /// <param name="type">The type of the new section.</param>
    public static int PlaceOf(string path, IReadOnlyList<Section> sections, string type) =>
        IsBase(type) && IsUniverse(path) && FirstSystem(sections) is var first and >= 0 ? first : sections.Count;

    /// <summary>The index of the first <c>[system]</c> of the sections, or -1 when they have none.</summary>
    public static int FirstSystem(IReadOnlyList<Section> sections)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i].Name.Equals(SystemType, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static bool IsBase(string type) => type.Equals(BaseType, StringComparison.OrdinalIgnoreCase);
}
