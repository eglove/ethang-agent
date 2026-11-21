using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The 'mcp' tool's configuration actions: strict parsing of add, update, and
///     remove (per-action key sets, typed collections, exact enum spellings), the
///     rendered success lines, and the full-reach grant gate that keeps a scoped agent
///     from widening its own reach by configuring a server.</summary>
public class McpToolManagementTests
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

  private sealed class FakeScope(bool canManage) : IMcpGrantScope
  {
    public string? RefusalFor(string resolvedId)
    {
      ArgumentNullException.ThrowIfNull(resolvedId);
      return null;
    }

    public bool Reachable(string serverName)
    {
      ArgumentNullException.ThrowIfNull(serverName);
      return true;
    }

    public bool CanManage => canManage;
  }

  private static async Task<ToolResult> Call(IMcpServerAccess access, string inner,
      IMcpGrantScope? scope = null)
  {
    McpTool tool = new(access, scope);
    return await tool.ExecuteAsync(new RawToolInput("mcp",
        "{" + "\"timeoutSeconds\":120," + inner + "}"),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  private const string AddBody =
      "\"action\":\"add\",\"server\":\"demo\",\"command\":\"npx demo\"";

  // ---- add: parsing ----

  [Fact]
  public async Task Add_With_Name_And_Command_Defaults_The_Rest()
  {
    ScriptedAccess access = new()
    {
      Next = new McpOutcome.Added("demo", McpServerScope.Global,
        McpApprovalState.Approved, [new McpToolInfo("echo", null)])
    };

    ToolResult result = await Call(access, AddBody).ConfigureAwait(true);

    McpCommand.AddServer add = Assert.IsType<McpCommand.AddServer>(Assert.Single(access.Commands));
    Assert.Equal("demo", add.Spec.Name);
    Assert.Equal(McpTransport.Stdio, add.Spec.Transport);
    Assert.Equal("npx demo", add.Spec.CommandOrUrl);
    Assert.Empty(add.Spec.Args);
    Assert.Empty(add.Spec.Env);
    Assert.Empty(add.Spec.Headers);
    Assert.Equal(McpServerScope.Global, add.Spec.Scope);
    Assert.Null(add.Spec.PinnedVersion);
    Assert.Equal(McpGateMode.None, add.Spec.GateMode);
    Assert.False(result.IsError);
  }

  [Fact]
  public async Task Add_Carries_Every_Supplied_Field()
  {
    ScriptedAccess access = new()
    {
      Next = new McpOutcome.Added("demo", McpServerScope.Workspace,
        McpApprovalState.Approved, [])
    };
    const string body = "\"action\":\"add\",\"server\":\"demo\",\"transport\":\"http\","
        + "\"command\":\"https://example.test/mcp\",\"args\":[\"--flag\",\"x\"],"
        + "\"env\":{\"TOKEN\":\"abc\"},\"headers\":{\"X-Key\":\"k\"},"
        + "\"scope\":\"workspace\",\"pinnedVersion\":\"1.2.3\",\"gate\":\"mutating\"";

    _ = await Call(access, body).ConfigureAwait(true);

    McpServerSpec spec = Assert.IsType<McpCommand.AddServer>(Assert.Single(access.Commands)).Spec;
    Assert.Equal(McpTransport.Http, spec.Transport);
    Assert.Equal(["--flag", "x"], spec.Args);
    Assert.Equal("abc", spec.Env["TOKEN"]);
    Assert.Equal("k", spec.Headers["X-Key"]);
    Assert.Equal(McpServerScope.Workspace, spec.Scope);
    Assert.Equal("1.2.3", spec.PinnedVersion);
    Assert.Equal(McpGateMode.Mutating, spec.GateMode);
  }

  [Fact]
  public async Task Add_Without_Command_Fails_MissingParameter()
  {
    ToolResult result = await Call(new ScriptedAccess(),
        "\"action\":\"add\",\"server\":\"demo\"").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [MissingParameter]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("command", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_With_An_Unknown_Transport_Fails_InvalidParameterValue()
  {
    ToolResult result = await Call(new ScriptedAccess(),
        AddBody + ",\"transport\":\"sse\"").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidParameterValue]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("stdio, http", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_With_Non_String_Env_Values_Fails_InvalidParameterType()
  {
    ToolResult result = await Call(new ScriptedAccess(),
        AddBody + ",\"env\":{\"PORT\":8080}").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [InvalidParameterType]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("env.PORT", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_With_An_Http_Endpoint_That_Is_Not_A_Url_Fails()
  {
    ToolResult result = await Call(new ScriptedAccess(),
        "\"action\":\"add\",\"server\":\"demo\",\"transport\":\"http\",\"command\":\"not a url\"")
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("absolute URL", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_Rejects_A_Key_The_Action_Does_Not_Declare()
  {
    ToolResult result = await Call(new ScriptedAccess(),
        AddBody + ",\"tool\":\"echo\"").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [UnknownParameter]:", result.Content, StringComparison.Ordinal);
  }

  // ---- update: parsing ----

  [Fact]
  public async Task Update_Carries_Only_The_Supplied_Fields()
  {
    ScriptedAccess access = new()
    {
      Next = new McpOutcome.Updated("demo", McpServerScope.Global,
        McpApprovalState.Approved, [])
    };

    _ = await Call(access, "\"action\":\"update\",\"server\":\"demo\",\"name\":\"renamed\"")
        .ConfigureAwait(true);

    McpCommand.UpdateServer update = Assert.IsType<McpCommand.UpdateServer>(Assert.Single(access.Commands));
    Assert.Equal("demo", update.Server);
    Assert.Equal("renamed", update.Edit.Name);
    Assert.Null(update.Edit.Transport);
    Assert.Null(update.Edit.CommandOrUrl);
    Assert.Null(update.Edit.Args);
    Assert.Null(update.Edit.Scope);
    Assert.Null(update.Edit.GateMode);
  }

  [Fact]
  public async Task Update_With_No_Fields_To_Change_Fails_MissingParameter()
  {
    ToolResult result = await Call(new ScriptedAccess(),
        "\"action\":\"update\",\"server\":\"demo\"").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [MissingParameter]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("at least one field", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Update_With_A_Blank_Pinned_Version_Clears_The_Pin()
  {
    ScriptedAccess access = new()
    {
      Next = new McpOutcome.Updated("demo", McpServerScope.Global,
        McpApprovalState.Approved, [])
    };

    _ = await Call(access, "\"action\":\"update\",\"server\":\"demo\",\"pinnedVersion\":\"\"")
        .ConfigureAwait(true);

    McpServerEdit edit = Assert.IsType<McpCommand.UpdateServer>(Assert.Single(access.Commands)).Edit;
    Assert.Equal(string.Empty, edit.PinnedVersion);
    Assert.False(edit.IsEmpty);
  }

  [Fact]
  public async Task Update_Rejects_A_Key_The_Action_Does_Not_Declare()
  {
    ToolResult result = await Call(new ScriptedAccess(),
        "\"action\":\"update\",\"server\":\"demo\",\"arguments\":{}").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [UnknownParameter]:", result.Content, StringComparison.Ordinal);
  }

  // ---- remove: parsing ----

  [Fact]
  public async Task Remove_Requires_The_Server_Name()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Removed("demo") };

    _ = await Call(access, "\"action\":\"remove\",\"server\":\"demo\"").ConfigureAwait(true);

    McpCommand.RemoveServer remove = Assert.IsType<McpCommand.RemoveServer>(Assert.Single(access.Commands));
    Assert.Equal("demo", remove.Server);
  }

  [Fact]
  public async Task Remove_Without_A_Server_Fails_MissingParameter()
  {
    ToolResult result = await Call(new ScriptedAccess(), "\"action\":\"remove\"").ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [MissingParameter]:", result.Content, StringComparison.Ordinal);
  }

  // ---- rendering ----

  [Fact]
  public async Task Add_Renders_The_Verified_Line()
  {
    ScriptedAccess access = new()
    {
      Next = new McpOutcome.Added("demo", McpServerScope.Global,
        McpApprovalState.Approved, [new McpToolInfo("echo", null), new McpToolInfo("ping", null)])
    };

    ToolResult result = await Call(access, AddBody).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal("[mcp] added demo | approved | global | verified, 2 tool(s): echo, ping", result.Content);
  }

  [Fact]
  public async Task Update_Renders_The_Updated_Line_With_The_Scope()
  {
    ScriptedAccess access = new()
    {
      Next = new McpOutcome.Updated("demo", McpServerScope.Workspace,
        McpApprovalState.Pending, [new McpToolInfo("echo", null)])
    };

    ToolResult result = await Call(access,
        "\"action\":\"update\",\"server\":\"demo\",\"command\":\"npx other\"")
        .ConfigureAwait(true);

    Assert.Equal("[mcp] updated demo | pending | workspace | verified, 1 tool(s): echo", result.Content);
  }

  [Fact]
  public async Task Remove_Renders_The_Removed_Line()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Removed("demo") };

    ToolResult result = await Call(access, "\"action\":\"remove\",\"server\":\"demo\"")
        .ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal("[mcp] removed demo", result.Content);
  }

  [Fact]
  public async Task List_Renders_Scope_And_Gate()
  {
    ScriptedAccess access = new()
    {
      Next = new McpOutcome.Status([new McpServerStatus("demo", McpApprovalState.Approved,
          McpTransport.Http, "https://x", McpConnectionState.NotConnected, [], null, null,
          McpServerScope.Workspace, McpGateMode.Mutating)]),
    };

    ToolResult result = await Call(access, "\"action\":\"list\"").ConfigureAwait(true);

    Assert.Contains("demo | approved | http | workspace | mutating | not connected",
        result.Content, StringComparison.Ordinal);
  }

  // ---- the full-reach gate ----

  [Theory]
  [InlineData(AddBody)]
  [InlineData("\"action\":\"update\",\"server\":\"demo\",\"name\":\"x\"")]
  [InlineData("\"action\":\"remove\",\"server\":\"demo\"")]
  public async Task Management_Without_Full_Reach_Fails_GrantViolation(string inner)
  {
    ScriptedAccess access = new();

    ToolResult result = await Call(access, inner, new FakeScope(canManage: false)).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Error [GrantViolation]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("full MCP reach", result.Content, StringComparison.Ordinal);
    Assert.Empty(access.Commands);
  }

  [Fact]
  public async Task Management_With_Full_Reach_Dispatches()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Removed("demo") };

    ToolResult result = await Call(access, "\"action\":\"remove\",\"server\":\"demo\"",
        new FakeScope(canManage: true)).ConfigureAwait(true);

    Assert.False(result.IsError);
    _ = Assert.IsType<McpCommand.RemoveServer>(Assert.Single(access.Commands));
  }

  [Fact]
  public async Task List_Is_Not_Gated()
  {
    ScriptedAccess access = new() { Next = new McpOutcome.Status([]) };

    ToolResult result = await Call(access, "\"action\":\"list\"", new FakeScope(canManage: false))
        .ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Equal("[mcp] 0 server(s) configured", result.Content);
  }

  // ---- the advertised contract ----

  [Fact]
  public void Definition_Documents_The_Management_Actions()
  {
    string description = new McpTool(new ScriptedAccess()).Definition.Description;

    foreach (string action in new[] { "add", "update", "remove" })
    {
      Assert.Contains(action, description, StringComparison.Ordinal);
    }

    foreach (string key in new[] { "name", "transport", "command", "args", "env", "headers",
                 "scope", "pinnedVersion", "gate" })
    {
      Assert.Contains(key, description, StringComparison.Ordinal);
    }

    Assert.Contains("VERIFY the launch", description, StringComparison.Ordinal);
    Assert.Contains("[mcp] added <name> | <approval> | <scope> | verified", description,
        StringComparison.Ordinal);
    Assert.Contains("McpDuplicateName", description, StringComparison.Ordinal);
  }

  [Fact]
  public void Definition_Advertises_The_Management_Parameters()
  {
    IReadOnlyList<ToolParameter> parameters = new McpTool(new ScriptedAccess()).Definition.Parameters;
    foreach (string name in new[] { "name", "transport", "command", "args", "env", "headers",
                 "scope", "pinnedVersion", "gate" })
    {
      Assert.Contains(parameters, p => p.Name == name);
    }
  }
}
