using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;

namespace OpenLancer.Data.Export;

/// <summary>A package file and its problems.</summary>
/// <param name="Bytes">The bytes of the package, or null when a problem refuses the package.</param>
public sealed record Package(string FileName, byte[]? Bytes, IReadOnlyList<Diagnostic> Problems);

/// <summary>
/// A package before its compression (finding R8 of the M11c design): the problems, the guard digest and a function
/// that makes the bytes. <c>ModExport.Prepare</c> keeps plans, and <c>ModExport.Write</c> makes and writes one package
/// at a time.
/// </summary>
/// <param name="Make">Makes the bytes of the package, or null when a problem refuses the package.</param>
/// <param name="GameFiles">The guard digest of the game files of the package (decision D589), or null when the package
/// is refused.</param>
internal sealed record PackagePlan(string FileName, Func<byte[]>? Make, IReadOnlyList<Diagnostic> Problems,
    string? GameFiles)
{
    /// <summary>The text of the README of the package, or null when the package is refused.</summary>
    public string? Readme { get; init; }

    /// <summary>The header element of the script of an FLMM package, else null.</summary>
    public string? ScriptHeader { get; init; }

    public Package ToPackage() => new(FileName, Make?.Invoke(), Problems);
}

/// <summary>The guard digest of a list of package entries (decision D589): the SHA-256 of the sorted entry names and
/// the SHA-256 of each entry. It does not depend on the Deflate bytes.</summary>
internal static class PackageDigest
{
    public static string Of(IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        var lines = entries
            .Select(e => $"{e.Name.Replace('\\', '/')}\t{ContentHash.Of(e.Bytes)}\n")
            .Order(StringComparer.Ordinal);
        return ContentHash.Of(Encoding.UTF8.GetBytes(string.Concat(lines)));
    }
}

/// <summary>
/// The drop-in zip of a retail target (milestone M11): the folders <c>DATA</c> and <c>EXE</c> at the top of the zip,
/// with the merged and new files of the mod, and the README. The player extracts it over the game folder.
/// </summary>
public static class DropInPackage
{
    public static string FileName(ModManifest mod, ExportTarget target) => $"{mod.Id}-{mod.Version}-{target.Id}.zip";

    /// <summary>Writes the zip of the files of a target build. A file with the bytes of a known game file refuses the
    /// package.</summary>
    public static Package Write(TargetFiles files, ModManifest mod, ExportTarget target) => Write(files, mod, target, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    internal static Package Write(TargetFiles files, ModManifest mod, ExportTarget target, KnownBuilds? builds) =>
        Plan(files, PublishTexts.Of(mod), target, builds, flmmPackage: false).ToPackage();

    /// <summary>The plan of the zip. The guard digest leaves out the README, so that a text change does not change
    /// it.</summary>
    /// <param name="flmmPackage">True when the export also makes the FLMM package of the target (see
    /// <see cref="PackageTexts.Readme"/>).</param>
    internal static PackagePlan Plan(TargetFiles files, PublishTexts texts, ExportTarget target, KnownBuilds? builds,
        bool flmmPackage)
    {
        var mod = texts.Manifest;
        var entries = files.Files
            .Select(f => (Name: PathRules.WithDiskCase(files.Base.Root, files.Base.ToDisk(f.Path)), f.Bytes))
            .ToList();
        var problems = entries.Select(e => PackageRules.GameFile(e.Name, e.Bytes, builds)).OfType<Diagnostic>().ToList();
        var name = FileName(mod, target);
        if (problems.Count > 0)
            return new PackagePlan(name, null, problems, null);
        var digest = PackageDigest.Of(entries);
        var readme = PackageTexts.Readme(texts, target, PackageForm.DropIn, files, entries.Select(e => e.Name).ToList(),
            flmmPackage: flmmPackage);
        entries.Add((PackageTexts.ReadmeName(mod), Encoding.UTF8.GetBytes(readme)));
        return new PackagePlan(name, () => DeterministicZip.Write(entries), problems, digest) { Readme = readme };
    }
}
