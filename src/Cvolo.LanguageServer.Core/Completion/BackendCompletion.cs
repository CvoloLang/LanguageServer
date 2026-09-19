using Cvolo.LanguageServer.Core.Diagnostics;

namespace Cvolo.LanguageServer.Core.Completion;

/// <summary>
/// Backend-neutral completion classification. The adapter maps these to the
/// protocol layer's wire kinds (§12.1); the core and the protocol layer never
/// see each other's enums.
/// </summary>
internal enum BackendCompletionKind
{
    Keyword,
    Local,
    Parameter,
    Global,
    Constant,
    Function,
    Method,
    ExtensionMethod,
    Constructor,
    Field,
    Property,
    Struct,
    Union,
    Enum,
    EnumMember,
    Interface,
    Protocol,
    Module,
    TypeAlias,
    OtherType,
    UnionVariant,
    Type,
    Namespace,
    StructField,
    EnumMetadata,
    ArrayLength,
}

/// <summary>
/// One completion candidate: the canonical identifier text to insert, the
/// label shown to the user, and its classification.
/// </summary>
internal sealed record BackendCompletionItem(string Label, string InsertText, BackendCompletionKind Kind, bool IsSnippet = false);

/// <summary>
/// The complete result of one completion query. All items share one
/// <see cref="ReplacementSpan"/> (absolute UTF-16 span over the captured
/// snapshot text); the span is never fabricated and always contains the request
/// offset (§4.5).
/// </summary>
internal sealed record BackendCompletionResult(TextSpan ReplacementSpan, IReadOnlyList<BackendCompletionItem> Items);
