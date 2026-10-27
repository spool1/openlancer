using System;
using System.Text.Json.Serialization;

namespace OpenLancer.Data.Editing.Commands;

/// <summary>The kind of an error of the command layer (section 4.0.6 of the M6b design). JSON writes it as
/// <c>code</c>, in snake case.</summary>
public enum EditErrorKind
{
    /// <summary>A reference does not resolve.</summary>
    NotFound,

    /// <summary>The session takes no edits, for example because of the mod lock or the write guard.</summary>
    ReadOnly,

    /// <summary>The arguments do not fit the schema.</summary>
    InvalidArgument,

    /// <summary>A value check refuses a value.</summary>
    InvalidValue,

    /// <summary>The plan refuses the edit, a play request cannot start, or an asset write runs in a batch.</summary>
    Refused,

    /// <summary>Strict mode: the batch adds errors. Nothing changed.</summary>
    NewErrors,

    /// <summary>The plan changes more than its targets. Nothing changed.</summary>
    NeedsConfirmation,

    /// <summary>Another item has the name or its game id.</summary>
    NameTaken,

    /// <summary>The revision, a script version, a row guard or a body guard differs.</summary>
    StaleRevision,

    /// <summary>A transaction is open, or the editor did not start the call in time.</summary>
    Busy,

    /// <summary>The save gate has errors.</summary>
    SaveBlocked,

    /// <summary>Files of the mod changed outside the editor.</summary>
    ChangedOnDisk,

    /// <summary>The same-version guard of an export stops it.</summary>
    GuardConflict,

    /// <summary>The user paused the agent edits.</summary>
    Paused,

    /// <summary>A limit of the host stops the call.</summary>
    Limit,

    /// <summary>The host or a milestone does not have the tool now.</summary>
    NotAvailable,

    /// <summary>The top undo entry is an edit of the user.</summary>
    NotAgentEntry,

    /// <summary>A step of a batch of the host failed. Nothing changed.</summary>
    StepFailed,
}

/// <summary>
/// An error of a command, a query, a play request or a host (section 4.0.6 of the M6b design). The Console of the
/// editor and the tool result of an agent show the same message.
/// </summary>
/// <param name="Kind">The kind. JSON writes it as <c>code</c>.</param>
/// <param name="Detail">The detail code, for example <c>RowChanged</c> or <c>NotInstallPath</c>.</param>
/// <param name="Message">What is wrong and the fix, in the strict form of Simplified Technical English.</param>
public sealed record EditError(
    [property: JsonPropertyName("code")] EditErrorKind Kind,
    string Detail,
    string Message)
{
    /// <summary>The index of the command in its batch, or null.</summary>
    public int? Index { get; init; }

    /// <summary>The reference of the item that the error is about, or null.</summary>
    public string? Target { get; init; }

    /// <summary>The field or the argument that the error is about, or null.</summary>
    public string? Field { get; init; }

    /// <summary>For the kind <see cref="EditErrorKind.StepFailed"/>: the kind of the error of the step that failed.
    /// Else null.</summary>
    public EditErrorKind? StepCode { get; init; }

    public override string ToString() => Message;
}

/// <summary>A command, a query or a parse refuses with an <see cref="EditError"/>. Nothing changed.</summary>
public sealed class EditException(EditError error) : Exception(error.Message)
{
    public EditError Error { get; } = error;
}
