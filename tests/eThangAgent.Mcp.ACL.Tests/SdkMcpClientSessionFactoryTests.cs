using System.ComponentModel;
using eThangAgent.ToolDomain.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace eThangAgent.Mcp.ACL.Tests;

/// <summary>End-to-end and option-builder tests for the SDK session factory
///     (issue #104). A REAL MCP server (the SDK's McpServer over in-memory duplex
///     streams) serves tools; the factory connects, lists, and calls through the
///     seam. The option builders pin the stdio environment allowlist (B3: no parent
///     credential variable reaches the server uninvited) and the HTTP static headers.</summary>
public class SdkMcpClientSessionFactoryTests
{
  private static McpServerConfig StdioConfig(
      string envJson = "{}", string argsJson = "[]", string name = "demo") => new(
      1, name, McpTransport.Stdio, "npx", argsJson, envJson, "{}", null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow);

  private static McpServerConfig HttpConfig(
      string headersJson = "{}", string url = "https://mcp.example.test/", string name = "remote") => new(
      2, name, McpTransport.Http, url, "[]", "{}", headersJson, null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow);

  // ---- option builders (pure functions over the config) ----

  [Fact]
  public void StdioOptions_Inheritance_Off()
  {
    StdioClientTransportOptions options = SdkMcpClientSessionFactory.BuildStdioOptions(StdioConfig());
    Assert.False(options.InheritEnvironmentVariables);
  }

  [Fact]
  public void StdioOptions_Carry_Command_And_Args()
  {
    StdioClientTransportOptions options = SdkMcpClientSessionFactory.BuildStdioOptions(
        StdioConfig(argsJson: "[" + "\"-y\",\"@demo/server\"]"));
    Assert.Equal("npx", options.Command);
    Assert.Equal(["-y", "@demo/server"], options.Arguments);
    Assert.Equal("demo", options.Name);
  }

  [Fact]
  public void StdioOptions_Environment_Is_Defaults_Plus_Configured()
  {
    StdioClientTransportOptions options = SdkMcpClientSessionFactory.BuildStdioOptions(
        StdioConfig(envJson: "{" + "\"DEMO_TOKEN\":\"abc\"}"));
    Assert.True(options.EnvironmentVariables!.ContainsKey("DEMO_TOKEN"));
    Assert.Equal("abc", options.EnvironmentVariables["DEMO_TOKEN"]);
    // The SDK default set is present (B3: the allowlist base).
    foreach (KeyValuePair<string, string?> kv in StdioClientTransportOptions.GetDefaultEnvironmentVariables())
    {
      Assert.True(options.EnvironmentVariables.ContainsKey(kv.Key),
          "missing SDK default env var: " + kv.Key);
    }
  }

  [Fact]
  public void StdioOptions_Never_Include_Parent_Only_Variables()
  {
    // A parent-only credential variable (simulated by the process env) must not
    // appear: the built dictionary is defaults + configured, nothing else.
    Environment.SetEnvironmentVariable("ETHANG_TEST_PARENT_SECRET", "leak");
    try
    {
      StdioClientTransportOptions options = SdkMcpClientSessionFactory.BuildStdioOptions(StdioConfig());
      Assert.False(options.EnvironmentVariables!.ContainsKey("ETHANG_TEST_PARENT_SECRET"));
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_TEST_PARENT_SECRET", null);
    }
  }

  [Fact]
  public void HttpOptions_Carry_Endpoint_And_Headers()
  {
    HttpClientTransportOptions options = SdkMcpClientSessionFactory.BuildHttpOptions(
        HttpConfig(headersJson: "{" + "\"Authorization\":\"Bearer t\"}"));
    Assert.Equal(new Uri("https://mcp.example.test/"), options.Endpoint);
    Assert.True(options.AdditionalHeaders!.ContainsKey("Authorization"));
    Assert.Equal("Bearer t", options.AdditionalHeaders["Authorization"]);
    Assert.Equal("remote", options.Name);
  }

  [Fact]
  public void CreateTransport_Picks_Transport_By_Config()
  {
    _ = Assert.IsType<StdioClientTransport>(
        SdkMcpClientSessionFactory.CreateTransport(StdioConfig()));
    _ = Assert.IsType<HttpClientTransport>(
        SdkMcpClientSessionFactory.CreateTransport(HttpConfig()));
  }

  // ---- end-to-end over the real SDK in-memory pair ----

  /// <summary>Connects through the factory with the transport swapped for an
  ///     in-memory pair served by a REAL one-tool MCP server.</summary>
  private static async Task<(IMcpClientSession Session, Func<Task> Dispose)> ConnectInMemoryAsync(
      string name, McpServerTool tool)
#pragma warning disable CA2000, S6966, CA1849 // named decision: the in-memory server outlives this helper through serverTask; trace writes are temporary instrumentation
  {
    File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcp-trace.log"), "pair created\n");
    (FullDuplexStream clientStream, FullDuplexStream serverStream) = FullDuplexStream.CreatePair();
    File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcp-trace.log"), "server options\n");
    McpServerOptions serverOptions = new()
    {
      ServerInfo = new Implementation { Name = name, Version = "1.0.0" },
      Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
      ToolCollection = [],
    };
    serverOptions.ToolCollection.Add(tool);
    File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcp-trace.log"), "tool added\n");
    StreamServerTransport serverTransport = new(serverStream, serverStream);
    McpServer server = McpServer.Create(serverTransport, serverOptions);
    using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(30));
    Task serverTask = server.RunAsync(serverCts.Token);
    File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcp-trace.log"), "server task started\n");

    using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(15));
    File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcp-trace.log"), "factory call\n");
    McpConnectResult result = await SdkMcpClientSessionFactory.ConnectAsync(
        StdioConfig(name: name), connectCts.Token,
        transportOverride: clientStream).ConfigureAwait(true);
    File.AppendAllText(Path.Combine(Path.GetTempPath(), "mcp-trace.log"), "factory returned: " + (result is McpConnectResult.Success ? "success" : "failure") + "\n");
    if (result is McpConnectResult.Failure failure)
    {
      throw new InvalidOperationException("connect failed: " + failure.Message);
    }

    IMcpClientSession session = ((McpConnectResult.Success)result).Session;
    return (session, async () =>
    {
      await session.DisposeAsync().ConfigureAwait(true);
      try
      {
        await serverTask.ConfigureAwait(true);
      }
      catch (OperationCanceledException)
      {
        // Named decision: server shutdown is cancellation by design.
      }
    }
    );
  }
#pragma warning restore CA2000, S6966, CA1849

  [Fact]
#pragma warning disable S6966, CA1849, xUnit1051, CA1031 // temporary trace + bounded teardown instrumentation
  public async Task Connect_List_Call_Over_Real_Sdk_Pair()
  {
    (IMcpClientSession session, Func<Task> dispose) = await ConnectInMemoryAsync("demo",
        McpServerTool.Create(([Description("Echoes the text back.")] string text) => "echo: " + text,
            new McpServerToolCreateOptions { Name = "echo", Description = "Echoes the text back." }))
        .ConfigureAwait(true);
    try
    {
      using CancellationTokenSource listCts = new(TimeSpan.FromSeconds(10));
      IReadOnlyList<McpToolInfo> tools = await session.ListToolsAsync(listCts.Token)
          .ConfigureAwait(true);
      _ = Assert.Single(tools);
      Assert.Equal("echo", tools[0].Name);
      Assert.NotNull(tools[0].Description);
      Assert.Contains("Echoes", tools[0].Description, StringComparison.Ordinal);

      using CancellationTokenSource callCts = new(TimeSpan.FromSeconds(10));
      McpToolCallResult call = await session
          .CallToolAsync("echo", "{" + "\"text\":\"hi\"}", callCts.Token)
          .ConfigureAwait(true);
      Assert.False(call.IsError);
      Assert.Contains("echo: hi", call.Content, StringComparison.Ordinal);
    }
    finally
    {
      // Bounded teardown on a worker thread: Cancel() can block on a registered callback,
      // so the whole teardown runs detached and the test never hangs on it.
      _ = Task.Run(async () =>
      {
        try
        {
          await dispose().ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch
        {
          // Named decision: a teardown fault is irrelevant once the assertions have run.
        }
#pragma warning restore CA1031
      });
      await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
    }
  }
#pragma warning restore S6966, CA1849, xUnit1051, CA1031

  [Fact]
#pragma warning disable S6966, CA1849, xUnit1051, CA1031 // bounded teardown instrumentation
  public async Task Call_IsError_Flag_Passes_Through()
  {
    (IMcpClientSession session, Func<Task> dispose) = await ConnectInMemoryAsync("boom",
        McpServerTool.Create([Description("Always fails.")] (string text) =>
        {
          throw new InvalidOperationException("boom: " + text);
        }, new McpServerToolCreateOptions { Name = "fail", Description = "Always fails." }))
        .ConfigureAwait(true);
    try
    {
      McpToolCallResult call = await session
          .CallToolAsync("fail", "{" + "\"text\":\"x\"}", TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      // The SDK renders a throwing tool as IsError with its own message; the flag is
      // the contract this test pins (the exception text is the SDK's wording).
      Assert.True(call.IsError);
      Assert.Contains("error", call.Content, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      // Named decision (CA1031): bounded teardown on a worker thread - see the first
      // e2e test's finally for the rationale (the in-memory server's Cancel() can
      // block on a registered callback).
      _ = Task.Run(async () =>
      {
        try
        {
          await dispose().ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch
        {
          // Named decision: a teardown fault is irrelevant once the assertions have run.
        }
#pragma warning restore CA1031
      });
      await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
    }
  }
#pragma warning restore S6966, CA1849, xUnit1051, CA1031
}
