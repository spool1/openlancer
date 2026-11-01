using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenLancer.Editor;
using OpenLancer.Editor.Assets;
using OpenLancer.Editor.Cutscene;
using OpenLancer.Editor.Database;
using OpenLancer.Editor.Hierarchy;
using OpenLancer.Editor.Inspector;
using OpenLancer.Editor.Logs;
using OpenLancer.Editor.Missions;
using OpenLancer.Editor.Mods;
using OpenLancer.Editor.Scene;
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The empty states of the main panels (#56). Each panel keeps its empty states as named fields. Each state tells what
/// the panel does not have, and what to do next. A panel can draw only such a field, so each empty panel tells the next
/// action.
/// </summary>
public sealed class EmptyStateTests
{
    /// <summary>The verbs that start the next action of an empty state.</summary>
    private static readonly HashSet<string> Actions =
        ["Open", "Select", "Double-click", "Click", "Right-click", "Pick", "Press", "Clear", "Turn", "Read"];

    /// <summary>Each empty state field of the editor, with the type that holds it.</summary>
    private static List<(Type Owner, string Name, EmptyState State)> States() =>
        typeof(EditorWindow).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.DeclaredOnly)
                .Where(f => f.FieldType == typeof(EmptyState))
                .Select(f => (t, f.Name, (EmptyState)f.GetValue(null)!)))
            .ToList();

    public static TheoryData<string> Names() => new(States().Select(s => $"{s.Owner.Name}.{s.Name}"));

    private static EmptyState Of(string name) => States().Single(s => $"{s.Owner.Name}.{s.Name}" == name).State;

    [Fact]
    public void EachMainPanelHasItsEmptyStates()
    {
        var owners = States().Select(s => s.Owner).ToHashSet();
        Type[] panels =
        [
            typeof(PanelStates), typeof(SceneView), typeof(HierarchyPanel), typeof(InspectorPanel),
            typeof(DatabasePanel), typeof(AssetsPanel), typeof(ConsolePanel), typeof(MissionsBrowser),
            typeof(MissionView), typeof(CutsceneBrowser), typeof(CutsceneView), typeof(TimelinePanel),
            typeof(ModsWindow),
        ];
        Assert.All(panels, p => Assert.Contains(p, owners));
    }

    /// <summary>A panel draws an empty state only from a named field. Each draw method of <see cref="PanelStates"/>
    /// takes an <see cref="EmptyState"/>, and only a static constructor makes one. So a panel cannot draw a message
    /// that the other tests of this class do not check.</summary>
    [Fact]
    public void APanelDrawsAnEmptyStateOnlyFromANamedField()
    {
        var draws = typeof(PanelStates).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Message", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(draws);
        Assert.All(draws, m => Assert.Equal(typeof(EmptyState), m.GetParameters()[0].ParameterType));

        var made = typeof(EditorWindow).Assembly.GetTypes()
            .Where(t => IlWalker.OwnerOf(t) != typeof(EmptyState))
            .SelectMany(IlWalker.Calls)
            .Where(c => c.Callee is ConstructorInfo { DeclaringType: var type } && type == typeof(EmptyState) &&
                        c.Caller is not ConstructorInfo { IsStatic: true })
            .Select(c => $"{c.Owner.Name}.{c.Caller.Name}")
            .Distinct()
            .ToList();
        Assert.Empty(made);
    }

    /// <summary>Each empty state shows in a panel: a method that is not a static constructor reads its field.</summary>
    [Fact]
    public void EachStateShowsInAPanel()
    {
        var read = typeof(EditorWindow).Assembly.GetTypes()
            .SelectMany(IlWalker.FieldLoads)
            .Where(l => l.Method is not ConstructorInfo { IsStatic: true })
            .Select(l => (l.Field.DeclaringType!, l.Field.Name))
            .ToHashSet();
        Assert.All(States(), s => Assert.Contains((s.Owner, s.Name), read));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EachStateSaysWhatToDoNext(string name)
    {
        var state = Of(name);
        Assert.False(string.IsNullOrWhiteSpace(state.Message));
        Assert.EndsWith(".", state.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(state.Next));
        Assert.Contains(state.Next.Split(' ')[0].TrimEnd(','), Actions);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EachStateIsSimplifiedTechnicalEnglish(string name)
    {
        var state = Of(name).With("ring");
        Assert.Empty(SteLint.Problems(state.Message));
        Assert.Empty(SteLint.Problems(state.Next));
    }

    [Fact]
    public void AStateGetsTheTextOfTheSearchInItsMessage()
    {
        Assert.Equal(new EmptyState("No asset matches 'ring'.", "Clear search"), AssetsPanel.NoAssetMatch.With("ring"));
        Assert.Equal("No rows match '{text}'.", DatabasePanel.NoRowMatch.With("{text}").Message);
        Assert.Equal(InspectorPanel.NothingSelected, InspectorPanel.NothingSelected.With("ring"));
        Assert.Equal("intro.thn has no preview.", CutsceneView.NoPreview.With("intro.thn").Message);
        Assert.Equal("No mod has the prefab 'patrol'.", MissionPage.NoPrefab.With("patrol").Message);
    }
}
