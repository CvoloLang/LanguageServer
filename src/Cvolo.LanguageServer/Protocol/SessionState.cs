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
            if (_initializeParams?.RootUri is { } rootUri)
            {
                return rootUri.LocalPath;
            }

            return _initializeParams?.RootPath;
        }
    }

    public void MarkInitializeReceived(InitializeRequestParams? initializeParams)
    {
        _initializeParams = initializeParams;
        _initializeReceived = true;
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
            && string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            localPath = parsed.LocalPath;
            return true;
        }

        localPath = null;
        return false;
    }
}