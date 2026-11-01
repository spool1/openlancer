using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Editor.Games;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Assets;

/// <summary>
/// The Inspector page of a model file or a prop group (section 6.3 of the M23 design): the thumbnail, the file, the
/// layer and "Used by" with follow buttons. A group page also shows its members and the problems of its file, with
/// Place, Rename, Delete and "Show in folder".
/// </summary>
internal sealed class AssetPage(EditorWindow window)
{
    // Sizes in unscaled points.
    private const float Padding = 10;
    private const float PreviewSize = 192;

    /// <summary>The title of the header of the page: the name of a group, or the file name of a model.</summary>
    public string Title(AssetRef asset) => asset.Kind == AssetKind.PropGroup && Group(asset) is { } group
        ? group.Name
        : Path.GetFileName(asset.Id);

    public void Draw(AssetRef asset)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        var padding = MathF.Truncate(Padding * ImGuiHelper.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        var visible = ImGui.BeginChild("##asset-page", Vector2.Zero, ImGuiChildFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        if (visible)
        {
            Preview(asset);
            if (asset.Kind == AssetKind.PropGroup)
            {
                GroupPage(session, asset);
            }
            else
            {
                ModelPage(session, asset);
            }
        }

        ImGui.EndChild();
    }

    private void Preview(AssetRef asset)
    {
        var side = MathF.Truncate(PreviewSize * ImGuiHelper.Scale);
        if (window.Assets.Preview(asset) is { } image)
        {
            ImGui.Image(image, new Vector2(side, side), new Vector2(0, 1), new Vector2(1, 0));
        }
        else
        {
            var kind = ObjectKinds.Assets.Of(asset.Kind);
            var min = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(side, side));
            Glyphs.Draw(ImGui.GetWindowDrawList(), kind.Icon, MathF.Round(side * 0.4f), min + new Vector2(side / 2), kind.Abgr);
        }
    }

    /// <summary>The page of a model file: its file, its layer, and the sections that name it in <c>DA_archetype</c>.</summary>
    private void ModelPage(EditorSession session, AssetRef asset)
    {
        Line("File", $@"DATA\{asset.Id}");
        if (session.Edits.Assets.Find(AssetKind.Model, asset.Id) is { } row)
        {
            Line("Layer", row.Origin == ValueOrigin.Base ? "Base data" : $"Mod {row.ModId}");
            if (row.NotPlaceable != null)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextDisabled(row.NotPlaceable);
                ImGui.PopTextWrapPos();
            }
        }

        var users = session.Edits.Assets.UsersOf(asset.Id);
        ImGui.Spacing();
        ImGui.Text($"Used by {users.Count}");
        foreach (var user in users)
        {
            ImGui.PushID(user.ToString());
            var kind = ObjectKinds.OfSection(user.Id.Type);
            if (ImGui.SmallButton($"{EditorIcons.UpRightFromSquare}##follow"))
            {
                window.Selection = user;
            }

            ImGui.SetItemTooltip($"Select {user.Id}");
            ImGui.SameLine();
            ImGui.Text($"{kind.Icon}  {user.Id.Key}");
            ImGui.SameLine();
            ImGui.TextDisabled(Path.GetFileName(user.Path));
            ImGui.PopID();
        }
    }

    /// <summary>The page of a prop group: its file, its mod, its members and its problems, with the actions.</summary>
    private void GroupPage(EditorSession session, AssetRef asset)
    {
        if (Group(asset) is not { } group)
        {
            ImGui.TextDisabled("The prop group is not on disk now.");
            return;
        }

        Line("File", group.File);
        Line("Mod", group.ModId ?? $"{session.Edits.ActiveMod?.Id} (active mod)");
        ImGui.Spacing();
        var reason = !group.Editable
            ? $"The group is of mod '{group.ModId}'. Only a group of the active mod can change."
            : session.NoEditReason;
        var placeable = window.Assets.RowOf(asset) is { } row ? row : null;
        var placeReason = group.Problems.Count > 0 ? "The file has a problem, so the group does not place."
            : window.SceneEdits.Unavailable(Data.Editing.Scene.SceneAction.Create, null);
        if (Button($"{EditorIcons.Crosshairs}  Place", placeReason == null) && placeable != null)
        {
            window.SceneEdits.Spawn(placeable, window.Scene.Pivot, null);
        }

        ImGui.SetItemTooltip(placeReason ?? "Places the group at the camera pivot.");
        ImGui.SameLine();
        if (Button($"{EditorIcons.Pen}  Rename", reason == null))
        {
            window.SceneEdits.RenameGroup(group);
        }

        if (reason != null)
        {
            ImGui.SetItemTooltip(reason);
        }

        ImGui.SameLine();
        if (Button($"{EditorIcons.TrashAlt}  Delete", reason == null))
        {
            window.SceneEdits.DeleteGroup(group);
        }

        if (reason != null)
        {
            ImGui.SetItemTooltip(reason);
        }

        ImGui.SameLine();
        if (ImGui.Button($"{EditorIcons.FolderOpen}  Show in folder"))
        {
            FolderActions.Open(window, AssetsPanel.Source, Path.GetDirectoryName(group.File)!);
        }

        foreach (var problem in group.Problems)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(EditorTheme.Error, $"{EditorIcons.ExclamationTriangle}  {problem.Message}");
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.Text($"Members ({group.Members.Count})");
        if (!ImGui.BeginTable("##members", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        ImGui.TableSetupColumn("Member");
        ImGui.TableSetupColumn("Archetype");
        ImGui.TableSetupColumn("Position");
        ImGui.TableHeadersRow();
        foreach (var member in group.Members)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var kind = member.Type switch
            {
                "zone" => ObjectKinds.Of(ZoneRole.Other),
                "LightSource" => ObjectKinds.Light,
                _ => ObjectKinds.OtherObject,
            };
            ImGui.Text($"{kind.Icon}  {member.Member}");
            ImGui.TableNextColumn();
            var archetype = member.Lines.LastOrDefault(l => l.Name.Equals("archetype", StringComparison.OrdinalIgnoreCase));
            ImGui.TextDisabled(archetype is { Values.Count: > 0 } line ? line.Values[0].ToString() : "");
            ImGui.TableNextColumn();
            ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
                $"{member.Position.X:0}, {member.Position.Y:0}, {member.Position.Z:0}"));
        }

        ImGui.EndTable();
    }

    private PropGroup? Group(AssetRef asset) =>
        window.Session is { } session ? PropGroups.Of(session.Edits).FirstOrDefault(g => g.File == asset.File) : null;

    private static void Line(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(MathF.Truncate(70 * ImGuiHelper.Scale));
        ImGui.PushTextWrapPos(0);
        ImGui.TextWrapped(value);
        ImGui.PopTextWrapPos();
    }

    private static bool Button(string label, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.Button(label);
        ImGui.EndDisabled();
        return clicked;
    }
}
