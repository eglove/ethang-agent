using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class TuiLayoutTests
{
    [Fact]
    public void Compute_SplitsRowsIntoTranscriptInputAndStatus()
    {
        var layout = TuiLayout.Compute(24);

        Assert.Equal(0, layout.TranscriptTop);
        Assert.Equal(22, layout.TranscriptHeight);
        Assert.Equal(22, layout.InputRow);
        Assert.Equal(23, layout.StatusRow);
    }

    [Fact]
    public void Compute_ClampsTranscriptToAtLeastOneRow()
    {
        var layout = TuiLayout.Compute(3);

        Assert.Equal(1, layout.TranscriptHeight);
        Assert.Equal(1, layout.InputRow);
        Assert.Equal(2, layout.StatusRow);
    }
}
