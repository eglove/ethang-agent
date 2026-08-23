using eThangAgent.SharedKernel;

namespace eThangAgent.SharedKernel.Tests;

// Streamed reasoning arrives as tiny fragments full of hard wraps and blank-line
// runs. The normalizer makes it readable: wraps between two letters join (the
// model hard-wraps inside CamelCase identifiers constantly), wraps before
// closing/comma punctuation attach directly, real sentence breaks stay,
// bullet/heading breaks stay, and blank-line floods collapse to one blank line.
public class StreamedTextNormalizerTests
{
    [Fact]
    public void MidWord_HardWrap_JoinsWithoutSpace()
    {
        var n = new StreamedTextNormalizer();
        n.Append("think");
        n.Append("ing");
        Assert.Equal("thinking", n.Text);
    }

    [Fact]
    public void CamelCaseIdentifier_HardWrap_JoinsWithoutSpace()
    {
        var n = new StreamedTextNormalizer();
        n.Append("SendMessageCommandHandler, Agent");
        n.Append("\nId, RootSessionLifecycle usage");
        Assert.Equal("SendMessageCommandHandler, AgentId, RootSessionLifecycle usage", n.Text);
    }

    [Fact]
    public void NewlineAfterComma_BecomesSpace()
    {
        var n = new StreamedTextNormalizer();
        n.Append("however,");
        n.Append("\nthe answer");
        Assert.Equal("however, the answer", n.Text);
    }

    [Fact]
    public void NewlineBeforeComma_JoinsDirectly()
    {
        var n = new StreamedTextNormalizer();
        n.Append("consider options");
        n.Append("\n, such as caching");
        Assert.Equal("consider options, such as caching", n.Text);
    }

    [Fact]
    public void NewlineBeforeClosingParen_JoinsDirectly()
    {
        var n = new StreamedTextNormalizer();
        n.Append("(see the handler above");
        n.Append("\n)");
        Assert.Equal("(see the handler above)", n.Text);
    }

    [Fact]
    public void OpeningParen_AfterNewline_GetsASpace()
    {
        var n = new StreamedTextNormalizer();
        n.Append("the Conversation");
        n.Append("\n( aggregate root)");
        Assert.Equal("the Conversation ( aggregate root)", n.Text);
    }

    [Fact]
    public void SentenceBreak_IsPreserved()
    {
        var n = new StreamedTextNormalizer();
        n.Append("done.\nNext point");
        Assert.Equal("done.\nNext point", n.Text);
    }

    [Fact]
    public void ColonBeforeText_JoinsWithSpace()
    {
        // Code-dense reasoning ends clauses with colons constantly; a bare wrap
        // there is a hard wrap, not structure.
        var n = new StreamedTextNormalizer();
        n.Append("plan:");
        n.Append("\nfirst step");
        Assert.Equal("plan: first step", n.Text);
    }

    [Fact]
    public void ColonBeforeCapital_IsPreserved()
    {
        // A capital item on the next line reads as a heading/list entry.
        var n = new StreamedTextNormalizer();
        n.Append("usage:");
        n.Append("\nAgentId handles identity");
        Assert.Equal("usage:\nAgentId handles identity", n.Text);
    }

    [Fact]
    public void BlankLineRun_CollapsesToOneBlankLine()
    {
        var n = new StreamedTextNormalizer();
        n.Append("step one\n\n\n\n\n\nstep two");
        Assert.Equal("step one\n\nstep two", n.Text);
    }

    [Fact]
    public void LeadingNewlines_AreDropped()
    {
        var n = new StreamedTextNormalizer();
        n.Append("\n\n\nhello");
        Assert.Equal("hello", n.Text);
    }

    [Fact]
    public void TrailingNewlines_AreTrimmedInText()
    {
        var n = new StreamedTextNormalizer();
        n.Append("hello\n\n");
        Assert.Equal("hello", n.Text);
    }

    [Fact]
    public void CarriageReturns_AreIgnored()
    {
        var n = new StreamedTextNormalizer();
        n.Append("end.\r\nNew thought");
        Assert.Equal("end.\nNew thought", n.Text);
    }

    [Fact]
    public void UnpunctuatedWrap_BeforeCapital_Joins_KnownTradeOff()
    {
        // A bare wrap between two letters is joined even across a capital: the
        // model's identifier wraps vastly outnumber unpunctuated sentence
        // boundaries, and a wrong join is cheaper to read than a wrong break
        // ("AgentId" vs "workingNow").
        var n = new StreamedTextNormalizer();
        n.Append("still working");
        n.Append("\nNow switch");
        Assert.Equal("still workingNow switch", n.Text);
    }

    [Fact]
    public void Capital_AfterNonLetter_IsPreserved()
    {
        var n = new StreamedTextNormalizer();
        n.Append("step 2");
        n.Append("\nNow check the result");
        Assert.Equal("step 2\nNow check the result", n.Text);
    }

    [Fact]
    public void BulletAfterNewline_IsPreserved()
    {
        var n = new StreamedTextNormalizer();
        n.Append("options");
        n.Append("\n- first");
        Assert.Equal("options\n- first", n.Text);
    }

    [Fact]
    public void EmptyAppend_IsNoop()
    {
        var n = new StreamedTextNormalizer();
        n.Append("");
        Assert.Equal("", n.Text);
    }

    [Fact]
    public void BreakState_CarriesAcrossAppends()
    {
        var n = new StreamedTextNormalizer();
        n.Append("a\n");
        n.Append("\n\nb");
        Assert.Equal("a\n\nb", n.Text);
    }
}
