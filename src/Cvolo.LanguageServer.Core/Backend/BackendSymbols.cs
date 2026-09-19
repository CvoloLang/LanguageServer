using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// Opaque handle to a semantic symbol resolved by a backend. Never inspected by
/// the core; only passed back to the backend that minted it, together with the
/// snapshot it was resolved from.
/// </summary>
internal abstract class BackendSymbolHandle
{
}

/// <summary>
/// Backend-neutral semantic symbol classification. The adapter maps these to the
/// protocol layer's wire kinds; the core and the protocol layer never see each
/// other's enums.
/// </summary>
internal enum BackendSymbolKind
{
    Namespace,
    Module,
    Struct,
    Union,
    Enum,
    EnumMember,
    Interface,
    Protocol,
    TypeAlias,
    TypeParameter,
    Function,
    Method,
    ExtensionMethod,
    Constructor,
    Destructor,
    Field,
    Property,
    Parameter,
    Local,
    Global,
    Constant,
    Operator,
    OtherType,
    Unknown,
}

/// <summary>
/// The symbol resolved at a source position: its opaque handle, the occurrence
/// span, its name and classification, and a backend-owned display string.
/// </summary>
internal sealed record BackendSymbolInfo(
    BackendSymbolHandle Symbol,
    TextSpan SubjectSpan,
    string Name,
    BackendSymbolKind Kind,
    string DisplayText,
    string? Documentation = null);

/// <summary>
/// One source declaration of a symbol, expressed in the backend's own spans.
/// </summary>
internal sealed record BackendDefinitionTarget(
    DocumentUri Document,
    TextSpan Range,
    TextSpan SelectionSpan);

/// <summary>
/// The definition locations of a symbol, together with the exact text of every
/// referenced document from the same captured snapshot. <see cref="DocumentTexts"/>
/// is required because definition targets may be closed editor documents that do
/// not exist in the document store.
/// </summary>
internal sealed record BackendDefinitionResult(
    IReadOnlyDictionary<DocumentUri, string> DocumentTexts,
    IReadOnlyList<BackendDefinitionTarget> Targets);

/// <summary>
/// One node of a document's declaration outline, in deterministic source order.
/// </summary>
internal sealed record BackendDocumentSymbol(
    string Name,
    string? Detail,
    BackendSymbolKind Kind,
    TextSpan Range,
    TextSpan SelectionSpan,
    IReadOnlyList<BackendDocumentSymbol> Children);
