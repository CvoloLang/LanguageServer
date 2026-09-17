using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class TextCoordinateMapperTests
{
    [Theory]
    [InlineData("abc\ndef", 0)]
    [InlineData("abc\ndef", 3)]
    [InlineData("abc\ndef", 4)]
    [InlineData("abc\ndef", 7)]
    [InlineData("ab\r\ncd", 4)]
    [InlineData("", 0)]
    public void TryGetPosition_MatchesLineIndex(string text, int offset)
    {
        var index = new LineIndex(text);

        Assert.True(index.TryGetPosition(offset, out TextPosition expected));
        Assert.True(TextCoordinateMapper.TryGetPosition(text, offset, out TextPosition actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryGetPosition_OutOfRange_ReturnsFalse()
    {
        Assert.False(TextCoordinateMapper.TryGetPosition("abc", 4, out _));
    }

    [Theory]
    [InlineData("abc\ndef", 1, 2)]
    [InlineData("abc\ndef", 4, 2)]
    [InlineData("a\nb\r\nc", 2, 3)]
    [InlineData("", 0, 0)]
    public void TryGetRange_MatchesLineIndex(string text, int start, int length)
    {
        var index = new LineIndex(text);
        var span = new TextSpan(start, length);

        Assert.True(index.TryGetRange(span, out TextRange expected));
        Assert.True(TextCoordinateMapper.TryGetRange(text, span, out TextRange actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryGetRange_OutOfRange_ReturnsFalse()
    {
        Assert.False(TextCoordinateMapper.TryGetRange("abc", new TextSpan(0, 4), out _));
    }
}
