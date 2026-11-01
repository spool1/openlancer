using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using LibreLancer.ImUI;
using OpenLancer.Editor;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Assets;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Database;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Help;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Ui;
using PlayMenu = OpenLancer.Editor.Play.PlayMenu;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// Each menu item that can be off tells why (#56). <see cref="MenuItems"/> draws such an item with its reason as the
/// tooltip. The test reads the IL of the editor and finds each call of ImGui or Theme that gives a menu item an enabled
/// flag that is not the constant true outside the helper. The main menu bar, the context menus of the panels and all
/// other menus are in the scan.
/// </summary>
public sealed class MenuItemsTests
{
    /// <summary>The methods of ImGui and Theme that draw a menu item or a submenu.</summary>
    private static readonly HashSet<string> MenuMethods = ["MenuItem", "BeginMenu", "IconMenuItem", "IconMenuToggle"];

    /// <summary>True for a call of ImGui or Theme that draws a menu item or a submenu with an enabled flag as its last
    /// parameter. An optional parameter is in the IL of each call, so <c>ImGui.BeginMenu("File")</c> passes the constant
    /// true.</summary>
    private static bool HasEnabledFlag(MethodBase callee) =>
        (callee.DeclaringType == typeof(ImGui) || callee.DeclaringType == typeof(Theme)) &&
        MenuMethods.Contains(callee.Name) && callee.GetParameters() is { Length: > 0 } parameters &&
        parameters[^1] is { Name: "enabled", ParameterType: var type } && type == typeof(bool);

    /// <summary>The menu calls of the types of an assembly with an enabled flag that can be false, as "type.method:
    /// callee".</summary>
    private static List<string> FlagsThatCanBeOff(IEnumerable<Type> types) =>
        types.SelectMany(IlWalker.LastArguments)
            .Where(a => HasEnabledFlag(a.Callee) && a.Value != 1)
            .Select(a => $"{a.Owner.Name}.{SourceName(a.Caller)}: {a.Callee.Name}")
            .Distinct()
            .ToList();

    /// <summary>The name of a method in the source: for a lambda or a local function, the name of the method that holds
    /// it, for example "InLambda" for "&lt;InLambda&gt;b__5_0".</summary>
    private static string SourceName(MethodBase method) =>
        method.Name.StartsWith('<') && method.Name.IndexOf('>', StringComparison.Ordinal) is > 1 and var end
            ? method.Name[1..end]
            : method.Name;

    private static IEnumerable<Type> EditorTypes() =>
        typeof(EditorWindow).Assembly.GetTypes().Where(t => IlWalker.OwnerOf(t) != typeof(MenuItems));

    [Fact]
    public void EachMenuItemThatCanBeOffGoesThroughTheHelper() => Assert.Empty(FlagsThatCanBeOff(EditorTypes()));

    /// <summary>The scan sees the menus: the editor draws its items that can be off with the helper, and its other
    /// items with the constant true. So the rule cannot pass with nothing to check.</summary>
    [Fact]
    public void TheScanFindsTheMenusOfTheEditor()
    {
        var calls = EditorTypes().SelectMany(IlWalker.LastArguments).ToList();
        var helper = calls.Where(c => c.Callee.DeclaringType == typeof(MenuItems)).Select(c => c.Owner).ToHashSet();
        Type[] menus =
        [
            typeof(EditorWindow), typeof(PlayMenu), typeof(AgentMenu), typeof(HelpMenu), typeof(SceneEdits),
            typeof(HierarchyPanel), typeof(AssetsPanel), typeof(InspectorPanel), typeof(DatabaseTable), typeof(GraphView),
            typeof(CutsceneBrowser), typeof(TimelinePanel),
        ];
        Assert.Empty(menus.Where(m => !helper.Contains(m)));
        Assert.Contains(calls, c => HasEnabledFlag(c.Callee) && c.Value == 1);
    }

    [Theory]
    [InlineData(MenuItems.NoProfile)]
    [InlineData(MenuItems.EditOpen)]
    [InlineData(MenuItems.NoNickname)]
    [InlineData(MenuItems.NoName)]
    [InlineData(MenuItems.NoRevert)]
    [InlineData(EditorWindow.CopyTip)]
    [InlineData(EditorWindow.RevertTip)]
    public void TheSharedReasonsAreSimplifiedTechnicalEnglish(string text) => Assert.Empty(SteLint.Problems(text));

    /// <summary>The rule finds each form of an enabled flag of <see cref="MenuSample"/> that can be false, and passes the
    /// constant true.</summary>
    [Fact]
    public void TheRuleFindsEachFlagThatCanBeOff()
    {
        Assert.Equal(
        [
            "MenuSample.Computed: MenuItem", "MenuSample.Either: IconMenuItem", "MenuSample.Never: MenuItem",
            "MenuSample.Submenu: BeginMenu", "MenuSample.InLambda: MenuItem",
        ], FlagsThatCanBeOff([typeof(MenuSample), .. typeof(MenuSample).GetNestedTypes(BindingFlags.NonPublic)]));
    }
}

/// <summary>Menu calls for the self-test of the rule. The test reads their IL and never runs them.</summary>
internal static class MenuSample
{
    public static bool Constant() => ImGui.MenuItem("Copy") | ImGui.BeginMenu("File") |
                                     Theme.IconMenuItem('x', "Copy", true) | ImGui.MenuItem("Thumbnails", "", true);

    public static bool Computed(string? reason) => ImGui.MenuItem("Save", "Ctrl+S", false, reason == null);

    public static bool Either(bool a, bool b) => Theme.IconMenuItem('x', "Delete", a || b);

    public static bool Never() => ImGui.MenuItem("No recent profiles", false);

    public static bool Submenu(bool open) => ImGui.BeginMenu("Create", open);

    public static Func<bool, bool> InLambda() => on => ImGui.MenuItem("Paste", "", false, on);
}
