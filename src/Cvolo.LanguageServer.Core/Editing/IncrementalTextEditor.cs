using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Editing;

/// <summary>
/// Applies the incremental <see cref="DocumentChange"/> stream of a didChange
/// notification. LSP ranges are zero-based UTF-16 line/character offsets and
/// never include the line break; the engine supports LF, CRLF and mixed
/// endings, empty files, empty lines, trailing newlines and non-BMP
/// surrogate pairs.
/// </summary>
internal static class IncrementalTextEditor
{
    /// <summary>
    /// Applies <paramref name="changes"/> sequentially against the evolving
    /// text. Returns false (leaving <paramref name="result"/> untouched) when
    /// any change is invalid, so the whole notification is transactional.
    /// </summary>
    public static bool TryApplyChanges(string current, IReadOnlyList<DocumentChange> changes, out string result)
    {
        var working = current;
        foreach (DocumentChange change in changes)
        {
            if (change.Range is null)
            {
                working = change.Text;
                continue;
            }

            if (!TryApplyChange(working, change.Range.Value, change.Text, out working))
            {
                result = string.Empty;
                return false;
            }
        }

        result = working;
        return true;
    }

    private static bool TryApplyChange(string text, TextRange range, string replacement, out string result)
    {
        var lineStarts = ComputeLineStarts(text);
        if (!TryGetOffset(text, lineStarts, range.Start, out int startOffset))
        {
            result = string.Empty;
            return false;
        }

        if (!TryGetOffset(text, lineStarts, range.End, out int endOffset))
        {
            result = string.Empty;
            return false;
        }

        if (endOffset < startOffset)
        {
            result = string.Empty;
            return false;
        }

        result = string.Concat(text.AsSpan(0, startOffset), replacement, text.AsSpan(endOffset));
        return true;
    }

    /// <summary>
    /// Maps a position to a UTF-16 offset. A "line" runs from its start to the
    /// following line break (or end of text); the range excludes the break, so
    /// for a CRLF ending the trailing CR is not addressable.
    /// </summary>
    private static bool TryGetOffset(string text, int[] lineStarts, TextPosition position, out int offset)
    {
        offset = 0;
        var line = position.Line;
        if (line < 0 || line >= lineStarts.Length)
        {
            return false;
        }

        var lineStart = lineStarts[line];
        var lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : text.Length;

        var contentEnd = lineEnd;
        if (contentEnd > lineStart && text[contentEnd - 1] == '\n')
        {
            contentEnd -= 1;
            if (contentEnd > lineStart && text[contentEnd - 1] == '\r')
            {
                contentEnd -= 1;
            }
        }

        var character = position.Character;
        if (character < 0 || character > contentEnd - lineStart)
        {
            return false;
        }

        offset = lineStart + character;
        return true;
    }

    private static int[] ComputeLineStarts(string text)
    {
        var lineStarts = new int[CountLines(text)];
        var index = 0;
        lineStarts[index++] = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts[index++] = i + 1;
            }
        }

        return lineStarts;
    }

    private static int CountLines(string text)
    {
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                count += 1;
            }
        }

        return count;
    }
}