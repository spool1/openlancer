using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.Data;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.GameData.World;
using LibreLancer.ImUI;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Data.Missions;
using OpenLancer.Data.Missions.Editing;
using OpenLancer.Data.Thn;
using OpenLancer.Editor.Assets;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// Shows the selected item: a header block with its glyph, name and nickname, then the page for its type and the raw
/// fields. The rows of ships, equipment, goods and the other sections of the item files edit the active mod.
/// </summary>
internal sealed class InspectorPanel : IDisposable
{
    /// <summary>The id of the panel. The layout reports its focus by this id.</summary>
    public const string PanelId = "inspector";

    // Sizes in unscaled points.
    private const float HeaderHeight = 52;
    private const float HeaderPaddingX = 10;
    private const float GlyphSize = 22;
    private const float GlyphGap = 10;
    private const float TitleTop = 8;
    private const float LineGap = 3;
    private const float NicknameSize = 14;
    private const float ItemGap = 6;
    private const float ChipPaddingX = 6;
    private const float ChipHeight = 20;
    private const float ChipRounding = 3;
    private const float BannerPadding = 8;

    private readonly EditorWindow window;
    private readonly PropertyGrid grid;
    private readonly RawFields rawFields;
    private readonly SectionExtras rawBlock;
    private readonly SystemObjectPage systemObjectPage;
    private readonly ZonePage zonePage;
    private readonly FieldPage fieldPage;
    private readonly LightPage lightPage;
    private readonly ShipPage shipPage;
    private readonly EquipmentPage equipmentPage;
    private readonly StarSystemPage starSystemPage;
    private readonly SchemaPage schemaPage;
    private readonly ThnPage thnPage;
    private readonly AssetPage assetPage;
    private readonly MissionPage missionPage;
    private readonly PlayStartPage playStartPage;

    public InspectorPanel(EditorWindow window)
    {
        this.window = window;
        grid = new PropertyGrid(window);
        rawFields = new RawFields(grid);
        rawBlock = new SectionExtras(window, grid);
        systemObjectPage = new SystemObjectPage(window, grid);
        zonePage = new ZonePage(window, grid);
        fieldPage = new FieldPage(window, grid);
        lightPage = new LightPage(window, grid);
        shipPage = new ShipPage(window, grid);
        equipmentPage = new EquipmentPage(window, grid);
        starSystemPage = new StarSystemPage(window, grid);
        schemaPage = new SchemaPage(window, grid);
        thnPage = new ThnPage(window, grid);
        assetPage = new AssetPage(window);
        missionPage = new MissionPage(window, grid);
        playStartPage = new PlayStartPage(window, grid);
    }

    /// <summary>True while a field edit or a drag of a curve point is open. The edit history waits for its end. A save
    /// ends it first.</summary>
    public bool IsEditing => grid.IsEditing || thnPage.IsDragging || missionPage.IsEditing;

    /// <summary>Ends an open field edit and keeps its text, and an open drag of a curve point, for example when the
    /// window closes.</summary>
    public void EndEdit()
    {
        grid.EndEdit();
        thnPage.EndDrag();
        missionPage.EndEdit();
    }

    /// <summary>Forgets the items that the pages keep. Call it when the profile closes.</summary>
    public void Reset()
    {
        grid.Reset();
        rawFields.Reset();
        shipPage.Reset();
        equipmentPage.Reset();
        starSystemPage.Reset();
        fieldPage.Reset();
        missionPage.Reset();
        thnPage.Reset();
    }

    /// <summary>Frees the native editors of the pages.</summary>
    public void Dispose()
    {
        missionPage.Dispose();
        playStartPage.Dispose();
        thnPage.Dispose();
    }

    public void Draw(object? item)
    {
        grid.EndLostEdit();
        thnPage.Frame();
        if (item == null)
        {
            PanelStates.Message("Nothing selected.", "Select an object in the Hierarchy, the Scene or the Database.");
            return;
        }

        // A mission item has the page of the mission studio, with its own header line (section 10.1 of the M6a
        // design).
        // The play start of a mission has its own page (section 8.3 of the M6c design).
        if (item is MissionRef { Kind: MissionItemKind.PlayStart } playStart)
        {
            missionPage.EndEdit();
            playStartPage.Draw(playStart);
            return;
        }

        if (item is MissionRef mission)
        {
            missionPage.Draw(mission);
            return;
        }

        // A mission prefab of the Assets panel shows its Prefab page (section 8.6 of the M6b design).
        if (item is AssetRef { Kind: AssetKind.MissionPrefab } prefab)
        {
            missionPage.Draw(MissionRef.Of(PrefabRef.ModOf(prefab.Id).Script));
            return;
        }

        // Another page ends an open edit of the INI block of a mission page.
        missionPage.EndEdit();

        Header(item, window.Cutscenes.KindOf(item) ?? window.Scene.KindOf(item) ?? ObjectKinds.Unknown);
        Banner();
        // An object, a zone, a light, a nebula or a field of the open system shows its section of W.
        var sceneSection = IsSceneItem(item) ? window.SectionOf(item) : null;
        if (sceneSection != null)
        {
            SceneProblems(sceneSection);
        }

        if (window.SectionOf(item) is { } itemSection)
        {
            SaveErrors(itemSection);
            BaseChecks(itemSection);
        }

        // The sections have no window padding, so their headers run from edge to edge.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = ImGui.BeginChild("##sections", Vector2.Zero);
        ImGui.PopStyleVar();
        if (visible)
        {
            // The other types show only the raw fields.
            switch (item)
            {
                case SystemObject obj:
                    systemObjectPage.Draw(obj, sceneSection);
                    break;
                case Zone zone:
                    zonePage.Draw(zone, sceneSection);
                    break;
                case Nebula or AsteroidField:
                    fieldPage.Draw(item, sceneSection);
                    break;
                case LightSource light:
                    lightPage.Draw(light, sceneSection);
                    break;
                case Ship ship:
                    shipPage.Draw(ship);
                    break;
                case Equipment equipment:
                    equipmentPage.Draw(equipment);
                    break;
                case StarSystem system:
                    starSystemPage.Draw(system);
                    break;
                case SectionRef section:
                    schemaPage.Draw(section);
                    break;
                case ThnRef or ThnEventSet or CutsceneRoot:
                    thnPage.Draw(item);
                    break;
                case AssetRef asset:
                    // A model file or a prop group of the Assets panel (section 6.3 of the M23 design).
                    assetPage.Draw(asset);
                    break;
            }

            if (item is not (SectionRef or ThnRef or ThnEventSet or CutsceneRoot or AssetRef))
            {
                rawFields.Draw(item);
            }

            // The raw INI block is the last section of each page that edits sections (decision D115).
            var (rawKey, rawSections) = RawSections(item, sceneSection);
            rawBlock.Raw(rawSections, rawKey);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// Draws the header block: the kind glyph, the name and the nickname with a copy button at the left, and the
    /// read-only chip and the item menu at the right.
    /// </summary>
    private void Header(object item, ObjectKind kind)
    {
        var scale = ImGuiHelper.Scale;
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var max = min + new Vector2(width, MathF.Truncate(HeaderHeight * scale));
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(min, max, ImGui.GetColorU32(EditorTheme.Section));
        drawList.AddRectFilled(new Vector2(min.X, max.Y - 1), max, ImGui.GetColorU32(EditorTheme.SectionLine));

        var padding = MathF.Truncate(HeaderPaddingX * scale);
        var centerY = MathF.Truncate((min.Y + max.Y) / 2);
        var right = Buttons(item, new Vector2(max.X - padding, centerY));

        // The glyph is centered on its visible shape, in a square of its size.
        var font = ImGuiHelper.Roboto;
        var glyphSize = MathF.Round(GlyphSize * scale);
        var glyph = font.GetFontBaked(glyphSize).FindGlyph(kind.Icon);
        var glyphCenter = new Vector2(min.X + padding + glyphSize / 2, centerY);
        font.RenderChar(drawList, glyphSize,
            glyphCenter - new Vector2(glyph.X0 + glyph.X1, glyph.Y0 + glyph.Y1) / 2, kind.Abgr, kind.Icon);

        var cutscene = window.Cutscenes.Describe(item);
        var nickname = cutscene?.Identifier ?? Nickname(item) ?? "";
        var name = cutscene?.Title ?? (item is AssetRef asset ? assetPage.Title(asset) : window.Session?.ItemName(item));
        var title = !string.IsNullOrEmpty(name) ? name : nickname.Length > 0 ? nickname : item.GetType().Name;
        // A long title or nickname ends with "..." before the chip.
        var x = min.X + padding + glyphSize + MathF.Truncate(GlyphGap * scale);
        var titleSize = MathF.Round(EditorTheme.TitleSize * scale);
        var titleY = min.Y + MathF.Truncate(TitleTop * scale);
        TextFit.AddText(drawList, font, titleSize, new Vector2(x, titleY), ImGui.GetColorU32(EditorTheme.TextBright),
            title, right - x);

        // The nickname is in the monospace font. That font has no icons, so the copy button uses Roboto. The nickname
        // leaves room for the button.
        if (nickname.Length > 0)
        {
            var mono = ImGuiHelper.SystemMonospace;
            var monoSize = MathF.Round(NicknameSize * scale);
            var lineY = titleY + titleSize + MathF.Truncate(LineGap * scale);
            var gap = MathF.Truncate(ItemGap * scale);
            var buttonSize = new Vector2(monoSize + 4 * scale, monoSize);
            var nicknameWidth = TextFit.AddText(drawList, mono, monoSize, new Vector2(x, lineY),
                ImGui.GetColorU32(EditorTheme.TextDim), nickname, right - x - gap - buttonSize.X);
            var buttonX = x + nicknameWidth + gap;
            if (nicknameWidth > 0 && buttonX + buttonSize.X <= right)
            {
                ImGui.SetCursorScreenPos(new Vector2(buttonX, lineY));
                if (FlatButton($"{EditorIcons.Copy}##copy-nickname", buttonSize, EditorTheme.CaptionSize))
                {
                    ImGui.SetClipboardText(nickname);
                }

                ImGui.SetItemTooltip("Copy the nickname");
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
    }

    /// <summary>
    /// Draws the item menu button and the read-only chip from the right edge to the left, centered on
    /// <paramref name="anchor"/>. Returns the left edge of the chip, so the title stops before it.
    /// </summary>
    private float Buttons(object item, Vector2 anchor)
    {
        var scale = ImGuiHelper.Scale;
        var height = ImGui.GetFrameHeight();
        var menuWidth = ToolbarItems.IconWidth;
        var menuMin = new Vector2(anchor.X - menuWidth, anchor.Y - MathF.Truncate(height / 2));
        ImGui.SetCursorScreenPos(menuMin);
        if (FlatButton($"{EditorIcons.EllipsisV}##more", new Vector2(menuWidth, height), EditorTheme.BodySize))
        {
            ImGui.OpenPopup("##item-menu");
        }

        ImGui.SetItemTooltip("More");
        if (ImGui.BeginPopup("##item-menu"))
        {
            EditorTheme.PushMenuColors();
            ItemMenu(item);
            EditorTheme.PopMenuColors();
            ImGui.EndPopup();
        }

        // The chip: "Editing in <mod>" with a pen glyph, or a lock glyph and "Read-only", in the caption size.
        var drawList = ImGui.GetWindowDrawList();
        var font = ImGuiHelper.Roboto;
        var fontSize = MathF.Round(EditorTheme.CaptionSize * scale);
        var (text, tooltip, editing) = Chip(item);
        var textSize = font.CalcTextSizeA(fontSize, float.MaxValue, 0, text);
        var chipSize = new Vector2(MathF.Ceiling(textSize.X) + 2 * MathF.Truncate(ChipPaddingX * scale),
            MathF.Truncate(ChipHeight * scale));
        var chipMin = new Vector2(menuMin.X - MathF.Truncate(ItemGap * scale) - chipSize.X,
            anchor.Y - MathF.Truncate(chipSize.Y / 2));
        drawList.AddRectFilled(chipMin, chipMin + chipSize,
            ImGui.GetColorU32(editing ? EditorTheme.AccentStrong : EditorTheme.Strip), ChipRounding * scale);
        var textOffset = (chipSize - textSize) / 2;
        drawList.AddText(font, fontSize,
            chipMin + new Vector2(MathF.Truncate(textOffset.X), MathF.Truncate(textOffset.Y)),
            ImGui.GetColorU32(editing ? EditorTheme.TextBright : EditorTheme.Label), text);
        ImGui.SetCursorScreenPos(chipMin);
        ImGui.InvisibleButton("##chip", chipSize);
        ImGui.SetItemTooltip(tooltip);
        return chipMin.X - MathF.Truncate(ItemGap * scale);
    }

    /// <summary>
    /// The text and the tooltip of the chip: "Editing in &lt;mod&gt;" when the rows of the item can edit the active
    /// mod, or "Read-only" with the reason.
    /// </summary>
    private (string Text, string Tooltip, bool Editing) Chip(object item)
    {
        if (item is ThnRef or ThnEventSet or CutsceneRoot)
        {
            var cutscene = window.Session;
            var why = cutscene?.NoEditReason ?? item switch
            {
                ThnRef element => cutscene?.Edits.CutsceneReadOnlyReason(element),
                ThnEventSet set => cutscene?.Edits.CutsceneReadOnlyReason(set.Path),
                _ => cutscene?.Edits.CutsceneReadOnlyReason(((CutsceneRoot)item).Path),
            };
            if (why == null && cutscene?.Edits.ActiveMod is { } active)
            {
                return ($"{EditorIcons.Pen}  Editing in {active.Id}",
                    $"The edits go into the mod '{active.Id}' as a THN patch. A blue label marks a value that the " +
                    "mod changes.", true);
            }

            return ($"{EditorIcons.Lock}  Read-only", why ?? "No mod is active, so the profile is read-only.", false);
        }

        if (item is AssetRef)
        {
            return ($"{EditorIcons.Images}  Asset", "A model file or a prop group of the Assets panel. A prop group of the " +
                                                    "active mod has Rename and Delete on its page.", false);
        }

        var session = window.Session;
        var reason = session?.NoEditReason;
        var section = window.SectionOf(item);
        if (reason == null && section != null && session!.Edits.Get(section) is { } view)
        {
            reason = FieldBinding.SectionLock(session.Edits, section, view);
        }

        if (session == null || section == null)
        {
            reason ??= IsSceneItem(item) || item is StarSystem
                ? "The edit view has no section for this item, so the editor cannot change it."
                : "The editor edits ships, equipment, goods, markets, and the objects, zones, lights, nebulae, " +
                  "asteroid fields and settings of star systems.";
        }
        else if (reason == null)
        {
            var mod = session.Edits.ActiveMod!.Id;
            return ($"{EditorIcons.Pen}  Editing in {mod}",
                $"The edits go into the mod '{mod}' as patches. A blue label marks a value that the mod sets.", true);
        }

        return ($"{EditorIcons.Lock}  Read-only", reason, false);
    }

    /// <summary>
    /// A note above the sections when the active mod waits for a decision: a mod in the OpenLancer repo needs one
    /// confirmation for each session (decision D5), and the unsaved edits of an earlier session wait for a restore or a
    /// discard.
    /// </summary>
    private void Banner()
    {
        if (window.Session is not { Edits.ActiveMod: { } mod } session)
        {
            return;
        }

        string message;
        string button;
        Action action;
        if (session.Edits.HasRecovery)
        {
            message = $"The mod '{mod.Id}' has unsaved edits of an earlier session.";
            button = "Restore or discard...";
            action = window.Edits.OfferRecovery;
        }
        else if (session.NeedsRepoConfirmation && session.Edits.CanEdit)
        {
            message = $"The mod '{mod.Id}' is part of the OpenLancer repo at {session.RepoRoot}. Its files are tracked " +
                      "files.";
            button = "Allow edits for this session";
            action = () => session.RepoEditsAllowed = true;
        }
        else
        {
            return;
        }

        Strip("##banner", () =>
        {
            ImGui.TextColored(EditorTheme.Warning, EditorIcons.ExclamationTriangle.ToString());
            ImGui.SameLine();
            ImGui.TextWrapped(message);
            if (ImGui.Button(button))
            {
                action();
            }
        });
    }

    /// <summary>
    /// The problems of the scene that are not about one key of the section of the item, above the sections. For
    /// example, the scene does not show a section that the system loaded without. A problem about one key shows below
    /// the row of the key.
    /// </summary>
    private void SceneProblems(SectionRef section)
    {
        var problems = window.Scene.Problems(section).Where(p => p.Key == null).ToList();
        if (problems.Count > 0)
        {
            Strip("##problems", () => problems.ForEach(PropertyGrid.ProblemText));
        }
    }

    /// <summary>The settings key and the sections of the raw block of the page of an item. No section for an item whose
    /// page edits no section.</summary>
    /// <param name="sceneSection">The section of an object, a zone, a light, a nebula or a field of the open system, or
    /// null.</param>
    private (string Key, IReadOnlyList<SectionRef> Sections) RawSections(object item, SectionRef? sceneSection) => item switch
    {
        SystemObject or Zone or LightSource => (item.GetType().Name, sceneSection == null ? [] : [sceneSection]),
        // The section of the item, then the section of its zone (section 10.1 of the M22 design).
        Nebula or AsteroidField => (item.GetType().Name,
            new[] { sceneSection, SceneItems.ZoneOf(item) is { } zone ? window.SectionOf(zone) : null }
                .OfType<SectionRef>().ToList()),
        StarSystem system => (nameof(StarSystem), starSystemPage.RawSections(system)),
        Ship ship => (nameof(Ship), shipPage.RawSections(ship)),
        Equipment equipment => (nameof(Equipment), equipmentPage.RawSections(equipment)),
        SectionRef section => ($"Section.{section.Id.Type}", [section]),
        _ => ("", []),
    };

    /// <summary>
    /// The number of errors of the section that block the save, above the sections (section 11.2 of the M21 design): the
    /// errors of the data lists, and the errors of the field checks of a section that the active mod adds or changes.
    /// The rows show each error below its key.
    /// </summary>
    private void SaveErrors(SectionRef section)
    {
        if (window.Session is not { Edits.ActiveMod: not null } session || session.Edits.Get(section) is not { } view)
        {
            return;
        }

        // An error that the base game has too does not block the save (the key rule, decision D508 of the console
        // filter design).
        var edits = session.Edits;
        var errors = edits.CheckLists(section)
            .Count(p => p.Severity == DiagnosticSeverity.Error && !edits.IsBaseGameProblem(p));
        if (view.IsChanged)
        {
            errors += edits.Check(section).Count(p => p.Severity == DiagnosticSeverity.Error && !edits.IsBaseGameProblem(p));
        }

        if (errors > 0)
        {
            Strip("##save-errors", () => ImGui.TextColored(EditorTheme.Error,
                $"{EditorIcons.ExclamationTriangle}  {(errors == 1 ? "1 error blocks" : $"{errors} errors block")} the save."));
        }
    }

    /// <summary>
    /// The warnings of a <c>[Base]</c> of <c>universe.ini</c> (decision D254 of the M3 design): no object of its system
    /// docks with it, or no object of its system is its object in space.
    /// </summary>
    private void BaseChecks(SectionRef section)
    {
        if (!section.Id.Type.Equals("Base", StringComparison.OrdinalIgnoreCase) || window.Session is not { } session)
        {
            return;
        }

        var warnings = Data.Editing.Scene.ObjectComponents.Checks(session.Edits, section);
        if (warnings.Count > 0)
        {
            Strip("##base-checks", () =>
            {
                foreach (var warning in warnings)
                {
                    PropertyGrid.ProblemText(warning);
                }
            });
        }
    }

    /// <summary>A strip with padding above the sections, for a note or a warning.</summary>
    private static void Strip(string id, Action content)
    {
        var padding = MathF.Truncate(BannerPadding * ImGuiHelper.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, EditorTheme.Strip);
        if (ImGui.BeginChild(id, Vector2.Zero, ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding))
        {
            content();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    /// <summary>The nickname of an item, the zone nickname of a nebula or a field, or the key of a section, or
    /// null.</summary>
    private static string? Nickname(object item) => item switch
    {
        SectionRef section => section.Id.Key ?? section.Id.ToString(),
        AssetRef asset => asset.Id,
        _ => SceneItems.Nickname(item),
    };

    /// <summary>True for an item of a system with a section in the system file. These are the objects, zones, lights,
    /// nebulae and asteroid fields.</summary>
    private static bool IsSceneItem(object item) =>
        item is SystemObject or Zone or LightSource or Nebula or AsteroidField;

    private void ItemMenu(object item)
    {
        var section = window.SectionOf(item);
        if (Theme.IconMenuItem(EditorIcons.Clone, "New from template...", window.CanCopy(section)))
        {
            window.NewFromTemplate(section!);
        }

        if (Theme.IconMenuItem(EditorIcons.Undo, "Revert", window.CanRevert(section)))
        {
            window.Revert(section!);
        }

        ImGui.SetItemTooltip(window.RevertTooltip(section));
        // Duplicate, Rename and Delete of an item of the open system (milestone M3). Rename starts the field of its
        // Hierarchy row.
        if (IsSceneItem(item))
        {
            window.SceneEdits.ItemMenu(item);
        }

        ImGui.Separator();
        var nickname = Nickname(item);
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy nickname", nickname != null))
        {
            ImGui.SetClipboardText(nickname!);
        }

        var name = window.Session?.ItemName(item) ?? "";
        if (Theme.IconMenuItem(EditorIcons.Copy, "Copy name", name.Length > 0))
        {
            ImGui.SetClipboardText(name);
        }

        ImGui.Separator();
        if (Theme.IconMenuItem(EditorIcons.CompressArrowsAlt, "Frame in Scene", window.Scene.Contains(item)))
        {
            window.Scene.Focus(item);
        }
    }

    /// <summary>A button with no fill and no border until the mouse is over it.</summary>
    private static bool FlatButton(string label, Vector2 size, float fontSize)
    {
        EditorTheme.PushFlatButtonColors();
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
        ImGui.PushFont(null, fontSize);
        var clicked = ImGui.Button(label, size);
        ImGui.PopFont();
        ImGui.PopStyleVar(2);
        EditorTheme.PopFlatButtonColors();
        return clicked;
    }
}
