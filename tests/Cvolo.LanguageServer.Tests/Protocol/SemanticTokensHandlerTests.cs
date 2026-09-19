using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// End-to-end semantic-token coverage against the real Cvolo-backed store: capability gating,
/// relative encoding, kind/modifier mapping and empty results.
/// </summary>
public class SemanticTokensHandlerTests : IDisposable
{
    private static readonly string[] CanonicalTypes =
    [
        "namespace", "type", "struct", "enum", "interface", "typeParameter",
        "parameter", "variable", "property", "enumMember", "function", "method", "operator",
    ];

    private static readonly string[] CanonicalModifiers = ["declaration", "readonly", "static"];

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public SemanticTokensHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"]);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private static InitializeRequestParams Capabilities(bool semanticTokens = true, bool refresh = true, bool full = true, string[]? formats = null)
    {
        return new InitializeRequestParams
        {
            Capabilities = new ClientCapabilitiesPayload
            {
                TextDocument = new TextDocumentClientCapabilitiesPayload
                {
                    SemanticTokens = semanticTokens
                        ? new SemanticTokensClientCapabilitiesPayload
                        {
                            Requests = new SemanticTokensRequestsPayload { Full = full },
                            TokenTypes = CanonicalTypes,
                            TokenModifiers = CanonicalModifiers,
                            Formats = formats ?? ["relative"],
                            AugmentsSyntaxTokens = true,
                        }
                        : null,
                },
                Workspace = new WorkspaceClientCapabilitiesPayload
                {
                    SemanticTokens = new SemanticTokensWorkspaceClientCapabilitiesPayload { RefreshSupport = refresh },
                },
            },
        };
    }

    private async Task<InitializeResponse> InitializeAsync(InitializeRequestParams payload)
    {
        InitializeResponse response = await _session.Client.InitializeWithAsync(payload).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        return response;
    }

    private async Task OpenAsync(string text)
    {
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(() => _session.Server.Store.TryGet(coreUri, out _), "didOpen applied");
    }

    [Fact]
    public async Task Initialize_WithSemanticTokens_AdvertisesFullProviderWithLegend()
    {
        InitializeResponse response = await InitializeAsync(Capabilities());

        SemanticTokensOptions? provider = response.Capabilities.SemanticTokensProvider;
        Assert.NotNull(provider);
        Assert.True(provider!.Full);
        Assert.False(provider.Range);
        Assert.Equal(CanonicalTypes, provider.Legend!.TokenTypes);
        Assert.Equal(CanonicalModifiers, provider.Legend.TokenModifiers);
    }

    [Fact]
    public async Task Initialize_WithoutSemanticTokens_DoesNotAdvertiseProvider()
    {
        InitializeResponse response = await InitializeAsync(Capabilities(semanticTokens: false));

        Assert.Null(response.Capabilities.SemanticTokensProvider);
    }

    [Fact]
    public async Task Initialize_WithoutRelativeFormat_DoesNotAdvertiseProvider()
    {
        InitializeResponse response = await InitializeAsync(Capabilities(formats: ["token"]));

        Assert.Null(response.Capabilities.SemanticTokensProvider);
    }

    [Fact]
    public async Task Full_ReturnsRelativeEncodedTokens()
    {
        await InitializeAsync(Capabilities());
        await OpenAsync("int Twice(int value) { return value + value; }\nint main() { return Twice(1); }\n");

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        var data = result!["data"]!.Values<int>().ToArray();
        Assert.NotEmpty(data);
        Assert.Equal(0, data.Length % 5);

        // First token: line 0, char 4, length 5, type "function", modifier declaration (bit 0).
        Assert.Equal(0, data[0]);
        Assert.Equal(4, data[1]);
        Assert.Equal(5, data[2]);
        Assert.Equal(Array.IndexOf(CanonicalTypes, "function"), data[3]);
        Assert.Equal(1, data[4]);
    }

    [Fact]
    public async Task Full_EmptyDocument_ReturnsEmptyData()
    {
        await InitializeAsync(Capabilities());
        await OpenAsync("");

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        Assert.Empty(result!["data"]!.Values<int>());
    }

    [Fact]
    public async Task Full_UnopenedDocument_ReturnsNull()
    {
        await InitializeAsync(Capabilities());

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.Null(result);
    }
}
