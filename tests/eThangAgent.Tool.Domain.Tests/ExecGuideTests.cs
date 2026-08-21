using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecGuideTests
{
    [Fact]
    public void Guide_IsVersionedAndNonEmpty()
    {
        Assert.Equal("1.1", ExecGuide.Version);
        Assert.True(ExecGuide.Text.Length >= 500);
    }

    [Fact]
    public void Guide_DocumentsIntrospection()
    {
        Assert.Contains("Get-AgentAction", ExecGuide.Text);
        Assert.Contains("Get-AgentProvider", ExecGuide.Text);
    }

    [Fact]
    public void Guide_DocumentsCoreCallPatterns()
    {
        Assert.Contains("read @{", ExecGuide.Text);
        Assert.Contains("Invoke-AgentTool", ExecGuide.Text);
        Assert.Contains("Get-AgentTool", ExecGuide.Text);
        Assert.Contains("try/catch", ExecGuide.Text);
        Assert.Contains("[exec:artifact", ExecGuide.Text);
    }
}
