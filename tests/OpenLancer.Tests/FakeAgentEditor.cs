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
    public EditSession? Edits { get; set; }

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
}
