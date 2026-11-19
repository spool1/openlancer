using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using OpenLancer.Data;
using OpenLancer.Data.Editing;
using OpenLancer.Data.Editing.Commands;

namespace OpenLancer.Studio.Agent;

/// <summary>An entry of the edit history that Undo or Redo takes, and who made it.</summary>
internal sealed record HistoryRow(string Label, EditOrigin Origin);

/// <summary>The state of the session and of the host (tool <c>studio_status</c>).</summary>
/// <param name="Host"><c>editor</c> or <c>headless</c>.</param>
internal sealed record StudioStatus(AgentHostKind Host)
{
    /// <summary>The name of the open profile, or null while no profile is open.</summary>
    public string? Profile { get; init; }

    /// <summary>The mods of the profile in load order.</summary>
    public IReadOnlyList<string> Mods { get; init; } = [];

    /// <summary>The mod that gets the edits, or null.</summary>
    public string? ActiveMod { get; init; }

    /// <summary>The tier of the active mod: 1 for data, 2 for the OpenLancer runtime.</summary>
    public int? Tier { get; init; }

    /// <summary>The target games of the active mod.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>True when the host takes edits now.</summary>
    public bool CanEdit { get; init; }

    /// <summary>Why the host takes no edits now, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>True when the session has edits that it did not save.</summary>
    public bool Dirty { get; init; }

    /// <summary>The revision of the edit view. A save does not change it.</summary>
    [JsonPropertyName("rev")]
    [JsonConverter(typeof(EditJson.LongAsText))]
    public long Revision { get; init; }

    public HistoryRow? Undo { get; init; }

    public HistoryRow? Redo { get; init; }

    /// <summary>What the editor shows now, or null in a host without a window.</summary>
    public AgentView? View { get; init; }

    /// <summary>True while the user pauses the agent edits.</summary>
    public bool Paused { get; init; }

    /// <summary>Ready, or reloading while the editor loads the game data. The host without a window always gives
    /// ready.</summary>
    public AgentDataLoad DataLoad { get; init; }

    /// <summary>The tool groups that the agent can use.</summary>
    public IReadOnlyList<ToolGroup> ToolGroups { get; init; } = [];

    public AgentLimits Limits { get; init; } = new();
}

/// <summary>The tool <c>studio_status</c>. It works also while no profile is open.</summary>
internal static class StatusQueries
{
    public static ToolStep Status(ToolCall call)
    {
        var host = call.Host;
        var status = new StudioStatus(host.Kind)
        {
            View = host.View,
            Paused = host.Paused,
            DataLoad = host.Views?.DataLoad ?? AgentDataLoad.Ready,
            ToolGroups = host.ShownGroups,
            Limits = host.Limits,
        };
        if (host.Session is not { } session)
            return ToolResult.Of(status with { Reason = AgentTexts.Error("no_profile") });
        var reason = host.NoEditReason;
        return ToolResult.Of(status with
        {
            Profile = session.Profile.Name,
            Mods = session.Profile.Mods.Select(m => m.Id).ToList(),
            ActiveMod = session.ActiveMod?.Id,
            Tier = session.ActiveMod?.Manifest.Tier,
            Targets = session.ActiveMod?.Manifest.Targets ?? [],
            CanEdit = reason == null,
            Reason = reason,
            Dirty = session.IsDirty,
            Revision = session.Revision,
            Undo = session.UndoLabel is { } undo ? new HistoryRow(undo, session.UndoOrigin ?? EditOrigin.User) : null,
            Redo = session.RedoLabel is { } redo ? new HistoryRow(redo, session.RedoOrigin ?? EditOrigin.User) : null,
        });
    }
}
