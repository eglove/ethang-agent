using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Dispatch-time grant binding (issue #108): the filtered registry sees the
///     dispatch tool (mcp), so the grant scope re-checks the RESOLVED id
///     (mcp.server.tool) per dispatch. A miss renders the verbatim GrantViolation
///     contract; no scope wired means the legacy full-reach behavior, byte-identical.</summary>
public class McpToolGrantScopeTests
{
  private sealed class ScriptedAccess : IMcpServerAccess
  {
    public McpOutcome Next { get; set; } = new McpOutcome.Called("server says hi", false);
    public List<McpCommand> Commands { get; } = [];

    public Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default)
    {
      Commands.Add(command);
      return Task.FromResult(Next);
    }
  }

  private sealed class FakeScope(string? allowed) : IMcpGrantScope
  {
    private readonly HashSet<string> _entries = allowed is null ? [] : [.. allowed.Split(';')];

    public string? RefusalFor(string resolvedId)
    {
      foreach (string entry in _entries)
      {
        bool covered = entry.EndsWith(".*", StringComparison.Ordinal)
            ? resolvedId.StartsWith(entry[..^2] + ".", StringComparison.Ordinal)
            : resolvedId == entry;
        if (covered)
        {
          return null;
        }
      }

      return GrantViolation.For(resolvedId);
    }

    public bool Reachable(string serverName)
    {
      string id = "mcp." + serverName;
      return _entries.Any(entry => entry.EndsWith(".*", StringComparison.Ordinal)
          ? (id == entry[..^2] || id.StartsWith(entry[..^2] + ".", StringComparison.Ordinal))
          : entry == "mcp" || entry == id);
    }
  }

  private static async Task<ToolResult> Call(IMcpServerAccess access, IMcpGrantScope? scope, string inner)
  {
    McpTool tool = new(access, scope);
    return await tool.ExecuteAsync(new RawToolInput("mcp",
        "{" + "\"timeoutSeconds\":120," + inner + "}"), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  private static McpOutcome.Status TwoServers() => new(
  [
    new McpServerStatus("github", McpApprovalState.Approved, McpTransport.Stdio, "x",
        McpConnectionState.NotConnected, [], null),
    new McpServerStatus("gitlab", McpApprovalState.Approved, McpTransport.Stdio, "y",
        McpConnectionState.NotConnected, [], null),
  ]);

  [Fact]
  public async Task Call_WithinScopePattern_Executes()
  {
    ScriptedAccess access = new();
    ToolResult result = await Call(access, new FakeScope("mcp.github.*"),
        "\"action\":\"call\",\"server\":\"github\",\"tool\":\"create_issue\"").ConfigureAwait(true);

    Assert.False(result.IsError);
    McpCommand.CallTool call = Assert.IsType<McpCommand.CallTool>(access.Commands.Single());
    Assert.Equal("github", call.Server);
  }

  [Fact]
  public async Task Call_OutsideScope_ReturnsGrantViolation_AndNeverDispatches()
  {
    ScriptedAccess access = new();
    ToolResult result = await Call(access, new FakeScope("mcp.github.*"),
        "\"action\":\"call\",\"server\":\"gitlab\",\"tool\":\"push\"").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Equal("Error [GrantViolation]: tool 'mcp.gitlab.push' is not granted to this agent.", result.Content);
    Assert.Empty(access.Commands);
  }

  [Fact]
  public async Task Call_ExactIdScope_RefusesUnlistedToolOnSameServer()
  {
    // The scope holds one exact tool id; another tool on the same server is refused.
    ScriptedAccess access = new();
    ToolResult result = await Call(access, new FakeScope("mcp.github.create_issue"),
        "\"action\":\"call\",\"server\":\"github\",\"tool\":\"delete_repo\"").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("GrantViolation", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Call_NoScopeWired_BehavesAsBefore()
  {
    ScriptedAccess access = new();
    ToolResult result = await Call(access, null,
        "\"action\":\"call\",\"server\":\"anything\",\"tool\":\"go\"").ConfigureAwait(true);

    Assert.False(result.IsError);
  }

  [Fact]
  public async Task List_WithScope_FiltersToReachableServers()
  {
    ScriptedAccess access = new() { Next = TwoServers() };
    ToolResult result = await Call(access, new FakeScope("mcp.github.*"), "\"action\":\"list\"").ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("github", result.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("gitlab", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_NoScope_ShowsEveryServer()
  {
    ScriptedAccess access = new() { Next = TwoServers() };
    ToolResult result = await Call(access, null, "\"action\":\"list\"").ConfigureAwait(true);

    Assert.Contains("gitlab", result.Content, StringComparison.Ordinal);
  }
}
