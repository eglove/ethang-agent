using System.Diagnostics;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Mcp.ACL.Tests;

/// <summary>Stdio containment tests (issue #105): a REAL stub MCP server process
///     (tests/StubServer) pins the three containment measures - the environment
///     allowlist end to end (B1), kill-on-close Job Object reaping (B2), and stderr
///     capture (B3) - plus the structured reconnect when a server dies mid-session
///     (B4). The factory self-spawns the process (the SDK offers no process-started
///     hook for Job Object attachment) and hands the process streams to the SDK's
///     StreamClientTransport.</summary>
public class StdioContainmentTests
{
  private static string StubExe => StubServerBuilder.Build();

  private static McpServerConfig StdioConfig(
      string envJson = "{}", string argsJson = "[]", string name = "stub") => new(
      1, name, McpTransport.Stdio, StubExe, argsJson, envJson, "{}", null,
      McpApprovalState.Approved, null, DateTimeOffset.UtcNow);

  // ---- B1: the environment allowlist, end to end ----

  [Fact]
  public async Task Spawned_Server_Sees_Only_Allowlisted_Environment()
  {
    // A parent-only decoy variable is seeded into THIS process; the stub's
    // dump_env tool must not see it, but must see the configured entry.
    Environment.SetEnvironmentVariable("ETHANG_MCP_DECOY_SECRET", "leak");
    SdkMcpClientSessionFactory factory = new();
    try
    {
      McpConnectResult result = await factory.ConnectAsync(
          StdioConfig(envJson: "{" + "\"ETHANG_MCP_CONFIGURED\":\"present\"}"),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      ContainedStdioSession session = AssertSuccess(result);
      try
      {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        McpToolCallResult env = await session
            .CallToolAsync("dump_env", "{}", cts.Token).ConfigureAwait(true);
        Assert.False(env.IsError);
        Assert.DoesNotContain("ETHANG_MCP_DECOY_SECRET", env.Content, StringComparison.Ordinal);
        Assert.Contains("ETHANG_MCP_CONFIGURED", env.Content, StringComparison.Ordinal);
      }
      finally
      {
        await session.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      Environment.SetEnvironmentVariable("ETHANG_MCP_DECOY_SECRET", null);
      await factory.DisposeAsync().ConfigureAwait(true);
    }
  }

  // ---- B2: kill-on-close Job Object ----

  [Fact]
  public async Task Dispose_Reaps_The_Server_Process()
  {
    SdkMcpClientSessionFactory factory = new();
    try
    {
      McpConnectResult result = await factory.ConnectAsync(
          StdioConfig(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      ContainedStdioSession session = AssertSuccess(result);
      Process server = session.TestProcess();
      await session.DisposeAsync().ConfigureAwait(true);
      Task exit = server.WaitForExitAsync(TestContext.Current.CancellationToken);
      bool reaped = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
      Assert.True(reaped || server.HasExited, "the server process must be reaped on dispose");
      server.Dispose();
    }
    finally
    {
      await factory.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task Closing_The_Job_Reaps_The_Server_Process()
  {
    // B2's kernel path: closing the kill-on-close job handle - what the OS does
    // when the harness process dies - reaps the server. The guard's test seam
    // closes ONLY the job, proving the job itself (not the explicit kill) did it.
    SdkMcpClientSessionFactory factory = new();
    try
    {
      McpConnectResult result = await factory.ConnectAsync(
          StdioConfig(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      ContainedStdioSession session = AssertSuccess(result);
      Process server = session.TestProcess();
      session.TestCloseJob();
      Task exit = server.WaitForExitAsync(TestContext.Current.CancellationToken);
      bool reaped = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
      Assert.True(reaped || server.HasExited, "closing the job must reap the server");
      server.Dispose();
    }
    finally
    {
      await factory.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task Failed_Handshake_Reaps_The_Spawned_Process()
  {
    // A command that runs but never speaks MCP: the connect handshake fails, and
    // the guard must reap the alive process it spawned - no orphan survives a
    // failed connect.
    SdkMcpClientSessionFactory factory = new();
    List<Process> spawned = [];
    factory.TestServerProcessSpawned = spawned.Add;
    try
    {
      McpServerConfig idle = new(2, "idle", McpTransport.Stdio, StubExe,
          "[" + "\"--idle\"]", "{}", "{}", null,
          McpApprovalState.Approved, null, DateTimeOffset.UtcNow);
      McpConnectResult result = await factory.ConnectAsync(
          idle, TestContext.Current.CancellationToken).ConfigureAwait(true);
      _ = Assert.IsType<McpConnectResult.Failure>(result);
      Process server = Assert.Single(spawned);
      Task exit = server.WaitForExitAsync(TestContext.Current.CancellationToken);
      bool reaped = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
      Assert.True(reaped || server.HasExited, "a failed handshake must reap the spawned process");
      server.Dispose();
    }
    finally
    {
      await factory.DisposeAsync().ConfigureAwait(true);
    }
  }

  // ---- B3: stderr capture ----

  [Fact]
  public async Task Server_Stderr_Is_Captured_And_Tool_Result_Stays_Clean()
  {
    SdkMcpClientSessionFactory factory = new();
    try
    {
      McpConnectResult result = await factory.ConnectAsync(
          StdioConfig(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      ContainedStdioSession session = AssertSuccess(result);
      try
      {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
        McpToolCallResult call = await session
            .CallToolAsync("chatty", "{" + "\"text\":\"hi\"}", cts.Token).ConfigureAwait(true);
        // The tool result carries ONLY the protocol response (B3).
        Assert.False(call.IsError);
        Assert.Contains("chatty: hi", call.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("stub-stderr-line", call.Content, StringComparison.Ordinal);

        // The stderr lines land in the captured tail.
        string stderr = session.StderrTail;
        Assert.Contains("stub-stderr-line-1: hi", stderr, StringComparison.Ordinal);
        Assert.Contains("stub-stderr-line-2", stderr, StringComparison.Ordinal);
      }
      finally
      {
        await session.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      await factory.DisposeAsync().ConfigureAwait(true);
    }
  }

  // ---- B4: a server that dies mid-session reconnects on the next dispatch ----

  [Fact]
  public async Task Crashed_Server_Is_Structured_Reconnect_On_Next_Dispatch()
  {
    SdkMcpClientSessionFactory factory = new();
    try
    {
      McpConnectResult first = await factory.ConnectAsync(
          StdioConfig(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      ContainedStdioSession session = AssertSuccess(first);
      using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));

      // The stub's crash tool exits the process mid-session; the call itself
      // fails in whatever shape the transport chooses - the CONTRACT is the
      // next dispatch, never the failure shape of the dying call.
      try
      {
        McpToolCallResult crashed = await session
            .CallToolAsync("crash", "{}", cts.Token).ConfigureAwait(true);
        _ = crashed;
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        // Named decision: a dying transport may surface as a value or an
        // exception; either is acceptable, the reconnect is the contract.
      }

      await session.WaitForExitAsync(cts.Token).ConfigureAwait(true);
      Assert.True(session.HasExited);
      await session.DisposeAsync().ConfigureAwait(true);

      McpConnectResult second = await factory.ConnectAsync(
          StdioConfig(), TestContext.Current.CancellationToken).ConfigureAwait(true);
      ContainedStdioSession fresh = AssertSuccess(second);
      try
      {
        McpToolCallResult echo = await fresh
            .CallToolAsync("echo", "{" + "\"text\":\"again\"}", cts.Token).ConfigureAwait(true);
        Assert.False(echo.IsError);
        Assert.Contains("echo: again", echo.Content, StringComparison.Ordinal);
      }
      finally
      {
        await fresh.DisposeAsync().ConfigureAwait(true);
      }
    }
    finally
    {
      await factory.DisposeAsync().ConfigureAwait(true);
    }
  }

  private static ContainedStdioSession AssertSuccess(McpConnectResult result) =>
      (ContainedStdioSession)((McpConnectResult.Success)result).Session;
}
