using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// The protocol layer must reject malformed backend completion spans instead of
/// turning them into unsafe edits. A fake backend supplies the malformed data so
/// production Tooling semantics are untouched.
/// </summary>
public class CompletionMalformedResultTests : IDisposable
{
    private const string DocumentText = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public CompletionMalformedResultTests()
    {
        _workspace = TestWorkspace.CreateProject(["a.cvl"]);
        _backend = new BlockingBackend();
        _store = new DocumentStore(_backend, new RecordingCoreLogger());
        _session = ProtocolSession.Start(storeFactory: () => _store);
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private async Task OpenAsync()
    {
        await _session.Client.InitializeAsync().WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("a.cvl"), "cvolo", 1, DocumentText).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private Task<CompletionList?> CompleteAsync(int line, int character)
    {
        return _session.Client.CompletionAsync(_workspace.DocumentUri("a.cvl"), line, character);
    }

    [Theory]
    [InlineData(0, -1, 0)] // start < 0
    [InlineData(0, 0, -1)] // length < 0
    [InlineData(0, 0, 1000)] // end beyond text
    [InlineData(0, 5, 0)] // starts after cursor
    [InlineData(5, 0, 2)] // ends before cursor
    public async Task MalformedReplacementSpan_IsDiscardedAsNull(int cursorChar, int start, int length)
    {
        await OpenAsync();
        _backend.CompletionResultFactory = _ => new BackendCompletionResult(
            new TextSpan(start, length),
            [new BackendCompletionItem("x", "x", BackendCompletionKind.Local, Detail: null, InsertionPlan: null, ResolveHandle: null, ResolvableFields: BackendCompletionResolvableFields.None)]);

        CompletionList? result = await CompleteAsync(0, cursorChar);

        Assert.Null(result);
    }

    [Fact]
    public async Task MultiLineReplacementSpan_IsDiscardedAsNull()
    {
        await OpenAsync();
        _backend.CompletionResultFactory = _ => new BackendCompletionResult(
            new TextSpan(0, DocumentText.Length),
            [new BackendCompletionItem("x", "x", BackendCompletionKind.Local, Detail: null, InsertionPlan: null, ResolveHandle: null, ResolvableFields: BackendCompletionResolvableFields.None)]);

        CompletionList? result = await CompleteAsync(0, 0);

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidZeroLengthSpanAtCursor_IsAccepted()
    {
        await OpenAsync();
        _backend.CompletionResultFactory = position => new BackendCompletionResult(
            new TextSpan(position, 0),
            [new BackendCompletionItem("x", "x", BackendCompletionKind.Local, Detail: null, InsertionPlan: null, ResolveHandle: null, ResolvableFields: BackendCompletionResolvableFields.None)]);

        CompletionList? result = await CompleteAsync(0, 0);

        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.Equal("x", result.Items[0].Label);
    }
}
