using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Diagnostics;

/// <summary>
/// Convenience helpers for one-off coordinate mapping. Each call builds a fresh
/// <see cref="LineIndex"/>; callers mapping several spans against the same text
/// should build one index and reuse it.
/// </summary>
internal static class TextCoordinateMapper
{
    public static bool TryGetPosition(string text, int offset, out TextPosition position)
    {
        return new LineIndex(text).TryGetPosition(offset, out position);
    }

    public static bool TryGetRange(string text, TextSpan span, out TextRange range)
    {
        return new LineIndex(text).TryGetRange(span, out range);
    }
}
