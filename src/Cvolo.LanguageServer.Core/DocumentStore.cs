using System.Collections.Concurrent;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Editing;
using Cvolo.LanguageServer.Core.Logging;

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

    public ICollection<DocumentUri> OpenUris => _documents.Keys;

    /// <summary>
    /// Opens a document with the editor's authoritative text. Returns null and
    /// leaves state unchanged when the document is already open (duplicate),
    /// cannot be hosted by the backend, or the backend rejects it.
    /// </summary>
    public DocumentState? Open(DocumentUri uri, string languageId, int version, string? text)
    {
        var gate = _gates.GetOrAdd(uri, static _ => new object());
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
    /// </summary>
    public void Close(DocumentUri uri)
    {
        var gate = _gates.GetOrAdd(uri, static _ => new object());
        lock (gate)
        {
            if (!_documents.TryRemove(uri, out DocumentEntry? current))
            {
                _logger.Write(CoreLogLevel.Warning, $"didClose for unopened document '{uri}' ignored.");
                return;
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
    /// Atomically captures the coherent request context (state plus its
    /// backend snapshot) for an open document. Reads never observe text and a
    /// backend snapshot from different publications.
    /// </summary>
    public bool TryCapture(DocumentUri uri, out RequestDocumentContext context)
    {
        if (_documents.TryGetValue(uri, out DocumentEntry? current))
        {
            DocumentState state = current.State;
            context = new RequestDocumentContext(
                state.Uri,
                state.SessionId,
                state.Version,
                state,
                state.BackendSnapshot);
            return true;
        }

        context = default;
        return false;
    }

    /// <summary>Ties together the published state and the backend tokens that produced it.</summary>
    private sealed record DocumentEntry(DocumentState State, BackendProject Project, BackendDocumentHandle Handle);
}