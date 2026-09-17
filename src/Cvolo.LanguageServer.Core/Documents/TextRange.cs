namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Half-open range over a text document ([Start, End)). Value-type mirror of
/// the LSP Range model so the core stays protocol-model-neutral.
/// </summary>
internal readonly record struct TextRange(TextPosition Start, TextPosition End)
{
    public override string ToString()
    {
        return $"{Start}-{End}";
    }
}