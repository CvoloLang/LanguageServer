using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Deterministic fake backend for completion concurrency tests. Completion can be
/// armed to block until released, so stale/cancel races are reproducible without
/// sleeps. Projects are keyed by directory so documents in one directory share a
/// project generation, matching the real adapter's project semantics.
/// </summary>
internal sealed class BlockingBackend : ILanguageBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FakeProject> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _block = new(initialState: true);
    private readonly ManualResetEventSlim _entered = new(initialState: false);

    /// <summary>When true, a completion call throws an ordinary exception.</summary>
    public bool ThrowOnCompletion { get; set; }

    public int CompletionCalls;

    public IReadOnlyList<BackendCompletionItem> CannedItems { get; set; } =
        [new BackendCompletionItem("canned", "canned", BackendCompletionKind.Local, Detail: null, InsertionPlan: null, ResolveHandle: null, ResolvableFields: BackendCompletionResolvableFields.None)];

    /// <summary>When set, replaces the canned result; used to feed malformed spans.</summary>
    public Func<int, BackendCompletionResult>? CompletionResultFactory { get; set; }

    /// <summary>Arms the next completion call to block until <see cref="Release"/>.</summary>
    public void Arm()
    {
        _entered.Reset();
        _block.Reset();
    }

    /// <summary>Lets a blocked completion call finish.</summary>
    public void Release()
    {
        _block.Set();
    }

    /// <summary>Waits until a completion call has entered the backend.</summary>
    public bool WaitUntilEntered(TimeSpan timeout)
    {
        return _entered.Wait(timeout);
    }

    public BackendProject? OpenProject(DocumentUri document)
    {
        lock (_gate)
        {
            string directory = Path.GetDirectoryName(document.LocalPath) ?? document.LocalPath;
            if (!_projects.TryGetValue(directory, out FakeProject? project))
            {
                project = new FakeProject();
                _projects[directory] = project;
            }

            return project;
        }
    }

    public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
    {
        handle = new FakeHandle(document);
        return true;
    }

    public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
    {
        lock (_gate)
        {
            var fake = (FakeProject)project;
            fake.Generation++;
            fake.Current = new FakeSnapshot(fake.Generation);
            return fake.Current;
        }
    }

    public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
    {
        return UpdateDocument(project, handle, string.Empty);
    }

    public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
    {
        lock (_gate)
        {
            return ((FakeProject)project).Current;
        }
    }

    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        lock (_gate)
        {
            return ((FakeSnapshot)snapshot).Generation == ((FakeProject)project).Generation;
        }
    }

    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        return new BackendDiagnosticRun(new Dictionary<DocumentUri, string>(), []);
    }

    public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref CompletionCalls);
        _entered.Set();
        _block.Wait();

        if (ThrowOnCompletion)
        {
            throw new InvalidOperationException("simulated completion failure");
        }

        return CompletionResultFactory is { } factory
            ? factory(position)
            : new BackendCompletionResult(new TextSpan(position, 0), CannedItems);
    }

    public BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref SignatureHelpCalls);
        _signatureHelpEntered.Set();
        _signatureHelpBlock.Wait();

        if (ThrowOnSignatureHelp)
        {
            throw new InvalidOperationException("simulated signature help failure");
        }

        return SignatureHelpResultFactory is { } factory ? factory() : CannedSignatureHelp;
    }

    public BackendCompletionResolvedInfo? ResolveCompletion(BackendSnapshot snapshot, BackendCompletionResolveHandle handle)
    {
        Interlocked.Increment(ref ResolveCalls);
        _resolveEntered.Set();
        _resolveBlock.Wait();

        if (ThrowOnResolve)
        {
            throw new InvalidOperationException("simulated resolve failure");
        }

        return CannedResolvedInfo;
    }

    /// <summary>Mints a distinct resolve handle for canned completion items.</summary>
    public BackendCompletionResolveHandle CreateResolveHandle()
    {
        return new FakeResolveHandle(Interlocked.Increment(ref _resolveHandleSeed));
    }

    private readonly ManualResetEventSlim _signatureHelpBlock = new(initialState: true);
    private readonly ManualResetEventSlim _signatureHelpEntered = new(initialState: false);

    public int SignatureHelpCalls;

    /// <summary>The signature help result returned by <see cref="GetSignatureHelp"/>.</summary>
    public BackendSignatureHelpResult? CannedSignatureHelp { get; set; }

    /// <summary>When set, replaces the canned signature help result.</summary>
    public Func<BackendSignatureHelpResult?>? SignatureHelpResultFactory { get; set; }

    /// <summary>When true, a signature help call throws an ordinary exception.</summary>
    public bool ThrowOnSignatureHelp { get; set; }

    /// <summary>Arms the next signature help call to block until <see cref="ReleaseSignatureHelp"/>.</summary>
    public void ArmSignatureHelp()
    {
        _signatureHelpEntered.Reset();
        _signatureHelpBlock.Reset();
    }

    /// <summary>Lets a blocked signature help call finish.</summary>
    public void ReleaseSignatureHelp()
    {
        _signatureHelpBlock.Set();
    }

    /// <summary>Waits until a signature help call has entered the backend.</summary>
    public bool WaitUntilSignatureHelpEntered(TimeSpan timeout)
    {
        return _signatureHelpEntered.Wait(timeout);
    }

    private int _resolveHandleSeed;
    private readonly ManualResetEventSlim _resolveBlock = new(initialState: true);
    private readonly ManualResetEventSlim _resolveEntered = new(initialState: false);

    public int ResolveCalls;

    /// <summary>The resolved fields returned by <see cref="ResolveCompletion"/>.</summary>
    public BackendCompletionResolvedInfo? CannedResolvedInfo { get; set; }

    /// <summary>When true, a resolve call throws an ordinary exception.</summary>
    public bool ThrowOnResolve { get; set; }

    /// <summary>Arms the next resolve call to block until <see cref="ReleaseResolve"/>.</summary>
    public void ArmResolve()
    {
        _resolveEntered.Reset();
        _resolveBlock.Reset();
    }

    /// <summary>Lets a blocked resolve call finish.</summary>
    public void ReleaseResolve()
    {
        _resolveBlock.Set();
    }

    /// <summary>Waits until a resolve call has entered the backend.</summary>
    public bool WaitUntilResolveEntered(TimeSpan timeout)
    {
        return _resolveEntered.Wait(timeout);
    }

    private readonly ManualResetEventSlim _navigationBlock = new(initialState: true);
    private readonly ManualResetEventSlim _navigationEntered = new(initialState: false);

    /// <summary>When true, a navigation call throws an ordinary exception.</summary>
    public bool ThrowOnNavigation { get; set; }

    /// <summary>The symbol returned by <see cref="GetSymbolAtPosition"/> (null means "no symbol").</summary>
    public BackendSymbolInfo? CannedSymbol { get; set; }

    /// <summary>The definitions returned by <see cref="GetDefinitions"/>.</summary>
    public BackendDefinitionResult CannedDefinitions { get; set; } =
        new(new Dictionary<DocumentUri, string>(), []);

    /// <summary>The outline returned by <see cref="GetDocumentSymbols"/>.</summary>
    public IReadOnlyList<BackendDocumentSymbol> CannedSymbols { get; set; } = [];

    /// <summary>Arms the next navigation call to block until <see cref="ReleaseNavigation"/>.</summary>
    public void ArmNavigation()
    {
        _navigationEntered.Reset();
        _navigationBlock.Reset();
    }

    /// <summary>Lets a blocked navigation call finish.</summary>
    public void ReleaseNavigation()
    {
        _navigationBlock.Set();
    }

    /// <summary>Waits until a navigation call has entered the backend.</summary>
    public bool WaitUntilNavigationEntered(TimeSpan timeout)
    {
        return _navigationEntered.Wait(timeout);
    }

    public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        EnterNavigation();
        return CannedSymbol;
    }

    public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        EnterNavigation();
        return CannedDefinitions;
    }

    public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        EnterNavigation();
        return CannedSymbols;
    }

    public BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        EnterNavigation();
        return CannedSemanticTokens;
    }

    /// <summary>The semantic-token result returned by <see cref="GetSemanticTokens"/>.</summary>
    public BackendSemanticTokenResult CannedSemanticTokens { get; set; } = new([]);

    private void EnterNavigation()
    {
        _navigationEntered.Set();
        _navigationBlock.Wait();

        if (ThrowOnNavigation)
        {
            throw new InvalidOperationException("simulated navigation failure");
        }
    }

    private sealed class FakeProject : BackendProject
    {
        public long Generation;

        public FakeSnapshot Current = new(0);
    }

    private sealed class FakeSnapshot(long generation) : BackendSnapshot
    {
        public long Generation { get; } = generation;
    }

    private sealed class FakeHandle(DocumentUri uri) : BackendDocumentHandle
    {
        public DocumentUri Uri { get; } = uri;
    }

    private sealed class FakeResolveHandle(int seed) : BackendCompletionResolveHandle
    {
        public int Seed { get; } = seed;
    }
}
