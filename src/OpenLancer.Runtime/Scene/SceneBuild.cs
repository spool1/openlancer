using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Ini;
using LibreLancer.Data.Schema;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using Universe = LibreLancer.Data.Schema.Universe;

namespace OpenLancer.Runtime.Scene;

/// <summary>
/// The engine's view of the sections of a star system in W: the items that the engine builds from them, and the rules
/// that compare a built item with a scene item.
/// </summary>
/// <remarks>
/// <para>The object builder calls the engine (<see cref="GameItemDb.GetSystemObject"/>). The engine builds a zone, a
/// light and the system fields inline in <c>GameItemDb.InitSystems</c> (GameItemDb.cs:1393-1676), so the builders
/// here follow those lines. The retail proof compares each built item with the item of a real load.</para>
/// <para>The fast values read one line directly, as the generated parser reads it from a binary file, so a drag needs
/// no parse. A fast value never logs: a text that is not a number reads as 0, as in the engine.</para>
/// <para>A fast value is the engine value only for a line without a check error (<see cref="EditSession.Check"/>).
/// The engine reads more number forms from a text file, for example <c>100x</c> as 100, and the checks report each
/// such value as an error. The kind of a value in W does not give the form that the engine reads, because a patch line
/// of a binary file holds strings of the text parser (see <c>SectionChecks.EngineValue</c>). So a caller must not use
/// the fast values of a line with a check error: no drag starts from such a line, and the scene keeps its last state
/// for it.</para>
/// <para>The builders do not log the warnings of the engine load, for example a light without a color. The validation
/// of a save loads the data with the engine, and it reports them.</para>
/// </remarks>
public static class SceneBuild
{
    /// <summary>The <c>pos</c> of an object, a zone or a light (GameItemDb.cs:2591, :1610, :1483-1490).</summary>
    /// <param name="pos">The values of the last <c>pos</c> line, or null when the section has no line.</param>
    public static Vector3 Position(IReadOnlyList<ValueBase>? pos) => Vector(pos);

    /// <summary>
    /// The vector of a <c>pos</c> line, or the angles in degrees of a <c>rotate</c> line. No line, or a line that the
    /// generated parser ignores, gives zero, as in the engine: the origin for <c>pos</c> and no rotation for
    /// <c>rotate</c> (GameItemDb.cs:2591, :2633-2636, :1610, :1647-1661).
    /// </summary>
    internal static Vector3 Vector(IReadOnlyList<ValueBase>? line) => ReadVector(line, false) ?? Vector3.Zero;

    /// <summary>The rotation of an object from its <c>rotate</c> line (GameItemDb.cs:2633-2636).</summary>
    public static Quaternion ObjectRotation(IReadOnlyList<ValueBase>? rotate) =>
        ReadVector(rotate, false) is { } degrees
            ? MathHelper.MatrixFromEulerDegrees(degrees).ExtractRotation()
            : Quaternion.Identity;

    /// <summary>The rotation matrix and the angles of a zone from its <c>rotate</c> line (GameItemDb.cs:1647-1661).</summary>
    public static Matrix4x4 ZoneRotation(IReadOnlyList<ValueBase>? rotate, out Vector3 radians) =>
        RotationOf(ReadVector(rotate, false), out radians);

    /// <summary>
    /// The first check error of a line that the fast values of a section kind read, or null. The fast values of such a
    /// line are not the engine values (see the remarks of <see cref="SceneBuild"/>).
    /// </summary>
    /// <param name="problems">The problems of the section (see <see cref="EditSession.Check"/>).</param>
    /// <param name="kind"><see cref="SceneSection.Object"/>, <see cref="SceneSection.Zone"/> or
    /// <see cref="SceneSection.LightSource"/>.</param>
    internal static Diagnostic? TransformError(IEnumerable<Diagnostic> problems, SceneSection kind)
    {
        string[] keys = kind switch
        {
            SceneSection.Object => ["pos", "rotate"],
            SceneSection.Zone => ["pos", "rotate", "shape", "size"],
            _ => ["pos"],
        };
        return problems.FirstOrDefault(p => p.Severity == DiagnosticSeverity.Error && p.Key != null &&
                                            keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The shape of a zone from its <c>shape</c> line (GameItemDb.cs:1663).</summary>
    public static ShapeKind ZoneShape(IReadOnlyList<ValueBase>? shape) =>
        ZoneShapes.ShapeOf(
            shape is { Count: > 0 } && Enum.TryParse<Universe.ZoneShape>(shape[0].ToString(), true, out var value)
                ? value
                : null);

    /// <summary>
    /// The size of a zone from its <c>size</c> line (GameItemDb.cs:1664-1673). One value <c>v</c> gives (v, v, v), two
    /// values give (a, b, 0), and no line gives (1, 1, 1). A ring has the size (outer, height, inner), from the line
    /// (outer, inner, height).
    /// </summary>
    public static Vector3 ZoneSize(IReadOnlyList<ValueBase>? size, ShapeKind shape) =>
        ZoneShapes.SizeOf(ReadVector(size, true), shape);

    /// <summary>
    /// Builds an object with <see cref="GameItemDb.GetSystemObject"/>. Null when the archetype does not exist, because
    /// the engine then drops the object. The check runs first, so the engine logs no error line.
    /// </summary>
    /// <param name="system">The nickname of the system, for the log lines of the engine.</param>
    /// <param name="problem">Why the result is null, or null.</param>
    public static SystemObject? Object(GameItemDb items, string system, Universe.SystemObject ini, out string? problem)
    {
        if (items.Archetypes.Get(ini.Archetype) == null)
        {
            problem = ini.Archetype == null
                ? "The game drops this object: it has no archetype."
                : $"The game drops this object: the archetype '{ini.Archetype}' does not exist.";
            return null;
        }
        problem = null;
        return items.GetSystemObject(system, ini);
    }

    /// <summary>Builds a zone as <c>InitSystems</c> does (GameItemDb.cs:1601-1673), in the same order.</summary>
    public static Zone Zone(Universe.Zone ini)
    {
        var zone = new Zone
        {
            Nickname = ini.Nickname,
            IdsName = ini.IdsName,
            IdsInfo = ini.IdsInfo,
            EdgeFraction = ini.EdgeFraction ?? 0.25f,
            PropertyFlags = (ZonePropFlags)ini.PropertyFlags,
            PropertyFogColor = ini.PropertyFogColor,
            VisitFlags = (VisitFlags)(ini.Visit ?? 0),
            Position = ini.Pos ?? Vector3.Zero,
            Sort = ini.Sort ?? 0,
            Music = ini.Music,
            Spacedust = ini.Spacedust,
            SpacedustMaxParticles = ini.SpacedustMaxParticles,
            Interference = ini.Interference,
            PowerModifier = ini.PowerModifier,
            DragModifier = ini.DragModifier,
            Comment = ini.Comment is not null ? CommentEscaping.Unescape(ini.Comment) : null,
            LaneId = ini.LaneId,
            TradelaneAttack = ini.TradelaneAttack,
            TradelaneDown = ini.TradelaneDown,
            Damage = ini.Damage,
            Toughness = ini.Toughness,
            Density = ini.Density,
            PopulationAdditive = ini.PopulationAdditive,
            MissionEligible = ini.MissionEligible,
            MaxBattleSize = ini.MaxBattleSize,
            PopType = ini.PopType,
            ReliefTime = ini.ReliefTime,
            RepopTime = ini.RepopTime,
            AttackIds = ini.AttackIds,
            MissionType = ini.MissionType,
            PathLabel = ini.PathLabel,
            Usage = ini.Usage,
            VignetteType = ini.VignetteType,
            Encounters = ini.Encounters.ToArray(),
            DensityRestrictions = ini.DensityRestrictions.ToArray(),
        };
        zone.RotationMatrix = RotationOf(ini.Rotate, out var radians);
        zone.RotationAngles = radians;
        zone.Shape = ZoneShapes.ShapeOf(ini.Shape);
        zone.Size = ZoneShapes.SizeOf(ini.Size, zone.Shape);
        return zone;
    }

    /// <summary>The rotation of a zone (see <see cref="ZoneShapes.RotationOf"/>), with its angles in radians. No
    /// rotation gives the angles zero.</summary>
    private static Matrix4x4 RotationOf(Vector3? degrees, out Vector3 radians)
    {
        radians = degrees is { } angles
            ? new Vector3(MathHelper.DegreesToRadians(angles.X), MathHelper.DegreesToRadians(angles.Y),
                MathHelper.DegreesToRadians(angles.Z))
            : Vector3.Zero;
        return ZoneShapes.RotationOf(degrees);
    }

    /// <summary>
    /// Builds a light as <c>InitSystems</c> does (GameItemDb.cs:1468-1516). Null when the engine cannot load the
    /// attenuation curve, because the engine then throws (GameItemDb.cs:1352). The check runs first, so this method
    /// never throws for a curve.
    /// </summary>
    /// <param name="problem">Why the result is null, or null.</param>
    public static LightSource? Light(GameItemDb items, Universe.LightSource ini, out string? problem)
    {
        var light = new RenderLight
        {
            Color = ini.Color is { } color ? new Color3f(color.R, color.G, color.B) : Color3f.White,
            Position = ini.Pos ?? Vector3.Zero,
            Range = ini.Range ?? 200000,
            Direction = ini.Direction ?? new Vector3(0, 0, 1),
            Kind = (ini.Type ?? Universe.LightType.Point) == Universe.LightType.Point ? LightKind.Point : LightKind.Directional,
            Attenuation = ini.Attenuation ?? Vector3.UnitY,
        };
        if (ini.AttenCurve != null)
        {
            if (CurveProblem(items.Ini.Graphs, ini.AttenCurve, out var graph) is { } curveProblem)
            {
                problem = curveProblem;
                return null;
            }
            light.Kind = LightKind.PointAttenCurve;
            light.Attenuation = ApproximateCurve.GetQuadraticFunction(graph!.Points.ToArray());
        }
        problem = null;
        return new LightSource { Light = light, AttenuationCurveName = ini.AttenCurve, Nickname = ini.Nickname };
    }

    /// <summary>
    /// The problem of an attenuation curve, or null when the engine can load it. The engine finds a curve only when
    /// exactly one graph has the name (GraphIni.cs:18-21), and it cannot make a curve from a graph without points
    /// (ApproximateCurve.cs:20-21). In both cases the engine throws when it loads the light (GameItemDb.cs:1352).
    /// </summary>
    /// <param name="graph">The graph that the engine finds for the name, or null.</param>
    internal static string? CurveProblem(GraphIni graphs, string curve, out FloatGraph? graph)
    {
        graph = graphs.FindFloatGraph(curve);
        return graph switch
        {
            null => $"No graph has the name '{curve}', or more than one graph has it. {CurveStop}",
            { Points.Count: 0 } => $"The graph '{curve}' has no points. {CurveStop}",
            _ => null,
        };
    }

    private const string CurveStop = "The engine stops with an error when it loads the game data.";

    /// <summary>A starsphere model from a <c>[Background]</c> path, as <c>InitSystems</c> resolves it
    /// (GameItemDb.cs:1462-1464, :2084-2101). Null for no path. The engine logs a path that does not exist.</summary>
    public static ResolvedModel? Starsphere(GameItemDb items, string? file) =>
        string.IsNullOrWhiteSpace(file) ? null : new ResolvedModel { ModelFile = items.DataPath(file), SourcePath = file };

    /// <summary>
    /// Reads the sections of a system in W as the engine reads them for the fields of the system: the
    /// <c>[system]</c> of <c>universe.ini</c>, and the <c>[SystemInfo]</c>, <c>[Archetype]</c>, <c>[Background]</c>,
    /// <c>[Ambient]</c>, <c>[Dust]</c>, <c>[Music]</c>, <c>[EncounterParameters]</c> and <c>[TexturePanels]</c>
    /// sections of its file. As in the generated parser, the last section of a single type wins, and a section that
    /// does not parse is left out. The result has no objects, zones, lights, fields or nebulae.
    /// </summary>
    /// <returns>Null when W has no single <c>[system]</c> with the nickname, or when it does not parse.</returns>
    public static Universe.StarSystem? ParseSystem(EditSession session, string systemNickname)
    {
        if (session.SystemSection(systemNickname) is not { } universe ||
            session.Parse<Universe.StarSystem>(universe, Universe.StarSystem.TryParse) is not { } system)
            return null;
        if (session.SystemPath(systemNickname) is not { } path)
            return system;
        foreach (var section in session.Sections(path))
        {
            switch (SceneFields.SectionOf(section.Id.Type, false))
            {
                case SceneSection.SystemInfo:
                    system.Info = session.Parse<Universe.SystemInfo>(section, Universe.SystemInfo.TryParse) ?? system.Info;
                    break;
                case SceneSection.Preloads:
                    if (session.Parse<Universe.SystemPreloads>(section, Universe.SystemPreloads.TryParse) is { } preloads)
                        system.Preloads.Add(preloads);
                    break;
                case SceneSection.Background:
                    system.Background = session.Parse<Universe.SystemBackground>(section, Universe.SystemBackground.TryParse) ??
                                        system.Background;
                    break;
                case SceneSection.Ambient:
                    system.Ambient =
                        session.Parse<Universe.SystemAmbient>(section, Universe.SystemAmbient.TryParse) ?? system.Ambient;
                    break;
                case SceneSection.Dust:
                    system.Dust = session.Parse<Universe.SystemDust>(section, Universe.SystemDust.TryParse) ?? system.Dust;
                    break;
                case SceneSection.Music:
                    system.Music = session.Parse<Universe.SystemMusic>(section, Universe.SystemMusic.TryParse) ?? system.Music;
                    break;
                case SceneSection.EncounterParameters:
                    if (session.Parse<Universe.EncounterParameter>(section, Universe.EncounterParameter.TryParse) is { } value)
                        system.EncounterParameters.Add(value);
                    break;
                case SceneSection.TexturePanels:
                    system.TexturePanels = session.Parse<Universe.TexturePanelsRef>(section, Universe.TexturePanelsRef.TryParse) ??
                                           system.TexturePanels;
                    break;
            }
        }
        return system;
    }

    /// <summary>
    /// Builds the fields of a system as <c>InitSystems</c> does (GameItemDb.cs:1396-1464). The result has no objects,
    /// zones, lights, fields or nebulae.
    /// </summary>
    /// <param name="ini">The sections of the system, for example from <see cref="ParseSystem"/>.</param>
    public static StarSystem StarSystem(GameItemDb items, Universe.StarSystem ini)
    {
        var system = new StarSystem
        {
            SourceFile = ini.File,
            LocalFaction = items.Factions.Get(ini.Info?.LocalFaction),
            UniversePosition = ini.Pos ?? Vector2.Zero,
            AmbientColor = ini.Ambient?.Color ?? Color3f.Black,
            IdsName = ini.IdsName,
            IdsInfo = ini.IdsInfo,
            Nickname = ini.Nickname,
            Visit = (VisitFlags)ini.Visit,
        };
        system.CRC = FLHash.CreateID(system.Nickname);
        system.MsgIdPrefix = ini.MsgIdPrefix;
        system.BackgroundColor = ini.Info?.SpaceColor ?? Color4.Black;
        system.MusicSpace = ini.Music?.Space;
        system.MusicBattle = ini.Music?.Battle;
        system.MusicDanger = ini.Music?.Danger;
        system.Spacedust = ini.Dust?.Spacedust;
        system.SpacedustMaxParticles = ini.Dust?.SpacedustMaxParticles ?? 0;
        system.FarClip = ini.Info?.SpaceFarClip ?? 20000f;
        system.NavMapScale = ini.NavMapScale;
        foreach (var encounter in ini.EncounterParameters)
            system.EncounterParameters.Add(new EncounterParameters(encounter.Nickname, encounter.Filename));
        system.Preloads =
        [
            ..ini.Preloads.SelectMany(p => p.ArchetypeShip).Select(a => new PreloadObject(PreloadType.Ship, a)),
            ..ini.Preloads.SelectMany(p => p.ArchetypeSimple).Select(a => new PreloadObject(PreloadType.Simple, a)),
            ..ini.Preloads.SelectMany(p => p.ArchetypeEquipment).Select(a => new PreloadObject(PreloadType.Equipment, a)),
            ..ini.Preloads.SelectMany(p => p.ArchetypeSnd).Select(a => new PreloadObject(PreloadType.Sound, a)),
            ..ini.Preloads.SelectMany(p => p.ArchetypeSolar).Select(a => new PreloadObject(PreloadType.Solar, a)),
            ..ini.Preloads.SelectMany(p => p.ArchetypeVoice)
                .Select(a => new PreloadObject(PreloadType.Voice, a.Select(x => new HashValue(x)).ToArray())),
        ];
        if (ini.TexturePanels != null)
            system.TexturePanelsFiles.AddRange(ini.TexturePanels.Files);
        system.StarsBasic = Starsphere(items, ini.Background?.BasicStarsPath);
        system.StarsComplex = Starsphere(items, ini.Background?.ComplexStarsPath);
        system.StarsNebula = Starsphere(items, ini.Background?.NebulaePath);
        return system;
    }

    /// <summary>
    /// True when a built object and a scene object have the same values. The compare covers each field except
    /// <see cref="SystemObject.IdsLeft"/> and <see cref="SystemObject.IdsRight"/>, which the engine sets in a walk over
    /// the lanes of the whole system (GameItemDb.cs:1537-1595).
    /// </summary>
    /// <remarks>
    /// The dock compares by its fields, because the builder makes a new dock and a clone keeps the dock that it was
    /// made from. <c>space_costume</c> compares by its values, because a clone gets a new array. The item references
    /// (archetype, base, loadout and the others) compare by reference, because the builder and the scene use the same
    /// <see cref="GameItemDb"/>.
    /// </remarks>
    public static bool SameObject(SystemObject built, SystemObject clone) =>
        built.Nickname == clone.Nickname &&
        built.IdsName == clone.IdsName &&
        built.IdsInfo == clone.IdsInfo &&
        ReferenceEquals(built.Base, clone.Base) &&
        ReferenceEquals(built.Archetype, clone.Archetype) &&
        ReferenceEquals(built.Star, clone.Star) &&
        built.Position.Equals(clone.Position) &&
        built.Spin.Equals(clone.Spin) &&
        built.Rotation.Equals(clone.Rotation) &&
        ReferenceEquals(built.Loadout, clone.Loadout) &&
        SameDock(built.Dock, clone.Dock) &&
        ReferenceEquals(built.Reputation, clone.Reputation) &&
        built.Visit == clone.Visit &&
        built.TradelaneSpaceName == clone.TradelaneSpaceName &&
        ReferenceEquals(built.Pilot, clone.Pilot) &&
        ReferenceEquals(built.Faction, clone.Faction) &&
        built.DifficultyLevel == clone.DifficultyLevel &&
        built.Behavior == clone.Behavior &&
        built.AtmosphereRange.Equals(clone.AtmosphereRange) &&
        built.MsgIdPrefix == clone.MsgIdPrefix &&
        Nullable.Equals(built.BurnColor, clone.BurnColor) &&
        Nullable.Equals(built.AmbientColor, clone.AmbientColor) &&
        built.Parent == clone.Parent &&
        built.Voice == clone.Voice &&
        SameValues(built.SpaceCostume, clone.SpaceCostume) &&
        built.JumpEffect == clone.JumpEffect &&
        built.RingZone == clone.RingZone &&
        built.RingFile == clone.RingFile &&
        built.Comment == clone.Comment;

    /// <summary>
    /// Copies the values of a built object into a scene object: each field that <see cref="SameObject"/> compares. The
    /// scene object keeps its <see cref="SystemObject.IdsLeft"/> and <see cref="SystemObject.IdsRight"/>, and it stays
    /// the object that its world object and the selection hold.
    /// </summary>
    public static void CopyObject(SystemObject built, SystemObject clone)
    {
        clone.Nickname = built.Nickname;
        clone.IdsName = built.IdsName;
        clone.IdsInfo = built.IdsInfo;
        clone.Base = built.Base;
        clone.Archetype = built.Archetype;
        clone.Star = built.Star;
        clone.Position = built.Position;
        clone.Spin = built.Spin;
        clone.Rotation = built.Rotation;
        clone.Loadout = built.Loadout;
        clone.Dock = built.Dock;
        clone.Reputation = built.Reputation;
        clone.Visit = built.Visit;
        clone.TradelaneSpaceName = built.TradelaneSpaceName;
        clone.Pilot = built.Pilot;
        clone.Faction = built.Faction;
        clone.DifficultyLevel = built.DifficultyLevel;
        clone.Behavior = built.Behavior;
        clone.AtmosphereRange = built.AtmosphereRange;
        clone.MsgIdPrefix = built.MsgIdPrefix;
        clone.BurnColor = built.BurnColor;
        clone.AmbientColor = built.AmbientColor;
        clone.Parent = built.Parent;
        clone.Voice = built.Voice;
        clone.SpaceCostume = built.SpaceCostume;
        clone.JumpEffect = built.JumpEffect;
        clone.RingZone = built.RingZone;
        clone.RingFile = built.RingFile;
        clone.Comment = built.Comment;
    }

    /// <summary>True when a built light and a scene light have the same nickname, the same render values and the same
    /// curve name.</summary>
    public static bool SameLight(LightSource built, LightSource clone) =>
        built.Nickname == clone.Nickname &&
        built.Light == clone.Light &&
        built.AttenuationCurveName == clone.AttenuationCurveName;

    /// <summary>
    /// True when a built system and a scene system have the same values in the fields that the sections of the system
    /// set (see <see cref="SceneBuild.StarSystem(GameItemDb, Universe.StarSystem)"/>). The compare does not cover the
    /// items of the system or the CRC, which a clone of the engine does not copy. A clone has no preloads where the
    /// engine has an empty list, so both compare equal.
    /// </summary>
    public static bool SameSystem(StarSystem built, StarSystem clone) =>
        built.Nickname == clone.Nickname &&
        built.SourceFile == clone.SourceFile &&
        built.IdsName == clone.IdsName &&
        built.IdsInfo == clone.IdsInfo &&
        built.UniversePosition.Equals(clone.UniversePosition) &&
        built.MsgIdPrefix == clone.MsgIdPrefix &&
        built.Visit == clone.Visit &&
        ReferenceEquals(built.LocalFaction, clone.LocalFaction) &&
        built.BackgroundColor.Equals(clone.BackgroundColor) &&
        built.AmbientColor.Equals(clone.AmbientColor) &&
        SameStarspheres(built, clone) &&
        built.MusicSpace == clone.MusicSpace &&
        built.MusicDanger == clone.MusicDanger &&
        built.MusicBattle == clone.MusicBattle &&
        built.FarClip.Equals(clone.FarClip) &&
        built.NavMapScale.Equals(clone.NavMapScale) &&
        built.Spacedust == clone.Spacedust &&
        built.SpacedustMaxParticles == clone.SpacedustMaxParticles &&
        built.EncounterParameters.SequenceEqual(clone.EncounterParameters) &&
        built.TexturePanelsFiles.SequenceEqual(clone.TexturePanelsFiles) &&
        (built.Preloads ?? []).SequenceEqual(clone.Preloads ?? [], PreloadComparer.Instance);

    /// <summary>
    /// Copies the values of a built system into a scene system: each field that <see cref="SameSystem"/> compares. The
    /// scene system keeps its items and its CRC.
    /// </summary>
    public static void CopySystem(StarSystem built, StarSystem clone)
    {
        clone.Nickname = built.Nickname;
        clone.SourceFile = built.SourceFile;
        clone.IdsName = built.IdsName;
        clone.IdsInfo = built.IdsInfo;
        clone.UniversePosition = built.UniversePosition;
        clone.MsgIdPrefix = built.MsgIdPrefix;
        clone.Visit = built.Visit;
        clone.LocalFaction = built.LocalFaction;
        clone.BackgroundColor = built.BackgroundColor;
        clone.AmbientColor = built.AmbientColor;
        clone.StarsBasic = built.StarsBasic;
        clone.StarsComplex = built.StarsComplex;
        clone.StarsNebula = built.StarsNebula;
        clone.MusicSpace = built.MusicSpace;
        clone.MusicDanger = built.MusicDanger;
        clone.MusicBattle = built.MusicBattle;
        clone.FarClip = built.FarClip;
        clone.NavMapScale = built.NavMapScale;
        clone.Spacedust = built.Spacedust;
        clone.SpacedustMaxParticles = built.SpacedustMaxParticles;
        clone.EncounterParameters = built.EncounterParameters;
        clone.TexturePanelsFiles = built.TexturePanelsFiles;
        clone.Preloads = built.Preloads;
    }

    /// <summary>True when two systems have the same starsphere models.</summary>
    internal static bool SameStarspheres(StarSystem a, StarSystem b) =>
        SameModel(a.StarsBasic, b.StarsBasic) && SameModel(a.StarsComplex, b.StarsComplex) &&
        SameModel(a.StarsNebula, b.StarsNebula);

    /// <summary>True when two docks have the same values. A new dock and a dock of a clone are different
    /// objects.</summary>
    internal static bool SameDock(DockAction? a, DockAction? b) =>
        a == null || b == null
            ? a == b
            : a.Kind == b.Kind && a.Target == b.Target && a.TargetLeft == b.TargetLeft && a.Exit == b.Exit &&
              a.Tunnel == b.Tunnel;

    private static bool SameModel(ResolvedModel? a, ResolvedModel? b) =>
        a == null || b == null
            ? a == b
            : a.ModelFile == b.ModelFile && a.SourcePath == b.SourcePath && SameValues(a.LibraryFiles, b.LibraryFiles);

    /// <summary>True when two arrays have the same values. No array and an empty array are the same.</summary>
    private static bool SameValues<T>(T[]? a, T[]? b) where T : IEquatable<T> =>
        (a ?? []).AsSpan().SequenceEqual(b ?? []);

    /// <summary>
    /// The values of a <see cref="Vector3"/> line as the generated parser reads them (SectionParser.cs:208-260 of the
    /// generator), or null when the parser leaves the field without a value. The one value 0 is the zero vector.
    /// </summary>
    /// <param name="size">True for the size mode, which also reads one value (v, v, v) and two values (a, b, 0).</param>
    private static Vector3? ReadVector(IReadOnlyList<ValueBase>? line, bool size)
    {
        if (line is not { Count: > 0 })
            return null;
        if (line.Count == 1 && Float(line[0]) == 0)
            return Vector3.Zero;
        return line.Count switch
        {
            >= 3 => new Vector3(Float(line[0]), Float(line[1]), Float(line[2])),
            1 when size => new Vector3(Float(line[0])),
            2 when size => new Vector3(Float(line[0]), Float(line[1]), 0),
            _ => null,
        };
    }

    /// <summary>The float that the engine reads from a value. A text that is not a number reads as 0, as in the
    /// engine, but without its log line.</summary>
    private static float Float(ValueBase value) => TryFloat(value, out var result) ? result : 0;

    /// <summary>
    /// Reads a value as the engine reads a float from a binary file, but without a log line. False for a text that
    /// is not a number: the engine then logs an error and reads 0 (StringValue.cs:71-82).
    /// </summary>
    internal static bool TryFloat(ValueBase value, out float result) =>
        value is StringValue
            ? float.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)
            : value.TryToSingle(out result);

    /// <summary>Compares preloads by type and by their values. The record compares its array by reference.</summary>
    private sealed class PreloadComparer : IEqualityComparer<PreloadObject>
    {
        public static readonly PreloadComparer Instance = new();

        public bool Equals(PreloadObject? a, PreloadObject? b) =>
            a == null || b == null ? a == b : a.Type == b.Type && a.Values.AsSpan().SequenceEqual(b.Values);

        public int GetHashCode(PreloadObject obj) => obj.Type.GetHashCode();
    }
}
