using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Assets;

/// <summary>
/// The Assets panel (milestone M23, decision D266): a Project window as in Unity, the first tab of the bottom group. A
/// category tree at the left, the tiles of the assets at the right, the search field and the size slider in the toolbar,
/// and the nickname and the file of the selected asset in the footer. The smallest tile size shows a list.
/// </summary>
/// <remarks>
/// <para>The rows come from the edit view W (decision D268), so a new item of the active mod shows before a save. The
/// rows need no loaded game data. The names and the new thumbnails wait for the game data.</para>
/// <para>A click selects the asset when the mouse button comes up without a drag (decision D269). A drag never changes
/// the selection. A double-click or Enter opens the asset: the Inspector shows it (decision D276).</para>
/// </remarks>
internal sealed class AssetsPanel : IDisposable
{
    public const string PanelId = "assets";

    // The empty states of the Assets panel (#56). The next action of a search without a match is a link.
    internal static readonly EmptyState NoAssetMatch =
        new($"No asset matches '{EmptyState.TextPlace}'.", "Clear search");
    internal static readonly EmptyState NoPropGroups = new("This category has no assets.",
        "Right-click an object in the Hierarchy and click Save as prop group.");
    internal static readonly EmptyState NoAssets =
        new("This category has no assets.", "Pick another category. A mod adds these assets in its data files.");

    // Sizes in unscaled points.
    private const float TreeMin = 120;
    private const float TreeMaxFraction = 0.4f;
    private const float SplitterGap = 4;
    private const float ListPadding = 4;
    private const float TileMin = 32;
    private const float TileMax = 192;
    private const float ListBelow = 48;
    private const float TileGap = 8;
    private const float SliderWidth = 90;
    private const float FilterMinWidth = 120;
    private const float BadgeSize = 13;

    private readonly EditorWindow window;
    private readonly ThumbnailCache thumbnails;
    private readonly Dictionary<AssetRef, string> names = [];
    private (EditorSession? Session, int Version, GameDataManager? Data) nameState;
    private string search = "";
    private bool focusSearch;
    private AssetRef? selected;
    private object? selectedItem;
    private bool revealSelected;
    private bool dragStarted;
    private int columns = 1;
    private IReadOnlyList<AssetRow> shown = [];
    private readonly RenamePopup prefabRename = new("##prefab-rename");
    // The prefab of the open rename popup, and true for a rename of the file name, false for the name.
    private (string Id, bool File)? renamingPrefab;

    /// <summary>True while the rename popup of a prefab is open. An agent call waits for it (section 5.5, item 3 of the
    /// MCP1 design).</summary>
    internal bool PrefabRenaming => prefabRename.IsOpen;

    /// <param name="thumbnailFolder">The folder of the cache of the thumbnails.</param>
    public AssetsPanel(EditorWindow window, string thumbnailFolder)
    {
        this.window = window;
        thumbnails = new ThumbnailCache(window, thumbnailFolder);
    }

    /// <summary>The radius of the model of an asset that the thumbnail renderer knows, for the ghost of a drag, or
    /// null.</summary>
    public float? RadiusOf(AssetRef asset) => thumbnails.RadiusOf(asset);

    /// <summary>Forgets the selection and frees the thumbnails. Call it when the profile closes.</summary>
    public void Reset()
    {
        selected = null;
        selectedItem = null;
        shown = [];
        names.Clear();
        nameState = default;
        thumbnails.Reset();
    }

    public void Dispose() => thumbnails.Dispose();

    /// <summary>
    /// Shows an asset in the panel, as the Unity "ping" does: the panel shows its category and selects and scrolls to
    /// its tile. "Show in Assets" of the Inspector uses it.
    /// </summary>
    public void Show(AssetKind kind, string id)
    {
        if (window.Session?.Edits.Assets.Find(kind, id) is { } row)
            Show(row);
    }

    /// <summary>Shows the tile of a mission prefab asset: "Select prefab asset" (section 8.11 of the M6b design).</summary>
    public void ShowPrefab(PrefabRef prefab)
    {
        if (window.Session?.Edits.Assets.Find(AssetKind.MissionPrefab, prefab.Id) is { } row)
            Show(row);
    }

    /// <summary>Shows an asset of a row, for example a new prop group.</summary>
    public void Show(AssetRef asset)
    {
        if (RowOf(asset) is { } row)
            Show(row);
    }

    /// <summary>The row of an asset, or null when the catalog has none now.</summary>
    public AssetRow? RowOf(AssetRef asset)
    {
        if (window.Session is not { } session)
            return null;
        return asset.Kind == AssetKind.PropGroup
            ? session.Edits.Assets.Rows("groups").FirstOrDefault(r => r.Asset.File == asset.File)
            : session.Edits.Assets.Find(asset.Kind, asset.Id);
    }

    /// <summary>The 256 px thumbnail of an asset for the Inspector page, or null while it is not ready.</summary>
    public ImTextureRef? Preview(AssetRef asset) =>
        window.Session is { } session
            ? thumbnails.Get(session.Edits, session.Version, asset, ThumbnailCache.LargeSize, out _)
            : null;

    private void Show(AssetRow row)
    {
        window.Settings.AssetsCategory = row.CategoryId;
        search = "";
        Select(row);
        revealSelected = true;
        window.ShowPanel(PanelId);
    }

    /// <summary>The display name of a row: the text of its <c>ids_name</c>, the name of a group, or the nickname. The
    /// names stay until the edit view, the text or the game data changes.</summary>
    public string NameOf(AssetRow row)
    {
        if (window.Session is not { } session)
            return row.Nickname;
        var state = (session, session.Version, session.GameData);
        if (!ReferenceEquals(nameState.Session, session) || nameState.Version != session.Version ||
            !ReferenceEquals(nameState.Data, session.GameData))
        {
            names.Clear();
            nameState = state;
        }
        if (names.TryGetValue(row.Asset, out var name))
            return name;
        name = row.Asset.Kind switch
        {
            AssetKind.PropGroup => Group(session, row.Asset)?.Name ?? row.Nickname,
            AssetKind.MissionPrefab => session.Edits.Missions.Prefab(PrefabRef.ModOf(row.Asset.Id))?.Name ?? row.Nickname,
            _ when row.Asset.Section != null && session.SectionName(row.Asset.Section) is { Length: > 0 } text => text,
            _ => row.Nickname,
        };
        names[row.Asset] = name;
        return name;
    }

    /// <summary>Draws the items of the toolbar strip: the search field, the path of the category, and the size slider
    /// at the right.</summary>
    public void Toolbar()
    {
        if (window.Session is not { } session)
            return;
        var scale = ImGuiHelper.Scale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var slider = MathF.Truncate(SliderWidth * scale);
        var avail = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorPosX();
        var fieldWidth = Math.Max(FilterMinWidth * scale, MathF.Truncate(avail * 0.35f));
        if (focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        ToolbarItems.SearchField("assets-search", "Search (Ctrl+F)", ref search, fieldWidth,
            "Search by name, nickname, type or model file.");
        ImGui.SameLine();
        var path = CategoryPath(session.Edits.Assets.Categories, window.Settings.AssetsCategory);
        var room = Math.Max(0, start + avail - ImGui.GetCursorPosX() - slider - spacing);
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(TextFit.CutRight(path, room));
        ImGui.SameLine();
        ImGui.SetCursorPosX(start + Math.Max(0, avail - slider));
        ImGui.SetNextItemWidth(slider);
        var size = Math.Clamp(window.Settings.AssetsTileSize, TileMin, TileMax);
        if (ImGui.SliderFloat("##tile-size", ref size, TileMin, TileMax, ""))
            window.Settings.AssetsTileSize = size;
        if (ImGui.IsItemDeactivatedAfterEdit())
            window.Settings.Save();
        ImGui.SetItemTooltip("The size of the tiles. The smallest size shows a list.");
    }

    public void Draw()
    {
        if (window.Session is not { } session)
        {
            PanelStates.Message(PanelStates.NoProfile);
            return;
        }

        var catalog = session.Edits.Assets;
        var categories = catalog.Categories;
        var current = categories.FirstOrDefault(c => c.Id.Equals(window.Settings.AssetsCategory, StringComparison.OrdinalIgnoreCase))
                      ?? categories.FirstOrDefault(c => c.Id == EditorSettings.DefaultAssetsCategory) ?? categories[0];
        var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows);
        shown = catalog.Search(current.Id, search, NameOf);
        if (focused && !ImGui.GetIO().WantTextInput)
            Keys(session);

        var scale = ImGuiHelper.Scale;
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail();
        var gap = MathF.Truncate(SplitterGap * scale);
        var settings = window.Settings;
        var maxWidth = Math.Max(TreeMin, TreeMaxFraction * avail.X / scale);
        var treeWidth = MathF.Truncate(Math.Clamp(settings.AssetsTreeWidth, TreeMin, maxWidth) * scale);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(MathF.Truncate(ListPadding * scale)));
        var visible = ImGui.BeginChild("##asset-tree", new Vector2(treeWidth, avail.Y), ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (visible)
            Tree(categories, current, focused);
        ImGui.EndChild();

        PixelLine.Vertical(ImGui.GetWindowDrawList(), origin.X + treeWidth + gap / 2, origin.Y, origin.Y + avail.Y,
            ImGui.GetColorU32(EditorTheme.Border));
        var shownWidth = treeWidth / scale;
        var value = shownWidth;
        if (Splitter.Draw("##split-assets", origin + new Vector2(treeWidth, 0), new Vector2(gap, avail.Y), true, ref value, 1,
                TreeMin, maxWidth, EditorSettings.DefaultAssetsTreeWidth))
            settings.Save();
        if (value != shownWidth)
            settings.AssetsTreeWidth = value;

        var footer = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(origin + new Vector2(treeWidth + gap, 0));
        var right = new Vector2(Math.Max(1, avail.X - treeWidth - gap), Math.Max(1, avail.Y - footer));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(MathF.Truncate(ListPadding * scale)));
        if (ImGui.BeginChild("##asset-tiles", right, ImGuiChildFlags.AlwaysUseWindowPadding))
        {
            ImGui.PopStyleVar();
            Tiles(session, current);
        }
        else
            ImGui.PopStyleVar();
        ImGui.EndChild();

        ImGui.SetCursorScreenPos(origin + new Vector2(treeWidth + gap + ImGui.GetStyle().FramePadding.X, avail.Y - footer));
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(TextFit.CutRight(Footer(session), right.X - 2 * ImGui.GetStyle().FramePadding.X));

        thumbnails.Frame(CanRender(session));
        if (prefabRename.Draw() is { } text && renamingPrefab is var (old, file))
        {
            renamingPrefab = null;
            window.Missions.Run(RenameBatch(old, text, file));
        }
    }

    /// <summary>True when a thumbnail can render in this frame (section 7.2 of the M23 design): the game data is loaded,
    /// no load or reload runs, and no camera move, gizmo drag, field edit or tile drag runs.</summary>
    private bool CanRender(EditorSession session) =>
        session.GameData != null && !session.IsBusy && !window.Scene.IsMoving && !window.IsEditing &&
        !window.AssetDrag.IsDragging;

    /// <summary>The category tree: the root, each top category, and the children of the top category that shows.</summary>
    private void Tree(IReadOnlyList<AssetCategory> categories, AssetCategory current, bool focused)
    {
        var style = ImGui.GetStyle();
        ListRow.PushStyle(focused);
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var currentTop = current.Id.Split('/')[0];
        foreach (var category in categories)
        {
            var top = category.Id.Split('/')[0];
            var depth = category.Id == AssetCatalog.AllId ? 0 : category.ParentId == AssetCatalog.AllId ? 1 : 2;
            // The children show under the top category of the category that shows, as a Unity folder opens.
            if (depth == 2 && !top.Equals(currentTop, StringComparison.OrdinalIgnoreCase))
                continue;
            var min = ImGui.GetCursorScreenPos();
            var isCurrent = ReferenceEquals(category, current);
            if (isCurrent)
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, ListRow.SelectionColor(focused));
            if (ImGui.Selectable($"##cat-{category.Id}", isCurrent, ImGuiSelectableFlags.None, new Vector2(0, ListRow.Height)) &&
                !isCurrent)
            {
                window.Settings.AssetsCategory = category.Id;
                window.Settings.Save();
            }

            if (isCurrent)
                ImGui.PopStyleColor();
            // A Hierarchy row dropped on the prop groups opens "Save as prop group" (Unity: drag a GameObject into the
            // Project window).
            if (category.Id == "groups")
                window.AssetDrag.Groups();
            var kind = ObjectKinds.Assets.OfCategory(category.Id);
            ListRow.Text(min, min.X + style.FramePadding.X + depth * style.IndentSpacing * 0.75f, right, kind.Icon, kind.Abgr,
                category.Label, "", category.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        ListRow.PopStyle();
    }

    /// <summary>The tiles of the rows that show, in a grid, or a list at the smallest tile size.</summary>
    private void Tiles(EditorSession session, AssetCategory current)
    {
        if (shown.Count == 0)
        {
            if (search.Length > 0)
            {
                if (PanelStates.MessageWithLink(NoAssetMatch.With(search)))
                    search = "";
            }
            else
                PanelStates.Message(current.Id == "groups" ? NoPropGroups : NoAssets);
            return;
        }

        var size = Math.Clamp(window.Settings.AssetsTileSize, TileMin, TileMax);
        if (size < ListBelow)
            List(session);
        else
            Grid(session, MathF.Truncate(size * ImGuiHelper.Scale));

        // The empty space below the tiles takes a Hierarchy row for a prop group too.
        if (current.Id == "groups" && ImGui.GetContentRegionAvail().Y > 1)
        {
            ImGui.Dummy(new Vector2(Math.Max(1, ImGui.GetContentRegionAvail().X), ImGui.GetContentRegionAvail().Y));
            window.AssetDrag.Groups();
        }
    }

    private void Grid(EditorSession session, float size)
    {
        var scale = ImGuiHelper.Scale;
        var gap = MathF.Truncate(TileGap * scale);
        var textHeight = ImGui.GetTextLineHeight();
        var height = size + textHeight + gap / 2;
        columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (size + gap)));
        var lines = (shown.Count + columns - 1) / columns;
        var reveal = revealSelected && selected != null ? IndexOf(selected) : -1;
        revealSelected = false;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, gap));
        var clipper = new ImGuiListClipper();
        clipper.Begin(lines, height + gap);
        if (reveal >= 0)
            clipper.IncludeItemByIndex(reveal / columns);
        while (clipper.Step())
        {
            for (var line = clipper.DisplayStart; line < clipper.DisplayEnd; line++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var index = line * columns + column;
                    if (index >= shown.Count)
                        break;
                    if (column > 0)
                        ImGui.SameLine();
                    Tile(session, shown[index], index, size, height);
                }

                if (reveal >= 0 && line == reveal / columns)
                    ImGui.SetScrollHereY(0.5f);
            }
        }

        clipper.End();
        ImGui.PopStyleVar();
    }

    /// <summary>One tile: the thumbnail or the glyph of the kind, the name below it, and the marks at the top
    /// right.</summary>
    private void Tile(EditorSession session, AssetRow row, int index, float size, float height)
    {
        ImGui.PushID(index);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##tile", new Vector2(size, height));
        Interact(session, row, clicked);
        var drawList = ImGui.GetWindowDrawList();
        var isSelected = IsSelected(row);
        var hovered = ImGui.IsItemHovered();
        if (isSelected || hovered)
            drawList.AddRectFilled(min, min + new Vector2(size, height),
                ImGui.GetColorU32(isSelected ? EditorTheme.AccentStrong : ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered]),
                3 * ImGuiHelper.Scale);
        var kind = ObjectKinds.Assets.Of(row);
        var imageMin = min + new Vector2(2, 2);
        var imageMax = min + new Vector2(size - 2, size - 2);
        var texture = window.Settings.Thumbnails == ThumbnailMode.Rendered
            ? thumbnails.Get(session.Edits, session.Version, row.Asset, size <= ThumbnailCache.SmallSize
                ? ThumbnailCache.SmallSize
                : ThumbnailCache.LargeSize, out _)
            : null;
        if (texture is { } image)
            drawList.AddImage(image, imageMin, imageMax, new Vector2(0, 1), new Vector2(1, 0));
        else
            Glyphs.Draw(drawList, kind.Icon, MathF.Round(size * 0.4f), (imageMin + imageMax) / 2, kind.Abgr);
        var name = NameOf(row);
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var textWidth = font.CalcTextSizeA(fontSize, float.MaxValue, 0, TextFit.CutRight(name, size)).X;
        TextFit.AddText(drawList, font, fontSize, new Vector2(min.X + Math.Max(0, (size - textWidth) / 2), min.Y + size),
            ImGui.GetColorU32(ImGuiCol.Text), name, size);
        Marks(drawList, row, min, size);
        ImGui.PopID();
    }

    /// <summary>The prefab of a row of a mission prefab asset.</summary>
    private static PrefabRef PrefabOf(AssetRow row) => PrefabRef.ModOf(row.Asset.Id);

    /// <summary>Why a mission prefab asset cannot change, or null: a prefab of a lower mod, or a session without
    /// edits.</summary>
    private static string? PrefabLock(EditorSession session, AssetRow row) =>
        row.Origin != ValueOrigin.ActiveMod ? MissionLibrary.LockTip(row.ModId ?? "") : session.NoEditReason;

    /// <summary>F2 and "Rename" of a mission prefab asset: the popup with the name that the tile shows. "Rename file..."
    /// opens it with the file name.</summary>
    private void RenamePrefab(AssetRow row, bool file = false)
    {
        renamingPrefab = (row.Asset.Id, file);
        prefabRename.Open(file ? row.Asset.Id : NameOf(row));
    }

    /// <summary>The batch of a rename of a prefab asset: a new name in the library, or a new file name. Null for the same
    /// text.</summary>
    internal static MissionBatch? RenameBatch(string id, string text, bool file)
    {
        text = text.Trim();
        if (text.Length == 0 || file && string.Equals(text, id, StringComparison.Ordinal))
            return null;
        return file
            ? new MissionBatch($"Rename prefab file {id} to {text}", [new RenamePrefabAsset(PrefabRef.ModOf(id), text)])
            : new MissionBatch($"Rename prefab {id} to '{text}'", [new RenamePrefabAsset(PrefabRef.ModOf(id), id) { NewName = text }]);
    }

    /// <summary>Delete and "Delete" of a mission prefab asset (decision D673): its instances keep their lines and get
    /// P02. The mission editor asks first when instances use the asset. Undo brings the asset back.</summary>
    private void DeletePrefab(AssetRow row) =>
        window.Missions.Run(new MissionBatch($"Delete prefab {row.Asset.Id}", [new RemovePrefabAsset(PrefabOf(row))]));

    /// <summary>The menu of a tile of a mission prefab asset (section 8.3 of the M6b design).</summary>
    private void PrefabMenu(EditorSession session, AssetRow row)
    {
        if (MenuItems.Item(EditorIcons.Cubes, "Open prefab", row.HasProblem ? row.NotPlaceable : null,
                MissionPageModel.OpenPrefabTip))
            window.Missions.OpenPrefabMode(PrefabOf(row));
        var reason = PrefabLock(session, row);
        if (MenuItems.Item(EditorIcons.Pen, "Rename", reason, "Change the name of the prefab in the library.", "F2"))
            RenamePrefab(row);
        if (MenuItems.Item(EditorIcons.Pen, "Rename file...", reason,
                $"Change the file name of the prefab. {Data.Missions.Prefabs.PrefabAsset.FileNameRule}"))
            RenamePrefab(row, file: true);
        if (MenuItems.Item(EditorIcons.TrashAlt, "Delete", reason,
                "Delete the prefab file. Its instances keep their lines and show the warning P02.", "Delete"))
            DeletePrefab(row);
        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy file name", true))
            ImGui.SetClipboardText(row.Asset.Id);
    }

    /// <summary>The marks of a tile: the blue bar and the "+" of the active mod (decision D245), the lock of an asset that
    /// cannot spawn, the error of a group with a problem, and the "prop" badge of a model with a solar archetype.</summary>
    private static void Marks(ImDrawListPtr drawList, AssetRow row, Vector2 min, float size)
    {
        var scale = ImGuiHelper.Scale;
        var badge = MathF.Round(BadgeSize * scale);
        var corner = new Vector2(min.X + size - badge / 2 - 2, min.Y + badge / 2 + 2);
        if ((row.Mark & (AssetMark.Added | AssetMark.Changed)) != 0)
            drawList.AddRectFilled(min, new Vector2(min.X + MathF.Max(2, 2 * scale), min.Y + size),
                ImGui.GetColorU32(EditorTheme.Accent));
        if ((row.Mark & AssetMark.Added) != 0)
            drawList.AddText(ImGui.GetFont(), badge, min + new Vector2(4 * scale, 0), ImGui.GetColorU32(EditorTheme.Accent), "+");
        if (row.Asset.Kind == AssetKind.PropGroup && row.NotPlaceable != null ||
            row.Asset.Kind == AssetKind.MissionPrefab && row.HasProblem)
            Glyphs.Draw(drawList, EditorIcons.ExclamationTriangle, badge, corner, EditorTheme.ToAbgr(EditorTheme.Error));
        else if (row.Asset.Kind == AssetKind.MissionPrefab)
        {
            // A prefab of a lower mod has the lock: it can place, but it cannot change.
            if (row.Origin != ValueOrigin.ActiveMod)
                Glyphs.Draw(drawList, EditorIcons.Lock, badge, corner, EditorTheme.ToAbgr(EditorTheme.TextDim));
        }
        else if (row.NotPlaceable != null && row.Asset.Kind is not (AssetKind.Loadout or AssetKind.Faction))
            Glyphs.Draw(drawList, EditorIcons.Lock, badge, corner, EditorTheme.ToAbgr(EditorTheme.TextDim));
        if (row.HasProp)
        {
            var text = "prop";
            var textSize = ImGui.GetFont().CalcTextSizeA(badge, float.MaxValue, 0, text);
            var at = new Vector2(min.X + 4 * scale, min.Y + size - textSize.Y - 2 * scale);
            drawList.AddRectFilled(at - new Vector2(2, 0), at + textSize + new Vector2(2, 0),
                ImGui.GetColorU32(EditorTheme.Strip), 2 * scale);
            drawList.AddText(ImGui.GetFont(), badge, at, ImGui.GetColorU32(EditorTheme.TextDim), text);
        }
    }

    /// <summary>The list of the rows at the smallest tile size: name, detail and layer.</summary>
    private void List(EditorSession session)
    {
        columns = 1;
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
                                      ImGuiTableFlags.BordersInnerV;
        if (!ImGui.BeginTable("##asset-list", 3, flags))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Layer", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelper.Scale);
        ImGui.TableHeadersRow();
        var reveal = revealSelected && selected != null ? IndexOf(selected) : -1;
        revealSelected = false;
        var clipper = new ImGuiListClipper();
        clipper.Begin(shown.Count);
        if (reveal >= 0)
            clipper.IncludeItemByIndex(reveal);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var row = shown[i];
                var kind = ObjectKinds.Assets.Of(row);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushID(i);
                var clicked = ImGui.Selectable($"{kind.Icon}  {NameOf(row)}##row", IsSelected(row),
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
                Interact(session, row, clicked);
                if (i == reveal)
                    ImGui.SetScrollHereY(0.5f);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(row.Detail);
                ImGui.TableNextColumn();
                ImGui.TextDisabled(Layer(row));
                ImGui.PopID();
            }
        }

        clipper.End();
        ImGui.EndTable();
    }

    /// <summary>
    /// The mouse on the item of a row: a click without a drag selects (decision D269), a double-click opens (decision
    /// D276), a drag carries the asset, a right click opens the menu, and a hover shows the preview.
    /// </summary>
    private void Interact(EditorSession session, AssetRow row, bool clicked)
    {
        if (ImGui.IsItemActivated())
            dragStarted = false;
        if (ImGui.BeginDragDropSource())
        {
            dragStarted = true;
            // A mission prefab goes into the graph of a mission, as an item of the library does (section 8.3 of the M6b
            // design).
            if (row.Asset.Kind == AssetKind.MissionPrefab)
            {
                if (session.Edits.Missions.Prefab(PrefabOf(row)) is { } asset)
                    MissionDrag.Source(MissionDrag.ItemType, MissionLibrary.Prefab(asset), $"{EditorIcons.Cubes}  {NameOf(row)}");
            }
            else
                window.AssetDrag.SourceOf(row, NameOf(row), ObjectKinds.Assets.Of(row));
            ImGui.EndDragDropSource();
        }

        if (clicked && !dragStarted)
            Select(row);
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            Open(row);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            Select(row);
        if (ImGui.BeginPopupContextItem("##asset-menu"))
        {
            EditorTheme.PushMenuColors();
            if (row.Asset.Kind == AssetKind.MissionPrefab)
                PrefabMenu(session, row);
            else
                Menu(session, row);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }
        else if (!window.AssetDrag.IsDragging && ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            Tooltip(session, row);
    }

    /// <summary>The hover tooltip (section 6.2): the 256 px preview, the nickname, the detail, the layer and the mod,
    /// the model file, the number of lines that name the entry, and why the asset cannot spawn.</summary>
    private void Tooltip(EditorSession session, AssetRow row)
    {
        ImGui.BeginTooltip();
        var preview = thumbnails.Get(session.Edits, session.Version, row.Asset, ThumbnailCache.LargeSize, out _);
        if (preview is { } image)
        {
            var side = MathF.Truncate(ThumbnailCache.LargeSize * ImGuiHelper.Scale * 0.75f);
            ImGui.Image(image, new Vector2(side, side), new Vector2(0, 1), new Vector2(1, 0));
        }

        ImGui.Text(NameOf(row));
        ImGui.TextDisabled(row.Nickname);
        if (row.Detail.Length > 0)
            ImGui.TextDisabled(row.Detail);
        ImGui.TextDisabled(Layer(row));
        if (row.ModelPath != null && row.Asset.Kind != AssetKind.Model)
            ImGui.TextDisabled($"Model: {row.ModelPath}");
        if (row.List != null && row.Asset.Kind != AssetKind.Model)
            ImGui.TextDisabled($"Used by {session.Edits.Lists.CountReferences(row.List, row.Asset.Id)}");
        if ((row.Mark & AssetMark.Unsaved) != 0)
            ImGui.TextDisabled("Not saved yet.");
        if (row.NotPlaceable != null)
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextColored(row.Asset.Kind == AssetKind.PropGroup ? EditorTheme.Error : EditorTheme.TextDim,
                row.NotPlaceable);
            ImGui.PopTextWrapPos();
        }

        ImGui.EndTooltip();
    }

    /// <summary>The menu of a tile (section 6.4). A disabled item has its reason as the tooltip.</summary>
    private void Menu(EditorSession session, AssetRow row)
    {
        var placeable = row.Asset.Kind is AssetKind.SolarArchetype or AssetKind.Ship or AssetKind.Asteroid or
            AssetKind.Model or AssetKind.PropGroup;
        var reason = placeable
            ? row.NotPlaceable ?? window.SceneEdits.Unavailable(Data.Editing.Scene.SceneAction.Create, null)
            : row.NotPlaceable ?? "The Scene does not place this asset.";
        if (MenuItems.Item(EditorIcons.Crosshairs, "Place at the camera pivot", reason))
            window.SceneEdits.Spawn(row, window.Scene.Pivot, null);
        if (row.Asset.Kind is AssetKind.Ship or AssetKind.Asteroid or AssetKind.Model)
        {
            if (MenuItems.Item(EditorIcons.Plus, "New static prop archetype", reason,
                    "Makes a new [Solar] with this model, and an object of it at the camera pivot."))
                window.SceneEdits.NewProp(row, window.Scene.Pivot);
        }

        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy nickname", true))
            ImGui.SetClipboardText(row.Nickname);
        if (MenuItems.Item(EditorIcons.Copy, "Copy model path",
                row.ModelPath == null ? "The asset has no model." : null))
            ImGui.SetClipboardText(row.ModelPath!);
        if (row.Asset.Kind != AssetKind.PropGroup || Group(session, row.Asset) is not { } group)
            return;
        ImGui.Separator();
        var groupReason = !group.Editable ? $"The group is of mod '{group.ModId}'. Only a group of the active mod can change."
            : session.NoEditReason;
        if (MenuItems.Item(EditorIcons.Pen, "Rename", groupReason, shortcut: "F2"))
            window.SceneEdits.RenameGroup(group);
        if (MenuItems.Item(EditorIcons.TrashAlt, "Delete", groupReason, shortcut: "Delete"))
            window.SceneEdits.DeleteGroup(group);
        if (Theme.IconMenuItem(EditorIcons.FolderOpen, "Show in folder", true))
            FolderActions.Open(window, Path.GetDirectoryName(group.File)!);
    }

    /// <summary>The keys of the panel while it has the focus and no text field is active (section 6.4).</summary>
    private void Keys(EditorSession session)
    {
        if (ImGui.Shortcut((int)(ImGuiKey.ImGuiMod_Ctrl | ImGuiKey.F)))
        {
            focusSearch = true;
            return;
        }

        if (shown.Count == 0)
            return;
        var at = selected == null ? -1 : IndexOf(selected);
        var step = ImGui.IsKeyPressed(ImGuiKey.RightArrow) ? 1
            : ImGui.IsKeyPressed(ImGuiKey.LeftArrow) ? -1
            : ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? columns
            : ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? -columns
            : 0;
        if (step != 0)
        {
            Select(shown[Math.Clamp(at < 0 ? 0 : at + step, 0, shown.Count - 1)]);
            revealSelected = true;
            return;
        }

        if (at < 0)
            return;
        var row = shown[at];
        if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            Open(row);
        else if (row.Asset.Kind == AssetKind.MissionPrefab && PrefabLock(session, row) == null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F2, false))
                RenamePrefab(row);
            else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
                DeletePrefab(row);
        }
        else if (row.Asset.Kind == AssetKind.PropGroup && Group(session, row.Asset) is { Editable: true } group &&
                 session.CanEdit)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F2, false))
                window.SceneEdits.RenameGroup(group);
            else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
                window.SceneEdits.DeleteGroup(group);
        }
    }

    /// <summary>Selects the asset of a row (decision D269): the Inspector shows its page, and the Scene selection
    /// clears.</summary>
    private void Select(AssetRow row)
    {
        selected = row.Asset;
        selectedItem = SelectionOf(row);
        window.Selection = selectedItem;
    }

    /// <summary>Opens the asset of a row (decision D276): it selects the asset and shows the Inspector panel. A mission
    /// prefab opens in prefab mode (section 8.6 of the M6b design).</summary>
    private void Open(AssetRow row)
    {
        Select(row);
        if (row.Asset.Kind == AssetKind.MissionPrefab)
        {
            window.Missions.OpenPrefabMode(PrefabOf(row));
            return;
        }

        window.ShowPanel(Inspector.InspectorPanel.PanelId);
    }

    private bool IsSelected(AssetRow row) => row.Asset.Equals(selected) && Equals(window.Selection, selectedItem);

    /// <summary>
    /// What a click on a row selects (review finding R23): the engine item of a ship or of equipment, so the pages of
    /// ships and equipment show, the section of each other entry, or the asset of a model and of a group. A new entry of
    /// the active mod has no engine item, so it selects its section.
    /// </summary>
    private object SelectionOf(AssetRow row)
    {
        var data = window.Session?.GameData;
        var added = (row.Mark & AssetMark.Added) != 0;
        return row.Asset.Kind switch
        {
            AssetKind.Ship when !added && data?.Items.Ships.Get(row.Asset.Id) is { } ship => ship,
            AssetKind.Equipment when !added && data?.Items.Equipment.Get(row.Asset.Id) is { } equipment => equipment,
            AssetKind.Model or AssetKind.PropGroup or AssetKind.MissionPrefab => row.Asset,
            _ => (object?)row.Asset.Section ?? row.Asset,
        };
    }

    private int IndexOf(AssetRef asset)
    {
        for (var i = 0; i < shown.Count; i++)
        {
            if (shown[i].Asset.Equals(asset))
                return i;
        }

        return -1;
    }

    /// <summary>The footer: the nickname and the file of the selected asset, as Unity shows the path of an asset.</summary>
    private string Footer(EditorSession session)
    {
        if (selected == null || !Equals(window.Selection, selectedItem))
            return "";
        var file = selected.Kind switch
        {
            AssetKind.PropGroup or AssetKind.MissionPrefab => selected.File ?? "",
            AssetKind.Model => $@"DATA\{selected.Id}",
            _ => selected.Section?.Path ?? "",
        };
        return $"{selected.Id}    {file}";
    }

    /// <summary>The layer of a row in plain words.</summary>
    private static string Layer(AssetRow row) => row.Origin switch
    {
        ValueOrigin.ActiveMod => $"Active mod {row.ModId}",
        ValueOrigin.LowerMod => $"Mod {row.ModId}",
        _ => "Base data",
    };

    private static PropGroup? Group(EditorSession session, AssetRef asset) =>
        PropGroups.Of(session.Edits).FirstOrDefault(g => g.File == asset.File);

    /// <summary>The path of a category, for example "All assets > Solars > Station".</summary>
    private static string CategoryPath(IReadOnlyList<AssetCategory> categories, string id)
    {
        var parts = new List<string>();
        for (var category = categories.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
             category != null;
             category = category.ParentId == null ? null : categories.FirstOrDefault(c => c.Id == category.ParentId))
            parts.Insert(0, category.Label);
        return string.Join(" > ", parts);
    }
}
