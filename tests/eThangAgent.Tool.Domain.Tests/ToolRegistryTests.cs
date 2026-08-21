using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class ToolRegistryTests
{
    private static ITool MakeTool(string name) => new FakeTool(new ToolDefinition(name, "desc", []));

    [Fact]
    public void Find_KnownName_ReturnsTool()
    {
        var tool = MakeTool("read");
        var registry = new ToolRegistry([tool]);

        var found = registry.Find("read");

        Assert.NotNull(found);
        Assert.Same(tool, found);
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        var registry = new ToolRegistry([MakeTool("read")]);

        var found = registry.Find("nope");

        Assert.Null(found);
    }

    [Fact]
    public void Find_MatchesCaseSensitive()
    {
        var registry = new ToolRegistry([MakeTool("read")]);

        var found = registry.Find("READ");

        Assert.Null(found);
    }

    [Fact]
    public void Definitions_ReturnsAll()
    {
        var a = MakeTool("read");
        var b = MakeTool("grep");
        var registry = new ToolRegistry([a, b]);

        var defs = registry.Definitions;

        Assert.Equal(2, defs.Count);
        Assert.Contains(defs, d => d.Name == "read");
        Assert.Contains(defs, d => d.Name == "grep");
    }

    [Fact]
    public void Registry_WithDuplicateName_ThrowsArgumentsException()
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry([MakeTool("read"), MakeTool("read")]));
    }

    private sealed class FakeTool : ITool
    {
        public ToolDefinition Definition { get; }
        public FakeTool(ToolDefinition def) => Definition = def;
        public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
