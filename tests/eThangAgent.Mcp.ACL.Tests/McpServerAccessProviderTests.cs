using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain.Mcp;

namespace eThangAgent.Mcp.ACL.Tests;

/// <summary>Provider tests (issue #104): the glue that turns configured rows plus the
///     SDK pool into the session's IMcpServerAccess, per workspace.</summary>
public class McpServerAccessProviderTests
{
  [Fact]
  public async Task ForWorkspace_Returns_Access_Bound_To_The_Workspace()
  {
    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-mcpprov-{Guid.NewGuid():N}.db"));
    try
    {
      SqliteMcpServerStore store = new(database);
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SdkMcpClientSessionFactory factory = new();
#pragma warning restore CA2007
      _ = factory;
      McpServerAccessProvider provider = new(store, factory);
      IMcpServerAccess access = provider.ForWorkspace("ws-a");
      _ = Assert.IsType<McpServerAccess>(access);
      IMcpServerAccess again = provider.ForWorkspace("ws-a");
      _ = Assert.IsType<McpServerAccess>(again);
    }
    finally
    {
      try
      {
        File.Delete(database.DatabasePath);
      }
      catch (IOException)
      {
        // Named decision: the pooled connection may still hold the file; temp cleanup is best effort.
      }
    }
  }

  [Fact]
  public async Task Access_Lists_Configured_Servers_Through_The_Store()
  {
    AppDatabase database = new(Path.Combine(Path.GetTempPath(), $"ethang-mcpprov-{Guid.NewGuid():N}.db"));
    try
    {
      SqliteMcpServerStore store = new(database);
      _ = await store.AddAsync(new McpServerConfig(0, "demo", McpTransport.Stdio, "npx", "[]", "{}", "{}",
          null, McpApprovalState.Approved, null, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken).ConfigureAwait(true);
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SdkMcpClientSessionFactory factory = new();
#pragma warning restore CA2007
      _ = factory;
      McpServerAccessProvider provider = new(store, factory);
      IMcpServerAccess access = provider.ForWorkspace("ws-a");
      McpOutcome outcome = await access.ExecuteAsync(new McpCommand.ListServers(),
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      McpOutcome.Status status = Assert.IsType<McpOutcome.Status>(outcome);
      _ = Assert.Single(status.Servers);
      Assert.Equal("demo", status.Servers[0].Name);
      Assert.Equal(McpConnectionState.NotConnected, status.Servers[0].State);
    }
    finally
    {
      try
      {
        File.Delete(database.DatabasePath);
      }
      catch (IOException)
      {
        // Named decision: the pooled connection may still hold the file; temp cleanup is best effort.
      }
    }
  }
}
