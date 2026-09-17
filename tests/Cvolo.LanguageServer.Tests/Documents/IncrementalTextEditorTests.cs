using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Editing;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Documents;

public class IncrementalTextEditorTests
{
    [Theory]
    [InlineData("abcd", "XY", "(0,2)-(0,2)", "abXYcd")]
    [InlineData("abcd", "XY", "(0,0)-(0,0)", "XYabcd")]
    [InlineData("abcd", "XY", "(0,4)-(0,4)", "abcdXY")]
    [InlineData("", "z", "(0,0)-(0,0)", "z")]
    public void Insert_ProducesExpectedText(string initial, string insert, string rangeSpec, string expected)
    {
        AssertApply(initial, [(rangeSpec, insert)], expected);
    }

    [Theory]
    [InlineData("abcd", "(0,1)-(0,3)", "ad")]
    [InlineData("a\nb\n", "(0,0)-(1,0)", "b\n")]
    [InlineData("hello world", "(0,6)-(0,11)", "hello ")]
    [InlineData("hello world", "(0,0)-(0,1)", "ello world")]
    public void Delete_ProducesExpectedText(string initial, string rangeSpec, string expected)
    {
        AssertApply(initial, [(rangeSpec, "")], expected);
    }

    [Theory]
    [InlineData("hello world", "cvolo", "(0,6)-(0,11)", "hello cvolo")]
    [InlineData("a\nb", "\nX\n", "(0,1)-(1,0)", "a\nX\nb")]
    [InlineData("ab\r\ncd\r\n", "Z", "(0,2)-(0,2)", "abZ\r\ncd\r\n")]
    public void Replace_ProducesExpectedText(string initial, string replacement, string rangeSpec, string expected)
    {
        AssertApply(initial, [(rangeSpec, replacement)], expected);
    }

    [Fact]
    public void EmptyLine_AllowsInsertAtBoundary()
    {
        AssertApply("a\n\nb", [("(1,0)-(1,0)", "X")], "a\nX\nb");
    }

    [Fact]
    public void TrailingNewline_ExposesEmptyLastLine()
    {
        AssertApply("x\n", [("(1,0)-(1,0)", "!")], "x\n!");
    }

    [Fact]
    public void SurrogatePair_CountsAsTwoUtf16Units()
    {
        AssertApply("\U0001F600", [("(0,2)-(0,2)", "Z")], "\U0001F600Z");
    }

    [Fact]
    public void MixedEndings_UseLineLocalCharacterColumn()
    {
        AssertApply("a\nb\r\nc", [("(1,1)-(1,1)", "Z")], "a\nbZ\r\nc");
    }

    [Fact]
    public void Crlf_CharacterWithinContentLength_IsValid_ButTrailingCrIsNot()
    {
        AssertApply("ab\r\n", [("(0,2)-(0,2)", "Z")], "abZ\r\n");
        AssertApplyFails("ab\r\n", [("(0,3)-(0,3)", "Z")]);
    }

    [Fact]
    public void FullReplacement_NullRange_ResetsThenAppliesSequentially()
    {
        AssertApply("abcdef", [("(0,1)-(0,2)", "1"), (null, "NEW"), ("(0,1)-(0,2)", "2")], "N2W");
    }

    [Fact]
    public void MultiChange_AppliesToIntermediateText()
    {
        AssertApply("abcdef", [("(0,1)-(0,2)", "1"), ("(0,3)-(0,4)", "2")], "a1c2ef");
    }

    [Fact]
    public void InvalidRange_IsRejected_WithoutOutput()
    {
        AssertApplyFails("abc\n", [("(2,0)-(2,0)", "Z")]);
        AssertApplyFails("abc\n", [("(0,4)-(0,4)", "Z")]);
        AssertApplyFails("abc\n", [("(-1,0)-(0,0)", "Z")]);
        AssertApplyFails("abc\n", [("(0,2)-(0,1)", "Z")]);
        AssertApplyFails("abc\n", [("(0,0)-(0,0)", "Z"), ("(5,0)-(5,0)", "Z")]);
    }

    [Fact]
    public void NoChanges_ReturnsOriginalText()
    {
        AssertApply("abc", [], "abc");
    }

    private static void AssertApply(string initial, IReadOnlyList<(string? Range, string Text)> changes, string expected)
    {
        var result = TryApply(initial, changes);
        Assert.True(result.Success);
        Assert.Equal(expected, result.Text);
    }

    private static void AssertApplyFails(string initial, IReadOnlyList<(string? Range, string Text)> changes)
    {
        Assert.False(TryApply(initial, changes).Success);
    }

    private static (bool Success, string? Text) TryApply(string initial, IReadOnlyList<(string? Range, string Text)> changes)
    {
        var coreChanges = changes.Select(c => new DocumentChange(c.Range is null ? null : ParseRange(c.Range), c.Text)).ToArray();
        var ok = IncrementalTextEditor.TryApplyChanges(initial, coreChanges, out var result);
        return (ok, result);
    }

    private static TextRange ParseRange(string spec)
    {
        var parts = spec.Split([' ', '-', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new TextRange(
            new TextPosition(int.Parse(parts[0]), int.Parse(parts[1])),
            new TextPosition(int.Parse(parts[2]), int.Parse(parts[3])));
    }
}