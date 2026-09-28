namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// Backend-neutral inlay-hint classification. Every kind is produced from a resolved compiler fact;
/// the core never synthesizes a hint the backend could not derive.
/// </summary>
internal enum BackendInlayHintKind
{
    /// <summary>The inferred type of a local declared without one.</summary>
    Type,
    /// <summary>The name of the parameter an argument position binds to.</summary>
    Parameter,
    /// <summary>The final receiver mutability of an instance extension method that omitted it.</summary>
    ReceiverMutability,
    /// <summary>Field offset, size and preceding padding.</summary>
    Layout,
    /// <summary>The compiler-selected value of an enum variant.</summary>
    EnumValue,
    /// <summary>A single authoritative generic argument substitution.</summary>
    GenericArgument,
}

/// <summary>
/// One inline annotation at an absolute UTF-16 code-unit position. <see cref="Label"/> is presented
/// as-is: the core never rephrases it, and a client never needs to parse it. <see cref="RelatedSymbol"/>
/// is the declaration the label was derived from, minted from the same immutable snapshot, so a
/// client can offer navigation without the core resolving anything again.
/// </summary>
internal sealed record BackendInlayHint(
    int Position,
    BackendInlayHintKind Kind,
    string Label,
    bool PaddingLeft = false,
    bool PaddingRight = false,
    BackendSymbolHandle? RelatedSymbol = null,
    BackendFieldLayoutInfo? FieldLayout = null);

/// <summary>
/// Which categories of inlay hint the caller wants. The defaults are the recommended settings: the
/// three scannable categories on and the visually dense ones off. The backend maps this onto its own
/// query options so a disabled category costs nothing to compute.
/// </summary>
internal sealed record BackendInlayHintOptions
{
    /// <summary>The recommended default set.</summary>
    public static BackendInlayHintOptions Default { get; } = new();

    /// <summary>Inferred types of declarations whose source omits them.</summary>
    public bool Types { get; init; } = true;

    /// <summary>Parameter names at argument positions.</summary>
    public bool Parameters { get; init; } = true;

    /// <summary>Final receiver mutability of an instance extension method that omitted it.</summary>
    public bool ReceiverMutability { get; init; } = true;

    /// <summary>Field offset, size and preceding padding.</summary>
    public bool Layout { get; init; }

    /// <summary>Compiler-selected enum variant values.</summary>
    public bool EnumValues { get; init; }

    /// <summary>Inferred generic type arguments.</summary>
    public bool GenericArguments { get; init; }
}
