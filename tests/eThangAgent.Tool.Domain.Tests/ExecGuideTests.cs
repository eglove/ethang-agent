using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecGuideTests
{
    [Fact]
    public void Guide_IsVersionedAndNonEmpty()
    {
        Assert.Equal("1.4", ExecGuide.Version);
        Assert.True(ExecGuide.Text.Length >= 500);
    }

    [Fact]
    public void Guide_DocumentsDurableState()
    {
        Assert.Contains("agent.spawn @{", ExecGuide.Text);
        Assert.Contains("Delegating subtasks", ExecGuide.Text);
        Assert.Contains("depth limit 3", ExecGuide.Text);
        Assert.Contains("state.set @{", ExecGuide.Text);
        Assert.Contains("state.verify @{}", ExecGuide.Text);
    }

    [Fact]
    public void Guide_TeachesNonBlockingDelegation_InOrder()
    {
        var section = DelegationSection();

        // (1) spawn returns immediately — never wait inside the spawn call.
        Assert.Contains("returns immediately with `id=<guid> status=running`", section);
        Assert.Contains("Never wait", section);
        // (2) continue useful work or fan out siblings.
        Assert.Contains("continue useful work", section);
        Assert.Contains("fan out siblings", section);
        // (3) poll agent.status between turns.
        Assert.Contains("agent.status @{", section);
        Assert.Contains("between turns", section);
        // (4) fetch agent.result — NotComplete = later, NotFound = wrong id.
        Assert.Contains("agent.result @{", section);
        Assert.Contains("`Error [NotComplete]`", section);
        Assert.Contains("try again later", section);
        Assert.Contains("`Error [NotFound]`", section);
        Assert.Contains("id is wrong", section);
        // (5) cap reached — retrieve pending results before spawning more.
        Assert.Contains("`Error [ConcurrencyCapReached]`", section);
        Assert.Contains("retrieve pending results before spawning more", section);
        // (6) depth limit unchanged.
        Assert.Contains("depth limit 3", section);

        var markers = new[]
        {
            IndexOf(section, "returns immediately"),
            IndexOf(section, "continue useful work"),
            IndexOf(section, "agent.status"),
            IndexOf(section, "agent.result"),
            IndexOf(section, "ConcurrencyCapReached"),
            IndexOf(section, "depth limit 3"),
        };
        Assert.All(markers, m => Assert.True(m >= 0, "teaching marker missing"));
        Assert.Equal(markers.OrderBy(m => m), markers);
    }

    private static string DelegationSection()
    {
        var start = ExecGuide.Text.IndexOf("### Delegating subtasks", StringComparison.Ordinal);
        var end = ExecGuide.Text.IndexOf("### Errors", StringComparison.Ordinal);
        Assert.True(start >= 0, "Delegating subtasks section missing");
        Assert.True(end > start, "Errors section missing after delegation section");
        return ExecGuide.Text[start..end];
    }

    private static int IndexOf(string text, string marker)
        => text.IndexOf(marker, StringComparison.Ordinal);

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
