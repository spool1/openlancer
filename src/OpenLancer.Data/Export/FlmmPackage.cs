using System.Linq;
using System.Text;
using OpenLancer.Data.Games;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Publishing;

namespace OpenLancer.Data.Export;

/// <summary>
/// The FLMM package of a retail target (milestone M11): <c>&lt;mod id&gt;.zip.flmod</c>, a zip with <c>script.xml</c>
/// at its top and the whole files of the mod in the folder tree of the game. The export wraps it with the README in a
/// zip.
/// </summary>
public static class FlmmPackage
{
    /// <summary>True when the FLMM check of the M11 manual test confirmed the package form. Until then, the editor
    /// shows the form as a preview.</summary>
    public static bool Checked => false;

    public static string FileName(ModManifest mod, ExportTarget target, bool probe) =>
        $"{mod.Id}-{mod.Version}-{target.Id}-flmm{(probe ? "-probe" : "")}.zip";

    /// <summary>The name of the package that FLMM imports: <c>&lt;mod id&gt;.zip.flmod</c>. FLMM 1.31 unpacks only a
    /// file whose name ends in <c>.zip.flmod</c>. It copies a file with another name as one file.</summary>
    public static string InnerFileName(ModManifest mod) => $"{mod.Id}.zip.flmod";

    /// <summary>Writes the package from a target build that keeps the text placeholders. A refusal of an operation, or
    /// a file with the bytes of a known game file, refuses the package.</summary>
    /// <param name="probe">True for the probe of the FLMM check: the macros hold each text as it is.</param>
    public static Package Write(TargetFiles files, Mod mod, ExportTarget target, bool probe) =>
        Write(files, mod, target, probe, null);

    /// <param name="builds">The builds of the game file rule, or null for the embedded manifests.</param>
    internal static Package Write(TargetFiles files, Mod mod, ExportTarget target, bool probe, KnownBuilds? builds) =>
        Plan(files, mod, PublishTexts.Of(mod.Manifest), target, probe, builds).ToPackage();

    /// <summary>
    /// The plan of the package. The header of the script holds the publish texts as ASCII (decision D580), and each
    /// character that FLMM cannot show gives a warning. The guard digest holds the files of the <c>.zip.flmod</c> tree
    /// and the script without its header (decision D589).
    /// </summary>
    internal static PackagePlan Plan(TargetFiles files, Mod mod, PublishTexts texts, ExportTarget target, bool probe,
        KnownBuilds? builds)
    {
        var manifest = mod.Manifest;
        var header = FlmmHeader.Of(texts, target);
        var script = FlmmScript.Build(files, mod, header, probe);
        var problems = script.Problems.ToList();
        problems.AddRange(script.Files.Select(f => PackageRules.GameFile(f.Path, f.Bytes, builds)).OfType<Diagnostic>());
        var name = FileName(manifest, target, probe);
        if (problems.Any(p => p.Severity == DiagnosticSeverity.Error))
            return new PackagePlan(name, null, problems, null);
        problems.AddRange(texts.FlmmLost().Select(lost =>
            Diagnostic.Warning(mod.Folder, lost.Message)));

        var tree = script.Files.Select(f => (f.Path, f.Bytes)).ToList();
        var digest = PackageDigest.Of([.. tree, (FlmmScript.FileName, Encoding.ASCII.GetBytes(script.OperationsXml))]);
        tree.Add((FlmmScript.FileName, Encoding.ASCII.GetBytes(script.Xml)));
        var readme = PackageTexts.Readme(texts, target, PackageForm.Flmm, files, script.Files.Select(f => f.Path).ToList(),
            probe);
        return new PackagePlan(name, () => DeterministicZip.Write([(InnerFileName(manifest), DeterministicZip.Write(tree)),
            (PackageTexts.ReadmeName(manifest), Encoding.UTF8.GetBytes(readme))]), problems, digest)
        {
            Readme = readme,
            ScriptHeader = FlmmScript.HeaderXml(header),
        };
    }
}
