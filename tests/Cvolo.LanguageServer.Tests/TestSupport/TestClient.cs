using Cvolo.LanguageServer.Protocol;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;
using Xunit.Sdk;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Client-side JSON-RPC endpoint used to drive the server in tests.
/// </summary>
internal sealed class TestClient : IDisposable
{
    private readonly HeaderDelimitedMessageHandler _handler;
    private readonly JsonRpc _rpc;
    private readonly RecordingStream _clientOutput;
    private bool _disposed;

    public TestClient(DuplexTestTransport transport)
    {
        _clientOutput = new RecordingStream(transport.ClientOutput);
        JsonMessageFormatter formatter = new();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
        formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;
        _handler = new HeaderDelimitedMessageHandler(_clientOutput, transport.ClientInput, formatter);
        _rpc = new JsonRpc(_handler);
        _rpc.StartListening();
    }

    public async Task<InitializeResponse> InitializeAsync(int? processId = null, string? rootPath = null)
    {
        InitializeResponse? response = await _rpc
            .InvokeWithParameterObjectAsync<InitializeResponse>(
                Methods.InitializeName,
                new InitializeParams { ProcessId = processId, RootUri = rootPath is null ? null : new Uri(rootPath) })
            .ConfigureAwait(false);
        return response!;
    }

    public Task NotifyInitializedAsync()
    {
        return _rpc.NotifyAsync(Methods.InitializedName, new InitializedParams());
    }

    public Task<object?> InvokeDelayedAsync(CancellationToken cancellationToken = default)
    {
        return _rpc.InvokeWithParameterObjectAsync<object?>("cvolo/delayed", null, cancellationToken);
    }

    public Task<object?> ShutdownAsync()
    {
        return _rpc.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, null);
    }

    public Task NotifyExitAsync()
    {
        return _rpc.NotifyAsync(Methods.ExitName);
    }

    public Task NotifyUnknownAsync()
    {
        return _rpc.NotifyAsync("cvolo/unknownNotification", null);
    }

    public Task NotifySetTraceAsync(string value)
    {
        return _rpc.NotifyAsync("$/setTrace", new SetTraceParams(value));
    }

    /// <summary>Invokes a method expected to produce a JSON-RPC error.</summary>
    public async Task<RemoteRpcException> ExpectErrorAsync(string method, object? parameters)
    {
        try
        {
            await _rpc.InvokeWithParameterObjectAsync<object?>(method, parameters).ConfigureAwait(false);
        }
        catch (RemoteRpcException ex)
        {
            return ex;
        }

        throw new XunitException($"Expected a JSON-RPC error response from '{method}', but the call succeeded.");
    }

    public async Task<RemoteRpcException?> TryUnknownRequestAsync()
    {
        try
        {
            await _rpc.InvokeWithParameterObjectAsync<object?>("cvolo/unknownRequest", null).ConfigureAwait(false);
            return null;
        }
        catch (RemoteRpcException ex)
        {
            return ex;
        }
    }

    /// <summary>Writes a raw JSON body as a framed JSON-RPC message.</summary>
    public void SendRawJson(string json)
    {
        _clientOutput.Write(FrameParser.EncodeFrame(json));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rpc.Dispose();
        _handler.DisposeAsync().GetAwaiter().GetResult();
    }
}