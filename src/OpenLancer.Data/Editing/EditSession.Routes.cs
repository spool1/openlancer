using System;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Data.Ini;
using OpenLancer.Data.Editing.Universe;
using OpenLancer.Data.Export;
using OpenLancer.Data.IO;
using OpenLancer.Data.Overlay;
using OpenLancer.Data.Validation;

namespace OpenLancer.Data.Editing;

/// <summary>The jump graph of a universe and its route tables.</summary>
internal sealed record UniverseRoutes(UniverseGraph Graph, RouteTables Tables);

public sealed partial class EditSession
{
    /// <summary>The routes of W, and the file reads of their build with the section list of each read.</summary>
    private (IReadOnlyList<(string Path, IReadOnlyList<Section>? Sections)> Reads, UniverseRoutes Routes)? routes;

    private UniverseRoutes? gameRoutes;

    /// <summary>The files of the edit view of the whole files of the active mod that W does not edit, read once for
    /// each edit view.</summary>
    private (ModOverlay View, Dictionary<string, IReadOnlyList<Section>?> Files)? editViewSections;

    /// <summary>
    /// The jump graph of W and its route tables. The session keeps them until a section list of W of
    /// <c>universe.ini</c>, of a system file, of a solar archetype file or of a gate lock file changes. A W state
    /// cannot change, so the session compares the section lists by reference.
    /// </summary>
    internal UniverseRoutes Routes
    {
        get
        {
            var source = new WorkingUniverse(this);
            if (routes is var (reads, kept) && reads.All(r => ReferenceEquals(source.Sections(r.Path), r.Sections)))
                return kept;
            var recorder = new RecordingSource(source);
            var graph = UniverseGraph.Build(recorder);
            var built = new UniverseRoutes(graph, RouteTables.Build(graph));
            routes = (recorder.Reads, built);
            return built;
        }
    }

    /// <summary>The jump graph and the route tables of the game data without mods, made at the first use. They do not
    /// change in a session.</summary>
    internal UniverseRoutes GameRoutes
    {
        get
        {
            if (gameRoutes != null)
                return gameRoutes;
            var fs = GameBase.Standard(Profile.BaseFolder).OpenFileSystem();
            var files = new Dictionary<string, IReadOnlyList<Section>?>(VfsPath.Comparer);
            var source = new FileUniverseSource(path =>
                files.TryGetValue(path, out var sections) ? sections : files[path] = CheckView.Read(fs, path), ruleData);
            var graph = UniverseGraph.Build(source);
            return gameRoutes = new UniverseRoutes(graph, RouteTables.Build(graph));
        }
    }

    /// <summary>
    /// The route tables of W in which some files have other sections. An example is the files after the command of a
    /// plan that does not run yet. The session does not keep these tables.
    /// </summary>
    /// <param name="files">The sections of each changed file, by install path.</param>
    internal RouteTables RoutesWith(IReadOnlyDictionary<string, List<Section>> files) =>
        RouteTables.Build(UniverseGraph.Build(new ChangedUniverse(new WorkingUniverse(this), files)));

    /// <summary>True when the route tables of W differ from the route tables of the game data without mods. Play in
    /// the game and the export of a retail target then write route tables.</summary>
    internal bool RouteFilesChange => !Routes.Tables.SameRoutes(GameRoutes.Tables);

    /// <summary>
    /// The sections of a file with the active mod, as the game reads them, or null when the file does not exist. This
    /// is W of the document of the file, also of a new file of the folder of the active mod before its save. A file
    /// that W deleted has no sections. For another whole file of the active mod, W has the lower data, so the result is
    /// the file of the edit view.
    /// </summary>
    internal IReadOnlyList<Section>? WorkingSections(string path)
    {
        if (VfsPath.Normalize(path) is not { } normal)
            return null;
        if (ActiveMod is { } active &&
            (active.Files.ContainsKey(normal) || OpenedDocument(normal) is { IsWholeFile: true }))
        {
            if (OpenDocument(normal) is { IsWholeFile: true } whole)
                return whole.State.Exists ? whole.Working : null;
            if (editViewSections is not var (view, files) || !ReferenceEquals(view, EditView))
            {
                files = new Dictionary<string, IReadOnlyList<Section>?>(VfsPath.Comparer);
                editViewSections = (EditView, files);
            }
            if (!files.TryGetValue(normal, out var sections))
                files[normal] = sections = CheckView.Read(EditViewFiles, normal);
            return sections;
        }
        return OpenDocument(normal)?.Working;
    }

    /// <summary>The jump graph source of W: the files of the edit view with the edits of the session.</summary>
    private sealed class WorkingUniverse(EditSession session) : IUniverseSource
    {
        public string? UniversePath => session.Systems.UniversePath;

        public IReadOnlyList<string> SolarPaths => session.DataFiles.Paths(UniverseGraph.SolarRole);

        public IReadOnlyList<string> LockPaths => session.DataFiles.Paths(UniverseGraph.LockRole);

        public IReadOnlyList<Section>? Sections(string path) => session.WorkingSections(path);
    }

    /// <summary>A source that gives other sections for some files.</summary>
    private sealed class ChangedUniverse(IUniverseSource source, IReadOnlyDictionary<string, List<Section>> files)
        : IUniverseSource
    {
        public string? UniversePath => source.UniversePath;

        public IReadOnlyList<string> SolarPaths => source.SolarPaths;

        public IReadOnlyList<string> LockPaths => source.LockPaths;

        public IReadOnlyList<Section>? Sections(string path) =>
            files.TryGetValue(path, out var changed) ? changed : source.Sections(path);
    }

    /// <summary>A source that records each read and the section list that it gave.</summary>
    private sealed class RecordingSource(IUniverseSource source) : IUniverseSource
    {
        public List<(string Path, IReadOnlyList<Section>? Sections)> Reads { get; } = [];

        public string? UniversePath => source.UniversePath;

        public IReadOnlyList<string> SolarPaths => source.SolarPaths;

        public IReadOnlyList<string> LockPaths => source.LockPaths;

        public IReadOnlyList<Section>? Sections(string path)
        {
            var sections = source.Sections(path);
            Reads.Add((path, sections));
            return sections;
        }
    }
}
