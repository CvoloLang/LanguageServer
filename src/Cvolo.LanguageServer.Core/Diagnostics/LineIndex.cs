using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Diagnostics;

/// <summary>
/// Immutable mapping from absolute UTF-16 code-unit offsets to zero-based
/// line/character positions. Line starts and content ends are precomputed once
/// so that any number of spans can be mapped with a binary search and no
/// rescanning. <c>\n</c>, <c>\r\n</c> and a lone <c>\r</c> are all treated as
/// line terminators; the terminator itself is never part of a line's visible
/// characters, so offsets inside a terminator map to the line's visible end
/// rather than to an impossible character position.
/// </summary>
internal sealed class LineIndex
{
    private readonly string _text;
    private readonly int[] _lineStarts;
    private readonly int[] _lineContentEnds;

    public LineIndex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _text = text;
        (_lineStarts, _lineContentEnds) = BuildLines(text);
    }

    /// <summary>Number of lines; at least one, even for an empty text.</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>
    /// Maps an absolute offset to a position. Offsets outside
    /// <c>[0, text.Length]</c> fail; <c>text.Length</c> (end of file) is valid.
    /// Offsets that fall on a line terminator map to the end of the preceding
    /// line's visible characters.
    /// </summary>
    public bool TryGetPosition(int offset, out TextPosition position)
    {
        if (offset < 0 || offset > _text.Length)
        {
            position = default;
            return false;
        }

        var line = FindLine(offset);
        var start = _lineStarts[line];
        var visibleLength = _lineContentEnds[line] - start;
        var character = offset - start;
        if (character > visibleLength)
        {
            character = visibleLength;
        }

        position = new TextPosition(line, character);
        return true;
    }

    /// <summary>
    /// Maps a span to a range. Fails when the span is negative or extends past
    /// the end of the text; never clamps a malformed span into a fabricated
    /// position.
    /// </summary>
    public bool TryGetRange(TextSpan span, out TextRange range)
    {
        if (span.Start < 0 || span.Length < 0 || span.End > _text.Length)
        {
            range = default;
            return false;
        }

        if (!TryGetPosition(span.Start, out TextPosition start) || !TryGetPosition(span.End, out TextPosition end))
        {
            range = default;
            return false;
        }

        range = new TextRange(start, end);
        return true;
    }

    private static (int[] Starts, int[] ContentEnds) BuildLines(string text)
    {
        var starts = new List<int> { 0 };
        var ends = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                ends.Add(i);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                ends.Add(i);
                starts.Add(i + 1);
            }
        }

        ends.Add(text.Length);
        return (starts.ToArray(), ends.ToArray());
    }

    private int FindLine(int offset)
    {
        var low = 0;
        var high = _lineStarts.Length - 1;
        var result = 0;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (_lineStarts[mid] <= offset)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }
}
