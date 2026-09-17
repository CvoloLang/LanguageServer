using Microsoft.VisualStudio.LanguageServer.Protocol;

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
    private InitializeParams? _initializeParams;

    public bool InitializeReceived => _initializeReceived;

    public bool InitializedReceived => _initializedReceived;

    public bool ShutdownReceived => _shutdownReceived;

    public void MarkInitializeReceived(InitializeParams? initializeParams)
    {
        _initializeParams = initializeParams;
        _initializeReceived = true;
    }

    public void MarkInitialized() => _initializedReceived = true;

    public void MarkShutdownReceived() => _shutdownReceived = true;
}