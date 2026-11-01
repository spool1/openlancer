using System;
using ImGuiNET;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Cutscene;

/// <summary>A command of the preview keys.</summary>
internal enum PreviewKey
{
    None,
    PlayPause,
    Start,
    End,
    FrameBack,
    FrameForward,
    SecondBack,
    SecondForward,
}

/// <summary>
/// The keys of the preview (section 7.7 of the M8 design): Space plays or pauses, Home and End go to the start and the
/// end, Left and Right step one frame, and Shift with Left or Right steps one second. The keys work only while the
/// Timeline or the Cutscene panel has the focus and no text field takes the keys. F5 and Shift+F5 stay the global Play
/// and Stop of the game (M4).
/// </summary>
internal static class PreviewKeys
{
    /// <summary>The key of each command and what it does, in the order of the commands. <see cref="Read"/> reads the keys
    /// from this table, and Help &gt; Keys shows it.</summary>
    public static readonly (PreviewKey Command, KeyChord Chord, string Action)[] Table =
    [
        (PreviewKey.PlayPause, new KeyChord(ImGuiKey.Space), "Play or pause the preview."),
        (PreviewKey.Start, new KeyChord(ImGuiKey.Home), "Go to the start of the cutscene."),
        (PreviewKey.End, new KeyChord(ImGuiKey.End), "Go to the end of the cutscene."),
        (PreviewKey.FrameBack, new KeyChord(ImGuiKey.LeftArrow), "Go back one frame."),
        (PreviewKey.FrameForward, new KeyChord(ImGuiKey.RightArrow), "Go forward one frame."),
        (PreviewKey.SecondBack, new KeyChord(ImGuiKey.LeftArrow, Shift: true), "Go back one second."),
        (PreviewKey.SecondForward, new KeyChord(ImGuiKey.RightArrow, Shift: true), "Go forward one second."),
    ];

    /// <summary>
    /// The command of the keys of this frame, or <see cref="PreviewKey.None"/>. The first row of <see cref="Table"/>
    /// whose key is pressed gives the command. A key that has a row with Shift needs the Shift state of its row. Each
    /// other key works with and without Shift.
    /// </summary>
    /// <param name="focused">The Timeline or the Cutscene panel has the focus.</param>
    /// <param name="textInput">A text field takes the keys.</param>
    /// <param name="pressed">True for a key that went down in this frame, or repeats.</param>
    /// <param name="shift">Shift is down.</param>
    public static PreviewKey Read(bool focused, bool textInput, Func<ImGuiKey, bool> pressed, bool shift)
    {
        if (!focused || textInput)
        {
            return PreviewKey.None;
        }

        foreach (var (command, chord, _) in Table)
        {
            if (pressed(chord.Key) && (chord.Shift == shift || !HasShiftRow(chord.Key)))
            {
                return command;
            }
        }

        return PreviewKey.None;
    }

    private static bool HasShiftRow(ImGuiKey key) => Array.Exists(Table, row => row.Chord.Key == key && row.Chord.Shift);
}
