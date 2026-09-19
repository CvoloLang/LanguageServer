using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Coalescing coordinator for the server-to-client <c>workspace/semanticTokens/refresh</c> request
/// (§23). At most one request is in flight and one follow-up is pending; refresh is only sent when
/// semantic tokens are enabled and the client supports refresh, and no new work starts after
/// shutdown.
/// </summary>
internal sealed class SemanticTokensRefreshCoordinator(Func<bool> enabled, ILspLogger logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private JsonRpc? _rpc;
    private bool _pending;
    private bool _stopped;

    public void Attach(JsonRpc rpc) => _rpc = rpc;

    /// <summary>Stops starting or following up refresh work (shutdown/disposal).</summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            _stopped = true;
            _pending = false;
        }
    }

    /// <summary>
    /// Requests a refresh after a project semantic change. Coalesces concurrent requests; dispatch
    /// is asynchronous so document synchronization is never blocked on the client.
    /// </summary>
    public void RequestRefresh()
    {
        if (!enabled())
            return;

        lock (_stateLock)
        {
            if (_stopped || _pending)
                return;
            _pending = true;
        }

        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            while (true)
            {
                lock (_stateLock)
                {
                    if (!_pending || _stopped)
                    {
                        _pending = false;
                        return;
                    }

                    _pending = false;
                }

                JsonRpc? rpc = _rpc;
                if (rpc is null)
                    return;

                try
                {
                    await rpc.InvokeWithParameterObjectAsync<object?>("workspace/semanticTokens/refresh", null).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Warning($"semantic token refresh failed: {ex.Message}");
                }

                if (_stopped)
                    return;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
