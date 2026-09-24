using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Logging;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Unit coverage for the session-scoped, bounded completion resolve store
/// (§27.3, §34): atomic commit, opaque token lookup, duplicate skipping,
/// bounded FIFO eviction, same-batch retention, and single/uri eviction.
/// </summary>
public class CompletionResolveStoreTests
{
    private static readonly DocumentUri A = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "resolve-a.cvl"));
    private static readonly DocumentUri B = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "resolve-b.cvl"));

    private static readonly SemanticRequestContext ContextA = Capture(A);
    private static readonly SemanticRequestContext ContextB = Capture(B);

    private static readonly BackendCompletionResolvableFields AllFields =
        BackendCompletionResolvableFields.Detail | BackendCompletionResolvableFields.Documentation;

    private static SemanticRequestContext Capture(DocumentUri uri)
    {
        var backend = new FakeBackend();
        var documentStore = new DocumentStore(backend, new RecordingCoreLogger());
        Assert.NotNull(documentStore.Open(uri, "cvolo", 1, "text"));
        Assert.True(documentStore.TryCapture(uri, out SemanticRequestContext context));
        return context;
    }

    private static StagedResolveItem Stage(string token, SemanticRequestContext context, int itemIndex = 0)
    {
        return new StagedResolveItem(token, itemIndex, new CompletionResolveEntry(context, new TestResolveHandle(), AllFields));
    }

    [Fact]
    public void Commit_AttachesEachStagedToken_HoldingContextHandleAndFields()
    {
        var store = new CompletionResolveStore();
        var handle = new TestResolveHandle();
        var entry = new CompletionResolveEntry(ContextA, handle, AllFields);

        HashSet<string> attached = store.Commit([
            new StagedResolveItem("t1", 0, entry),
            Stage("t2", ContextA, 1),
            Stage("t3", ContextB, 2),
        ]);

        Assert.Equal(new HashSet<string> { "t1", "t2", "t3" }, attached);
        Assert.True(store.TryGet("t1", out var t1));
        Assert.Equal(ContextA, t1.Context);
        Assert.Same(handle, t1.Handle);
        Assert.Equal(AllFields, t1.EffectiveResolvableFields);
        Assert.True(store.TryGet("t2", out _));
        Assert.True(store.TryGet("t3", out var t3));
        Assert.Equal(ContextB, t3.Context);
    }

    [Fact]
    public void Commit_EmptyBatch_AttachesNothing()
    {
        var store = new CompletionResolveStore();

        Assert.Empty(store.Commit([]));
    }

    [Fact]
    public void Commit_DuplicateToken_IsSkipped_AndDoesNotRebounceToBackOfQueue()
    {
        var store = new CompletionResolveStore(2);
        Assert.Equal(new HashSet<string> { "t1", "t2" }, store.Commit([Stage("t1", ContextA), Stage("t2", ContextA)]));

        var duplicated = store.Commit([Stage("t1", ContextA)]);
        Assert.DoesNotContain("t1", duplicated);

        // t1 stayed at the oldest position: committing t3 at capacity evicts it.
        Assert.Equal(new HashSet<string> { "t3" }, store.Commit([Stage("t3", ContextA)]));
        Assert.False(store.TryGet("t1", out _));
        Assert.True(store.TryGet("t2", out _));
        Assert.True(store.TryGet("t3", out _));
    }

    [Fact]
    public void Commit_AtCapacity_EvictsOldestToken()
    {
        var store = new CompletionResolveStore(2);
        Assert.Equal(new HashSet<string> { "t1", "t2" }, store.Commit([Stage("t1", ContextA), Stage("t2", ContextA)]));

        Assert.Equal(new HashSet<string> { "t3" }, store.Commit([Stage("t3", ContextA)]));

        Assert.False(store.TryGet("t1", out _));
        Assert.True(store.TryGet("t2", out _));
        Assert.True(store.TryGet("t3", out _));
    }

    [Fact]
    public void Commit_BatchLargerThanCapacity_KeepsOnlyFirstBatchEntries()
    {
        var store = new CompletionResolveStore(2);

        HashSet<string> attached = store.Commit([Stage("t1", ContextA), Stage("t2", ContextA), Stage("t3", ContextA), Stage("t4", ContextA)]);

        Assert.Equal(new HashSet<string> { "t1", "t2" }, attached);
        Assert.True(store.TryGet("t1", out _));
        Assert.True(store.TryGet("t2", out _));
        Assert.False(store.TryGet("t3", out _));
        Assert.False(store.TryGet("t4", out _));
    }

    [Fact]
    public void Commit_OldestBatchMember_IsNeverEvictedByItsOwnBatch()
    {
        var store = new CompletionResolveStore(2);
        Assert.Equal(new HashSet<string> { "u1" }, store.Commit([Stage("u1", ContextA)]));

        HashSet<string> attached = store.Commit([Stage("t1", ContextA), Stage("t2", ContextA), Stage("t3", ContextA)]);

        Assert.Equal(new HashSet<string> { "t1", "t2" }, attached);
        Assert.False(store.TryGet("u1", out _));
        Assert.True(store.TryGet("t1", out _));
        Assert.True(store.TryGet("t2", out _));
        Assert.False(store.TryGet("t3", out _));
    }

    [Fact]
    public void TryGet_UnknownToken_ReturnsFalse()
    {
        var store = new CompletionResolveStore();

        Assert.False(store.TryGet("never-committed", out _));
    }

    [Fact]
    public void Evict_RemovesToken_AndKeepsFifoAccounting_SoLaterCommitsAttach()
    {
        var store = new CompletionResolveStore(2);
        Assert.Equal(new HashSet<string> { "t1", "t2" }, store.Commit([Stage("t1", ContextA), Stage("t2", ContextA)]));

        store.Evict("t1");
        Assert.False(store.TryGet("t1", out _));
        Assert.True(store.TryGet("t2", out _));

        // One live entry remains, so t3 attaches without any eviction.
        Assert.Equal(new HashSet<string> { "t3" }, store.Commit([Stage("t3", ContextA)]));
        Assert.True(store.TryGet("t2", out _));
        Assert.True(store.TryGet("t3", out _));
    }

    [Fact]
    public void Evict_UnknownToken_IsHarmless()
    {
        var store = new CompletionResolveStore();

        store.Evict("missing");
        store.EvictForUri(A);

        Assert.Equal(new HashSet<string> { "t1" }, store.Commit([Stage("t1", ContextA)]));
        Assert.True(store.TryGet("t1", out _));
    }

    [Fact]
    public void EvictForUri_RemovesOnlyEntriesOfThatDocument()
    {
        var store = new CompletionResolveStore();
        Assert.Equal(new HashSet<string> { "a1", "a2", "b1" }, store.Commit([Stage("a1", ContextA), Stage("a2", ContextA), Stage("b1", ContextB)]));

        store.EvictForUri(A);

        Assert.False(store.TryGet("a1", out _));
        Assert.False(store.TryGet("a2", out _));
        Assert.True(store.TryGet("b1", out _));

        // The other document keeps attaching new entries.
        Assert.Equal(new HashSet<string> { "b2" }, store.Commit([Stage("b2", ContextB)]));
        Assert.True(store.TryGet("b2", out _));
    }

    [Fact]
    public void EvictForUri_PrunesInsertionOrder_SoCapacityStillEvictsOldestLive()
    {
        var store = new CompletionResolveStore(2);
        Assert.Equal(new HashSet<string> { "a1", "b1" }, store.Commit([Stage("a1", ContextA), Stage("b1", ContextB)]));

        store.EvictForUri(A);

        // No phantom a-entry holds the oldest slot: b2 attaches next.
        Assert.Equal(new HashSet<string> { "b2" }, store.Commit([Stage("b2", ContextB)]));
        Assert.Equal(new HashSet<string> { "a3" }, store.Commit([Stage("a3", ContextA)]));

        Assert.False(store.TryGet("b1", out _));
        Assert.True(store.TryGet("b2", out _));
        Assert.True(store.TryGet("a3", out _));
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletionResolveStore(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompletionResolveStore(-5));
    }

    private sealed class TestResolveHandle : BackendCompletionResolveHandle
    {
    }

    private sealed class FakeBackend : ILanguageBackend
    {
        private readonly Dictionary<DocumentUri, FakeProject> _projects = new();

        public BackendProject? OpenProject(DocumentUri document)
        {
            if (!_projects.TryGetValue(document, out var project))
            {
                project = new FakeProject { CurrentSnapshot = new FakeSnapshot(string.Empty, 0) };
                _projects[document] = project;
            }

            return project;
        }

        public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
        {
            handle = new FakeHandle();
            return true;
        }

        public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
        {
            var fake = (FakeProject)project;
            fake.CurrentSnapshot = new FakeSnapshot(text, ++fake.Generation);
            return fake.CurrentSnapshot;
        }

        public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
        {
            var fake = (FakeProject)project;
            fake.CurrentSnapshot = new FakeSnapshot(string.Empty, ++fake.Generation);
            return fake.CurrentSnapshot;
        }

        public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
        {
            var fake = (FakeProject)project;
            return fake.CurrentSnapshot ?? new FakeSnapshot(string.Empty, fake.Generation);
        }

        public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
        {
            var fake = (FakeProject)project;
            return snapshot is FakeSnapshot current && current.Generation == fake.Generation;
        }

        public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
        {
            return new BackendDiagnosticRun(new Dictionary<DocumentUri, string>(), []);
        }

        public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        {
            return new BackendCompletionResult(new TextSpan(position, 0), []);
        }

        public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        {
            return null;
        }

        public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
        {
            return new BackendDefinitionResult(new Dictionary<DocumentUri, string>(), []);
        }

        public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document)
        {
            return [];
        }
    }

    private sealed class FakeProject : BackendProject
    {
        public long Generation { get; set; }

        public FakeSnapshot? CurrentSnapshot { get; set; }
    }

    private sealed class FakeSnapshot(string text, long generation) : BackendSnapshot
    {
        public long Generation { get; } = generation;
    }

    private sealed class FakeHandle : BackendDocumentHandle
    {
    }
}