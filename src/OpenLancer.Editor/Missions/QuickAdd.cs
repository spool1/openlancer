using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Missions;

/// <summary>
/// The quick add popup of the mission graph (section 8.3 of the M6b design): a search over the items of the library,
/// as the fuzzy finder of Unity Visual Scripting. Space in the graph, the toolbar button "Add...", "Add..." in a menu
/// and a drag from a pin to an empty place open it. Up, Down, Enter and Esc work. From a pin, it shows only the items
/// that fit the pin.
/// </summary>
/// <param name="frames">Gives the number of the frame now. The default is the frame count of ImGui. The tests give a fake
/// count.</param>
internal sealed class QuickAdd(Func<int>? frames = null)
{
    // Sizes in unscaled points.
    private const float PopupWidth = 320;
    private const float ListHeight = 300;
    private const string PopupId = "##quick-add";

    private readonly DrawnFrame drawn = new(frames);
    private QuickAddContext? context;
    private Vector2 at;
    private bool openNow;
    private string search = "";
    private int selected;

    /// <summary>True while the popup is open. An agent call waits for it (section 5.5, item 3 of the MCP1 design). A popup
    /// whose view does not draw any more is not open.</summary>
    public bool IsOpen => context != null && drawn.Recent;

    /// <summary>Opens the popup at a screen point in the next draw.</summary>
    public void Open(QuickAddContext place, Vector2 screen)
    {
        context = place;
        at = screen;
        openNow = true;
        drawn.Drew();
        search = "";
        selected = 0;
    }

    /// <summary>Draws the popup. Returns the place and the item of a pick, or null.</summary>
    public (QuickAddContext Context, LibraryItem Item)? Draw(IReadOnlyList<LibrarySection> sections)
    {
        drawn.Drew();
        if (context is not { } place)
        {
            return null;
        }

        if (openNow)
        {
            ImGui.OpenPopup(PopupId);
            ImGui.SetNextWindowPos(at);
        }

        ImGui.SetNextWindowSize(new Vector2(PopupWidth * ImGuiHelper.Scale, 0));
        if (!ImGui.BeginPopup(PopupId))
        {
            context = null;
            return null;
        }

        (QuickAddContext, LibraryItem)? result = null;
        EditorTheme.PushMenuColors();
        if (openNow)
        {
            ImGui.SetKeyboardFocusHere();
            openNow = false;
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "Search", ref search, 128))
        {
            selected = 0;
        }

        var items = MissionLibrary.Search(sections, search, i => GraphEditModel.Fits(place, i));
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
        {
            selected = Math.Min(selected + 1, items.Count - 1);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
        {
            selected = Math.Max(selected - 1, 0);
        }

        if ((ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)) && selected >= 0 &&
            selected < items.Count)
        {
            result = (place, items[selected]);
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            ImGui.CloseCurrentPopup();
            context = null;
        }

        if (items.Count == 0)
        {
            ImGui.TextDisabled("No item matches the search.");
        }
        else if (ImGui.BeginChild("##items", new Vector2(0, ListHeight * ImGuiHelper.Scale)))
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (ImGui.Selectable($"{item.Glyph}  {item.Name}##{item.Kind}.{item.Id}", i == selected))
                {
                    result = (place, item);
                }

                if (i == selected && ImGui.IsKeyPressed(ImGuiKey.DownArrow) | ImGui.IsKeyPressed(ImGuiKey.UpArrow))
                {
                    ImGui.SetScrollHereY();
                }

                ImGui.SameLine();
                ImGui.TextDisabled(item.Group);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(item.Help.WhatItIs);
                }
            }
        }

        if (items.Count > 0)
        {
            ImGui.EndChild();
        }

        if (result != null)
        {
            ImGui.CloseCurrentPopup();
            context = null;
        }

        EditorTheme.PopMenuColors();
        ImGui.EndPopup();
        return result;
    }
}
