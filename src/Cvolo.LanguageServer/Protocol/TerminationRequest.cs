namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Idempotent, first-wins termination gate. All requests to end the process go
/// through this single atomic gate so a process watcher and the JSON-RPC
/// lifecycle can never race into conflicting exit codes.
/// </summary>
internal sealed class TerminationRequest
{
    private const int None = 0;
    private const int Normal = 1;
    private const int Abnormal = 2;

    private int _state;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> Task => _completion.Task;
    public bool NormalRequested => Volatile.Read(ref _state) == Normal;
    public bool AbnormalRequested => Volatile.Read(ref _state) == Abnormal;
    public bool IsRequested => Volatile.Read(ref _state) != None;

    public bool TryRequestNormal()
    {
        if (Interlocked.CompareExchange(ref _state, Normal, None) == None)
        {
            _completion.TrySetResult(true);
            return true;
        }

        return false;
    }

    public bool TryRequestAbnormal()
    {
        if (Interlocked.CompareExchange(ref _state, Abnormal, None) == None)
        {
            _completion.TrySetResult(false);
            return true;
        }

        return false;
    }
}