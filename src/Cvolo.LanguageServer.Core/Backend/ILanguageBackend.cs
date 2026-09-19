using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// Opaque handle to a language backend's open project session. Never
/// inspected by the core; only passed back to the backend that minted it.
/// </summary>
internal abstract class BackendProject
{
}

/// <summary>
/// Opaque immutable view of the backend's project state. Snapshots are
/// advanced with <see cref="ILanguageBackend.UpdateDocument"/> and never
/// mutated in place.
/// </summary>
internal abstract class BackendSnapshot
{
}

/// <summary>
/// Opaque handle to one document within a <see cref="BackendProject"/>.
/// </summary>
internal abstract class BackendDocumentHandle
{
}

/// <summary>
/// Boundary the core calls to keep compiler-side state coherent with the
/// editor text it manages. Implemented by a language-specific adapter
/// (CvoloLanguageServer.Cvolo) that owns project discovery and the
/// per-project serialization of snapshot advancement.
/// </summary>
internal interface ILanguageBackend
{
    /// <summary>
    /// Opens, or reuses, the project hosting <paramref name="document"/>, or
    /// returns null when the document cannot be hosted (no project found,
    /// ambiguous project, or resolution failure). Discovery failures must be
    /// non-fatal: they only suppress state publication.
    /// </summary>
    BackendProject? OpenProject(DocumentUri document);
    /// <summary>
    /// Resolves <paramref name="document"/> inside the project.
    /// </summary>
    bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle);
    /// <summary>
    /// Advances <paramref name="project"/> so <paramref name="document"/>'s
    /// text is <paramref name="text"/> and returns the new snapshot. The
    /// returned snapshot must be coherent with the new text.
    /// </summary>
    BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text);
    /// <summary>
    /// Removes <paramref name="document"/>'s overlay, restoring the project
    /// baseline text, and returns the resulting snapshot.
    /// </summary>
    BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle);
    /// <summary>
    /// Captures the project's current immutable snapshot together with its
    /// adapter-owned generation. Used for semantic work that must be pinned to
    /// one project state.
    /// </summary>
    BackendSnapshot CaptureCurrentSnapshot(BackendProject project);
    /// <summary>
    /// Reports whether <paramref name="snapshot"/> is still the project's
    /// current snapshot. Implementations compare an adapter-owned generation,
    /// not object identity.
    /// </summary>
    bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot);
    /// <summary>
    /// Computes diagnostics for <paramref name="targets"/> from
    /// <paramref name="snapshot"/>. Every call must use that same snapshot so
    /// one project analysis backs the whole result.
    /// </summary>
    BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets);

    /// <summary>
    /// Computes backend-neutral completion candidates for
    /// <paramref name="document"/> at <paramref name="position"/> (an absolute
    /// UTF-16 code-unit offset over the captured snapshot text) from the one
    /// coherent <paramref name="snapshot"/>. The returned replacement span must
    /// be a valid span over that same snapshot text that contains
    /// <paramref name="position"/> (§4.5, §7, §17). Implementations must never
    /// degrade to an unfiltered global list when triggered by a bare '.', never
    /// fabricate a replacement span, and never produce stale or cross-generation
    /// results (§4.4, §16.6, §30).
    /// </summary>
    BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position);

    /// <summary>
    /// Resolves the backend-neutral symbol at <paramref name="position"/> (an
    /// absolute UTF-16 code-unit offset over the captured snapshot text) from the
    /// one coherent <paramref name="snapshot"/>. Returns null when no trustworthy
    /// symbol is bound there. The returned handle is scoped to
    /// <paramref name="snapshot"/> and must not be used with another snapshot
    /// (§12, §15).
    /// </summary>
    BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position);

    /// <summary>
    /// Returns the source declarations of <paramref name="symbol"/> from the same
    /// immutable <paramref name="snapshot"/> the handle was resolved from. A
    /// handle from a foreign snapshot yields an empty result, never an unrelated
    /// symbol. Target document texts are included so closed documents can be
    /// mapped (§17, §18).
    /// </summary>
    BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol);

    /// <summary>
    /// Returns the declaration outline of <paramref name="document"/> from the
    /// captured <paramref name="snapshot"/> in deterministic source order (§19).
    /// </summary>
    IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document);
}