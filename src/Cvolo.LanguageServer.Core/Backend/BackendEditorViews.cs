using Cvolo.LanguageServer.Core.Diagnostics;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// How a semantic occurrence uses the declaration it is bound to. The protocol layer maps the read
/// and write kinds to the editor's highlight kinds and reports the rest as plain text, because a
/// colour the compiler did not describe would be a guess.
/// </summary>
internal enum BackendReferenceAccessKind
{
    Read,
    Write,
    ReadWrite,
    Declaration,
}

/// <summary>One semantic occurrence of a single binding inside one document.</summary>
internal sealed record BackendDocumentHighlight(TextSpan Range, BackendReferenceAccessKind AccessKind);

/// <summary>Whether a foldable region is plain syntax, an ordinary comment, or a documentation block.</summary>
internal enum BackendFoldingRangeKind
{
    None,
    Comment,
    Documentation,
}

/// <summary>
/// One foldable region. <see cref="Range"/> covers the whole region including its braces or comment
/// markers, so a client folds the interior between the region's first and last line and keeps the
/// delimiters visible.
/// </summary>
internal sealed record BackendFoldingRange(TextSpan Range, BackendFoldingRangeKind Kind = BackendFoldingRangeKind.None);

/// <summary>
/// One level of a smart-selection chain. The outermost context holds the <see cref="Parent"/> link to
/// the next larger region; a reader starts from the innermost level and follows <see cref="Parent"/>
/// outwards.
/// </summary>
internal sealed record BackendSelectionRange(TextSpan Range, BackendSelectionRange? Parent);
