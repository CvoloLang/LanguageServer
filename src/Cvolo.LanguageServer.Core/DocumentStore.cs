using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Editing;
using Cvolo.LanguageServer.Core.Logging;
using System.Collections.Concurrent;

namespace Cvolo.LanguageServer.Core;

/// <summary>
/// Thread-safe home for the open documents of one server session. Every open
/// document lives under an immutable, atomically-published entry so readers
/// never observe text without a matching backend snapshot. Mutations for the
/// same document are serialized on a per-uri gate; different documents are
/// independent.
/// </summary>
internal sealed class DocumentStore(ILanguageBackend backend, ICoreLogger? logger = null)
{
    private readonly ICoreLogger _logger = logger ?? new NullCoreLogger();
    private readonly ConcurrentDictionary<DocumentUri, DocumentEntry> _documents = new();
    private readonly ConcurrentDictionary<DocumentUri, object> _gates = new();

    // Serializes document/project advancement against diagnostic publication so
    // that a result can only be committed while all freshness conditions still
    // hold atomically (see TryCommitDiagnostics).
    private readonly Lock _publicationGate = new();

    public ICollection<DocumentUri> OpenUris => _documents.Keys;

    /// <summary>
    /// Opens a document with the editor's authoritative text. Returns null and
    /// leaves state unchanged when the document is already open (duplicate),
    /// cannot be hosted by the backend, or the backend rejects it.
    /// </summary>
    public DocumentState? Open(DocumentUri uri, string languageId, int version, string? text)
    {
        var gate = _gates.GetOrAdd(uri, static _ => new object());
        lock (_publicationGate)
            lock (gate)
            {
                if (_documents.ContainsKey(uri))
                {
                    _logger.Write(CoreLogLevel.Warning, $"Duplicate didOpen ignored for '{uri}'.");
                    return null;
                }

                BackendProject? project = backend.OpenProject(uri);
                if (project is null)
                {
                    _logger.Write(CoreLogLevel.Warning, $"No backend project for '{uri}'; didOpen suppressed.");
                    return null;
                }

                if (!backend.TryResolveDocument(project, uri, out BackendDocumentHandle? handle))
                {
                    _logger.Write(CoreLogLevel.Error, $"Backend could not resolve '{uri}' in its project; didOpen suppressed.");
                    return null;
                }

                var effectiveText = text ?? string.Empty;
                BackendSnapshot snapshot = backend.UpdateDocument(project, handle, effectiveText);
                var state = new DocumentState(
                    uri,
                    DocumentSessionId.Next(),
                    new DocumentVersion(version),
                    languageId,
                    effectiveText,
                    handle,
                    snapshot);
                _documents[uri] = new DocumentEntry(state, project, handle);
                _logger.Write(CoreLogLevel.Info, $"Opened '{uri}' v{version} ({languageId}).");
                return state;
            }
    }

    /// <summary>
    /// Applies a didChange payload to an open document. Returns null without
    /// touching state when the document is not open, the version is stale
    /// (not strictly greater than the current open version), or any change in
    /// the payload is invalid. The whole payload is transactional.
    /// </summary>
    public DocumentState? ApplyChanges(DocumentUri uri, int version, IReadOnlyList<DocumentChange> changes)
    {
        var gate = _gates.GetOrAdd(uri, static _ => new object());
        lock (_publicationGate)
            lock (gate)
            {
                if (!_documents.TryGetValue(uri, out DocumentEntry? current))
                {
                    _logger.Write(CoreLogLevel.Warning, $"didChange for unopened document '{uri}' ignored.");
                    return null;
                }

                DocumentState currentState = current.State;
                if (version <= currentState.Version.Value)
                {
                    _logger.Write(CoreLogLevel.Warning, $"Stale didChange v{version} for '{uri}' (current v{currentState.Version.Value}) ignored.");
                    return null;
                }

                if (!IncrementalTextEditor.TryApplyChanges(currentState.Text, changes, out string newText))
                {
                    _logger.Write(CoreLogLevel.Warning, $"Invalid range in didChange v{version} for '{uri}'; nothing applied.");
                    return null;
                }

                BackendSnapshot snapshot = backend.UpdateDocument(current.Project, current.Handle, newText);
                DocumentState next = currentState with
                {
                    Version = new DocumentVersion(version),
                    Text = newText,
                    BackendSnapshot = snapshot,
                };
                _documents[uri] = current with { State = next };
                _logger.Write(CoreLogLevel.Debug, $"Changed '{uri}' v{version}: {changes.Count} change(s).");
                return next;
            }
    }

    /// <summary>
    /// Closes a document: removes its overlay, restores the project baseline,
    /// and invalidates its session lifetime. Tolerates close-for-unopened.
    /// Returns the backend project the document belonged to, or null when it
    /// was not open.
    /// </summary>
    public BackendProject? Close(DocumentUri uri)
    {
        var gate = _gates.GetOrAdd(uri, static _ => new object());
        lock (_publicationGate)
            lock (gate)
            {
                if (!_documents.TryRemove(uri, out DocumentEntry? current))
                {
                    _logger.Write(CoreLogLevel.Warning, $"didClose for unopened document '{uri}' ignored.");
                    return null;
                }

                try
                {
                    backend.RestoreBaseline(current.Project, current.Handle);
                }
                catch (Exception ex)
                {
                    _logger.Write(CoreLogLevel.Error, $"Restoring baseline for '{uri}' failed: {ex.Message}");
                }

                _logger.Write(CoreLogLevel.Info, $"Closed '{uri}'.");
                return current.Project;
            }
    }

    public bool TryGet(DocumentUri uri, out DocumentState state)
    {
        if (_documents.TryGetValue(uri, out DocumentEntry? current))
        {
            state = current.State;
            return true;
        }

        state = null!;
        return false;
    }

    /// <summary>
    /// Atomically captures the coherent semantic request context (state, its
    /// backend snapshot, the owning project and the project's current snapshot)
    /// for an open document. Reads never observe text and a backend snapshot
    /// from different publications.
    /// </summary>
    public bool TryCapture(DocumentUri uri, out SemanticRequestContext context)
    {
        if (_documents.TryGetValue(uri, out DocumentEntry? current))
        {
            DocumentState state = current.State;
            BackendSnapshot currentProjectSnapshot = backend.CaptureCurrentSnapshot(current.Project);
            context = new SemanticRequestContext(
                state.Uri,
                state.SessionId,
                state.Version,
                state,
                current.Project,
                currentProjectSnapshot);
            return true;
        }

        context = default;
        return false;
    }

    /// <summary>Returns the backend project hosting an open document.</summary>
    public bool TryGetProject(DocumentUri uri, out BackendProject project)
    {
        if (_documents.TryGetValue(uri, out DocumentEntry? current))
        {
            project = current.Project;
            return true;
        }

        project = null!;
        return false;
    }

    /// <summary>
    /// Captures a diagnostic run for <paramref name="project"/>: the current
    /// snapshot plus every currently open document of that project. Returns
    /// false when the project has no open documents.
    /// </summary>
    public bool TryCaptureDiagnosticRun(BackendProject project, out DiagnosticRunContext context)
    {
        BackendSnapshot snapshot = backend.CaptureCurrentSnapshot(project);
        var targets = new List<DiagnosticPublishTarget>();
        foreach (var pair in _documents)
        {
            if (!ReferenceEquals(pair.Value.Project, project))
            {
                continue;
            }

            DocumentState state = pair.Value.State;
            targets.Add(new DiagnosticPublishTarget(state.Uri, state.SessionId, state.Version, pair.Value.Handle));
        }

        context = new DiagnosticRunContext(project, snapshot, targets);
        return targets.Count > 0;
    }

    /// <summary>Whether <paramref name="project"/> still has any open document.</summary>
    public bool HasOpenDocuments(BackendProject project)
    {
        foreach (var pair in _documents)
        {
            if (ReferenceEquals(pair.Value.Project, project))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Freshness check for a single publication target: the URI must still be
    /// open, in the same session lifetime and version, under the same project.
    /// </summary>
    public bool IsCurrent(DocumentUri uri, DocumentSessionId sessionId, DocumentVersion version, BackendProject project)
    {
        return _documents.TryGetValue(uri, out DocumentEntry? entry)
            && entry.State.SessionId == sessionId
            && entry.State.Version == version
            && ReferenceEquals(entry.Project, project);
    }

    /// <summary>Delegates the project-generation check to the backend.</summary>
    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        return backend.IsCurrentSnapshot(project, snapshot);
    }

    /// <summary>
    /// Atomically commits a captured diagnostic run. Under the store's
    /// publication gate the captured project snapshot is re-validated as current
    /// and the targets are filtered to those still fresh (same URI, session,
    /// version and project). <paramref name="commit"/> is invoked with the
    /// still-eligible targets while the gate is held, so document/project
    /// advancement cannot interleave between validation and publication.
    /// Returns false when the captured snapshot is no longer current, in which
    /// case the whole result must be discarded and nothing is committed.
    /// </summary>
    public bool TryCommitDiagnostics(DiagnosticRunContext context, Action<IReadOnlyList<DiagnosticPublishTarget>> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_publicationGate)
        {
            if (!backend.IsCurrentSnapshot(context.Project, context.CurrentProjectSnapshot))
            {
                return false;
            }

            var eligible = new List<DiagnosticPublishTarget>(context.Targets.Count);
            foreach (DiagnosticPublishTarget target in context.Targets)
            {
                if (IsCurrent(target.Uri, target.SessionId, target.Version, context.Project))
                {
                    eligible.Add(target);
                }
            }

            commit(eligible);
            return true;
        }
    }

    /// <summary>Computes diagnostics for a captured run from one snapshot.</summary>
    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        return backend.GetDiagnostics(snapshot, targets);
    }

    /// <summary>Ties together the published state and the backend tokens that produced it.</summary>
    private sealed record DocumentEntry(DocumentState State, BackendProject Project, BackendDocumentHandle Handle);
}