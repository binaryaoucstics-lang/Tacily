using ScreenTranslator.Core.Models;
using ScreenTranslator.Core.Translation;

namespace ScreenTranslator.Core.Tests.Translation;

public sealed class PageTextNumericTransferTests
{
    [Theory]
    [InlineData("The entrance is blue.", false)]
    [InlineData("Bring bottles on Monday", true)]
    [InlineData("with capacity 250 ml", true)]
    [InlineData("The event began in 1951.", true)]
    [InlineData("Meet at 14:30.", true)]
    [InlineData("Its 740M parameters produce 768-dimensional embeddings.", true)]
    public void SourceFactPresenceUsesTheExistingGuardParser(string source, bool expected)
        => Assert.Equal(expected, PageTextSemanticGuard.HasProtectedFacts(source));

    [Fact]
    public void TransferEdgesRemainVisibleWhenReceiverAlsoChangesWeekday()
    {
        BrowserPageTextBlock[] source = [new("owner", "with capacity 250 ml"),
            new("receiver", "Bring bottles on Monday")];
        var answers = new Dictionary<string, string> { ["owner"] = "容量合适", ["receiver"] = "周二带250毫升的瓶子" };
        var verdict = PageTextSemanticGuard.Assess(source, answers);
        Assert.Equal(PageTextSemanticGuard.FindUnsafeIds(source, answers), verdict.UnsafeIds);
        Assert.Equal([new PageTextNumericTransfer("owner", "receiver")], verdict.NumericTransfers);
        Assert.Contains("receiver", verdict.UnsafeIds);
    }

    [Fact]
    public void IndependentWrongDatesDoNotCreateTransferEdges()
    {
        BrowserPageTextBlock[] source = [new("a", "The event began in 1951."), new("b", "The event began in 1985.")];
        var verdict = PageTextSemanticGuard.Assess(source,
            new Dictionary<string, string> { ["a"] = "活动于1952年开始。", ["b"] = "活动于1986年开始。" });
        Assert.Equal(["a", "b"], verdict.UnsafeIds);
        Assert.Empty(verdict.NumericTransfers);
    }

    [Fact]
    public void CorrectFactsAndMissingTranslationPreserveExistingVerdicts()
    {
        BrowserPageTextBlock[] source = [new("a", "Bring 3 bottles."), new("b", "with capacity 250 ml")];
        var answers = new Dictionary<string, string> { ["a"] = "带3个瓶子。", ["b"] = "容量为250毫升" };
        Assert.Empty(PageTextSemanticGuard.Assess(source, answers).UnsafeIds);
        Assert.Empty(PageTextSemanticGuard.Assess(source, answers).NumericTransfers);
        answers.Remove("b");
        Assert.Equal(["b"], PageTextSemanticGuard.Assess(source, answers).UnsafeIds);
    }
}
