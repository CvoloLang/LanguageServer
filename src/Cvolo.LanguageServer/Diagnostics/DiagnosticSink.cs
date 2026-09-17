using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Server-to-client notification channel for <c>textDocument/publishDiagnostics</c>.
/// The JSON-RPC endpoint is attached after construction, and the related
/// information capability is captured from <c>initialize</c>. Publishing is
/// fire-and-forget and never blocks the JSON-RPC receive loop; asynchronous
/// failures are observed and logged rather than crashing the server.
/// </summary>
internal sealed class DiagnosticSink(ILspLogger? logger = null)
{
    private readonly ILspLogger _logger = logger ?? new NullLspLogger();
    private readonly Lock _lock = new();
    private JsonRpc? _rpc;
    private bool _relatedInformationSupported;

    /// <summary>Whether the client advertised related diagnostic information.</summary>
    public bool RelatedInformationSupported
    {
        get
        {
            lock (_lock)
            {
                return _relatedInformationSupported;
            }
        }
    }

    public void SetRelatedInformationSupported(bool supported)
    {
        lock (_lock)
        {
            _relatedInformationSupported = supported;
        }
    }

    public void Attach(JsonRpc rpc)
    {
        lock (_lock)
        {
            _rpc = rpc;
        }
    }

    public void Publish(PublishDiagnosticsPayload parameters)
    {
        JsonRpc? rpc;
        lock (_lock)
        {
            rpc = _rpc;
        }

        if (rpc is null)
        {
            _logger.Warning("[diag] publishDiagnostics requested before the JSON-RPC channel was attached; notification dropped.");
            return;
        }

        try
        {
            Task notification = rpc.NotifyWithParameterObjectAsync(Methods.TextDocumentPublishDiagnosticsName, parameters);
            ObserveNotification(notification, _logger);
            _logger.Debug($"[diag] sent publishDiagnostics for {parameters.Uri} ({parameters.Diagnostics.Length} diagnostic(s)).");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Publishing diagnostics failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Observes a fire-and-forget notification so a later fault is logged
    /// instead of surfacing as an unobserved task exception. Synchronous
    /// completion is inspected inline; otherwise a fault-only continuation
    /// records the failure without blocking the caller.
    /// </summary>
    internal static void ObserveNotification(Task notification, ILspLogger logger)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(logger);

        if (notification.IsCompleted)
        {
            if (notification.IsFaulted)
            {
                LogFault(notification.Exception, logger);
            }

            return;
        }

        _ = notification.ContinueWith(
            task => LogFault(task.Exception, logger),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void LogFault(Exception? exception, ILspLogger logger)
    {
        var message = exception?.GetBaseException().Message ?? "unknown error";
        logger.Warning($"Publishing diagnostics failed: {message}");
    }
}
