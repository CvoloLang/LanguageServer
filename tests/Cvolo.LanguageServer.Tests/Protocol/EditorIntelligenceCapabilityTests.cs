using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;
using ServerCapabilities = Cvolo.LanguageServer.Protocol.ServerCapabilities;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// The editor-intelligence capabilities are advertised unconditionally, and advertising them does not
/// cost any capability the session had before (§4, §82).
/// </summary>
public class EditorIntelligenceCapabilityTests : IDisposable
{
    private static readonly string[] CanonicalTypes = ["namespace", "type", "function"];
    private static readonly string[] CanonicalModifiers = ["declaration", "readonly", "static"];

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public EditorIntelligenceCapabilityTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"]);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Task<InitializeResponse> InitializeAsync(InitializeRequestParams payload)
    {
        return _session.Client.InitializeWithAsync(payload).WithTimeout("initialize");
    }

    private static InitializeRequestParams FullClientCapabilities() => new()
    {
        Capabilities = new ClientCapabilitiesPayload
        {
            TextDocument = new TextDocumentClientCapabilitiesPayload
            {
                Hover = new HoverClientCapabilitiesPayload { ContentFormat = ["markdown", "plaintext"] },
                DocumentSymbol = new DocumentSymbolClientCapabilitiesPayload { HierarchicalDocumentSymbolSupport = true },
                CodeAction = new CodeActionClientCapabilitiesPayload
                {
                    CodeActionLiteralSupport = new CodeActionLiteralSupportPayload
                    {
                        CodeActionKind = new CodeActionKindPayload { ValueSet = ["quickfix"] },
                    },
                    DataSupport = true,
                    ResolveSupport = new CodeActionResolveSupportPayload { Properties = ["edit"] },
                },
                SemanticTokens = new SemanticTokensClientCapabilitiesPayload
                {
                    Requests = new SemanticTokensRequestsPayload { Full = true },
                    TokenTypes = CanonicalTypes,
                    TokenModifiers = CanonicalModifiers,
                    Formats = ["relative"],
                    AugmentsSyntaxTokens = true,
                },
                Rename = new RenameClientCapabilitiesPayload { PrepareSupport = true },
            },
            Workspace = new WorkspaceClientCapabilitiesPayload
            {
                WorkspaceEdit = new WorkspaceEditClientCapabilitiesPayload { DocumentChanges = true },
                SemanticTokens = new SemanticTokensWorkspaceClientCapabilitiesPayload { RefreshSupport = true },
                CodeLens = new CodeLensWorkspaceClientCapabilitiesPayload { RefreshSupport = true },
                InlayHint = new InlayHintWorkspaceClientCapabilitiesPayload { RefreshSupport = true },
            },
        },
    };

    [Fact]
    public async Task Initialize_AdvertisesEveryEditorIntelligenceCapability()
    {
        InitializeResponse response = await InitializeAsync(FullClientCapabilities());

        ServerCapabilities capabilities = response.Capabilities;
        Assert.NotNull(capabilities.CodeLensProvider);
        Assert.False(capabilities.CodeLensProvider!.ResolveProvider);
        Assert.True(capabilities.InlayHintProvider);
        Assert.True(capabilities.DocumentHighlightProvider);
        Assert.True(capabilities.FoldingRangeProvider);
        Assert.True(capabilities.SelectionRangeProvider);
    }

    [Fact]
    public async Task Initialize_KeepsEveryPreviousCapability()
    {
        InitializeResponse response = await InitializeAsync(FullClientCapabilities());

        ServerCapabilities capabilities = response.Capabilities;
        Assert.NotNull(capabilities.TextDocumentSync);
        Assert.NotNull(capabilities.CompletionProvider);
        Assert.NotNull(capabilities.SignatureHelpProvider);
        Assert.True(capabilities.HoverProvider);
        Assert.True(capabilities.DefinitionProvider);
        Assert.True(capabilities.ReferencesProvider);
        Assert.NotNull(capabilities.RenameProvider);
        Assert.True(capabilities.DocumentSymbolProvider);
        Assert.NotNull(capabilities.SemanticTokensProvider);
        Assert.NotNull(capabilities.CodeActionProvider);
    }

    [Fact]
    public async Task Initialize_WithoutEditorIntelligenceCapabilities_StillAdvertisesTheProviders()
    {
        InitializeResponse response = await InitializeAsync(new InitializeRequestParams());

        ServerCapabilities capabilities = response.Capabilities;
        Assert.NotNull(capabilities.CodeLensProvider);
        Assert.True(capabilities.InlayHintProvider);
        Assert.True(capabilities.DocumentHighlightProvider);
        Assert.True(capabilities.FoldingRangeProvider);
        Assert.True(capabilities.SelectionRangeProvider);
    }

    [Fact]
    public async Task Initialize_AdvertisesNoExecuteCommandProvider()
    {
        // The layout detail is a custom request, and the reference lens is a client-side command, so
        // the server has no server-executed command surface to expose (§57, Inc 8).
        InitializeResponse response = await InitializeAsync(FullClientCapabilities());

        IReadOnlyList<string> names = response.Capabilities.GetType()
            .GetProperties()
            .Select(property => property.Name)
            .ToList();
        Assert.DoesNotContain("ExecuteCommandProvider", names);
    }
}
