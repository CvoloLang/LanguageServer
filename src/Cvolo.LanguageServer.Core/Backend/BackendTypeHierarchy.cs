using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// One contract in the declared type hierarchy. <see cref="Key"/> is the compiler-owned identity the
/// client stores and echoes back so a follow-up request re-resolves the contract against the current
/// snapshot instead of a stale handle (§14, §15, §42). Only declared contract inheritance is ever
/// reported here; structural conformance belongs to implementations.
/// </summary>
internal sealed record BackendHierarchyItem(
    string Name,
    BackendSymbolKind Kind,
    DocumentUri Document,
    TextSpan Range,
    TextSpan SelectionSpan,
    string Key);

/// <summary>
/// The hierarchy answer along with the source text captured for each document a contract lives in, so
/// the handler can turn a compiler span into an LSP range without the client having the file open.
/// </summary>
internal sealed record BackendTypeHierarchyResult(
    IReadOnlyDictionary<DocumentUri, string> DocumentTexts,
    IReadOnlyList<BackendHierarchyItem> Items)
{
    public static BackendTypeHierarchyResult Empty { get; } = new(
        new Dictionary<DocumentUri, string>(),
        Array.Empty<BackendHierarchyItem>());
}
