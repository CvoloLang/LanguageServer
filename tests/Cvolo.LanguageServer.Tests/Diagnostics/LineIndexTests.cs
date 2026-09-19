using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class LineIndexTests
{
    [Fact]
    public void EmptyText_MapsOffsetZero_AndNothingElse()
    {
        var index = new LineIndex(string.Empty);

        Assert.True(index.TryGetPosition(0, out TextPosition origin));
        Assert.Equal(new TextPosition(0, 0), origin);
        Assert.False(index.TryGetPosition(1, out _));
    }

    [Fact]
    public void LfEndings_MapLineAndCharacter()
    {
        var index = new LineIndex("abc\ndef");

        Assert.Equal(new TextPosition(0, 0), Position(index, 0));
        Assert.Equal(new TextPosition(0, 3), Position(index, 3));
        Assert.Equal(new TextPosition(1, 0), Position(index, 4));
        Assert.Equal(new TextPosition(1, 3), Position(index, 7));
    }

    [Fact]
    public void CrLfEnding_IsOneTerminator()
    {
        var index = new LineIndex("ab\r\ncd");

        Assert.Equal(new TextPosition(0, 2), Position(index, 2));
        Assert.Equal(new TextPosition(0, 2), Position(index, 3));
        Assert.Equal(new TextPosition(1, 0), Position(index, 4));
        Assert.Equal(new TextPosition(1, 2), Position(index, 6));
    }

    [Fact]
    public void OffsetInsideTerminator_MapsToVisibleLineEnd()
    {
        var index = new LineIndex("ab\r\ncd");

        Assert.Equal(new TextPosition(0, 2), Position(index, 3));

        Assert.True(index.TryGetRange(new TextSpan(3, 1), out TextRange range));
        Assert.Equal(new TextPosition(0, 2), range.Start);
        Assert.Equal(new TextPosition(1, 0), range.End);
    }

    [Fact]
    public void CrLfSpan_NeverFabricatesCharacterBeyondVisibleLine()
    {
        var index = new LineIndex("ab\r\ncd");

        Assert.True(index.TryGetRange(new TextSpan(2, 1), out TextRange cr));
        Assert.Equal(new TextPosition(0, 2), cr.Start);
        Assert.Equal(new TextPosition(0, 2), cr.End);
    }

    [Fact]
    public void LoneCr_IsATerminator()
    {
        var index = new LineIndex("ab\rcd");

        Assert.Equal(new TextPosition(0, 2), Position(index, 2));
        Assert.Equal(new TextPosition(1, 0), Position(index, 3));
        Assert.Equal(new TextPosition(1, 2), Position(index, 5));
    }

    [Fact]
    public void MixedEndings_MapCorrectly()
    {
        var index = new LineIndex("a\nb\r\nc\rd");

        Assert.Equal(new TextPosition(0, 0), Position(index, 0));
        Assert.Equal(new TextPosition(1, 0), Position(index, 2));
        Assert.Equal(new TextPosition(2, 0), Position(index, 5));
        Assert.Equal(new TextPosition(3, 0), Position(index, 7));
        Assert.Equal(new TextPosition(3, 1), Position(index, 8));
    }

    [Fact]
    public void EmptyLines_ArePreserved()
    {
        var index = new LineIndex("a\n\nb");

        Assert.Equal(new TextPosition(1, 0), Position(index, 2));
        Assert.Equal(new TextPosition(2, 0), Position(index, 3));
        Assert.Equal(new TextPosition(2, 1), Position(index, 4));
    }

    [Fact]
    public void TrailingNewline_ProducesEmptyFinalLine()
    {
        var index = new LineIndex("a\n");

        Assert.Equal(2, index.LineCount);
        Assert.Equal(new TextPosition(1, 0), Position(index, 2));
    }

    [Fact]
    public void SurrogatePair_CountsAsTwoUtf16Units()
    {
        var index = new LineIndex("a\U0001F600b");

        Assert.Equal(1, index.LineCount);
        Assert.Equal(new TextPosition(0, 1), Position(index, 1));
        Assert.Equal(new TextPosition(0, 2), Position(index, 2));
        Assert.Equal(new TextPosition(0, 3), Position(index, 3));
        Assert.Equal(new TextPosition(0, 4), Position(index, 4));
    }

    [Fact]
    public void OffsetOutOfRange_Fails_ButLengthIsValid()
    {
        var index = new LineIndex("abc");

        Assert.False(index.TryGetPosition(-1, out _));
        Assert.True(index.TryGetPosition(3, out TextPosition end));
        Assert.Equal(new TextPosition(0, 3), end);
        Assert.False(index.TryGetPosition(4, out _));
    }

    [Fact]
    public void Range_MapsStartAndEnd()
    {
        var index = new LineIndex("abc\ndef");

        Assert.True(index.TryGetRange(new TextSpan(1, 2), out TextRange range));
        Assert.Equal(new TextPosition(0, 1), range.Start);
        Assert.Equal(new TextPosition(0, 3), range.End);

        Assert.True(index.TryGetRange(new TextSpan(4, 2), out TextRange second));
        Assert.Equal(new TextPosition(1, 0), second.Start);
        Assert.Equal(new TextPosition(1, 2), second.End);
    }

    [Fact]
    public void ZeroLengthSpan_AtEndOfDocument_IsValid()
    {
        var index = new LineIndex("abc");

        Assert.True(index.TryGetRange(new TextSpan(3, 0), out TextRange range));
        Assert.Equal(new TextPosition(0, 3), range.Start);
        Assert.Equal(new TextPosition(0, 3), range.End);
    }

    [Fact]
    public void OutOfRangeSpan_Fails_WithoutClamping()
    {
        var index = new LineIndex("abc");

        Assert.False(index.TryGetRange(new TextSpan(-1, 1), out _));
        Assert.False(index.TryGetRange(new TextSpan(0, -1), out _));
        Assert.False(index.TryGetRange(new TextSpan(0, 4), out _));
        Assert.False(index.TryGetRange(new TextSpan(3, 1), out _));
    }

    [Fact]
    public void OneIndex_MapsManySpans()
    {
        var index = new LineIndex("one\ntwo\nthree");

        Assert.True(index.TryGetRange(new TextSpan(0, 3), out TextRange first));
        Assert.True(index.TryGetRange(new TextSpan(4, 3), out TextRange second));
        Assert.True(index.TryGetRange(new TextSpan(8, 5), out TextRange third));

        Assert.Equal(new TextPosition(0, 0), first.Start);
        Assert.Equal(new TextPosition(1, 0), second.Start);
        Assert.Equal(new TextPosition(2, 0), third.Start);
        Assert.Equal(new TextPosition(2, 5), third.End);
    }

    [Fact]
    public void Tab_CountsAsSingleUtf16Unit()
    {
        var index = new LineIndex("\tvalue");

        Assert.Equal(new TextPosition(0, 0), Position(index, 0));
        Assert.Equal(new TextPosition(0, 1), Position(index, 1));
        Assert.True(index.TryGetRange(new TextSpan(1, 5), out TextRange range));
        Assert.Equal(new TextPosition(0, 1), range.Start);
        Assert.Equal(new TextPosition(0, 6), range.End);
    }

    [Fact]
    public void TryGetOffset_Lf_BeginningContentAndEndOfLine()
    {
        var index = new LineIndex("abc\ndef");

        Assert.Equal(0, Offset(index, 0, 0));
        Assert.Equal(1, Offset(index, 0, 1));
        Assert.Equal(3, Offset(index, 0, 3));
        Assert.False(index.TryGetOffset(new TextPosition(0, 4), out _));
        Assert.Equal(4, Offset(index, 1, 0));
        Assert.Equal(7, Offset(index, 1, 3));
        Assert.False(index.TryGetOffset(new TextPosition(1, 4), out _));
    }

    [Fact]
    public void TryGetOffset_CrLf_RejectsCharacterInsideTerminator()
    {
        // LSP characters are boundaries in the visible line content, never the
        // CR/LF terminator code units (§8): "a\r\nb" has (0,1) as end-of-line,
        // (0,2) is invalid, and line 1 starts at offset 3.
        var index = new LineIndex("a\r\nb");

        Assert.Equal(0, Offset(index, 0, 0));
        Assert.Equal(1, Offset(index, 0, 1));
        Assert.False(index.TryGetOffset(new TextPosition(0, 2), out _));
        Assert.Equal(3, Offset(index, 1, 0));
        Assert.Equal(4, Offset(index, 1, 1));
        Assert.False(index.TryGetOffset(new TextPosition(1, 2), out _));
    }

    [Fact]
    public void TryGetOffset_LoneCr_RejectsCharacterInsideTerminator()
    {
        var index = new LineIndex("a\rb");

        Assert.Equal(0, Offset(index, 0, 0));
        Assert.Equal(1, Offset(index, 0, 1));
        Assert.False(index.TryGetOffset(new TextPosition(0, 2), out _));
        Assert.Equal(2, Offset(index, 1, 0));
        Assert.Equal(3, Offset(index, 1, 1));
        Assert.False(index.TryGetOffset(new TextPosition(1, 2), out _));
    }

    [Fact]
    public void TryGetOffset_EmptyString_HasOnlyOrigin()
    {
        var index = new LineIndex(string.Empty);

        Assert.Equal(0, Offset(index, 0, 0));
        Assert.False(index.TryGetOffset(new TextPosition(0, 1), out _));
        Assert.False(index.TryGetOffset(new TextPosition(1, 0), out _));
    }

    [Fact]
    public void TryGetOffset_EmptyLine_ResolvesOnlyOrigin()
    {
        var index = new LineIndex("a\n\nb");

        Assert.Equal(2, Offset(index, 1, 0));
        Assert.False(index.TryGetOffset(new TextPosition(1, 1), out _));
        Assert.Equal(3, Offset(index, 2, 0));
    }

    [Fact]
    public void TryGetOffset_TrailingNewline_HasEmptyFinalLine()
    {
        var index = new LineIndex("a\n");

        Assert.Equal(2, Offset(index, 1, 0));
        Assert.False(index.TryGetOffset(new TextPosition(1, 1), out _));
    }

    [Fact]
    public void TryGetOffset_RejectsNegativeAndOutOfRange()
    {
        var index = new LineIndex("abc");

        Assert.False(index.TryGetOffset(new TextPosition(-1, 0), out _));
        Assert.False(index.TryGetOffset(new TextPosition(1, 0), out _));
        Assert.False(index.TryGetOffset(new TextPosition(0, -1), out _));
        Assert.False(index.TryGetOffset(new TextPosition(0, 4), out _));
    }

    [Fact]
    public void TryGetOffset_SurrogatePair_UsesUtf16Units()
    {
        var index = new LineIndex("a\U0001F600b");

        Assert.Equal(0, Offset(index, 0, 0));
        Assert.Equal(1, Offset(index, 0, 1));
        Assert.Equal(2, Offset(index, 0, 2));
        Assert.Equal(3, Offset(index, 0, 3));
        Assert.Equal(4, Offset(index, 0, 4));
        Assert.False(index.TryGetOffset(new TextPosition(0, 5), out _));
    }

    [Fact]
    public void TryGetOffset_RoundTripsWithTryGetPosition()
    {
        var index = new LineIndex("one\r\ntwo\nthree\rfour");

        for (var offset = 0; offset <= 20; offset++)
        {
            if (!index.TryGetPosition(offset, out TextPosition position))
            {
                continue;
            }

            // A position maps back to an offset that maps to the same position,
            // except offsets inside a terminator which collapse to visible end.
            Assert.True(index.TryGetOffset(position, out int roundTrip));
            Assert.True(index.TryGetPosition(roundTrip, out TextPosition reprojected));
            Assert.Equal(position, reprojected);
        }
    }

    private static TextPosition Position(LineIndex index, int offset)
    {
        Assert.True(index.TryGetPosition(offset, out TextPosition position));
        return position;
    }

    private static int Offset(LineIndex index, int line, int character)
    {
        Assert.True(index.TryGetOffset(new TextPosition(line, character), out int offset));
        return offset;
    }
}
