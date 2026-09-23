using System.Diagnostics.CodeAnalysis;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Minimal, instance-scoped session state. Each server instance owns exactly
/// one <see cref="SessionState"/>; no process-wide statics.
/// </summary>
internal sealed class SessionState
{
    private volatile bool _initializeReceived;
    private volatile bool _initializedReceived;
    private volatile bool _shutdownReceived;
    private InitializeRequestParams? _initializeParams;

    public bool InitializeReceived => _initializeReceived;

    public bool InitializedReceived => _initializedReceived;

    public bool ShutdownReceived => _shutdownReceived;

    /// <summary>
    /// Workspace folders from initialize (most specific containing folder
    /// becomes the per-document discovery boundary). Empty when the client did
    /// not send them.
    /// </summary>
    public IReadOnlyList<string> WorkspaceFolders
    {
        get
        {
            var folders = _initializeParams?.WorkspaceFolders;
            if (folders is null || folders.Length == 0)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>(folders.Length);
            foreach (var folder in folders)
            {
                if (folder is null || !TryGetLocalPath(folder.Uri, out string? localPath))
                {
                    continue;
                }

                result.Add(localPath);
            }

            return result;
        }
    }

    /// <summary>
    /// Workspace root from initialize: rootUri, falling back to rootPath.
    /// </summary>
    public string? WorkspaceRoot
    {
        get
        {
            if (_initializeParams?.RootUri is { } rootUri && TryGetLocalPath(rootUri, out string? localPath))
            {
                return localPath;
            }

            return _initializeParams?.RootPath;
        }
    }

    public void MarkInitializeReceived(InitializeRequestParams? initializeParams)
    {
        _initializeParams = initializeParams;
        HoverPrefersMarkdown = ComputeHoverMarkdown(initializeParams);
        HierarchicalDocumentSymbols =
            initializeParams?.Capabilities?.TextDocument?.DocumentSymbol?.HierarchicalDocumentSymbolSupport == true;
        ComputeSemanticTokens(initializeParams);
        PrepareRenameSupported = initializeParams?.Capabilities?.TextDocument?.Rename?.PrepareSupport == true;
        DocumentChangesSupported = initializeParams?.Capabilities?.Workspace?.WorkspaceEdit?.DocumentChanges == true;
        _initializeReceived = true;
    }


    /// <summary>Whether the client supports textDocument/prepareRename.</summary>
    public bool PrepareRenameSupported { get; private set; }

    /// <summary>Whether WorkspaceEdit.documentChanges is supported.</summary>
    public bool DocumentChangesSupported { get; private set; }

    private static readonly string[] CanonicalTokenTypes =
    [
        "namespace", "type", "struct", "enum", "interface", "typeParameter",
        "parameter", "variable", "property", "enumMember", "function", "method", "operator",
    ];

    private static readonly string[] CanonicalTokenModifiers = ["declaration", "readonly", "static"];

    /// <summary>
    /// Whether full semantic tokens are enabled for the session (all compatibility gates passed
    /// and the effective token-type legend is non-empty).
    /// </summary>
    public bool SemanticTokensEnabled { get; private set; }

    /// <summary>Whether the client supports <c>workspace/semanticTokens/refresh</c>.</summary>
    public bool RefreshSupported { get; private set; }

    /// <summary>The negotiated effective token-type legend (server canonical order).</summary>
    public string[] SemanticTokenTypes { get; private set; } = [];

    /// <summary>The negotiated effective token-modifier legend (server canonical order).</summary>
    public string[] SemanticTokenModifiers { get; private set; } = [];

    private void ComputeSemanticTokens(InitializeRequestParams? initializeParams)
    {
        var semanticTokens = initializeParams?.Capabilities?.TextDocument?.SemanticTokens;
        RefreshSupported = initializeParams?.Capabilities?.Workspace?.SemanticTokens?.RefreshSupport == true;

        var clientTypes = semanticTokens?.TokenTypes ?? [];
        SemanticTokenTypes = [.. CanonicalTokenTypes.Where(clientTypes.Contains)];

        var clientModifiers = semanticTokens?.TokenModifiers ?? [];
        SemanticTokenModifiers = [.. CanonicalTokenModifiers.Where(clientModifiers.Contains)];

        var fullRequested = IsFullRequested(semanticTokens?.Requests?.Full);
        var relativeFormat = semanticTokens?.Formats?.Contains("relative", StringComparer.Ordinal) == true;
        // LSP-5 is augmentation-only: absence is not consent to mix semantic and lexical streams.
        var augments = semanticTokens?.AugmentsSyntaxTokens == true;

        SemanticTokensEnabled = fullRequested && relativeFormat && augments && SemanticTokenTypes.Length > 0;
    }

    private static bool IsFullRequested(object? full) => full switch
    {
        null => false,
        bool value => value,
        _ => true,
    };

    /// <summary>
    /// Whether hover content should be returned as markdown. Prefers markdown
    /// when the client lists it before plaintext; otherwise plaintext (§7.2).
    /// </summary>
    public bool HoverPrefersMarkdown { get; private set; }

    /// <summary>
    /// Whether the client supports hierarchical document symbols. When false,
    /// document symbols are flattened to <c>SymbolInformation[]</c> (§7.3).
    /// </summary>
    public bool HierarchicalDocumentSymbols { get; private set; }

    private static bool ComputeHoverMarkdown(InitializeRequestParams? initializeParams)
    {
        string[]? formats = initializeParams?.Capabilities?.TextDocument?.Hover?.ContentFormat;
        if (formats is null || formats.Length == 0)
        {
            return false;
        }

        var markdown = Array.IndexOf(formats, "markdown");
        if (markdown < 0)
        {
            return false;
        }

        var plaintext = Array.IndexOf(formats, "plaintext");
        return plaintext < 0 || markdown < plaintext;
    }

    public void MarkInitialized()
    {
        _initializedReceived = true;
    }

    public void MarkShutdownReceived()
    {
        _shutdownReceived = true;
    }

    private static bool TryGetLocalPath(string? uriText, [NotNullWhen(true)] out string? localPath)
    {
        if (uriText is not null
            && Uri.TryCreate(uriText, UriKind.Absolute, out Uri? parsed)
            && TryGetLocalPath(parsed, out localPath))
        {
            return true;
        }

        localPath = null;
        return false;
    }

    private static bool TryGetLocalPath(Uri uri, [NotNullWhen(true)] out string? localPath)
    {
        if (uri.IsAbsoluteUri && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            localPath = uri.LocalPath;
            return true;
        }

        if (!uri.IsAbsoluteUri && Path.IsPathRooted(uri.OriginalString))
        {
            localPath = Path.GetFullPath(uri.OriginalString);
            return true;
        }

        localPath = null;
        return false;
    }
}