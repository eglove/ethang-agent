using eThangAgent.SharedKernel;

namespace eThangAgent.SharedKernel.Tests;

// Streamed reasoning arrives as tiny fragments full of hard wraps and blank-line
// runs. The normalizer makes it readable: mid-word wraps join, comma wraps get a
// space, real sentence breaks stay, blank-line floods collapse to one blank line.
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
    public void NewlineAfterComma_BecomesSpace()
    {
        var n = new StreamedTextNormalizer();
        n.Append("however,");
        n.Append("\nthe answer");
        Assert.Equal("however, the answer", n.Text);
    }

    [Fact]
    public void SentenceBreak_IsPreserved()
    {
        var n = new StreamedTextNormalizer();
        n.Append("done.\nNext point");
        Assert.Equal("done.\nNext point", n.Text);
    }

    [Fact]
    public void ColonBreak_IsPreserved()
    {
        var n = new StreamedTextNormalizer();
        n.Append("plan:\nfirst step");
        Assert.Equal("plan:\nfirst step", n.Text);
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
    public void UppercaseAfterNewline_WithoutTerminalPunctuation_IsABreak()
    {
        var n = new StreamedTextNormalizer();
        n.Append("still working\nNow switch");
        Assert.Equal("still working\nNow switch", n.Text);
    }

    [Fact]
    public void BulletAfterNewline_IsPreserved()
    {
        var n = new StreamedTextNormalizer();
        n.Append("options\n- first");
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
