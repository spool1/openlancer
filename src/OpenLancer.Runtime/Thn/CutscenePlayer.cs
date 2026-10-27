using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using LibreLancer;
using LibreLancer.Client;
using LibreLancer.Client.Components;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.World;
using LibreLancer.Data.Schema.Missions;
using LibreLancer.Render;
using LibreLancer.Resources;
using LibreLancer.Thn;
using LibreLancer.Utf.Anm;
using LibreLancer.Utf.Dfm;
using LibreLancer.World;
using OpenLancer.Runtime.Play;

namespace OpenLancer.Runtime.Thn;

/// <summary>The steps of the players of one preview, for the debug readout and the bench.</summary>
public sealed class PreviewMeter
{
    /// <summary>The steps that the players ran.</summary>
    public long Steps { get; private set; }

    /// <summary>The time of those steps.</summary>
    public TimeSpan StepTime { get; private set; }

    internal void Add(TimeSpan time)
    {
        Steps++;
        StepTime += time;
    }
}

/// <summary>A scene object as the Free view of the editor draws it (section 7.6 of the M8 design).</summary>
/// <param name="Name">The name of the object: the name of its entity.</param>
/// <param name="Type">The type of the entity of the object.</param>
/// <param name="Pose">The pose now, as <c>GetTransform</c> gives it (ThnSceneObject.cs:34). After a step that failed, the
/// pose of the last update.</param>
/// <param name="FovH">The horizontal field of view of a camera now, as THN gives it: half the angle in degrees for a
/// 4:3 screen (ThnCamera.cs:68-75). Null for an object that is not a camera.</param>
/// <param name="LightColor">The color of a light now, or null.</param>
/// <param name="Path">The points of a motion path in the scene, from its start to its end, or null.</param>
public sealed record PreviewObject(string Name, EntityTypes Type, Transform3D Pose, float? FovH, Color3f? LightColor,
    IReadOnlyList<Vector3>? Path);

/// <summary>
/// The models of a character of a scene, for a preview that makes its own copy of the character, for example the clip
/// preview of the editor.
/// </summary>
/// <remarks>The models are the live objects of the scene, and the scene and the resource cache share them. A caller must
/// not change them.</remarks>
public sealed record CharacterModels(DfmFile Body, DfmFile? Head, DfmFile? LeftHand, DfmFile? RightHand,
    Accessory? Accessory, RigidModel? AccessoryModel)
{
    /// <summary>The models of the character of an object, or null when the object is not a character.</summary>
    public static CharacterModels? Of(GameObject? obj) =>
        obj?.RenderComponent is CharacterRenderer { Skeleton: var skeleton } character
            ? new CharacterModels(skeleton.Body, skeleton.Head, skeleton.LeftHand, skeleton.RightHand,
                character.Accessory, character.AccessoryModel)
            : null;
}

/// <summary>The pose and the lens of the active THN camera, as <c>ThnCamera</c> reads them (ThnCamera.cs:85).</summary>
/// <param name="FovH">Half the horizontal angle for a 4:3 screen, in degrees.</param>
public readonly record struct ShotPose(Vector3 Position, Quaternion Orientation, float FovH);

/// <summary>
/// The player of the editor's preview: one engine <see cref="Cutscene"/>, built from a <see cref="PreviewPlan"/> as the
/// game builds a room or a cutscene (section 8.1 of the M8 design). It uses only the public members of
/// <see cref="Cutscene"/>. It has no sound manager, so it plays no sound (section 8.7).
/// </summary>
public sealed class CutscenePlayer : ICutscenePlayer
{
    /// <summary>The name of the marker at the origin where the stand-in of a fidget without a room stands.</summary>
    private const string OriginMarker = "OpenLancer_FidgetOrigin";

    /// <summary>The points of a motion path that <see cref="Find"/> gives.</summary>
    private const int PathPoints = 64;

    private readonly Cutscene cutscene;
    private readonly PreviewMeter? meter;
    private readonly GameObject[] scriptObjects;
    private bool disposed;

    // A step failed. The attachments of that step can stay on the objects, and a chain of them can be too deep to
    // resolve (OL-0054). So the poses come from the last update.
    private bool stopped;

    private CutscenePlayer(Cutscene cutscene, PreviewMeter? meter, bool inHostWorld, GameObject[] scriptObjects,
        string? mainObjectNote)
    {
        this.cutscene = cutscene;
        this.meter = meter;
        this.scriptObjects = scriptObjects;
        InHostWorld = inHostWorld;
        MainObjectNote = mainObjectNote;
    }

    /// <summary>True when the scene plays in a world that the host owns and draws, for example the system of a space
    /// script. Then the cutscene has no renderer, and the host draws the world.</summary>
    public bool InHostWorld { get; }

    /// <summary>The objects that the scripts made in the world of the host. Each step updates them. Empty when the
    /// player owns its world.</summary>
    public IReadOnlyList<GameObject> ScriptObjects => scriptObjects;

    /// <summary>The banner about the <c>main_object</c> marker of a space script in its system, or null.</summary>
    public string? MainObjectNote { get; }

    public double Time => cutscene.CurrentTime;

    /// <summary>
    /// The name of the scene object that the THN camera looks through, or null when no scene object is the camera. A
    /// scene without a camera keeps the default object of <see cref="ThnCamera"/>.
    /// </summary>
    public string? CameraName =>
        cutscene.CameraHandle is ThnCamera { Object: { } camera } &&
        ReferenceEquals(cutscene.GetObject(camera.Name), camera)
            ? camera.Name
            : null;

    /// <summary>
    /// Builds the scene of a plan at script time 0, in the order of the game (section 8.2): the set script, the
    /// substitutions and the player ship in the context, the fixtures that play characters of the cutscene,
    /// <c>BeginScene</c>, then the room camera and the ship on its marker, or the stand-in of a fidget. Last, an update
    /// of 0 s applies the poses of time 0, as the first frame of a room scene does (RoomGameplay.cs:1432, :1545).
    /// </summary>
    /// <param name="scripts">Gives the script of an install path of the plan: W for the open script, else the script of
    /// the session.</param>
    /// <param name="viewport">The first viewport of the THN camera. The view sets it again for each draw.</param>
    /// <param name="meter">Counts the steps, or null.</param>
    /// <param name="hostWorld">The world of the system of a space plan, or null. With a system in the plan, the scripts
    /// make their objects in this world, and the main object stands at its start. The static stand-ins of a local
    /// layout follow it (decisions D644 and D658). The player does not dispose this world.</param>
    /// <exception cref="Exception">The engine cannot build the scene, or the room camera is not a CAMERA entity. The
    /// message tells why.</exception>
    public static CutscenePlayer Build(PreviewPlan plan, Func<string, ThnScript> scripts, GameDataManager data,
        GameResourceManager resources, Game game, Rectangle viewport, PreviewMeter? meter = null,
        GameWorld? hostWorld = null)
    {
        var items = data.Items;
        var room = plan.Base != null ? items.Bases.Get(plan.Base)?.Rooms.Get(plan.Room) : null;
        var context = new ThnScriptContext(plan.SetScript != null ? scripts(plan.SetScript) : null);
        foreach (var (key, value) in plan.Substitutions)
            context.Substitutions[key] = value;

        // The player ship, as RoomGameplay.cs:1036-1043 sets it.
        var ship = PlayerShip(items, resources, plan.ShipPackage);
        if (ship != null)
        {
            context.PlayerShip = ship;
            if (ship.CoreEquipment.Engine != null)
            {
                context.PlayerEngine = ship.CoreEquipment.Engine;
                context.PlayerEngine.Active = false;
            }
        }

        var space = plan.System != null && hostWorld != null;
        var cutscene = space
            ? new Cutscene(context, hostWorld!, data, resources, null, viewport, game)
            : new Cutscene(context, data, resources, null, viewport, game);
        string? note = null;
        GameObject[] made = [];
        try
        {
            var played = plan.Scripts.Select(scripts).ToList();
            Transform3D? frame = null;
            if (space)
                (frame, note) = MainObject(plan, played, context);
            var actors = new List<ThnSceneObject>();
            if (plan.Cutscene != null && room != null)
            {
                // The fixtures that play characters of the cutscene, with the [Char] lines of an RTC
                // (RoomGameplay.cs:1443-1456).
                var chars = plan.Encounter != null ? new StoryCutsceneIni(plan.Encounter, items.VFS).Chars : null;
                var script = played[^1];
                var animations = data.GetCharacterAnimations();
                foreach (var (npc, entity) in BaseNpcPopulation.FindFixtureActors(room, script, chars))
                {
                    if (FixtureActor(resources, animations, npc, entity) is { } actor)
                        actors.Add(actor);
                }
            }

            cutscene.BeginScene(played, actors);
            if (frame is { } mainMarker && plan.MainObjectStart is { } start)
                PlaceStandIns(cutscene, played, mainMarker, new Transform3D(start.Position, start.Orientation));
            if (plan.RoomCamera != null)
            {
                RequireCamera(cutscene.GetObject(plan.RoomCamera), plan.RoomCamera);
                cutscene.SetCamera(plan.RoomCamera);
            }

            if (plan.ShipMarker != null && ship != null && cutscene.GetObject(plan.ShipMarker) is { } marker)
                PlaceShip(cutscene, ship, marker);
            if (plan.Fidget != null)
                AddFidget(cutscene, scripts(plan.Fidget), plan.FidgetSpot, room, data, resources);
            // The events of time 0 ran in BeginScene. The poses that they set apply in an update (Cutscene.cs:424). The
            // update adds no time, so the clock counts the player at time 0.
            cutscene._Update(0);
            if (space)
                made = GameObjectsOf(cutscene);
        }
        catch
        {
            Free(cutscene, !space);
            throw;
        }

        return new CutscenePlayer(cutscene, meter, space, made, note);
    }

    /// <summary>
    /// Gives the context a stand-in of the main object at its start, when the <c>main_object</c> marker of the scripts
    /// stands at the origin and the mission data gives a start (decision D644). The stand-in is not in the world. Gives
    /// the pose of the marker in the script when the marker moves, and the banner of the main object.
    /// </summary>
    private static (Transform3D? Marker, string? Note) MainObject(PreviewPlan plan, List<ThnScript> played,
        ThnScriptContext context)
    {
        var entity = played.SelectMany(s => s.Entities.Values)
            .FirstOrDefault(e => e.MainObject && e.Type == EntityTypes.Marker);
        if (entity == null || plan.MainObject == null)
            return (null, null);

        var note = SpaceFrame.MainObjectNote(entity.Name, entity.Position, plan.MainObject, plan.MainObjectStart != null);
        if (!SpaceFrame.MovesMarker(entity.Position) || plan.MainObjectStart is not { } start)
            return (null, note);

        var stand = new GameObject { Name = new ObjectName(plan.MainObject), Nickname = plan.MainObject };
        stand.SetTransform(new Transform3D(start.Position, start.Orientation));
        context.MainObject = stand;
        return (new Transform3D(entity.Position ?? Vector3.Zero, entity.Rotation), note);
    }

    /// <summary>The static stand-ins of a local layout keep their pose relative to the marker (decision D658). The main
    /// object stands still, so the build places them one time. The update of time 0 gives the models the pose.</summary>
    private static void PlaceStandIns(Cutscene cutscene, List<ThnScript> played, Transform3D marker,
        Transform3D start)
    {
        foreach (var name in played.SelectMany(SpaceFrame.StaticStandIns))
        {
            if (cutscene.GetObject(name) is not { PosFromObject: false } obj)
                continue;
            var pose = SpaceFrame.Place(new Transform3D(obj.Translate, obj.Rotate), marker, start);
            obj.Translate = pose.Position;
            obj.Rotate = pose.Orientation;
        }
    }

    /// <summary>
    /// The objects that the scripts made in the world of the host: the object of each scene object that does not read
    /// its pose from an object of the host. Each model gets the lighting of a game object (decision D645): light group
    /// 0, the dynamic lights and the ambient of the system, and the fog.
    /// </summary>
    private static GameObject[] GameObjectsOf(Cutscene cutscene)
    {
        var objects = cutscene.AllObjects.Where(o => o is { Object: not null, PosFromObject: false })
            .Select(o => o.Object!).Distinct().ToArray();
        // The engine sets the lighting of a script object on its root renderer only (ThnScriptInstance.cs).
        foreach (var renderer in objects.Select(o => o.RenderComponent).OfType<ModelRenderer>())
        {
            renderer.LightGroup = 0;
            renderer.LitDynamic = true;
            renderer.LitAmbient = true;
            renderer.NoFog = false;
        }

        return objects;
    }

    /// <summary>
    /// One fixed step: <c>Cutscene._Update</c> (Cutscene.cs:398-432). In the world of a host, the cutscene does not
    /// step the world, so the step updates the objects of the scripts, for example the animations of the characters.
    /// It never steps the objects of the host or of another player. After a step that throws, <see cref="Find"/> gives
    /// the poses of the last update.
    /// </summary>
    public void Step(double seconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var start = Stopwatch.GetTimestamp();
        try
        {
            cutscene._Update(seconds);
            foreach (var obj in scriptObjects)
                obj.Update(seconds, cutscene.World);
        }
        catch
        {
            stopped = true;
            throw;
        }

        meter?.Add(Stopwatch.GetElapsedTime(start));
    }

    /// <summary>The pose and the lens of the active THN camera, as the THN camera reads them, or null when no scene
    /// object is the camera.</summary>
    public ShotPose? ShotLens()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return CameraName != null && cutscene.CameraHandle is ThnCamera { Object: { Camera: { } lens } camera }
            ? new ShotPose(camera.Translate, camera.Rotate, lens.FovH)
            : null;
    }

    /// <summary>
    /// Makes a CAMERA entity of the scene the THN camera, as a <c>SET_CAMERA</c> event does (Cutscene.cs:508-513). The
    /// next draw shows its view with its lens and its planes. A later step can change the camera again, when an event
    /// sets another camera. The names compare without letter case.
    /// </summary>
    /// <returns>False when the scene has no object with the name or the object is not a camera. The camera then stays
    /// as it is.</returns>
    public bool UseCamera(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cutscene.GetObject(name) is not { Camera: not null } camera)
            return false;
        cutscene.SetCamera(camera.Name);
        return true;
    }

    /// <summary>
    /// Draws the scene into the current viewport of the render context: <c>Cutscene.UpdateViewport</c> and
    /// <c>Cutscene.Draw</c> (Cutscene.cs:126-129, :434-447). The camera takes the ratio of the viewport, so the view
    /// can change its size while the preview does not step.
    /// </summary>
    /// <param name="stepSeconds">The script time that the clock stepped in this frame. The particle effects advance by
    /// it, so they stop while the preview does not step (section 8.6).</param>
    /// <param name="viewport">The viewport, in pixels.</param>
    /// <param name="screenAspect">The ratio of the screen that the viewport is in (section 7.6).</param>
    /// <param name="overrideCamera">A camera that replaces the THN camera, or null.</param>
    public void Draw(double stepSeconds, Rectangle viewport, float screenAspect, ICamera? overrideCamera)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cutscene.UpdateViewport(viewport, screenAspect);
        // The projection of the THN camera is made in each step. ThnCamera.Update only reads the pose and the lens.
        ((ThnCamera)cutscene.CameraHandle).Update();
        cutscene.Draw(stepSeconds, viewport.Width, viewport.Height, overrideCamera);
    }

    /// <summary>
    /// The models of the character of the scene object with a name, or null when the scene has no such object or the
    /// object is not a character. The names compare without letter case. The models are live objects of the scene, so a
    /// caller must not change them.
    /// </summary>
    public CharacterModels? CharacterOf(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return CharacterModels.Of(cutscene.GetObject(name)?.Object);
    }

    /// <summary>
    /// The scene object with a name now, or null when the scene has no such object. The names compare without letter
    /// case, as the engine compares them (Cutscene.cs:41). A REFERENCE entity has the object of its name in the set
    /// script, because the script does not make it (ThnScriptInstance.cs:70-71). The stand-in of a fidget is a
    /// character without an entity (ThnRoomHandler.cs:146-187), so it has the type DEFORMABLE.
    /// </summary>
    public PreviewObject? Find(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cutscene.GetObject(name) is not { } obj)
            return null;
        var pose = PoseOf(obj, stopped);
        IReadOnlyList<Vector3>? path = null;
        // A scene object that a room handler makes has no entity until it binds one (ThnSceneObject.cs:20).
        var entity = (ThnEntity?)obj.Entity;
        if (entity?.Path is { } motion)
        {
            // The points of a path are in the frame of its entity (ThnSceneObject.cs:209-235).
            var points = new Vector3[PathPoints + 1];
            for (var i = 0; i <= PathPoints; i++)
                points[i] = pose.Transform(motion.GetPosition((float)i / PathPoints));
            path = points;
        }
        return new PreviewObject(obj.Name, entity?.Type ?? EntityTypes.Deformable, pose, obj.Camera?.FovH,
            obj.Light?.Light.Color, path);
    }

    /// <summary>The pose of a scene object now. After a step that failed, the pose of the last update: the object does
    /// not resolve its attachments again (decision D655).</summary>
    /// <param name="stopped">True after a step that failed.</param>
    internal static Transform3D PoseOf(ThnSceneObject obj, bool stopped) =>
        !stopped ? obj.GetTransform()
        : obj.PosFromObject ? obj.Object!.Transform
        : new Transform3D(obj.Translate, obj.Rotate);

    /// <summary>
    /// The voice and the actor of the scene object with a name, or null when the scene has no such object. A
    /// <c>VoiceProfile_</c> sound with a speaker plays its line with the voice of the scene object of the speaker
    /// (ThnSound.cs:78-102). A bound NPC gives the object its voice. The names compare without letter case.
    /// </summary>
    public (string? Voice, string? Actor)? VoiceOf(string name)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return cutscene.GetObject(name) is { } obj ? (obj.Voice, obj.Actor) : null;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Free(cutscene, !InHostWorld);
    }

    /// <summary>The cutscene, and the world that it made. <c>Cutscene.Dispose</c> keeps that world. In the world of a
    /// host, <c>Cutscene.Dispose</c> removes the objects of the scripts, and the host keeps its world.</summary>
    private static void Free(Cutscene cutscene, bool ownsWorld)
    {
        cutscene.Dispose();
        if (ownsWorld)
            cutscene.World?.Dispose();
    }

    /// <summary>
    /// Makes sure that the room camera is a CAMERA entity of the scene. <c>SetCamera</c> takes any name
    /// (Cutscene.cs:492-497), and each step and each draw then read the lens of the object (ThnCamera.cs:68-72).
    /// </summary>
    /// <param name="camera">The scene object of the room camera, or null when the scene has no object of that
    /// name.</param>
    /// <param name="name">The <c>[Camera] name</c> of the room.</param>
    /// <exception cref="InvalidOperationException">The object is missing, or it is not a camera.</exception>
    internal static void RequireCamera(ThnSceneObject? camera, string name)
    {
        if (camera?.Camera == null)
            throw new InvalidOperationException(
                $"The room camera {name} is not a CAMERA entity of the scripts of the room. The [Camera] name of " +
                "the room must name one.");
    }

    /// <summary>
    /// The room NPC that plays a character of the cutscene, or null. When the engine cannot make the NPC, for example
    /// because a body part does not load, the script makes the character, as in the game (RoomGameplay.cs:1388-1403).
    /// </summary>
    private static ThnSceneObject? FixtureActor(ResourceManager resources, AnmFile animations, BaseNpc npc,
        ThnEntity entity)
    {
        try
        {
            return ThnRoomHandler.CreateBoundNpc(resources, animations, npc, entity);
        }
        catch (Exception e)
        {
            FLLog.Warning("Preview",
                $"The room NPC {npc.Nickname} cannot play {entity.Name}: {e.Message} The script makes the character, " +
                "as in the game.");
            return null;
        }
    }

    /// <summary>
    /// The player ship: the ship of the package with the equipment that has a hardpoint, as RoomGameplay makes the ship
    /// of the session (RoomGameplay.cs:1018-1023, :961-975). Without a package, the ship and the loadout of the package
    /// of a new multiplayer character, which Play gives too. Null when the data has no such ship.
    /// </summary>
    private static GameObject? PlayerShip(GameItemDb items, ResourceManager resources, string? package)
    {
        Ship? ship;
        IEnumerable<(string? Hardpoint, Equipment Equipment)> mounts;
        if (package != null)
        {
            var found = items.GetShipPackage(FLHash.CreateID(package));
            ship = found?.Ship;
            mounts = found?.Addons.Where(a => a.Amount == 1).Select(a => (a.Hardpoint, a.Equipment)) ?? [];
        }
        else
        {
            var start = PlaySave.DefaultPackage(items);
            ship = items.Ships.Get(start?.Ship);
            mounts = start?.Loadout is { } name && items.TryGetLoadout(name, out var loadout)
                ? loadout.Items.Select(i => (i.Hardpoint, i.Equipment))
                : [];
        }

        if (ship?.ModelFile?.LoadFile(resources) is not { } model)
            return null;
        var obj = new GameObject(model, resources, true, false);
        foreach (var (hardpoint, equipment) in mounts.Where(m => !string.IsNullOrEmpty(m.Hardpoint)))
            EquipmentObjectManager.InstantiateEquipment(obj, resources, null, EquipmentType.Cutscene, hardpoint,
                equipment);
        return obj;
    }

    /// <summary>Attaches the ship to its marker, as <c>SetRoomCameraAndShip</c> does (RoomGameplay.cs:1418-1431).</summary>
    private static void PlaceShip(Cutscene cutscene, GameObject ship, ThnSceneObject marker)
    {
        var offset = ship.HardpointExists("HpMount")
            ? ship.GetHardpoint("HpMount")!.Transform.Inverse()
            : Transform3D.Identity;
        var thnObject = new ThnSceneObject { Name = "$$_PLAYER_SHIP", Object = ship };
        thnObject.Attachments.Add(new ThnAttachment(new ThnObjectParent(marker, null, null) { ChildOffset = offset })
        {
            Position = true, Orientation = true
        });
        cutscene.AddObject(thnObject);
    }

    /// <summary>
    /// Adds the stand-in character of a fidget and plays the fidget on it, as the game plays the fidget of a room NPC
    /// (ThnRoomHandler.cs:99-121, Cutscene.cs:229-241). The stand-in has the appearance of the fixture NPC at the spot,
    /// or the costume of the first DEFORMABLE of the script.
    /// </summary>
    private static void AddFidget(Cutscene cutscene, ThnScript fidget, string? spot, BaseRoom? room,
        GameDataManager data, ResourceManager resources)
    {
        if (spot == null || cutscene.GetObject(spot) == null)
        {
            spot = OriginMarker;
            cutscene.AddObject(new ThnSceneObject
            {
                Name = OriginMarker,
                Translate = Vector3.Zero,
                Rotate = Quaternion.Identity,
                Object = new GameObject { Name = new ObjectName("Marker"), Nickname = "" },
            });
        }

        var npc = room?.Npcs.FirstOrDefault(n =>
            n.Placement?.Spot.Equals(spot, StringComparison.OrdinalIgnoreCase) == true);
        var appearance = npc != null ? BaseNpcPopulation.ResolveAppearance(npc) : default;
        var character = fidget.Entities.Values.FirstOrDefault(e => e.Type == EntityTypes.Deformable);
        if (!appearance.HasBody && data.Items.Costumes.Get(character?.Template) is { } costume)
            appearance = new BaseNpcAppearance(costume.Body, costume.Head, costume.LeftHand, costume.RightHand,
                costume.Accessory);
        if (!appearance.HasBody)
            throw new InvalidOperationException(
                "The fidget has no character: no room NPC stands at the spot, and the script has no DEFORMABLE with " +
                "a costume.");

        var name = npc?.Nickname ?? character?.Name ?? "Stand-in";
        ThnRoomHandler.AddNpc(cutscene, resources, data.GetCharacterAnimations(), name, spot, npc?.Voice,
            appearance.Head, appearance.Body, appearance.RightHand, appearance.LeftHand, appearance.Accessory, null);
        cutscene.FidgetScript(fidget, name);
    }
}
