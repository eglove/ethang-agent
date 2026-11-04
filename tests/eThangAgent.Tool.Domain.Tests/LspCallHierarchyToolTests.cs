using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.ToolDomain.Tests;

public class LspCallHierarchyToolTests
{
  private sealed class ScriptedSymbolAccess : ILspSymbolAccess
  {
    public LspSymbolOutcome Next { get; set; } = new LspSymbolOutcome.Ready([]);
    public List<LspSymbolCommand> Commands { get; } = [];

    public Task<LspSymbolOutcome> ExecuteAsync(LspSymbolCommand command, CancellationToken ct = default)
    {
      Commands.Add(command);
      return Task.FromResult(Next);
    }
  }

  private static string Args(string inner)
  {
    string q = ((char)34).ToString();
    return "{" + q + "timeoutSeconds" + q + ":120," + inner + "}";
  }

  private static async Task<ToolResult> Call(ScriptedSymbolAccess access, string inner)
  {
    LspSymbolTool tool = new(new WorkspacePathResolver("C:\\ws"), access);
    return await tool.ExecuteAsync(new RawToolInput("lsp", Args(inner)), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  private static string J(string key, string value)
  {
    string q = ((char)34).ToString();
    return q + key + q + ":" + q + value + q;
  }

  [Fact]
  public async Task CallHierarchyIncoming_Dispatches_Command()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 10, 2, "Caller")]) };
    string inner = J("action", "call-hierarchy-incoming") + "," + J("path", "A.cs") + "," + J("symbolName", "Foo");
    ToolResult r = await Call(access, inner);
    Assert.False(r.IsError);
    LspSymbolCommand.CallHierarchyIncoming cmd = Assert.IsType<LspSymbolCommand.CallHierarchyIncoming>(Assert.Single(access.Commands));
    Assert.Equal(1, cmd.Depth);
    Assert.Contains("Ready", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CallHierarchyOutgoing_DepthTwo_Dispatches()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Ready([new LspSymbolSite("C:\\ws\\A.cs", 20, 1, "Callee")]) };
    string q = ((char)34).ToString();
    string inner = J("action", "call-hierarchy-outgoing") + "," + J("path", "A.cs") + "," + J("symbolName", "Foo") + "," + q + "depth" + q + ":2";
    ToolResult r = await Call(access, inner);
    Assert.False(r.IsError);
    LspSymbolCommand.CallHierarchyOutgoing cmd = Assert.IsType<LspSymbolCommand.CallHierarchyOutgoing>(Assert.Single(access.Commands));
    Assert.Equal(2, cmd.Depth);
  }

  [Fact]
  public async Task CallHierarchy_Unsupported_RendersLabel()
  {
    ScriptedSymbolAccess access = new() { Next = new LspSymbolOutcome.Unsupported("capability not advertised for callHierarchy/prepare") };
    string inner = J("action", "call-hierarchy-incoming") + "," + J("path", "A.cs") + "," + J("symbolName", "Foo");
    ToolResult r = await Call(access, inner);
    Assert.False(r.IsError);
    Assert.Contains("Unsupported", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CallHierarchy_DepthThree_Fails()
  {
    string q = ((char)34).ToString();
    string inner = J("action", "call-hierarchy-incoming") + "," + J("path", "A.cs") + "," + J("symbolName", "Foo") + "," + q + "depth" + q + ":3";
    ToolResult r = await Call(new ScriptedSymbolAccess(), inner);
    Assert.True(r.IsError);
    Assert.Contains("depth", r.Content, StringComparison.Ordinal);
  }
}
