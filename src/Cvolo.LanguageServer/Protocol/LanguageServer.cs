using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// JSON-RPC target for the language server lifecycle. An exception thrown by
/// one handler is contained by StreamJsonRpc (converted to a JSON-RPC error
/// response) and never kills the process when the protocol state is valid.
/// </summary>
internal sealed class LanguageServer(
    TerminationRequest termination,
    ILspLogger logger,
    IClientProcessWatcher clientWatcher,
    Func<DocumentStore>? storeFactory = null) : IDisposable
{
    private readonly SessionState _state = new();
    private readonly DiagnosticSink _diagnostics = new(logger);
    private readonly CancellationTokenSource _sessionCancellation = new();
    private DocumentStore? _store;
    private TextDocumentSyncHandler? _sync;
    private CompletionHandler? _completion;
    private HoverHandler? _hover;
    private DefinitionHandler? _definition;
    private DocumentSymbolHandler? _documentSymbols;
    private SemanticTokensHandler? _semanticTokens;
    private SemanticTokensRefreshCoordinator? _refresh;
    private Timer? _deadClientExitTimer;

    /// <summary>
    /// Server-to-client diagnostic notification channel. The transport attaches
    /// the JSON-RPC endpoint once it exists.
    /// </summary>
    internal DiagnosticSink Diagnostics => _diagnostics;

    /// <summary>
    /// Session-scoped document store shared by every semantic handler. The
    /// workspace folders and fallback root from initialize are fixed for the
    /// session, so the store is built once on first use.
    /// </summary>
    internal DocumentStore Store => _store ??= CreateStore();

    /// <summary>
    /// Text document synchronization (didOpen/didChange/didClose) handler.
    /// </summary>
    internal TextDocumentSyncHandler Sync => _sync ??= new TextDocumentSyncHandler(logger, _diagnostics, () => Store, () => Refresh.RequestRefresh());

    /// <summary>
    /// textDocument/completion handler.
    /// </summary>
    internal CompletionHandler Completion => _completion ??= new CompletionHandler(logger, () => Store);

    /// <summary>
    /// textDocument/hover handler.
    /// </summary>
    internal HoverHandler Hover => _hover ??= new HoverHandler(logger, () => Store, () => _state.HoverPrefersMarkdown);

    /// <summary>
    /// textDocument/definition handler.
    /// </summary>
    internal DefinitionHandler Definition => _definition ??= new DefinitionHandler(logger, () => Store);

    /// <summary>
    /// textDocument/documentSymbol handler.
    /// </summary>
    internal DocumentSymbolHandler DocumentSymbols => _documentSymbols ??= new DocumentSymbolHandler(logger, () => Store, () => _state.HierarchicalDocumentSymbols);

    /// <summary>
    /// textDocument/semanticTokens/full handler.
    /// </summary>
    internal SemanticTokensHandler SemanticTokens => _semanticTokens ??= new SemanticTokensHandler(logger, () => Store, () => _state.SemanticTokenTypes, () => _state.SemanticTokenModifiers);

    /// <summary>
    /// Server-to-client semantic-token refresh coordinator.
    /// </summary>
    internal SemanticTokensRefreshCoordinator Refresh => _refresh ??= new SemanticTokensRefreshCoordinator(
        () => _state.SemanticTokensEnabled && _state.RefreshSupported,
        logger);

    public InitializeResponse Initialize(InitializeRequestParams? initializeParams)
    {
        if (initializeParams is null)
        {
            throw new LocalRpcException("Invalid initialize parameters.")
            {
                ErrorCode = (int)JsonRpcErrorCode.InvalidParams,
            };
        }

        if (_state.InitializeReceived)
        {
            throw new LocalRpcException("The server is already initialized.")
            {
                ErrorCode = (int)JsonRpcErrorCode.InvalidRequest,
            };
        }

        if (initializeParams.ProcessId is { } clientPid)
        {
            try
            {
                if (!clientWatcher.IsProcessAlive(clientPid))
                {
                    logger.Error($"Client process {clientPid} is not alive; refusing the session.");
                    _deadClientExitTimer = new Timer(
                        _ => termination.TryRequestAbnormal(),
                        null,
                        TimeSpan.FromMilliseconds(150),
                        Timeout.InfiniteTimeSpan);
                    throw new LocalRpcException($"Client process {clientPid} is not alive.")
                    {
                        ErrorCode = (int)JsonRpcErrorCode.InvalidParams,
                    };
                }

                if (clientWatcher.TryWatch(clientPid, _sessionCancellation.Token, OnClientTerminated))
                {
                    logger.Info($"Watching client process {clientPid}.");
                }
                else
                {
                    logger.Info($"Could not watch client process {clientPid}; continuing best-effort.");
                }
            }
            catch (LocalRpcException)
            {
                // Confirmed client death at initialization.
                throw;
            }
            catch (Exception ex)
            {
                // Watcher/probe INFRASTRUCTURE failure is not confirmed client
                // death: degrade the session (no watching, keep it alive) rather
                // than terminating it.
                logger.Error($"Client process watcher unavailable for PID {clientPid}; continuing without watching: {ex.Message}");
            }
        }

        _state.MarkInitializeReceived(initializeParams);
        logger.Info($"Semantic tokens: enabled={_state.SemanticTokensEnabled}, refresh={_state.RefreshSupported}, tokenTypes={_state.SemanticTokenTypes.Length}, tokenModifiers={_state.SemanticTokenModifiers.Length}.");
        _diagnostics.SetRelatedInformationSupported(
            initializeParams.Capabilities?.TextDocument?.PublishDiagnostics?.RelatedInformation == true);
        logger.Info("Client initialized.");

        return new InitializeResponse(
            new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Incremental,
                },
                CompletionProvider = new CompletionOptions
                {
                    ResolveProvider = false,
                    TriggerCharacters = [".", "~"],
                },
                HoverProvider = true,
                DefinitionProvider = true,
                DocumentSymbolProvider = true,
                SemanticTokensProvider = _state.SemanticTokensEnabled
                    ? new SemanticTokensOptions
                    {
                        Legend = new SemanticTokensLegend
                        {
                            TokenTypes = _state.SemanticTokenTypes,
                            TokenModifiers = _state.SemanticTokenModifiers,
                        },
                        Full = true,
                        Range = false,
                    }
                    : null,
            },
            new ServerInfo(ServerMetadata.ServerName, ServerMetadata.ServerVersion));
    }

    public object? Shutdown(object? _ = null)
    {
        if (!_state.InitializeReceived)
        {
            throw ServerNotInitialized("The server has not been initialized.");
        }

        _state.MarkShutdownReceived();
        DisarmClientWatcher();
        logger.Info("Shutdown received; awaiting exit. Watcher disarmed.");
        return null;
    }

    public void Exit()
    {
        DisarmClientWatcher();
        if (_state.ShutdownReceived)
        {
            termination.TryRequestNormal();
            logger.Info("Exit after shutdown; exiting cleanly.");
        }
        else
        {
            termination.TryRequestAbnormal();
            logger.Info("Exit without shutdown; exiting abnormally.");
        }
    }

    public void Initialized(InitializedParams? _)
    {
        if (!_state.InitializeReceived)
        {
            // An 'initialized' before 'initialize' must not mutate SessionState:
            // ignore it and keep the session usable.
            logger.Error("initialized received before initialize; ignoring (session stays usable, state not mutated).");
            return;
        }

        _state.MarkInitialized();
        logger.Info("initialized notification received.");
    }

    [JsonRpcMethod("$/setTrace", UseSingleObjectParameterDeserialization = true)]
    public void HandleSetTrace(SetTraceParams? parameters)
    {
        logger.Verbose($"$/setTrace: {parameters?.Value}");
    }

    private DocumentStore CreateStore()
    {
        // Test seam: when supplied, the session uses the provided store instead of
        // building the production Cvolo-backed one.
        if (storeFactory is not null)
        {
            return storeFactory();
        }

        IReadOnlyList<string> folders = _state.WorkspaceFolders;
        var root = _state.WorkspaceRoot;

        if (folders.Count > 0)
        {
            logger.Info($"Document synchronization active; {folders.Count} workspace folder(s).");
        }
        else if (root is not null)
        {
            logger.Info($"Document synchronization active; workspace root '{root}'.");
        }
        else
        {
            logger.Info("Document synchronization active; no workspace root (project discovery is unbounded).");
        }

        CoreLoggerBridge coreLogger = new(logger);
        return new DocumentStore(new CvoloLanguageBackend(folders, root, coreLogger), coreLogger);
    }

    private void OnClientTerminated()
    {
        termination.TryRequestAbnormal();
        logger.Error("Client process terminated unexpectedly.");
    }

    private void DisarmClientWatcher()
    {
        try
        {
            _sessionCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Test-only read accessor: whether the 'initialized' notification has been
    /// accepted. Exposed through the test-host path; does not affect production
    /// behaviour.
    /// </summary>
    internal bool InitializedReceived => _state.InitializedReceived;

    public void Dispose()
    {
        DisarmClientWatcher();
        _refresh?.Stop();
        _sync?.Dispose();
        _deadClientExitTimer?.Dispose();
        _deadClientExitTimer = null;
        _sessionCancellation.Dispose();
    }

    private static LocalRpcException ServerNotInitialized(string message)
    {
        return new(message) { ErrorCode = ProtocolErrorCodes.ServerNotInitialized };
    }
}