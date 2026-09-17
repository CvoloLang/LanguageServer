using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Logging;
using System.Collections.Concurrent;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Owns one logical diagnostic worker per backend project. At most one analysis
/// executes per project; a newer request supersedes any older pending one.
/// Different projects run concurrently. Work never blocks the JSON-RPC receive
/// loop, and stale results never publish.
/// </summary>
internal sealed class DiagnosticScheduler(DocumentStore store, IDiagnosticPublisher publisher, ILspLogger logger) : IDisposable
{
    private const int FailureRecoveryThreshold = 3;

    private readonly ConcurrentDictionary<BackendProject, ProjectWorker> _workers = new();
    private volatile bool _stopped;

    /// <summary>
    /// Requests a diagnostic run for <paramref name="project"/>. Repeated
    /// requests collapse into a single pending run of the newest state.
    /// </summary>
    public void Schedule(BackendProject project)
    {
        if (_stopped)
        {
            return;
        }

        logger.Debug($"[diag] schedule requested for project '{project}'.");
        ProjectWorker worker = _workers.GetOrAdd(project, p => new ProjectWorker(this, p));
        worker.Request();
    }

    /// <summary>
    /// Called when the last open document of a project closes: stops its worker
    /// and resets the consecutive-failure count.
    /// </summary>
    public void OnProjectDrained(BackendProject project)
    {
        if (_workers.TryRemove(project, out ProjectWorker? worker))
        {
            worker.Stop();
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _workers.Clear();
    }

    private void RunOnce(BackendProject project, ProjectWorker worker)
    {
        DiagnosticRunContext context;
        try
        {
            if (!store.TryCaptureDiagnosticRun(project, out context))
            {
                logger.Debug($"[diag] no open documents for project '{project}'; nothing to analyze.");
                return;
            }
        }
        catch (Exception ex)
        {
            HandleFailure(project, worker, null, ex);
            return;
        }

        logger.Debug($"[diag] captured {context.Targets.Count} open document(s) for project '{project}'.");

        BackendDiagnosticRun run;
        try
        {
            BackendDocumentHandle[] handles = context.Targets.Select(target => target.Document).ToArray();
            run = store.GetDiagnostics(context.CurrentProjectSnapshot, handles);
        }
        catch (Exception ex)
        {
            HandleFailure(project, worker, context, ex);
            return;
        }

        // A successful analysis always resets the consecutive-failure count,
        // even when the result is superseded before it can be published.
        worker.ResetFailures();

        logger.Info($"[diag] analysis for project '{project}' returned {run.Diagnostics.Count} diagnostic(s) for {context.Targets.Count} document(s).");

        if (_stopped)
        {
            return;
        }

        bool committed;
        try
        {
            committed = store.TryCommitDiagnostics(
                context,
                eligible => publisher.Publish(context with { Targets = eligible }, run));
        }
        catch (Exception ex)
        {
            logger.Warning($"Publishing diagnostics for project '{project}' failed: {ex.Message}");
            return;
        }

        if (committed)
        {
            logger.Info($"[diag] committed diagnostics for project '{project}'.");
        }
        else
        {
            logger.Debug($"[diag] discarded diagnostics for project '{project}': captured snapshot is stale.");
        }
    }

    private void HandleFailure(BackendProject project, ProjectWorker worker, DiagnosticRunContext? context, Exception ex)
    {
        logger.Error($"Diagnostic analysis failed for project '{project}': {ex.Message}");
        var failures = worker.IncrementFailures();
        if (failures < FailureRecoveryThreshold)
        {
            return;
        }

        logger.Warning($"Diagnostic analysis failed {failures} times consecutively for project '{project}'; clearing published diagnostics.");

        if (context is null || _stopped)
        {
            return;
        }

        try
        {
            store.TryCommitDiagnostics(context, eligible => publisher.PublishEmpty(eligible));
        }
        catch (Exception publishEx)
        {
            logger.Warning($"Clearing diagnostics for project '{project}' failed: {publishEx.Message}");
        }
    }

    private sealed class ProjectWorker(DiagnosticScheduler owner, BackendProject project)
    {
        private readonly Lock _lock = new();
        private bool _pending;
        private bool _running;
        private bool _stopped;
        private int _consecutiveFailures;

        public void Request()
        {
            lock (_lock)
            {
                if (_stopped)
                {
                    return;
                }

                _pending = true;
                if (_running)
                {
                    return;
                }

                _running = true;
            }

            _ = Task.Run(Process);
        }

        public void Stop()
        {
            lock (_lock)
            {
                _stopped = true;
            }
        }

        public void ResetFailures()
        {
            lock (_lock)
            {
                _consecutiveFailures = 0;
            }
        }

        public int IncrementFailures()
        {
            lock (_lock)
            {
                return ++_consecutiveFailures;
            }
        }

        private void Process()
        {
            while (true)
            {
                lock (_lock)
                {
                    if (!_pending || _stopped || owner._stopped)
                    {
                        _running = false;
                        return;
                    }

                    _pending = false;
                }

                owner.RunOnce(project, this);
            }
        }
    }
}
