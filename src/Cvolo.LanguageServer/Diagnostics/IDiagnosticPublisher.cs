using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Publication seam for diagnostics. The production implementation maps and
/// sends over JSON-RPC; tests substitute a recording implementation.
/// </summary>
internal interface IDiagnosticPublisher
{
    void Publish(DiagnosticRunContext context, BackendDiagnosticRun run);
    void PublishEmpty(IReadOnlyList<DiagnosticPublishTarget> targets);
    void PublishEmpty(DocumentUri uri);
}
