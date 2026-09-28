using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Maps a compiler-owned hierarchy item to the LSP 3.17 <c>TypeHierarchyItem</c> shape the server
/// defines locally. The source spans are mapped against the text captured with the result, so a
/// contract declared in a closed document still navigates. An item whose text is missing or whose
/// selection span cannot be mapped is dropped, never clamped (§13, §14, §15, §19).
/// </summary>
internal static class HierarchyItemMapper
{
    /// <summary>Maps every item in a result, dropping the ones that cannot be mapped. Null when none survive.</summary>
    internal static TypeHierarchyItemPayload[]? MapAll(BackendTypeHierarchyResult result)
    {
        if (result.Items.Count == 0)
        {
            return null;
        }

        var indexes = new Dictionary<DocumentUri, LineIndex>();
        var items = new List<TypeHierarchyItemPayload>(result.Items.Count);
        foreach (BackendHierarchyItem item in result.Items)
        {
            if (ToPayload(item, result.DocumentTexts, indexes) is { } payload)
            {
                items.Add(payload);
            }
        }

        return items.Count == 0 ? null : [.. items];
    }

    internal static TypeHierarchyItemPayload? ToPayload(
        BackendHierarchyItem item,
        IReadOnlyDictionary<DocumentUri, string>? texts,
        Dictionary<DocumentUri, LineIndex> indexes)
    {
        if (texts is null || !texts.TryGetValue(item.Document, out string? text))
        {
            return null;
        }

        if (!indexes.TryGetValue(item.Document, out LineIndex? index))
        {
            index = new LineIndex(text);
            indexes[item.Document] = index;
        }

        if (!index.TryGetRange(item.SelectionSpan, out TextRange selection))
        {
            return null;
        }

        LspRange selectionRange = ToLspRange(selection);
        LspRange range = index.TryGetRange(item.Range, out TextRange full) ? ToLspRange(full) : selectionRange;

        return new TypeHierarchyItemPayload
        {
            Name = item.Name,
            Kind = ToSymbolKind(item.Kind),
            Uri = new Uri(item.Document.LocalPath).AbsoluteUri,
            Range = range,
            SelectionRange = selectionRange,
            Data = item.Key,
        };
    }

    internal static SymbolKind ToSymbolKind(BackendSymbolKind kind) => kind switch
    {
        BackendSymbolKind.Namespace => SymbolKind.Namespace,
        BackendSymbolKind.Module => SymbolKind.Module,
        BackendSymbolKind.Struct => SymbolKind.Struct,
        BackendSymbolKind.Union => SymbolKind.Struct,
        BackendSymbolKind.Enum => SymbolKind.Enum,
        BackendSymbolKind.EnumMember => SymbolKind.EnumMember,
        BackendSymbolKind.Interface => SymbolKind.Interface,
        BackendSymbolKind.Protocol => SymbolKind.Interface,
        BackendSymbolKind.Delegate => SymbolKind.Class,
        BackendSymbolKind.TypeAlias => SymbolKind.Class,
        BackendSymbolKind.TypeParameter => SymbolKind.TypeParameter,
        BackendSymbolKind.Function => SymbolKind.Function,
        BackendSymbolKind.Method => SymbolKind.Method,
        BackendSymbolKind.ExtensionMethod => SymbolKind.Method,
        BackendSymbolKind.AssociatedFunction => SymbolKind.Method,
        BackendSymbolKind.Constructor => SymbolKind.Constructor,
        BackendSymbolKind.Destructor => SymbolKind.Method,
        BackendSymbolKind.Field => SymbolKind.Field,
        BackendSymbolKind.Property => SymbolKind.Property,
        BackendSymbolKind.Parameter => SymbolKind.Variable,
        BackendSymbolKind.Local => SymbolKind.Variable,
        BackendSymbolKind.Global => SymbolKind.Variable,
        BackendSymbolKind.Constant => SymbolKind.Constant,
        BackendSymbolKind.Operator => SymbolKind.Operator,
        BackendSymbolKind.OtherType => SymbolKind.Class,
        _ => SymbolKind.Object,
    };

    private static LspRange ToLspRange(TextRange range) => new()
    {
        Start = new Position(range.Start.Line, range.Start.Character),
        End = new Position(range.End.Line, range.End.Character),
    };
}
