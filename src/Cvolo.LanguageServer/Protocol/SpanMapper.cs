using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Maps absolute UTF-16 offsets from a captured snapshot onto protocol ranges. Every mapping is a
/// pure lookup: a span that cannot exist in the captured text is dropped, never clamped, so an
/// impossible backend result can never become an unsafe editor range (§55, §92).
/// </summary>
internal static class SpanMapper
{
    /// <summary>Converts a captured-text position to its protocol shape.</summary>
    public static Microsoft.VisualStudio.LanguageServer.Protocol.Position ToPosition(TextPosition position)
    {
        return new Microsoft.VisualStudio.LanguageServer.Protocol.Position(position.Line, position.Character);
    }

    /// <summary>
    /// Maps a span to a protocol range, allowing a region that spans lines. Fails for a negative or
    /// out-of-text span and for a span that does not land on a real position pair.
    /// </summary>
    public static bool TryMapRange(LineIndex index, int textLength, TextSpan span, out LspRange range)
    {
        if (span.Start < 0
            || span.Length < 0
            || span.End > textLength
            || !index.TryGetRange(span, out TextRange mapped))
        {
            range = null!;
            return false;
        }

        range = new LspRange
        {
            Start = ToPosition(mapped.Start),
            End = ToPosition(mapped.End),
        };
        return true;
    }

    /// <summary>
    /// Maps a span that must describe exactly one occurrence inside one line. A semantic occurrence
    /// never covers a line terminator, so a span that does is malformed rather than merely long.
    /// </summary>
    public static bool TryMapSingleLineRange(
        string text,
        LineIndex index,
        int textLength,
        TextSpan span,
        out LspRange range)
    {
        if (!TryMapRange(index, textLength, span, out LspRange mapped)
            || mapped.Start.Line != mapped.End.Line
            || mapped.End.Character - mapped.Start.Character != span.Length
            || IntersectsLineTerminator(text, span))
        {
            range = null!;
            return false;
        }

        range = mapped;
        return true;
    }

    /// <summary>Whether any code unit of the span is a line terminator.</summary>
    public static bool IntersectsLineTerminator(string text, TextSpan span)
    {
        for (int i = span.Start; i < span.End; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }
}
