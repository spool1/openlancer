using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data.Ini;
using LibreLancer.Data.IO;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Editing;

public sealed partial class EditSession
{
    /// <summary>
    /// The problems of the format checks and of the content checks of the retail targets (section 9.2 of the M11
    /// design), for the save gate. They run only for a tier-1 mod that names a retail target. They compare W with B, so
    /// they report only what the active mod adds. They do not merge on the files of each target game: the export and
    /// Play in the game do that. The count of the jump objects of a system is not part of them, because the jump
    /// connection checks give that error at each save of a tier-1 mod (see <see cref="Scene.ConnectionChecks"/>).
    /// </summary>
    internal IReadOnlyList<Diagnostic> TargetProblems()
    {
        if (ActiveMod is not { } active || !HasRetailTarget(active.Manifest))
            return [];
        var view = new SessionCheckView(this, active);
        return
        [
            .. FormatChecks.Run(view, path => OpenDocument(path)?.IsBinary == true, active.Files.ContainsKey,
                text.Infocards, Path.Combine(active.Folder, Mod.StringsFile)),
            .. TargetChecks.Run(view, jumpCount: false),
        ];
    }

    /// <summary>True when the mod is a tier-1 mod that names a target of the retail game.</summary>
    private static bool HasRetailTarget(ModManifest manifest) =>
        manifest.Tier == ModManifest.DataTier && manifest.Targets.Any(t => ExportTarget.Find(t) is { IsRetail: true });

    /// <summary>The view of the target checks of a session: B of each file without the active mod, and W with
    /// it.</summary>
    /// <remarks>A whole file of the active mod has no B. Without the active mod, the view gives the file of the lower
    /// layer, or no sections for a new file. With it, the view gives W of a document of the kind "whole file" (section
    /// 6.7 of the M6b design), so the checks see the unsaved edits. This includes a new file of the folder of the mod
    /// before its save. A file that W deleted has no sections. For another whole file of the active mod, W has the lower
    /// data, so the view gives the file of the edit view.</remarks>
    internal sealed class SessionCheckView(EditSession session, Mod active) : CheckView
    {
        private readonly Dictionary<string, IReadOnlyList<Section>?> lowerFiles = new(VfsPath.Comparer);
        private FileSystem? editView;

        public override IReadOnlyCollection<string> ChangedPaths { get; } = session.ActiveChangedPaths();

        public override IReadOnlyList<Section>? Sections(string path, bool withMods)
        {
            if (withMods)
                return session.WorkingSections(path);
            if (VfsPath.Normalize(path) is not { } normal)
                return null;
            if (active.Files.ContainsKey(normal) || session.OpenedDocument(normal) is { IsWholeFile: true })
            {
                if (!lowerFiles.TryGetValue(normal, out var sections))
                    lowerFiles[normal] = sections = session.lowerView.Document(normal)?.Sections;
                return sections;
            }
            return session.OpenDocument(normal)?.Base;
        }

        public override IReadOnlyList<string> Folders(string path) => EditView.GetDirectories(path);

        private FileSystem EditView => editView ??= session.EditView.CreateFileSystem();
    }
}
