using System;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using OpenLancer.Editor;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Play;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The keys of the editor have one source: the key handlers read their chords from <see cref="EditorKeys"/> and
/// <see cref="PreviewKeys.Table"/>, and Help &gt; Keys shows the same lists. The chords are the keys that the editor
/// used before the lists.
/// </summary>
public sealed class EditorKeysTests
{
    private const ImGuiKey Ctrl = ImGuiKey.ImGuiMod_Ctrl;
    private const ImGuiKey Shift = ImGuiKey.ImGuiMod_Shift;

    public static TheoryData<string, int> Chords => new()
    {
        { nameof(EditorKeys.Save), (int)(Ctrl | ImGuiKey.S) },
        { nameof(EditorKeys.Undo), (int)(Ctrl | ImGuiKey.Z) },
        { nameof(EditorKeys.Redo), (int)(Ctrl | ImGuiKey.Y) },
        { nameof(EditorKeys.RedoShift), (int)(Ctrl | Shift | ImGuiKey.Z) },
        { nameof(EditorKeys.Play), (int)ImGuiKey.F5 },
        { nameof(EditorKeys.PlayInGame), (int)(Ctrl | ImGuiKey.F5) },
        { nameof(EditorKeys.Stop), (int)(Shift | ImGuiKey.F5) },
        { nameof(EditorKeys.Rename), (int)ImGuiKey.F2 },
        { nameof(EditorKeys.Duplicate), (int)(Ctrl | ImGuiKey.D) },
        { nameof(EditorKeys.Delete), (int)ImGuiKey.Delete },
        { nameof(EditorKeys.Frame), (int)ImGuiKey.F },
        { nameof(EditorKeys.CancelDrag), (int)ImGuiKey.Escape },
        { nameof(EditorKeys.SelectAbove), (int)ImGuiKey.UpArrow },
        { nameof(EditorKeys.SelectBelow), (int)ImGuiKey.DownArrow },
        { nameof(EditorKeys.FrameFromHierarchy), (int)ImGuiKey.Enter },
        { nameof(EditorKeys.CopyName), (int)(Ctrl | ImGuiKey.C) },
    };

    [Theory]
    [MemberData(nameof(Chords))]
    public void EachKeyHasTheChordOfItsHandler(string name, int shortcut)
    {
        Assert.Equal(shortcut, Key(name).Chord.Shortcut);
    }

    [Fact]
    public void TheListsHoldEachKeyOnce()
    {
        var keys = KeyFields().Select(f => (EditorKey)f.GetValue(null)!).ToList();

        Assert.Equal(Chords.Count, keys.Count);
        Assert.Equal(keys.OrderBy(k => k.Chord.Text),
            EditorKeys.Global.Concat(EditorKeys.Scene).Concat(EditorKeys.Hierarchy).OrderBy(k => k.Chord.Text));
        Assert.Equal(keys.Count, keys.Select(k => k.Chord).Distinct().Count());
        Assert.All(keys, k => Assert.EndsWith(".", k.Action));
    }

    [Theory]
    [InlineData(ImGuiKey.S, true, false, "Ctrl+S")]
    [InlineData(ImGuiKey.Z, true, true, "Ctrl+Shift+Z")]
    [InlineData(ImGuiKey.F5, false, true, "Shift+F5")]
    [InlineData(ImGuiKey.Escape, false, false, "Esc")]
    [InlineData(ImGuiKey.LeftArrow, false, true, "Shift+Left arrow")]
    [InlineData(ImGuiKey.Delete, false, false, "Delete")]
    public void AChordHasAPlainText(ImGuiKey key, bool ctrl, bool shift, string text)
    {
        Assert.Equal(text, new KeyChord(key, ctrl, shift).Text);
    }

    /// <summary>Each key of the lists has a handler outside the lists and the Keys window. So the Keys window cannot
    /// show a key that the editor does not read. A static field initializer only lists keys, for example for a help
    /// popover, so it is not a handler.</summary>
    [Fact]
    public void AHandlerReadsEachKeyOfTheList()
    {
        var readers = typeof(EditorWindow).Assembly.GetTypes()
            .SelectMany(IlWalker.FieldLoads)
            .Where(l => l.Owner != typeof(EditorKeys) && l.Owner.Namespace != "OpenLancer.Editor.Help" &&
                        l.Method is not ConstructorInfo { IsStatic: true })
            .Select(l => l.Field)
            .ToHashSet();

        var unread = KeyFields().Where(f => !readers.Contains(f)).Select(f => f.Name).ToList();

        Assert.True(unread.Count == 0, "Keys without a handler: " + string.Join(", ", unread));
    }

    /// <summary>The handlers of the keys of the lists read no key outside the lists. So the Keys window shows each key
    /// of these handlers. A lambda of a handler counts as a part of the handler.</summary>
    [Fact]
    public void TheHandlersReadNoKeyOutsideTheLists()
    {
        (Type Type, string Method)[] handlers =
        [
            (typeof(EditorWindow), "Shortcuts"), (typeof(SceneEdits), "Keys"), (typeof(HierarchyPanel), "Keys"),
            (typeof(SceneView), "Draw"), (typeof(TransformGizmo), "BeginFrame"), (typeof(TransformGizmo), "Keys"),
        ];
        var types = typeof(EditorWindow).Assembly.GetTypes();

        foreach (var (type, name) in handlers)
        {
            var parts = types.Where(t => IlWalker.OwnerOf(t) == type).ToList();
            bool InHandler(MethodBase method) => method.Name == name || method.Name.StartsWith($"<{name}>");

            var queries = parts.SelectMany(IlWalker.Calls)
                .Count(c => InHandler(c.Caller) && c.Callee is MethodInfo m && IsKeyQuery(m));
            var literals = parts.SelectMany(IlWalker.ConstantFirstArguments)
                .Where(a => InHandler(a.Caller) && IsKeyQuery(a.Callee))
                .Select(a => $"{a.Caller.Name}: {a.Callee.Name}({a.Value})")
                .ToList();

            Assert.True(queries > 0, $"{type.Name}.{name} reads no key.");
            Assert.True(literals.Count == 0, $"{type.Name}.{name} reads keys outside the lists: " +
                                             string.Join(", ", literals));
        }
    }

    /// <summary>The menus show the shortcut texts of the lists, so a menu and the Keys window give a key one
    /// name.</summary>
    [Fact]
    public void TheMenusShowTheShortcutTextsOfTheLists()
    {
        Type[] menus = [typeof(EditorWindow), typeof(SceneEdits), typeof(PlayMenu)];
        var texts = EditorKeys.Global.Concat(EditorKeys.Scene).Concat(EditorKeys.Hierarchy)
            .Select(k => k.Chord.Text)
            .Where(t => t.Contains('+') || t.StartsWith('F') && t.Length > 1)
            .Append("Del")
            .ToHashSet();

        var literals = typeof(EditorWindow).Assembly.GetTypes()
            .Where(t => menus.Contains(IlWalker.OwnerOf(t)))
            .SelectMany(IlWalker.Literals)
            .Where(l => texts.Contains(l.Text))
            .Select(l => $"{l.Owner.Name}.{l.Method.Name}: {l.Text}")
            .ToList();

        Assert.Contains("Ctrl+S", texts);
        Assert.Contains("F2", texts);
        Assert.True(literals.Count == 0, "Shortcut texts outside the lists: " + string.Join(", ", literals));
    }

    /// <summary>Ctrl+F5 installs the mods into a play copy. The Keys window says so, because a new modder must not
    /// think that the key writes into the game folder.</summary>
    [Fact]
    public void TheKeyOfPlayInTheGameSaysThatTheGameFolderDoesNotChange()
    {
        Assert.Contains("play copy", EditorKeys.PlayInGame.Action, StringComparison.Ordinal);
        Assert.EndsWith("Your game folder does not change.", EditorKeys.PlayInGame.Action, StringComparison.Ordinal);
        Assert.Empty(SteLint.Problems(EditorKeys.PlayInGame.Action));
    }

    [Fact]
    public void ThePreviewTableHasOneRowForEachCommand()
    {
        var commands = Enum.GetValues<PreviewKey>().Where(k => k != PreviewKey.None).ToList();

        Assert.Equal(commands, PreviewKeys.Table.Select(r => r.Command));
        Assert.Equal(commands.Count, PreviewKeys.Table.Select(r => r.Chord).Distinct().Count());
        Assert.All(PreviewKeys.Table, r => Assert.EndsWith(".", r.Action));
    }

    /// <summary>Shift picks the one-second step of an arrow key, and the other keys work with or without
    /// Shift.</summary>
    [Theory]
    [InlineData(ImGuiKey.Space, true, "PlayPause")]
    [InlineData(ImGuiKey.Home, true, "Start")]
    [InlineData(ImGuiKey.End, true, "End")]
    public void AKeyWithoutAShiftRowWorksWithShift(ImGuiKey key, bool shift, string command)
    {
        Assert.Equal(Enum.Parse<PreviewKey>(command), PreviewKeys.Read(true, false, k => k == key, shift));
    }

    /// <summary>Two keys in one frame give the command of the first row, as before the table.</summary>
    [Theory]
    [InlineData(true, "SecondBack")]
    [InlineData(false, "FrameBack")]
    public void TwoArrowKeysGiveTheLeftArrow(bool shift, string command)
    {
        Assert.Equal(Enum.Parse<PreviewKey>(command),
            PreviewKeys.Read(true, false, k => k is ImGuiKey.LeftArrow or ImGuiKey.RightArrow, shift));
    }

    [Fact]
    public void SpaceWinsOverAnArrowKeyAlsoWithShift()
    {
        Assert.Equal(PreviewKey.PlayPause,
            PreviewKeys.Read(true, false, k => k is ImGuiKey.Space or ImGuiKey.LeftArrow, true));
    }

    /// <summary>True for a method of ImGui that reads the state of a key or of a chord.</summary>
    private static bool IsKeyQuery(MethodBase method) =>
        method.DeclaringType == typeof(ImGui) &&
        method.Name is "IsKeyPressed" or "IsKeyDown" or "IsKeyReleased" or "Shortcut" or "IsKeyChordPressed";

    private static FieldInfo[] KeyFields() => typeof(EditorKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(EditorKey)).ToArray();

    private static EditorKey Key(string name) => (EditorKey)typeof(EditorKeys).GetField(name)!.GetValue(null)!;
}
