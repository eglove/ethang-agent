using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

// Reasoning floods arrive as fragments full of newlines. The pane normalizes them
// through the shared StreamedTextNormalizer: mid-word wraps join, blank-line runs
// collapse, and the open reasoning block re-renders in place instead of stacking
// one line per delta fragment.
public class TranscriptPaneReasoningTests
{
    private const string Dim = "\u001b[2m";
    private const string Reset = "\u001b[0m";

    [Fact]
    public void Reasoning_NewlineFlood_RendersOneBlankLine()
    {
        var pane = new TranscriptPane();
        pane.AppendReasoning("step one\n\n\n\n\n\nstep two");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 3, width: 40);

        Assert.Equal(Dim + "step one" + Reset, writer.RowWrites[0].Text.TrimEnd());
        Assert.Equal(string.Empty, writer.RowWrites[1].Text.TrimEnd());
        Assert.Equal(Dim + "step two" + Reset, writer.RowWrites[2].Text.TrimEnd());
    }

    [Fact]
    public void Reasoning_MidWordFragments_JoinIntoOneLine()
    {
        var pane = new TranscriptPane();
        pane.AppendReasoning("think");
        pane.AppendReasoning("ing.\nThen more");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 3, width: 40);

        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith(Dim + "thinking."));
        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith(Dim + "Then more"));
        Assert.DoesNotContain(writer.RowWrites, w => w.Text.StartsWith(Dim + "ing"));
    }

    [Fact]
    public void Reasoning_CommaNewline_BecomesSpace()
    {
        var pane = new TranscriptPane();
        pane.AppendReasoning("however,");
        pane.AppendReasoning("\nthe answer is");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 2, width: 40);

        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith(Dim + "however, the answer is"));
    }

    [Fact]
    public void AddMessage_ClosesReasoningBlock_NextAppendStartsFresh()
    {
        var pane = new TranscriptPane();
        pane.AppendReasoning("earlier reasoning");
        pane.AddMessage("\u203a user input");
        pane.AppendReasoning("new reasoning");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 10, width: 40);

        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("\u203a user input"));
        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith(Dim + "new reasoning"));
        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith(Dim + "earlier reasoning"));
    }
}
