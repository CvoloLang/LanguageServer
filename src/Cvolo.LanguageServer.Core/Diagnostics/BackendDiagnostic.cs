using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Diagnostics;

/// <summary>
/// Backend-neutral diagnostic severity. The executable layer maps these to the
/// wire protocol; the core never sees protocol severities.
/// </summary>
internal enum BackendDiagnosticSeverity
{
    Error,
    Warning,
    Info,
    Hint,
}

/// <summary>
/// A location inside a diagnostic: the document, its span in UTF-16 code units
/// of the captured snapshot text, and an optional message.
/// </summary>
internal sealed record BackendDiagnosticLocation(DocumentUri Document, TextSpan Span, string? Message);

/// <summary>
/// Backend-neutral diagnostic. <see cref="Code"/> and <see cref="Message"/> are
/// authoritative; a primary location's message is not.
/// </summary>
internal sealed record BackendDiagnostic(
    BackendDiagnosticSeverity Severity,
    string Code,
    string Message,
    BackendDiagnosticLocation Location,
    IReadOnlyList<BackendDiagnosticLocation> RelatedLocations);

/// <summary>
/// The complete result of one diagnostic analysis: every document text the
/// diagnostics were mapped against (primary and related) plus the diagnostics
/// themselves. All texts belong to the same captured project snapshot.
/// </summary>
internal sealed record BackendDiagnosticRun(
    IReadOnlyDictionary<DocumentUri, string> DocumentTexts,
    IReadOnlyList<BackendDiagnostic> Diagnostics);
