using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic LSP-7 ??45 wire coverage for signature help using a blocking fake
/// backend: rendering, label offsets, per-signature active parameters, malformed
/// results that must never crash or clamp, staleness and cancellation.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class SignatureHelpHandlerTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public SignatureHelpHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(["a.cvl", "b.cvl"], relative => "int main() { return 0; }\n");
        _backend = new BlockingBackend();
        _store = new DocumentStore(_backend, new RecordingCoreLogger());
        _session = ProtocolSession.Start(storeFactory: () => _store);
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Uri Uri(string name = "a.cvl") => _workspace.DocumentUri(name);

    private async Task StartAsync()
    {
        await _session.Client.InitializeAsync().WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task StartWithCapabilitiesAsync(ClientCapabilitiesPayload capabilities)
    {
        await _session.Client.InitializeWithAsync(new InitializeRequestParams
        {
            ProcessId = null,
            RootUri = new Uri(_workspace.DirectoryPath),
            Capabilities = capabilities,
        }).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(string name, int version, string text)
    {
        await _session.Client.NotifyDidOpenAsync(Uri(name), "cvolo", version, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private BackendSignatureHelpResult SingleSignature(string label, int spanStart, int spanLength, int? activeParameter)
    {
        BackendSignatureParameter parameter = new(new BackendSignatureLabelSpan(spanStart, spanLength));
        return new BackendSignatureHelpResult(
            [new BackendSignatureCandidate(label, null, [parameter], activeParameter)],
            0);
    }

    private void ResetFramesAndSet(BackendSignatureHelpResult? result)
    {
        _session.Server.ResetServerFrames();
        _backend.CannedSignatureHelp = result;
        _backend.SignatureHelpResultFactory = null;
        _backend.ThrowOnSignatureHelp = false;
    }

    [Fact]
    public async Task Request_RendersToolingSignatureAndActiveParameter()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        Assert.NotNull(result);
        SignatureInformation signature = Assert.Single(result!.Signatures);
        Assert.Equal("f(int x)", signature.Label);
        Assert.Equal(0, result.ActiveSignature);
        Assert.Equal(0, result.ActiveParameter);
        Assert.True(signature.Parameters[0].Label.TryGetFirst(out string? label));
        Assert.Equal("int x", label);
    }

    [Fact]
    public async Task NullResult_IsReturnedAsProtocolNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(null);

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        Assert.Null(result);
    }

    [Fact]
    public async Task EmptySignatures_AreReturnedAsProtocolNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(new BackendSignatureHelpResult([], 0));

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        Assert.Null(result);
    }

    [Fact]
    public async Task OutOfRangeActiveSignature_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        var result = new BackendSignatureHelpResult(
            [new BackendSignatureCandidate("f(int x)", null,
                [new BackendSignatureParameter(new BackendSignatureLabelSpan(3, 5))], 0)],
            5);
        ResetFramesAndSet(result);

        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help"));
    }

    [Fact]
    public async Task EmptyLabel_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("", 0, 1, null));

        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help"));
    }

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(0, 0)]
    [InlineData(3, 6)]
    [InlineData(30, 5)]
    public async Task MalformedParameterSpan_ReturnsNull_NeverClamps(int start, int length)
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("f(int x)", start, length, 0));

        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help"));

        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));
        Assert.NotNull(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("next signature help"));
    }

    [Fact]
    public async Task ZeroParameterActiveSignature_WithNonNullActiveParameter_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int Ping() { return 1; }\nint main() { return Ping(|); }\n");
        ResetFramesAndSet(new BackendSignatureHelpResult(
            [new BackendSignatureCandidate("Ping()", null, [], 0)],
            0));

        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help"));
    }

    [Fact]
    public async Task EffectiveSignature_WithNullActiveParameter_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("f(int x)", 3, 5, null));

        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help"));
    }

    [Fact]
    public async Task EffectiveSignature_WithOutOfRangeActiveParameter_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("f(int x)", 3, 5, 17));

        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help"));
    }

    [Fact]
    public async Task NonEffectiveOutOfRangeActiveParameter_ReturnsNull()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        BackendSignatureHelpResult result = new(
            [
                new BackendSignatureCandidate("f(int x)", null,
                    [new BackendSignatureParameter(new BackendSignatureLabelSpan(3, 5))], 0),
                new BackendSignatureCandidate("g(int y)", null,
                    [new BackendSignatureParameter(new BackendSignatureLabelSpan(3, 5))], 9),
            ],
            0);
        ResetFramesAndSet(result);

        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help"));
    }

    [Fact]
    public async Task NonEffectiveInRangeActiveParameter_IsAccepted()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        BackendSignatureHelpResult result = new(
            [
                new BackendSignatureCandidate("f(int x)", null,
                    [new BackendSignatureParameter(new BackendSignatureLabelSpan(3, 5))], 0),
                new BackendSignatureCandidate("g(int y)", null,
                    [new BackendSignatureParameter(new BackendSignatureLabelSpan(3, 5))], 0),
            ],
            0);
        ResetFramesAndSet(result);

        SignatureHelp? wire = await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        Assert.NotNull(wire);
        Assert.Equal(2, wire!.Signatures.Length);
        Assert.Equal(0, wire.ActiveSignature);
    }

    [Fact]
    public async Task LabelOffsetSupport_EmitsStartEndRanges()
    {
        await StartWithCapabilitiesAsync(SignatureHelpCapabilities(labelOffset: true, activeParameter: false));
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));

        await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        JObject response = GetLastResponseFrame();
        var label = response["result"]!["signatures"]![0]!["parameters"]![0]!["label"];
        var range = Assert.IsType<JArray>(label);
        Assert.Equal(2, (int)range[0]);
        Assert.Equal(7, (int)range[1]);
        Assert.Equal(0, (int)response["result"]!["activeSignature"]);
        Assert.Equal(0, (int)response["result"]!["activeParameter"]);
    }

    [Fact]
    public async Task SubstringLabels_WhenLabelOffsetUnsupported()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        Assert.NotNull(result);
        ParameterInformation parameter = Assert.Single(result!.Signatures[0].Parameters);
        Assert.True(parameter.Label.TryGetFirst(out string? label));
        Assert.Equal("int x", label);
    }

    [Fact]
    public async Task PerSignatureActiveParameter_IsEmittedWhenNegotiated()
    {
        await StartWithCapabilitiesAsync(SignatureHelpCapabilities(labelOffset: false, activeParameter: true));
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        BackendSignatureHelpResult result = new(
            [
                new BackendSignatureCandidate("f(int x)", null,
                    [new BackendSignatureParameter(new BackendSignatureLabelSpan(3, 5))], 0),
                new BackendSignatureCandidate("g(int y, int z)", null,
                    [new BackendSignatureParameter(new BackendSignatureLabelSpan(3, 5)), new BackendSignatureParameter(new BackendSignatureLabelSpan(9, 5))], 1),
            ],
            0);
        _session.Server.ResetServerFrames();
        _backend.CannedSignatureHelp = result;

        await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        JObject response = GetLastResponseFrame();
        var signatures = Assert.IsType<JArray>(response["result"]!["signatures"]);
        Assert.Equal(0, (int)signatures[0]["activeParameter"]);
        Assert.Equal(1, (int)signatures[1]["activeParameter"]);
        Assert.Equal(0, (int)response["result"]!["activeSignature"]);
    }

    [Fact]
    public async Task Utf16SurrogatePairs_AreHandledInUtf16LabelSpace()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("int \U0001F916(int p)", 7, 5, 0));

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        // The astral char above occupies two UTF-16 units; label[7..12] must be exactly "int p".
        Assert.NotNull(result);
        ParameterInformation parameter = Assert.Single(result!.Signatures[0].Parameters);
        Assert.True(parameter.Label.TryGetFirst(out string? label));
        Assert.Equal("int p", label);
    }

    [Fact]
    public async Task FullWireRequest_WithTriggerContext_IsAccepted()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));

        string request = JsonRpcFrames.Request(
            77,
            "textDocument/signatureHelp",
            $"{{\"textDocument\":{{\"uri\":\"{Uri()}\"}},\"position\":{{\"line\":1,\"character\":20}},\"context\":{{\"triggerKind\":2,\"triggerCharacter\":\"(\",\"isRetrigger\":false}}}}");
        _session.Client.SendRawJson(request);
        await _session.Client.WaitUntilAsync(
            () => _session.Server.GetServerFrames().Any(frame => frame.Contains("\"id\":77", StringComparison.Ordinal)),
            "signature help response for id 77");

        JObject response = GetLastResponseFrame();
        Assert.Equal(77, (int)response["id"]!);
        Assert.Equal(0, (int)response["result"]!["activeSignature"]);
    }

    [Fact]
    public async Task BackendException_IsContained_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");

        _backend.ThrowOnSignatureHelp = true;
        Assert.Null(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("failing signature help"));

        _backend.ThrowOnSignatureHelp = false;
        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));
        Assert.NotNull(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("next signature help"));
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightSignatureHelp()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");

        _backend.ArmSignatureHelp();
        Task<SignatureHelp?> inFlight = _session.Client.SignatureHelpAsync(Uri(), 1, 20);
        Assert.True(_backend.WaitUntilSignatureHelpEntered(TimeSpan.FromSeconds(5)), "backend signature help should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri(), 2, new TextDocumentContentChangeEvent { Text = "int g(int y) { return y; }\nint main() { return g(|); }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out var state) && state.Version.Value == 2, "a.cvl advanced to v2");
        _backend.ReleaseSignatureHelp();

        Assert.Null(await inFlight.WithTimeout("stale signature help"));

        ResetFramesAndSet(SingleSignature("g(int y)", 3, 5, 0));
        Assert.NotNull(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("next signature help"));
    }

    [Fact]
    public async Task CrossFileEdit_DiscardsInFlightSignatureHelp()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");
        await OpenAsync("b.cvl", 1, "int other() { return 0; }\n");

        _backend.ArmSignatureHelp();
        Task<SignatureHelp?> inFlight = _session.Client.SignatureHelpAsync(Uri(), 1, 20);
        Assert.True(_backend.WaitUntilSignatureHelpEntered(TimeSpan.FromSeconds(5)), "backend signature help should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri("b.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int other2() { return 1; }\n" });
        var coreUriB = DocumentUri.Create(_workspace.PathOf("b.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUriB, out var state) && state.Version.Value == 2, "b.cvl advanced to v2");
        _backend.ReleaseSignatureHelp();

        Assert.Null(await inFlight.WithTimeout("cross-file stale signature help"));

        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));
        Assert.NotNull(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("next signature help"));
    }

    [Fact]
    public async Task CloseReopen_DiscardsInFlightSignatureHelp()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");

        _backend.ArmSignatureHelp();
        Task<SignatureHelp?> inFlight = _session.Client.SignatureHelpAsync(Uri(), 1, 20);
        Assert.True(_backend.WaitUntilSignatureHelpEntered(TimeSpan.FromSeconds(5)), "backend signature help should be entered");

        await _session.Client.NotifyDidCloseAsync(Uri());
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => !_store.TryGet(coreUri, out _), "didClose applied");
        await OpenAsync("a.cvl", 1, "int h(int z) { return z; }\nint main() { return h(|); }\n");
        _backend.ReleaseSignatureHelp();

        Assert.Null(await inFlight.WithTimeout("old-lifetime signature help"));

        ResetFramesAndSet(SingleSignature("h(int z)", 3, 5, 0));
        Assert.NotNull(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("next signature help"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int x) { return x; }\nint main() { return f(|); }\n");

        _backend.ArmSignatureHelp();
        using var cts = new CancellationTokenSource();
        Task<SignatureHelp?> inFlight = _session.Client.SignatureHelpWithTokenAsync(Uri(), 1, 20, cts.Token);
        Assert.True(_backend.WaitUntilSignatureHelpEntered(TimeSpan.FromSeconds(5)), "backend signature help should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseSignatureHelp();

        try
        {
            SignatureHelp? result = await inFlight.WithTimeout("cancelled signature help");
            Assert.Fail($"Expected cancellation, but signature help returned {(result?.Signatures.Length ?? 0)} signature(s).");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        ResetFramesAndSet(SingleSignature("f(int x)", 2, 5, 0));
        Assert.NotNull(await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("next signature help"));
    }


    [Fact]
    public async Task ParameterDocumentation_IsMappedSeparatelyFromSignatureLabel()
    {
        // Parameter documentation is protocol metadata, not text embedded into the signature label.
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int f(int value) { return value; }\nint main() { return f(|); }\n");
        var parameter = new BackendSignatureParameter(new BackendSignatureLabelSpan(6, 9), "Input value.");
        ResetFramesAndSet(new BackendSignatureHelpResult(
            [new BackendSignatureCandidate("int f(int value)", "Function docs.", [parameter], 0)],
            0));

        SignatureHelp? result = await _session.Client.SignatureHelpAsync(Uri(), 1, 20).WithTimeout("signature help");

        Assert.NotNull(result);
        JObject frame = GetLastResponseFrame();
        Assert.Equal("int f(int value)", (string?)frame["result"]?["signatures"]?[0]?["label"]);
        Assert.Equal("Function docs.", (string?)frame["result"]?["signatures"]?[0]?["documentation"]);
        Assert.Equal("Input value.", (string?)frame["result"]?["signatures"]?[0]?["parameters"]?[0]?["documentation"]);
    }

    private JObject GetLastResponseFrame()
    {
        JObject? match = null;
        foreach (string frame in _session.Server.GetServerFrames())
        {
            if (frame.Length > 0 && JObject.Parse(frame) is { } json && json["result"] is not null)
            {
                match = json;
            }
        }

        Assert.NotNull(match);
        return match!;
    }

    private static ClientCapabilitiesPayload SignatureHelpCapabilities(bool labelOffset, bool activeParameter)
    {
        return new ClientCapabilitiesPayload
        {
            TextDocument = new TextDocumentClientCapabilitiesPayload
            {
                SignatureHelp = new SignatureHelpClientCapabilitiesPayload
                {
                    SignatureInformation = new SignatureInformationClientCapabilitiesPayload
                    {
                        ActiveParameterSupport = activeParameter,
                        ParameterInformation = new SignatureParameterInformationClientCapabilitiesPayload
                        {
                            LabelOffsetSupport = labelOffset,
                        },
                    },
                },
            },
        };
    }
}