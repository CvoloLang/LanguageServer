namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// Offset and length of one parameter's display text inside its signature's own label,
/// as UTF-16 code units over that label (display span, never a source span). The protocol
/// layer maps it to either substring extraction or an LSP [start, end] label depending on
/// the client's labelOffsetSupport capability (§11, §15).
/// </summary>
internal readonly record struct BackendSignatureLabelSpan(int Start, int Length);

/// <summary>One parameter displayed by protocol-neutral signature help.</summary>
internal sealed record BackendSignatureParameter(BackendSignatureLabelSpan LabelSpan, string? Documentation = null);

/// <summary>One callable signature produced by the language backend.</summary>
internal sealed record BackendSignatureCandidate(
    string Label,
    string? Documentation,
    IReadOnlyList<BackendSignatureParameter> Parameters,
    int? ActiveParameter);

/// <summary>
/// Signature help resolved from one immutable backend snapshot. The active signature is
/// compiler-resolved; the active parameter is carried per-signature, so it can be omitted
/// from candidates where the compiler chose none (null means "no active parameter").
/// </summary>
internal sealed record BackendSignatureHelpResult(
    IReadOnlyList<BackendSignatureCandidate> Signatures,
    int ActiveSignature);