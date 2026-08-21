using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class StatusLineTests
{
    [Fact]
    public void RendersModelMessageCountAndState()
    {
        var writer = new FakeWriter();
        var status = new StatusLine();

        status.Render(writer, row: 5, width: 80, "stealth/ox-alpha", 3, "Ready");

        Assert.Contains("stealth/ox-alpha", writer.AllText);
        Assert.Contains("3 msgs", writer.AllText);
        Assert.Contains("Ready", writer.AllText);
    }

    [Fact]
    public void Row_IsPaddedToFullWidth()
    {
        var writer = new FakeWriter();
        var status = new StatusLine();

        status.Render(writer, row: 0, width: 40, "m", 0, "Ready");

        Assert.Contains(writer.Writes, w => w.Text.Length == 40);
    }

    [Fact]
    public void OverlongContent_IsTruncatedToWidth()
    {
        var writer = new FakeWriter();
        var status = new StatusLine();

        status.Render(writer, row: 0, width: 20, new string('m', 100), 0, "Ready");

        Assert.All(writer.Writes, w => Assert.True(w.Text.Length <= 20));
    }
}
