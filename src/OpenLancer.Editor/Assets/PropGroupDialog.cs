using System;
using System.Globalization;
using System.Linq;
using ImGuiNET;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Assets;
using OpenLancer.Editor.Dialogs;
using OpenLancer.Editor.Ui;
using OpenLancer.Studio.Commands;

namespace OpenLancer.Editor.Assets;

/// <summary>
/// The dialog "Save as prop group" (section 9.3 of the M23 design), and the rename of a prop group (decision D294). The
/// save dialog shows the members with their relative positions, the lines that the group leaves out with their reasons,
/// and the warnings. It writes the files at once, as Unity writes a prefab asset, so Undo does not remove them.
/// </summary>
internal sealed class PropGroupDialog : ModalDialog
{
    // The number of member rows that show before the member list scrolls.
    private const int ListRows = 8;

    private EditSession? session;
    private PropGroupDraft? draft;
    private PropGroup? renaming;
    private string nickname = "";
    private string name = "";

    // The name field shows the nickname until the user types a name (review finding R24).
    private bool nameTyped;
    private string? writeError;
    private Action<PropGroup>? done;

    protected override string Title => renaming != null ? "Rename prop group###propgroup" : "Save as prop group###propgroup";

    /// <param name="saved">Gets the new group after the write.</param>
    public void ShowSave(EditSession edits, PropGroupDraft groupDraft, Action<PropGroup> saved)
    {
        session = edits;
        draft = groupDraft;
        renaming = null;
        nickname = groupDraft.SuggestedNickname;
        name = nickname;
        nameTyped = false;
        writeError = null;
        done = saved;
        Open();
    }

    /// <param name="renamed">Gets the group with its new nickname.</param>
    public void ShowRename(EditSession edits, PropGroup group, Action<PropGroup> renamed)
    {
        session = edits;
        draft = null;
        renaming = group;
        nickname = group.Nickname;
        writeError = null;
        done = renamed;
        Open();
    }

    protected override void Body()
    {
        if (session == null)
        {
            return;
        }

        if (renaming != null)
        {
            RenameBody(session, renaming);
            return;
        }

        if (draft == null)
        {
            return;
        }

        if (draft.Refusals.Count > 0)
        {
            foreach (var refusal in draft.Refusals)
            {
                ImGui.TextWrapped(refusal.Message);
            }

            ImGui.Spacing();
            if (Button("OK", primary: true))
            {
                Close();
            }

            return;
        }

        ImGui.TextWrapped("The editor writes the prop group now. Undo does not remove it.");
        ImGui.Spacing();
        ImGui.Text($"Members ({draft.Members.Count}):");
        var height = Math.Min(draft.Members.Count, ListRows) * ImGui.GetTextLineHeightWithSpacing();
        if (ImGui.BeginChild("##members", new System.Numerics.Vector2(0, height + ImGui.GetStyle().WindowPadding.Y)))
        {
            foreach (var member in draft.Members)
            {
                var kind = KindOf(member.Type);
                ImGui.Text($"{kind.Icon}  {member.Member}");
                ImGui.SameLine();
                ImGui.TextDisabled(string.Create(CultureInfo.InvariantCulture,
                    $"{member.Position.X:0}, {member.Position.Y:0}, {member.Position.Z:0}"));
            }
        }

        ImGui.EndChild();
        if (draft.LeftOut.Count > 0)
        {
            ImGui.Text("These lines stay out of the group:");
            foreach (var line in draft.LeftOut)
            {
                ImGui.BulletText(line.Message);
            }
        }

        foreach (var warning in draft.Warnings)
        {
            ImGui.TextColored(EditorTheme.Warning, $"{EditorIcons.ExclamationTriangle}  {warning.Message}");
        }

        ImGui.Separator();
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputText("Nickname", ref nickname, 64);
        var problem = PropGroups.NicknameProblem(session, nickname.Trim());
        Problem(problem);
        if (!nameTyped)
        {
            name = nickname;
        }

        if (ImGui.InputText("Name", ref name, 128))
        {
            nameTyped = true;
        }

        var nameProblem = PropGroups.NameProblem(name);
        Problem(nameProblem);
        Problem(writeError);
        if (Button("Save", primary: true, enabled: problem == null && nameProblem == null))
        {
            var write = PropGroupCommands.Save(session, draft, nickname.Trim(), name.Trim());
            if (write.Error is { } error)
            {
                writeError = $"The editor could not write the prop group: {error.Message}";
            }
            else
            {
                Close();
                done?.Invoke(write.Group!);
            }

            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    private void RenameBody(EditSession edits, PropGroup group)
    {
        ImGui.TextWrapped($"The files of the prop group {group.Name} get the new nickname. The placed copies stay.");
        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.InputText("Nickname", ref nickname, 64);
        var trimmed = nickname.Trim();
        var problem = trimmed == group.Nickname ? "Type a new nickname." : PropGroups.NicknameProblem(edits, trimmed, group);
        Problem(problem);
        Problem(writeError);
        if (Button("Rename", primary: true, enabled: problem == null))
        {
            var rename = PropGroupCommands.Rename(edits, group, trimmed);
            if (rename.Error is { } error)
            {
                writeError = $"The editor could not rename the prop group: {error.Message}";
            }
            else
            {
                Close();
                done?.Invoke(rename.Group!);
            }

            return;
        }

        ImGui.SameLine();
        if (Button("Cancel"))
        {
            Close();
        }
    }

    private static ObjectKind KindOf(string type) => type.ToLowerInvariant() switch
    {
        "zone" => ObjectKinds.Of(ZoneRole.Other),
        "lightsource" => ObjectKinds.Light,
        _ => ObjectKinds.OtherObject,
    };
}
