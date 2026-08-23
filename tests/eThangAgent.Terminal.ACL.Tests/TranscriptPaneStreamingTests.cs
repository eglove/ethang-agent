using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class TranscriptPaneStreamingTests
{
    [Fact]
    public void StreamedDeltas_LandOnAFreshLine_BelowPreviousMessage()
    {
        var pane = new TranscriptPane();
        pane.AddMessage("\u203a hello");
        pane.BeginStream();
        pane.AppendStream("He");
        pane.AppendStream("llo");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 10, width: 40);

        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("\u203a hello"));
        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("Hello"));
    }

    [Fact]
    public void EmbeddedNewlines_InDelta_SplitLines()
    {
        var pane = new TranscriptPane();
        pane.BeginStream();
        pane.AppendStream("one\ntwo");
        pane.AppendStream("\nthree");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 10, width: 40);

        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("one"));
        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("two"));
        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("three"));
    }

    [Fact]
    public void SecondBeginStream_AfterContent_SeparatesIterations()
    {
        var pane = new TranscriptPane();
        pane.AddMessage("\u203a hi");
        pane.BeginStream(); // turn start
        pane.AppendStream("thinking");
        pane.BeginStream(); // iteration end -> fresh separated line
        pane.AppendStream("final");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 10, width: 40);

        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("thinking"));
        Assert.Contains(writer.RowWrites, w => w.Text.StartsWith("final"));
        Assert.DoesNotContain(writer.RowWrites, w => w.Text.StartsWith("thinkingfinal"));
    }

    [Fact]
    public void AddMessage_ClosesOpenStream_SubsequentAppendsIgnored()
    {
        var pane = new TranscriptPane();
        pane.BeginStream();
        pane.AppendStream("partial");
        pane.AddMessage("Error [X]: failed");
        pane.AppendStream("-leak");

        var writer = new FakeWriter();
        pane.Render(writer, top: 0, height: 10, width: 40);

        Assert.Contains("partial", writer.AllText);
        Assert.Contains("Error [X]: failed", writer.AllText);
        Assert.DoesNotContain("leak", writer.AllText);
    }
}
