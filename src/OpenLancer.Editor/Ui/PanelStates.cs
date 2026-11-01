using System;
using System.Numerics;
using ImGuiNET;
using LibreLancer.ImUI;

namespace OpenLancer.Editor.Ui;

/// <summary>
/// An empty state of a panel: what the panel does not have, and what to do next (#56). Each main panel keeps its empty
/// states as named fields, so a test checks that each one tells the next action.
/// </summary>
/// <param name="Message">What the panel does not have, for example "No system is open." A message can hold the text of
/// a search or a filter as <c>{text}</c>.</param>
/// <param name="Next">What to do next, for example "Select a system in the Scene toolbar." For a state with a link, it
/// is the text of the link, for example "Clear search".</param>
internal sealed record EmptyState(string Message, string Next)
{
    /// <summary>The place of the text of a search or a filter in <see cref="Message"/>.</summary>
    public const string TextPlace = "{text}";

    /// <summary>The state with the text of a search or a filter in its message.</summary>
    public EmptyState With(string text) =>
        this with { Message = Message.Replace(TextPlace, text, StringComparison.Ordinal) };
}

/// <summary>
/// The states of a panel body that has nothing to show yet: a loading spinner or a message, centered in the free space.
/// </summary>
internal static class PanelStates
{
    // Sizes in unscaled points.
    private const float SpinnerRadius = 12;
    private const float SpinnerThickness = 3;
    private const float LineGap = 8;

    /// <summary>The state of a panel while no profile is open.</summary>
    public static readonly EmptyState NoProfile = new("No profile is open.", "Open a profile from the File menu.");

    /// <summary>The state of a panel when the game data of the profile did not load.</summary>
    public static readonly EmptyState LoadFailed = new("The game data did not load.", "Read the error in the Console.");

    /// <summary>
    /// Draws the state of a panel that needs the game data, when the data is not there: no profile, the load, or a
    /// failed load.
    /// </summary>
    public static void NoGameData(EditorSession? session)
    {
        if (session == null)
        {
            Message(NoProfile);
        }
        else if (session.IsLoading)
        {
            Loading("Loading game data");
        }
        else
        {
            Message(LoadFailed);
        }
    }

    /// <summary>Draws a spinner above a line of text, both centered in the free space.</summary>
    public static void Loading(string text)
    {
        var scale = ImGuiHelper.Scale;
        var radius = MathF.Truncate(SpinnerRadius * scale);
        // The spinner item is as high as its circle plus two frame paddings.
        var spinnerHeight = 2 * (radius + ImGui.GetStyle().FramePadding.Y);
        var gap = MathF.Truncate(LineGap * scale);
        var textSize = ImGui.CalcTextSize(text);
        var (start, avail) = (ImGui.GetCursorPos(), ImGui.GetContentRegionAvail());
        var top = start.Y + MathF.Max(0, MathF.Truncate((avail.Y - spinnerHeight - gap - textSize.Y) / 2));

        ImGui.SetCursorPos(new Vector2(start.X + MathF.Truncate((avail.X - 2 * radius) / 2), top));
        ImGuiExt.Spinner("##loading", radius, (int)MathF.Round(SpinnerThickness * scale),
            ImGui.GetColorU32(EditorTheme.Accent));
        ImGui.SetCursorPos(new Vector2(start.X + MathF.Truncate((avail.X - textSize.X) / 2),
            top + spinnerHeight + gap));
        ImGui.TextDisabled(text);
    }

    /// <summary>Draws a dim message and a smaller hint below it, both centered in the free space.</summary>
    public static void Message(string message, string hint) => Lines(message, hint, false);

    /// <summary>Draws an empty state: its message, and what to do next below it, both centered in the free
    /// space.</summary>
    public static void Message(EmptyState state) => Lines(state.Message, state.Next, false);

    /// <summary>
    /// Draws the message of an empty state and its next action as a link below it, both centered in the free space.
    /// Returns true when the user clicks the link.
    /// </summary>
    public static bool MessageWithLink(EmptyState state) => Lines(state.Message, state.Next, true);

    /// <summary>Draws a message and a second line in the caption size: a dim hint, or a link.</summary>
    private static bool Lines(string message, string second, bool isLink)
    {
        var gap = MathF.Truncate(LineGap * ImGuiHelper.Scale / 2);
        var messageSize = ImGui.CalcTextSize(message);
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        var secondSize = ImGui.CalcTextSize(second);
        ImGui.PopFont();
        var (start, avail) = (ImGui.GetCursorPos(), ImGui.GetContentRegionAvail());
        var top = start.Y + MathF.Max(0, MathF.Truncate((avail.Y - messageSize.Y - gap - secondSize.Y) / 2));

        ImGui.SetCursorPos(new Vector2(start.X + MathF.Max(0, MathF.Truncate((avail.X - messageSize.X) / 2)), top));
        ImGui.TextDisabled(message);
        ImGui.PushFont(null, EditorTheme.CaptionSize);
        ImGui.SetCursorPos(new Vector2(start.X + MathF.Max(0, MathF.Truncate((avail.X - secondSize.X) / 2)),
            top + messageSize.Y + gap));
        var clicked = false;
        if (isLink)
        {
            clicked = ImGui.TextLink(second);
        }
        else
        {
            ImGui.TextDisabled(second);
        }

        ImGui.PopFont();
        return clicked;
    }
}
