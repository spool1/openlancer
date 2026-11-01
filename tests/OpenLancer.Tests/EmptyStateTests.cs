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
using OpenLancer.Editor.Ui;
using Xunit;

namespace OpenLancer.Tests;

/// <summary>
/// The empty states of the main panels (#56). Each panel keeps its empty states as named fields. Each state tells what
/// the panel does not have, and what to do next.
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
            typeof(PanelStates), typeof(HierarchyPanel), typeof(InspectorPanel), typeof(DatabasePanel),
            typeof(AssetsPanel), typeof(ConsolePanel), typeof(MissionsBrowser), typeof(MissionView),
            typeof(CutsceneBrowser), typeof(CutsceneView), typeof(TimelinePanel), typeof(ModsWindow),
        ];
        Assert.All(panels, p => Assert.Contains(p, owners));
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
    }
}
