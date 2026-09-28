namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>Whether a padding run sits between two members or after the last one.</summary>
internal enum BackendTypeLayoutPaddingKind
{
    Internal,
    Tail,
}

/// <summary>One member's placement inside its type. A union's members all start at the same offset.</summary>
internal sealed record BackendTypeLayoutMember(
    string Name,
    string TypeDisplay,
    long Offset,
    long Size,
    long Alignment);

/// <summary>One run of bytes that carry no member: either between members or after the last one.</summary>
internal sealed record BackendTypeLayoutPadding(long Offset, long Size, BackendTypeLayoutPaddingKind Kind);

/// <summary>
/// One type's complete object layout for one target, as the compiler computed it. This is object
/// layout, not the platform ABI: it answers "how big is this and where does each field sit", never
/// "which register carries it".
/// </summary>
/// <param name="TypeDisplay">The type as the user wrote it.</param>
/// <param name="TargetDisplay">The target the numbers were computed for, so a client can label them.</param>
/// <param name="Stride">The element stride of an array type, otherwise null.</param>
/// <param name="ElementCount">The element count of an array type, otherwise null.</param>
internal sealed record BackendTypeLayoutInspection(
    string TypeDisplay,
    string TargetDisplay,
    long Size,
    long Alignment,
    long PayloadSize,
    long PaddingSize,
    long? Stride,
    long? ElementCount,
    long? ElementSize,
    long? ElementAlignment,
    IReadOnlyList<BackendTypeLayoutMember> Members,
    IReadOnlyList<BackendTypeLayoutPadding> Padding);
