using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenLancer.Data.Editing.Commands;
using OpenLancer.Data.Export;
using OpenLancer.Data.Publishing;
using OpenLancer.Editor.Ui;

namespace OpenLancer.Editor.Export;

/// <summary>The end of the write of an export: the result and the record in the publish history.</summary>
/// <param name="Record">The record in the history, or null when the history did not change.</param>
/// <param name="Warnings">The warnings of the write of the history.</param>
/// <param name="Closed">True when the Export dialog closed before the write ended.</param>
internal sealed record ExportWritten(ExportResult Result, RecordResult? Record, IReadOnlyList<string> Warnings, bool Closed);

/// <summary>
/// The tasks of one export of the Export dialog (milestone M11c): the prepare with the same-version guard, and the write
/// with the record in the publish history. Both run on a worker thread. <see cref="Update"/> takes their results on the
/// UI thread in each frame, also while the dialog is closed. So a close of the dialog cannot leave an export that never
/// ends, and a write that ends after the close still goes into the history.
/// </summary>
/// <remarks>Each member runs on the UI thread. The prepare and the write have no cancel. A close of the dialog drops the
/// result of a prepare, but the packages of a write that started go on to the disk.</remarks>
internal sealed class ExportRun
{
    private Task<GuardedExport>? preparing;
    private Task<ExportResult>? writing;
    private GuardedExport? writes;
    private bool replace;

    /// <summary>Runs when a prepare ends while the dialog is open.</summary>
    public event Action<GuardedExport>? Prepared;

    /// <summary>Runs when a write ends, also after a close of the dialog.</summary>
    public event Action<ExportWritten>? Written;

    /// <summary>Runs when a prepare or a write stops with an exception, with the reason.</summary>
    public event Action<string>? Failed;

    /// <summary>True while the export prepares or writes.</summary>
    public bool Running => preparing != null || writing != null;

    /// <summary>True while the export prepares.</summary>
    public bool Preparing => preparing != null;

    /// <summary>True when the dialog closed after the start of this run.</summary>
    public bool Closed { get; private set; }

    /// <summary>Starts the prepare of a new run on a worker thread.</summary>
    /// <exception cref="InvalidOperationException">A run is not complete.</exception>
    public void Prepare(Func<GuardedExport> prepare)
    {
        if (Running)
        {
            throw new InvalidOperationException("An export runs now.");
        }

        Closed = false;
        preparing = Task.Run(prepare);
    }

    /// <summary>Starts the write of a prepared export on a worker thread.</summary>
    /// <param name="replace">True after the answer "Replace version" of the guard.</param>
    /// <exception cref="InvalidOperationException">A run is not complete.</exception>
    public void Write(GuardedExport export, bool replace)
    {
        if (Running)
        {
            throw new InvalidOperationException("An export runs now.");
        }

        writes = export;
        this.replace = replace;
        writing = Task.Run(export.Write);
    }

    /// <summary>Tells the run that the dialog closed. A prepare that ends after this gives no event. A write still
    /// records its packages in the history.</summary>
    public void Close() => Closed = true;

    /// <summary>Takes the result of a task that ended. Call it once in each frame on the UI thread, also while the
    /// dialog is closed.</summary>
    public void Update()
    {
        if (preparing is { IsCompleted: true } prepared)
        {
            preparing = null;
            if (prepared.Exception?.GetBaseException() is { } failure)
            {
                Failed?.Invoke(ErrorText.Of(failure));
            }
            else if (!Closed)
            {
                Prepared?.Invoke(prepared.Result);
            }
        }

        if (writing is not { IsCompleted: true } written)
        {
            return;
        }

        writing = null;
        var export = writes!;
        writes = null;
        if (written.Exception?.GetBaseException() is { } writeFailure)
        {
            Failed?.Invoke(ErrorText.Of(writeFailure));
            return;
        }

        var (record, warnings) = export.RecordExport(written.Result, replace);
        Written?.Invoke(new ExportWritten(written.Result, record, warnings, Closed));
    }
}
