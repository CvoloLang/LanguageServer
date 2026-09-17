using Microsoft.VisualStudio.LanguageServer.Protocol;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Server-side wire shape for <c>textDocument/publishDiagnostics</c>. The pinned
/// protocol library's <c>Diagnostic</c> predates <c>relatedInformation</c>, so
/// the server serializes its own superset payload.
/// </summary>
internal sealed record PublishDiagnosticsPayload(Uri Uri, DiagnosticPayload[] Diagnostics);

/// <summary>
/// LSP 3.17 diagnostic payload with related information support.
/// </summary>
internal sealed record DiagnosticPayload(
    LspRange Range,
    DiagnosticSeverity Severity,
    string? Code,
    string? Source,
    string? Message,
    DiagnosticRelatedInformationPayload[]? RelatedInformation);

/// <summary>
/// LSP 3.17 <c>DiagnosticRelatedInformation</c>.
/// </summary>
internal sealed record DiagnosticRelatedInformationPayload(Location Location, string Message);
