using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class TranscriptPaneTests
{
    [Fact]
    public void RendersOnlyTheLastVisibleLines()
    {
        var pane = new TranscriptPane();
        foreach (var line in new[] { "l1", "l2", "l3", "l4", "l5" })
            pane.AddMessage(line);
        var writer = new FakeWriter();

        pane.Render(writer, top: 2, height: 3, width: 40);

        Assert.DoesNotContain("l1", writer.AllText);
        Assert.DoesNotContain("l2", writer.AllText);
        Assert.Contains("l3", writer.AllText);
        Assert.Contains("l4", writer.AllText);
        Assert.Contains("l5", writer.AllText);
    }

    [Fact]
    public void RendersEachVisibleRowAtItsOwnPosition()
    {
        var pane = new TranscriptPane();
        pane.AddMessage("l1");
        pane.AddMessage("l2");
        var writer = new FakeWriter();

        pane.Render(writer, top: 5, height: 10, width: 40);

        Assert.Contains((0, 5), writer.Moves);
        Assert.Contains((0, 6), writer.Moves);
    }

    [Fact]
    public void WrapsLongLinesToWidth()
    {
        var pane = new TranscriptPane();
        pane.AddMessage("hello world foo");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 5, width: 10);

        Assert.Contains(writer.Writes, w => w.Text.StartsWith("hello"));
        Assert.Contains(writer.Writes, w => w.Text.StartsWith("world"));
    }

    [Fact]
    public void MultiLineMessage_RendersEachSourceLine()
    {
        var pane = new TranscriptPane();
        pane.AddMessage("first\nsecond");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 5, width: 40);

        Assert.Contains(writer.Writes, w => w.Text.StartsWith("first"));
        Assert.Contains(writer.Writes, w => w.Text.StartsWith("second"));
    }

    [Fact]
    public void Rows_ArePaddedToFullWidthSoPreviousContentIsCleared()
    {
        var pane = new TranscriptPane();
        pane.AddMessage("hi");
        var writer = new FakeWriter();

        pane.Render(writer, top: 0, height: 2, width: 20);

        Assert.All(
            writer.Writes.Where(w => w.Text.Contains("hi") || w.Text.Length > 0),
            w => Assert.True(w.Text.Length <= 20));
        Assert.Contains(writer.Writes, w => w.Text.Length == 20);
    }
}
