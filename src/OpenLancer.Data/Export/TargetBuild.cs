using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LibreLancer.Data.IO;
using OpenLancer.Data.Ini;
using OpenLancer.Data.IO;
using OpenLancer.Data.Mods;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Strings;
using OpenLancer.Data.Thn;

namespace OpenLancer.Data.Export;

/// <summary>What a file of a target build is.</summary>
public enum TargetFileKind
{
    /// <summary>An INI file that the mods merge or add.</summary>
    Ini,

    /// <summary>A cutscene script that the mods merge or add.</summary>
    Cutscene,

    /// <summary>Another file of a mod, for example a model.</summary>
    ModFile,

    /// <summary>The generated text DLL of the mods.</summary>
    TextDll,
}

/// <summary>One file that a target build changes or adds (milestone M11).</summary>
public sealed class TargetFile
{
    internal TargetFile(string path, TargetFileKind kind, byte[] bytes)
    {
        Path = path;
        Kind = kind;
        Bytes = bytes;
    }

    /// <summary>The install path, in the normal form of <see cref="VfsPath"/>.</summary>
    public string Path { get; }

    public TargetFileKind Kind { get; }

    /// <summary>The bytes for a package or a play copy.</summary>
    public byte[] Bytes { get; }

    /// <summary>True when the target game has a file at <see cref="Path"/>.</summary>
    public bool HasBase { get; internal init; }

    /// <summary>True when the file is a whole file of a mod, which no patch changes.</summary>
    public bool IsModFile { get; internal init; }

    /// <summary>True for a file that OpenLancer makes for the target game, for example a route table. It has no merged
    /// document and no difference of sections, so the format checks and the section lines of the checklist do not
    /// read it.</summary>
    public bool Generated { get; internal init; }

    /// <summary>What a generated route table changes, or null for another file.</summary>
    internal RouteFileChange? Routes { get; init; }

    /// <summary>The parsed base file of an INI file, or null.</summary>
    internal IniDocument? BaseDocument { get; init; }

    /// <summary>The bytes of the base file of an INI file, or null.</summary>
    internal byte[]? BaseBytes { get; init; }

    /// <summary>The merged document of an INI file.</summary>
    internal IniDocument? Merged { get; init; }

    /// <summary>The difference of the sections of an INI file. All sections are added sections for a new file.</summary>
    internal FileDiff? Diff { get; init; }

    /// <summary>The tree of the base file of a cutscene, or null.</summary>
    internal ThnTable? BaseTree { get; init; }

    /// <summary>The merged tree of a cutscene, before the export writes the numbers of the Librelancer-only
    /// names.</summary>
    internal ThnTable? MergedTree { get; init; }
}

/// <summary>The files that a merge on the files of a target game changes or adds (milestone M11).</summary>
public sealed class TargetFiles
{
    internal TargetFiles(GameBase gameBase, ModOverlay overlay, IReadOnlyList<TargetFile> files,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Base = gameBase;
        Overlay = overlay;
        Files = files;
        Diagnostics = diagnostics;
    }

    /// <summary>The files of the target game.</summary>
    public GameBase Base { get; }

    /// <summary>The mods in load order.</summary>
    public IReadOnlyList<Mod> Mods => Overlay.Mods;

    public OverlayOptions Options => Overlay.Options;

    public StringIds StringIds => Overlay.StringIds;

    /// <summary>The changed and added files, by install path in ordinal order. An INI file or a cutscene whose content
    /// does not change is not in the list, also when its bytes change.</summary>
    public IReadOnlyList<TargetFile> Files { get; }

    /// <summary>The problems of the merge and of the files.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    internal ModOverlay Overlay { get; }

    /// <summary>The file of an install path, or null.</summary>
    public TargetFile? File(string path) => Files.FirstOrDefault(f => VfsPath.Comparer.Equals(f.Path, path));
}

/// <summary>
/// Merges mods on the files of a target game with the retail rules (milestone M11), and gives the files that change.
/// The export and Play in the game use it. It only reads the files of the game.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The text DLL gets the index of the retail rule (<see cref="DllIndexRule.Retail"/>).</item>
/// <item>A changed INI file is text. A text base keeps its other lines and comments (<see cref="IniTextEdit"/>). A binary
/// base gives the canonical text of <see cref="IniDocument.ToText"/>.</item>
/// <item>A changed cutscene is Lua text with the numbers of the Librelancer-only names
/// (<see cref="ThnRetailNames"/>).</item>
/// <item>A hidden, system or temporary file of a mod is not part of the result (<see cref="PackageRules.SkipReason"/>).</item>
/// <item>A build for a retail game adds the three route tables of the game when the mods change the routes
/// (<see cref="TargetRoutes"/>).</item>
/// </list>
/// </remarks>
public static class TargetBuild
{
    /// <param name="stringsDllName">The file name of the text DLL in the EXE folder.</param>
    /// <param name="keepPlaceholders">True to keep the text placeholders as strings, for the FLMM package.</param>
    /// <param name="routeTables">True to make the route tables of the retail game when the mods change the routes (see
    /// <see cref="TargetRoutes"/>). The packages and the play copy of a retail game need them. OpenLancer computes its
    /// own routes, so the checklist of the target OpenLancer does not use them.</param>
    public static TargetFiles Run(GameBase gameBase, IReadOnlyList<Mod> mods, string stringsDllName,
        IStringResourceWriter stringWriter, IThnEvaluator thn, bool keepPlaceholders = false, bool routeTables = false)
    {
        var options = new OverlayOptions
        {
            StringsDllName = stringsDllName,
            DllIndexRule = DllIndexRule.Retail,
            KeepDocuments = true,
            KeepPlaceholders = keepPlaceholders,
        };
        var overlay = ModOverlay.Build(gameBase, mods, stringWriter, thn, options);
        var diagnostics = overlay.Diagnostics.ToList();
        var baseFs = overlay.Base.OpenFileSystem();
        var routes = routeTables ? TargetRoutes.Of(overlay, baseFs, mods) : null;
        var files = new List<TargetFile>();
        foreach (var file in overlay.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var path = file.Path;
            // A route file of a mod goes to the game only when the build makes no route tables.
            var isRoute = routes?.Holds(path) == true;
            var into = isRoute ? routes!.ModFiles : files;
            var problems = isRoute ? routes!.ModProblems : diagnostics;
            var owner = mods.LastOrDefault(m => m.Files.ContainsKey(path));
            var patched = owner != null
                ? mods.SkipWhile(m => m != owner).Any(m => m.Patches.ContainsKey(path))
                : mods.Any(m => m.Patches.ContainsKey(path));
            var isModFile = owner != null && !patched;
            if (isModFile && owner!.Files[path] is var disk &&
                PackageRules.SkipReason(disk, System.IO.Path.GetRelativePath(owner.Folder, disk)) is { } skip)
            {
                problems.Add(Diagnostic.Warning(disk, PackageRules.SkippedText(path, skip)));
                continue;
            }

            var hasBase = baseFs.FileExists(path);
            var result = VfsPath.Comparer.Equals(path, options.StringsDllPath)
                ? new TargetFile(path, TargetFileKind.TextDll, file.ReadAllBytes())
                : path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                    ? Ini(overlay, baseFs, file, hasBase, isModFile, problems)
                    : path.EndsWith(".thn", StringComparison.OrdinalIgnoreCase)
                        ? Cutscene(baseFs, file, hasBase, isModFile, thn, problems)
                        : new TargetFile(path, TargetFileKind.ModFile, file.ReadAllBytes()) { HasBase = hasBase, IsModFile = true };
            if (result != null)
                into.Add(result);
        }
        routes?.Finish(files, diagnostics);
        return new TargetFiles(overlay.Base, overlay, files, diagnostics);
    }

    /// <summary>The file of a merged or added INI file, or null when its sections do not change.</summary>
    private static TargetFile? Ini(ModOverlay overlay, FileSystem baseFs, OverlayFile file, bool hasBase, bool isModFile,
        List<Diagnostic> diagnostics)
    {
        var path = file.Path;
        var source = file.DiskPath ?? path;
        var bytes = file.ReadAllBytes();
        IniDocument merged;
        try
        {
            merged = overlay.MergedDocument(path) ?? IniDocument.Parse(path, bytes);
        }
        catch (Exception e)
        {
            diagnostics.Add(Diagnostic.Error(source, "The engine cannot parse this file: " + e.Message));
            return null;
        }
        if (!hasBase)
        {
            return new TargetFile(path, TargetFileKind.Ini, isModFile ? bytes : merged.ToText())
            {
                IsModFile = isModFile,
                Merged = merged,
                Diff = IniSectionDiff.Compare([], merged.Sections),
            };
        }

        var baseBytes = baseFs.ReadAllBytes(path);
        IniDocument baseDocument;
        try
        {
            baseDocument = IniDocument.Parse(path, baseBytes);
        }
        catch (Exception e)
        {
            diagnostics.Add(Diagnostic.Error(path, "The engine cannot parse the file of the game: " + e.Message));
            return null;
        }
        var diff = IniSectionDiff.Compare(baseDocument.Sections, merged.Sections);
        if (diff.IsEmpty)
        {
            if (isModFile)
                diagnostics.Add(Diagnostic.Error(source, PackageRules.GameFileInOtherForm(source, path)));
            return null;
        }
        if (isModFile)
            diagnostics.Add(Diagnostic.Warning(source, PackageRules.ReplacesGameFile(path)));

        byte[] output;
        if (isModFile)
            output = bytes;
        else if (baseDocument.IsBinary)
            output = merged.ToText();
        else
        {
            var edit = IniTextEdit.Write(baseBytes, baseDocument, merged, diff);
            if (edit.Fallback != null)
                diagnostics.Add(Diagnostic.Warning(path,
                    $"OpenLancer wrote {path} as plain text without the comments of the game file, because " +
                    $"{edit.Fallback}."));
            output = edit.Bytes;
        }
        return new TargetFile(path, TargetFileKind.Ini, output)
        {
            HasBase = true,
            IsModFile = isModFile,
            BaseDocument = baseDocument,
            BaseBytes = baseBytes,
            Merged = merged,
            Diff = diff,
        };
    }

    /// <summary>The file of a merged or added cutscene, or null when its tree does not change.</summary>
    private static TargetFile? Cutscene(FileSystem baseFs, OverlayFile file, bool hasBase, bool isModFile,
        IThnEvaluator thn, List<Diagnostic> diagnostics)
    {
        var path = file.Path;
        var source = file.DiskPath ?? path;
        var bytes = file.ReadAllBytes();
        ThnTable tree;
        ThnTable? baseTree = null;
        try
        {
            tree = thn.Evaluate(bytes, source);
            if (hasBase)
                baseTree = thn.Evaluate(baseFs.ReadAllBytes(path), path);
        }
        catch (ThnEvaluationException e)
        {
            diagnostics.Add(Diagnostic.Error(source, "Cannot read the cutscene: " + e.Message));
            return null;
        }
        if (baseTree != null && baseTree.Equals(tree))
        {
            if (isModFile)
                diagnostics.Add(Diagnostic.Error(source, PackageRules.GameFileInOtherForm(source, path)));
            return null;
        }
        if (isModFile && hasBase)
            diagnostics.Add(Diagnostic.Warning(source, PackageRules.ReplacesGameFile(path)));

        byte[] output = bytes;
        if (!isModFile)
        {
            try
            {
                output = Encoding.ASCII.GetBytes(ThnWriter.Write(ThnRetailNames.Apply(tree, thn.Symbols)));
            }
            catch (ArgumentException e)
            {
                diagnostics.Add(Diagnostic.Error(path, "Cannot write the cutscene: " + e.Message));
                return null;
            }
        }
        return new TargetFile(path, TargetFileKind.Cutscene, output)
        {
            HasBase = hasBase,
            IsModFile = isModFile,
            BaseTree = baseTree,
            MergedTree = tree,
        };
    }
}
