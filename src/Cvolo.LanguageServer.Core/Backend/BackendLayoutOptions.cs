namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// How byte offsets are written in a layout annotation. The numbers are always the compiler's; only
/// the notation is a presentation choice, so switching it never re-derives or re-rounds a value.
/// </summary>
internal enum BackendOffsetFormat
{
    /// <summary>Plain decimal byte counts, e.g. <c>24</c>.</summary>
    Decimal,

    /// <summary>Hexadecimal, zero padded to at least two digits, e.g. <c>0x18</c>.</summary>
    Hex,

    /// <summary>Decimal followed by the hexadecimal value in parentheses, e.g. <c>24 (0x18)</c>.</summary>
    DecimalAndHex,
}

/// <summary>
/// The layout viewer's presentation settings. They change how compiler-owned numbers are written,
/// never which numbers they are: the core and the protocol layer never compute a layout fact, so a
/// format switch can only re-render text that already came from the compiler.
/// </summary>
internal sealed record BackendLayoutOptions
{
    /// <summary>The recommended default set.</summary>
    public static BackendLayoutOptions Default { get; } = new();

    /// <summary>How byte offsets are written.</summary>
    public BackendOffsetFormat OffsetFormat { get; init; } = BackendOffsetFormat.Decimal;

    /// <summary>Whether the padding summary also shows the padding share of the object size.</summary>
    public bool ShowPaddingPercentage { get; init; }

    /// <summary>Whether an open layout view refreshes when the project's semantics change.</summary>
    public bool AutoRefresh { get; init; } = true;
}
