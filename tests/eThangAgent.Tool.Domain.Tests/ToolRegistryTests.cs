namespace eThangAgent.ToolDomain.Tests;

public class ToolRegistryTests
{
  private static FakeTool MakeTool(string name) => new(new ToolDefinition(name, "desc", []));

  [Fact]
  public void Find_KnownName_ReturnsTool()
  {
    FakeTool tool = MakeTool("read");
    ToolRegistry registry = new([tool]);

    ITool? found = registry.Find("read");

    Assert.NotNull(found);
    Assert.Same(tool, found);
  }

  [Fact]
  public void Find_UnknownName_ReturnsNull()
  {
    ToolRegistry registry = new([MakeTool("read")]);

    ITool? found = registry.Find("nope");

    Assert.Null(found);
  }

  [Fact]
  public void Find_MatchesCaseSensitive()
  {
    ToolRegistry registry = new([MakeTool("read")]);

    ITool? found = registry.Find("READ");

    Assert.Null(found);
  }

  [Fact]
  public void Definitions_ReturnsAll()
  {
    FakeTool a = MakeTool("read");
    FakeTool b = MakeTool("grep");
    ToolRegistry registry = new([a, b]);

    IReadOnlyList<ToolDefinition> defs = registry.Definitions;

    Assert.Equal(2, defs.Count);
    Assert.Contains(defs, d => d.Name == "read");
    Assert.Contains(defs, d => d.Name == "grep");
  }

  [Fact]
  public void Registry_WithDuplicateName_ThrowsArgumentsException() => _ = Assert.Throws<ArgumentException>(() => new ToolRegistry([MakeTool("read"), MakeTool("read")]));

  private sealed class FakeTool(ToolDefinition def) : ITool
  {
    public ToolDefinition Definition { get; } = def;

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => throw new NotImplementedException();
  }
}
