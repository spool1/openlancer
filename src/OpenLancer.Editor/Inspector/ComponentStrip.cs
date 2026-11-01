using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Scene;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Inspector;

/// <summary>
/// The components strip at the top of the page of an object (section 15.4 of the M3 design): one chip for each component
/// that the object has, in table order, with a glyph when the component has a problem, and "Add component". A click on
/// a chip opens the section of the page that holds its keys. A right click offers "Remove component", as the menu of a
/// Unity component does.
/// </summary>
internal sealed class ComponentStrip(EditorWindow window, PropertyGrid grid)
{
    /// <summary>The section of the object page that holds the keys of each component.</summary>
    private static readonly Dictionary<ObjectComponent, string> Sections = new()
    {
        [ObjectComponent.Solar] = "SystemObject.Archetype",
        [ObjectComponent.LoadoutUser] = "SystemObject.Archetype",
        [ObjectComponent.DockableStation] = "SystemObject.Docking",
        [ObjectComponent.BaseObject] = "SystemObject.Identity",
        [ObjectComponent.FactionMember] = "SystemObject.Faction",
        [ObjectComponent.JumpConnection] = "SystemObject.Docking",
        [ObjectComponent.TradeLaneRing] = "SystemObject.Docking",
        [ObjectComponent.PlanetRing] = "SystemObject.Appearance",
    };

    public void Draw(SectionRef section)
    {
        if (window.Session is not { } session)
        {
            return;
        }

        var components = ObjectComponents.Of(session.Edits, section);
        if (components.Count == 0)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.Indent();
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        var first = true;
        foreach (var component in components)
        {
            var name = ObjectComponents.Name(component.Kind);
            var errors = component.Problems.Any(p => p.Severity == DiagnosticSeverity.Error);
            var glyph = errors ? $"{EditorIcons.TimesCircle} " : component.Problems.Count > 0 ? $"{EditorIcons.ExclamationTriangle} " : "";
            var label = $"{glyph}{name}##component{component.Kind}";
            if (!first)
            {
                SameLineIfRoom(label, right);
            }

            first = false;
            if (errors)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Error);
            }
            else if (component.Problems.Count > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, EditorTheme.Warning);
            }

            if (ImGui.SmallButton(label) && Sections.TryGetValue(component.Kind, out var key))
            {
                grid.RequestSection(key);
            }

            if (errors || component.Problems.Count > 0)
            {
                ImGui.PopStyleColor();
            }

            ImGui.SetItemTooltip(component.Problems.Count == 0
                ? $"{name}: {string.Join(", ", ObjectComponents.Keys(component.Kind))}"
                : string.Join("\n", component.Problems.Select(p => p.Message)));
            if (ImGui.BeginPopupContextItem($"##remove{component.Kind}"))
            {
                EditorTheme.PushMenuColors();
                var problem = ObjectComponents.RemoveProblem(component.Kind) ?? session.NoEditReason ??
                              session.Edits.Get(section)?.ReadOnlyReason;
                if (MenuItems.Item(EditorIcons.TrashAlt, "Remove component", problem))
                {
                    window.SceneEdits.RemoveComponent(section, component.Kind);
                }

                EditorTheme.PopMenuColors();
                ImGui.EndPopup();
            }
        }

        var add = ObjectComponents.CanAdd(session.Edits, section);
        if (add.Count > 0)
        {
            SameLineIfRoom($"{EditorIcons.Plus} Add component", right);
            if (ImGui.SmallButton($"{EditorIcons.Plus} Add component"))
            {
                ImGui.OpenPopup("##add-component");
            }

            if (ImGui.BeginPopup("##add-component"))
            {
                EditorTheme.PushMenuColors();
                foreach (var (kind, unavailable) in add)
                {
                    if (MenuItems.Item(ObjectComponents.Name(kind) + "...", unavailable ?? session.NoEditReason))
                    {
                        window.SceneEdits.AddComponent(section, kind);
                    }
                }

                EditorTheme.PopMenuColors();
                ImGui.EndPopup();
            }
        }

        ImGui.Unindent();
        ImGui.Spacing();
    }

    /// <summary>Puts the next chip on the line of the chip before it when it fits.</summary>
    private static void SameLineIfRoom(string label, float right)
    {
        var width = ImGui.CalcTextSize(label.Split("##")[0]).X + 2 * ImGui.GetStyle().FramePadding.X;
        var x = ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X;
        if (x + width < right)
        {
            ImGui.SameLine();
        }
    }
}
