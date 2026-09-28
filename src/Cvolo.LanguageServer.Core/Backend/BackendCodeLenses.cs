using Cvolo.LanguageServer.Core.Diagnostics;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// Backend-neutral CodeLens classification. The protocol layer maps these to the
/// editor's lens rendering; the core and the protocol layer never see each
/// other's enums.
/// </summary>
internal enum BackendCodeLensKind
{
    /// <summary>How many places reference the declaration the lens is anchored to.</summary>
    References,
    /// <summary>Size, alignment and padding of the declared type for the active target.</summary>
    Layout,
    /// <summary>Resolved native linkage of the declaration, never a guessed ABI.</summary>
    NativeInterop,
}

/// <summary>One argument of a CodeLens command. A command carries only what a client needs to re-run the query.</summary>
internal abstract record BackendCodeLensArgument;

/// <summary>A UTF-16 code-unit position inside the same document the lens belongs to.</summary>
internal sealed record BackendCodeLensPositionArgument(int Position) : BackendCodeLensArgument;

/// <summary>The client command a CodeLens runs when it is activated, or null for a lens that only displays text.</summary>
internal sealed record BackendCodeLensCommand(string Name, IReadOnlyList<BackendCodeLensArgument> Arguments);

/// <summary>
/// Where one stored field sits inside its containing type, as the compiler's layout service computed
/// it. The lens is anchored to the field's own declaration and the numbers are copied verbatim: the
/// protocol layer formats them for the reader but never re-derives an offset, a size or a padding.
/// </summary>
internal sealed record BackendFieldLayoutInfo(
    string ContainingTypeDisplay,
    string FieldName,
    long Offset,
    long Size,
    long Alignment,
    long PaddingBefore);

/// <summary>
/// One editor-anchored annotation. <see cref="Range"/> is the line the lens renders above and
/// <see cref="Title"/> is already-presented text: the backend never rephrases it client-side.
/// </summary>
internal sealed record BackendCodeLensInfo(
    TextSpan Range,
    BackendCodeLensKind Kind,
    string Title,
    BackendCodeLensCommand? Command = null,
    BackendFieldLayoutInfo? FieldLayout = null);

/// <summary>
/// Which categories of CodeLens the caller wants. The defaults are the recommended settings: the
/// declaration-level lenses on and the per-member lenses off. The core and the protocol layer own
/// this shape; the backend maps it onto its own query options rather than the core filtering results.
/// </summary>
internal sealed record BackendCodeLensOptions
{
    /// <summary>The recommended default set.</summary>
    public static BackendCodeLensOptions Default { get; } = new();

    /// <summary>Emit reference-count lenses, including a computed zero.</summary>
    public bool References { get; init; } = true;

    /// <summary>Emit compact object-layout lenses for concrete types.</summary>
    public bool Layout { get; init; } = true;

    /// <summary>Emit reference-count lenses for fields and enum variants too.</summary>
    public bool Members { get; init; }

    /// <summary>
    /// Emit a per-field layout lens (offset, size, alignment and the padding before the field) next
    /// to every field whose containing type has an authoritative layout.
    /// </summary>
    public bool FieldLayout { get; init; }

    /// <summary>Emit lenses for declarations with resolved native linkage.</summary>
    public bool NativeInterop { get; init; } = true;
}
