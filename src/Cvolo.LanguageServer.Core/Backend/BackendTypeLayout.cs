using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>Whether a padding run sits between two members or after the last one.</summary>
internal enum BackendTypeLayoutPaddingKind
{
    Internal,
    Tail,
}

/// <summary>
/// What one member row of the layout view may do, resolved by the compiler so the client never has to.
/// Each target is a source declaration the compiler already indexed; nothing here is derived from the
/// text the view displays.
/// </summary>
/// <param name="Signature">The field as the compiler renders it, qualified by the type that stores it.</param>
/// <param name="Documentation">The field's <c>///</c> documentation, when it has any.</param>
/// <param name="Definition">Where the field name is declared, or null when it is not source backed.</param>
/// <param name="TypeDefinition">Where the field's type is declared, or null when it has no declaration.</param>
/// <param name="NestedLayout">
/// Where the layout of the field's own type can be requested, or null when the compiler cannot lay that
/// type out as a type of its own (a generic template, for instance).
/// </param>
internal sealed record BackendTypeLayoutMemberNavigation(
    string Signature,
    string? Documentation,
    BackendDefinitionTarget? Definition,
    BackendDefinitionTarget? TypeDefinition,
    BackendDefinitionTarget? NestedLayout);

/// <summary>One member's placement inside its type. A union's members all start at the same offset.</summary>
internal sealed record BackendTypeLayoutMember(
    string Name,
    string TypeDisplay,
    long Offset,
    long Size,
    long Alignment,
    BackendTypeLayoutMemberNavigation? Navigation = null);

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
/// <param name="Definition">Where the inspected type is declared, or null when it has no declaration.</param>
/// <param name="DocumentTexts">
/// The exact text of every document a navigation target points into, from the same captured snapshot.
/// Navigation targets are character spans against a document the client may not have open, so the
/// handler needs the text to turn a span into a line/character location, exactly as definitions do.
/// </param>
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
    IReadOnlyList<BackendTypeLayoutPadding> Padding,
    BackendDefinitionTarget? Definition = null,
    IReadOnlyDictionary<DocumentUri, string>? DocumentTexts = null);
