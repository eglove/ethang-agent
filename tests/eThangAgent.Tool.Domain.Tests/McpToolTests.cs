using System.Text.Json;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the 'mcp' dispatch tool (issue #104): strict input
///     parsing, dispatch to the IMcpServerAccess seam, the verbatim listing output
///     contract, call passthrough, and the typed failure rendering. The seam is
///     scripted - the pool policy lives in McpServerAccessTests.</summary>
public class McpToolTests
{
  private sealed class ScriptedAccess : IMcpServerAccess
  {
    public McpOutcome Next { get; set; } = new McpOutcome.Status([]);
    public List<McpCommand> Commands { get; } = [];

    public Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default)
    {
      Commands.Add(command);
      return Task.FromResult(Next);
    }
  }

  private static string Args(string inner) =>
      "{" + "\"timeoutSeconds\":120" + (inner.Length == 0 ? "" : "," + inner) + "}";

  private static async Task<ToolResult> Call(ScriptedAccess access, string inner)
  {
    McpTool tool = new(access);
    return await tool.ExecuteAsync(new RawToolInput("mcp", Args(inner)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  private static McpOutcome.Status Report(params McpServerStatus[] servers) => new([.. servers]);

  private static McpServerStatus Connected(string name = "demo", params string[] tools) => new(
      name, McpApprovalState.Approved, McpTransport.Stdio, "npx demo",
      McpConnectionState.Connected,
      [.. tools.Select(t => new McpToolInfo(t, null))], null);

  // ---- definition contract ----

  [Fact]
  public void Definition_NameAndRequired()
  {
    McpTool tool = new(new ScriptedAccess());
    Assert.Equal("mcp", tool.Definition.Name);
    Assert.Equal(["timeoutSeconds", "action"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void Definition_DescriptionDocumentsActionsKeysAndErrorCodes()
  {
    string d = new McpTool(new ScriptedAccess()).Definition.Description;
    foreach (string a in new[] { "list", "call" })
    {
      Assert.Contains(a, d, StringComparison.Ordinal);
    }

    foreach (string k in new[] { "server", "tool", "arguments" })
    {
      Assert.Contains(k, d, StringComparison.Ordinal);
    }

    foreach (string code in new[] { "McpServerNotFound", "McpToolNotFound", "McpServerNotApproved",
             "McpCallGated", "GrantViolation", "McpConnectFailed", "McpCallFailed", "StorageUnavailable" })
    {
      Assert.Contains(code, d, StringComparison.Ordinal);
    }

    Assert.Contains("[mcp]", d, StringComparison.Ordinal);
    Assert.Contains("Error [Code]:", d, StringComparison.Ordinal);
    Assert.Contains("lazily", d, StringComparison.Ordinal);
    // Issues #108/#109: the scoped listing, the gate, and the stale-launch reconnect
    // are part of the contract the model reads.
    Assert.Contains("visible to this agent's grants", d, StringComparison.Ordinal);
    Assert.Contains("gates mutating calls", d, StringComparison.Ordinal);
    Assert.Contains("reconnects under the new launch", d, StringComparison.Ordinal);
  }

  // ---- strict parsing ----

  [Fact]
  public async Task MissingAction_Fails_InvalidAction()
  {
    ToolResult r = await Call(new ScriptedAccess(), "").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidAction]:", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownAction_Fails_InvalidAction()
  {
    ToolResult r = await Call(new ScriptedAccess(), "\"action\": \"invoke\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidAction]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("list, call", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CallWithoutServer_Fails_MissingParameter()
  {
    ToolResult r = await Call(new ScriptedAccess(), "\"action\": \"call\", \"tool\": \"echo\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]: Missing required parameter 'server'.", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CallWithoutTool_Fails_MissingParameter()
  {
    ToolResult r = await Call(new ScriptedAccess(), "\"action\": \"call\", \"server\": \"demo\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [MissingParameter]: Missing required parameter 'tool'.", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownKey_Fails_UnknownParameter()
  {
    ToolResult r = await Call(new ScriptedAccess(), "\"action\": \"list\", \"wat\": 1").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [UnknownParameter]:", r.Content, StringComparison.Ordinal);
    Assert.Contains("wat", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ArgumentsNotObject_Fails_ParameterType()
  {
    ToolResult r = await Call(new ScriptedAccess(),
        "\"action\": \"call\", \"server\": \"demo\", \"tool\": \"echo\", \"arguments\": \"x\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Contains("Error [InvalidParameterType]:", r.Content, StringComparison.Ordinal);
  }

  // ---- dispatch and rendering ----

  [Fact]
  public async Task List_Dispatches_ListCommand()
  {
    ScriptedAccess access = new() { Next = Report(Connected("demo", "echo")) };
    ToolResult r = await Call(access, "\"action\": \"list\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    McpCommand.ListServers listed = Assert.IsType<McpCommand.ListServers>(Assert.Single(access.Commands));
    Assert.NotNull(listed);
  }

  [Fact]
  public async Task List_Renders_StatusLines()
  {
    ScriptedAccess access = new()
    {
      Next = Report(
          Connected("demo", "echo", "ping"),
          new McpServerStatus("other", McpApprovalState.Pending, McpTransport.Http, "https://x",
              McpConnectionState.NotConnected, [], null))
    };
    ToolResult r = await Call(access, "\"action\": \"list\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("[mcp] 2 server(s) configured", r.Content, StringComparison.Ordinal);
    Assert.Contains("demo | approved | stdio | connected, 2 tool(s): echo, ping", r.Content, StringComparison.Ordinal);
    Assert.Contains("other | pending | http | not connected", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_ZeroServers_Renders_ZeroLine()
  {
    ScriptedAccess access = new() { Next = Report() };
    ToolResult r = await Call(access, "\"action\": \"list\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Equal("[mcp] 0 server(s) configured", r.Content);
  }

  [Fact]
  public async Task List_FailedServer_Shows_ErrorState()
  {
    ScriptedAccess access = new()
    {
      Next = Report(new McpServerStatus("demo", McpApprovalState.Approved, McpTransport.Stdio,
          "npx demo", McpConnectionState.Failed, [], "spawn failed"))
    };
    ToolResult r = await Call(access, "\"action\": \"list\"").ConfigureAwait(true);
    Assert.False(r.IsError);
    Assert.Contains("demo | approved | stdio | error: spawn failed", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Call_Passes_Server_Tool_Arguments()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Called("hi there", false) };
    ToolResult r = await Call(access,
        "\"action\": \"call\", \"server\": \"demo\", \"tool\": \"echo\", \"arguments\": {\"text\": \"hi there\"}")
        .ConfigureAwait(true);
    Assert.False(r.IsError);
    McpCommand.CallTool call = Assert.IsType<McpCommand.CallTool>(Assert.Single(access.Commands));
    Assert.Equal("demo", call.Server);
    Assert.Equal("echo", call.Tool);
    Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
    Assert.Equal("hi there", call.Arguments.GetProperty("text").GetString());
    Assert.Equal("hi there", r.Content);
  }

  [Fact]
  public async Task Call_WithoutArguments_Sends_EmptyObject()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Called("ok", false) };
    _ = await Call(access, "\"action\": \"call\", \"server\": \"demo\", \"tool\": \"echo\"").ConfigureAwait(true);
    McpCommand.CallTool call = Assert.IsType<McpCommand.CallTool>(Assert.Single(access.Commands));
    Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
    Assert.False(call.Arguments.EnumerateObject().MoveNext());
  }

  [Fact]
  public async Task Call_ServerError_Marks_Result_Error()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Called("boom: bad input", true) };
    ToolResult r = await Call(access, "\"action\": \"call\", \"server\": \"demo\", \"tool\": \"echo\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Equal("boom: bad input", r.Content);
  }

  [Fact]
  public async Task Failure_Renders_Typed_Error()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Failure("McpServerNotFound", "no server 'x'") };
    ToolResult r = await Call(access, "\"action\": \"call\", \"server\": \"x\", \"tool\": \"t\"").ConfigureAwait(true);
    Assert.True(r.IsError);
    Assert.Equal("Error [McpServerNotFound]: no server 'x'", r.Content);
  }
}
