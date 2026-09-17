namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Zero-based position in a text document: line index and UTF-16 code-unit
/// offset inside that line. Value-type mirror of the LSP Position model so
/// the core stays protocol-model-neutral.
/// </summary>
internal readonly record struct TextPosition(int Line, int Character)
{
    public static TextPosition Zero => new(0, 0);

    public override string ToString()
    {
        return $"({Line},{Character})";
    }
}