namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>One parameter displayed by protocol-neutral signature help.</summary>
internal sealed record BackendSignatureHelpParameter(string Label);

/// <summary>One callable signature produced by the language backend.</summary>
internal sealed record BackendSignatureHelpItem(
    string Label,
    IReadOnlyList<BackendSignatureHelpParameter> Parameters,
    string? Documentation = null);

/// <summary>Signature help resolved from one immutable backend snapshot.</summary>
internal sealed record BackendSignatureHelpResult(
    IReadOnlyList<BackendSignatureHelpItem> Signatures,
    int ActiveSignature,
    int ActiveParameter);
