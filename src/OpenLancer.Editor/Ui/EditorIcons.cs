using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// The Font Awesome 5 glyphs of the editor. Editor code takes its glyphs only from this list.
/// </summary>
/// <remarks>
/// ImGuiHelper merges Font Awesome 5.15.4 Free Solid into the Roboto font, so each glyph draws in normal text. The
/// names are the Font Awesome 5 names. Where the LibreLancer list has a glyph, this list gives an alias.
/// </remarks>
internal static class EditorIcons
{
    // Aliases of the LibreLancer glyphs.
    public const char Globe = Icons.Globe;
    public const char Cloud = Icons.Cloud;
    public const char Ban = Icons.Ban;
    public const char VectorSquare = Icons.VectorSquare;
    public const char Lightbulb = Icons.Lightbulb;
    public const char Map = Icons.Map;
    public const char Bullseye = Icons.Bullseye;
    public const char Bomb = Icons.Bomb;
    public const char Fire = Icons.Fire;
    public const char Bolt = Icons.Bolt;
    public const char Wind = Icons.Wind;
    public const char EyeSlash = Icons.EyeSlash;
    public const char Cog = Icons.Cog;
    public const char BoxOpen = Icons.BoxOpen;
    public const char Star = Icons.Star;
    public const char Th = Icons.Th;
    public const char Copy = Icons.Copy;
    public const char TrashAlt = Icons.TrashAlt;
    public const char MagnifyingGlass = Icons.MagnifyingGlass;
    public const char QuestionCircle = Icons.QuestionCircle;
    public const char ExclamationTriangle = Icons.ExclamationTriangle;
    public const char Video = Icons.Video;
    public const char Open = Icons.Open;
    public const char FileImport = Icons.FileImport;
    public const char Table = Icons.Table;
    public const char List = Icons.List;
    public const char X = Icons.X;

    /// <summary>A frame of four corners. The glyph list has it for a frame action, but no view uses it yet.</summary>
    public const char SquareCorners = Icons.SquareCorners;
    public const char UpRightFromSquare = Icons.UpRightFromSquare;
    public const char DoorOpen = Icons.Quit;
    public const char ArrowLeft = Icons.ArrowLeft;
    public const char ArrowRight = Icons.ArrowRight;
    public const char SyncAlt = Icons.SyncAlt;

    // Glyphs that the LibreLancer list does not have.
    public const char Sun = '\uf185';
    public const char LifeRing = '\uf1cd';
    public const char Building = '\uf1ad';
    public const char Dungeon = '\uf6d9';
    public const char CircleNotch = '\uf1ce';
    public const char DotCircle = '\uf192';
    public const char Satellite = '\uf7bf';
    public const char Crosshairs = '\uf05b';
    public const char Box = '\uf466';
    public const char CubeSolid = '\uf1b2';
    public const char Cubes = '\uf1b3';
    public const char Images = '\uf302';
    public const char Meteor = '\uf753';
    public const char Users = '\uf0c0';
    public const char Route = '\uf4d7';
    public const char Road = '\uf018';
    public const char SpaceShuttle = '\uf197';
    public const char Stream = '\uf550';
    public const char InfoCircle = '\uf05a';
    public const char Database = '\uf1c0';
    public const char Terminal = '\uf120';
    public const char LayerGroup = '\uf5fd';
    public const char TimesCircle = '\uf057';
    public const char CheckCircle = '\uf058';
    public const char Lock = '\uf023';
    public const char MousePointer = '\uf245';
    public const char TachometerAlt = '\uf3fd';
    public const char BorderAll = '\uf84c';
    public const char MapMarkerAlt = '\uf3c5';
    public const char Tag = '\uf02b';
    public const char CompressArrowsAlt = '\uf78c';
    public const char Folder = '\uf07b';
    public const char FolderOpen = '\uf07c';
    public const char ShieldAlt = '\uf3ed';
    public const char Rocket = '\uf135';
    public const char SatelliteDish = '\uf7c0';
    public const char Magnet = '\uf076';
    public const char Wrench = '\uf0ad';
    public const char BatteryFull = '\uf240';
    public const char Boxes = '\uf468';
    public const char Magic = '\uf0d0';
    public const char Circle = '\uf111';
    public const char EllipsisV = '\uf142';
    public const char Film = '\uf008';
    public const char Asterisk = '\uf069';
    public const char Undo = '\uf0e2';
    public const char Redo = '\uf01e';
    public const char Save = '\uf0c7';
    public const char Clone = '\uf24d';
    public const char Plus = '\uf067';
    public const char Store = '\uf54e';
    public const char Pen = '\uf304';
    public const char Tags = '\uf02c';
    public const char ArrowsAlt = '\uf0b2';
    public const char ExpandAlt = '\uf424';
    public const char CaretDown = '\uf0d7';
    public const char CaretRight = '\uf0da';
    public const char Play = '\uf04b';
    public const char Pause = '\uf04c';
    public const char Stop = '\uf04d';

    // The mark of the agent edits in the Hierarchy (section 8.5 of the MCP1 design).
    public const char Robot = '\uf544';

    // The Preview controls of a cutscene have the circle icons, so they do not look like the Play of the game (section
    // 7.7 of the M8 design).
    public const char PlayCircle = '\uf144';
    public const char PauseCircle = '\uf28b';
    public const char StopCircle = '\uf28d';
    public const char StepBackward = '\uf048';
    public const char StepForward = '\uf051';
    public const char History = '\uf1da';

    // The dry run of the Mission tab has a test glyph, so it does not look like the Play of the game (section 8.1 of
    // the M6c design).
    public const char Flask = '\uf0c3';

    // The entity types of a cutscene, and the tools of the Timeline and the Cutscene view.
    public const char User = '\uf007';
    public const char VolumeUp = '\uf028';
    public const char VolumeMute = '\uf6a9';
    public const char Desktop = '\uf108';
    public const char Image = '\uf03e';
    public const char Filter = '\uf0b0';
    public const char SearchPlus = '\uf00e';
    public const char SearchMinus = '\uf010';
    public const char ArrowsAltH = '\uf337';
    public const char Eye = '\uf06e';
    public const char Link = '\uf0c1';
    public const char Smog = '\uf75f';

    // The Mods window and its lists (milestone M11c).
    public const char ArrowUp = Icons.ArrowUp;
    public const char ArrowDown = Icons.ArrowDown;
    public const char GripLines = '\uf7a4';
    public const char Check = Icons.Check;
    public const char Sync = '\uf021';

    // The mission graph and the mission layer of the Scene (milestone M6a).
    public const char Flag = '\uf024';
    public const char Bell = '\uf0f3';
    public const char Music = '\uf001';
    public const char CodeBranch = '\uf126';
    public const char Comments = '\uf086';
    public const char ProjectDiagram = '\uf542';
}
