using System;
using OpenLancer.Data.Editing;
using OpenLancer.Editor;
using OpenLancer.Editor.Agent;
using OpenLancer.Editor.Editing;
using OpenLancer.Editor.Logs;
using OpenLancer.Studio.Agent;

namespace OpenLancer.Tests;

/// <summary>The editor of the editor host of the agent tools for the tests: a session, the Console, the settings and the
/// state that each test sets.</summary>
internal sealed class FakeAgentEditor : IAgentEditor
{
    public FakeAgentEditor() => Follow = new AgentFollow(Settings, FollowView);

    private EditSession? edits;

    /// <summary>The session of the test. Follow mode follows its changes, as the window makes it do in each
    /// frame.</summary>
    public EditSession? Edits
    {
        get => edits;
        set
        {
            edits = value;
            Follow.Track(value);
        }
    }

    public string? EditReason { get; set; }

    public string? RepoReason { get; set; }

    public string Name(int ids) => ids == 1 ? "Test Name" : "";

    public string InfoText(int ids) => "";

    public AgentSettings Settings { get; } = new();

    public ConsoleLog Log { get; } = new();

    public AgentView View => new("system:Li01", null, null);

    public IAgentViews Views { get; } = new FakeAgentViews();

    public IAgentPlay Play { get; } = new EditorAgentPlay(new AgentPlayToolTests.FakePlayEditor());

    /// <summary>The save of the test, or null for a save that does not start.</summary>
    public Func<SaveOutcome>? Saving { get; set; }

    public SaveOutcome Save() => Saving?.Invoke() ?? SaveOutcome.NotSaved;

    public ValidationState? Validation { get; set; }

    public event Action<ValidationState>? Validated;

    /// <summary>Ends a validation, as the runner of the editor does on the UI thread.</summary>
    public void Validate(ValidationState state)
    {
        Validation = state;
        Validated?.Invoke(state);
    }

    public int ValidatedHandlers => Validated?.GetInvocationList().Length ?? 0;

    /// <summary>The game links of the test.</summary>
    public AgentGames TestGames { get; set; } = new([], [], []);

    public AgentGames Games(OpenLancer.Data.Mods.ModManifest manifest) => TestGames;

    public string? ExportBlock { get; set; }

    public bool Paused { get; set; }

    /// <summary>The views of follow mode. They keep each request.</summary>
    public FakeFollowView FollowView { get; } = new();

    /// <summary>Follow mode over the settings and the views of the test. It follows the changes of
    /// <see cref="Edits"/>.</summary>
    public AgentFollow Follow { get; }
}

/// <summary>An item of the Scene of a test: the item of a section. The real Scene keeps the same item when its section
/// gets a new nickname.</summary>
internal sealed record FakeSceneItem(SectionRef Section);

/// <summary>The views of follow mode for the tests: the selection, and each request to show an item in the
/// Scene.</summary>
internal sealed class FakeFollowView : IFollowView
{
    public object? Selection { get; set; }

    /// <summary>Each request to show an item in the Scene: the section, the system and the selection flag.</summary>
    public System.Collections.Generic.List<(SectionRef Section, string System, bool Select)> Shown { get; } = [];

    /// <summary>False to act as a Scene that cannot show the system, for example while the game data loads.</summary>
    public bool CanShow { get; set; } = true;

    public SectionRef? SectionOf(object? item) => item switch
    {
        SectionRef section => section,
        FakeSceneItem scene => scene.Section,
        _ => null,
    };

    public bool ShowInScene(SectionRef section, string system, bool select)
    {
        if (!CanShow)
            return false;
        Shown.Add((section, system, select));
        if (select)
            Selection = new FakeSceneItem(section);
        return true;
    }
}
