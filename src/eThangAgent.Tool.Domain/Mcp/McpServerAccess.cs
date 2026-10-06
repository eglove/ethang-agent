using System.Collections.Concurrent;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The MCP session pool (issue #104): the domain's policy over configured
///     servers. Lazy connect (B2) - a server spawns or connects on its FIRST dispatch,
///     never at session start; the tool list caches per workspace; a failed connect is
///     recorded as a status entry and retried on the next dispatch (B3). Pending and
///     revoked servers never connect (B5). Unknown servers and tools fail with typed
///     errors that name what is configured and connected (B4). Listing never connects
///     anything. One instance per session container = one pool per workspace.</summary>
public sealed class McpServerAccess(IMcpServerStore store, IMcpClientSessionPool pool, string workspaceId)
    : IMcpServerAccess, IAsyncDisposable
{
  private readonly IMcpServerStore _store = store ?? throw new ArgumentNullException(nameof(store));
  private readonly IMcpClientSessionPool _pool = pool ?? throw new ArgumentNullException(nameof(pool));
  private readonly string _workspaceId = string.IsNullOrWhiteSpace(workspaceId)
      ? throw new ArgumentException("Workspace id must be non-empty.", nameof(workspaceId))
      : workspaceId;
  private readonly ConcurrentDictionary<string, PoolEntry> _entries = new(StringComparer.Ordinal);


  /// <inheritdoc />
  public async Task<McpOutcome> ExecuteAsync(McpCommand command, CancellationToken ct = default)
  {
    Result<IReadOnlyList<McpServerConfig>> configs;
    try
    {
      configs = await _store.ListAsync(_workspaceId, ct).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
    {
      // Named decision (CA1031): a store implementation fault must fail the dispatch
      // with StorageUnavailable, never crash the turn.
      return new McpOutcome.Failure("StorageUnavailable",
          "the MCP server config store could not be read: " + ex.Message);
    }
    return configs.IsSuccess
      ? await Dispatch(configs.Value, command, ct).ConfigureAwait(false)
      : new McpOutcome.Failure("StorageUnavailable",
          "the MCP server config store could not be read: " + configs.Error.Message);
  }

  private Task<McpOutcome> Dispatch(
      IReadOnlyList<McpServerConfig> configs, McpCommand command, CancellationToken ct)
  {
    return command switch
    {
      McpCommand.ListServers => ListAsync(configs),
      McpCommand.CallTool call => CallAsync(configs, call, ct),
      _ => Task.FromResult<McpOutcome>(
          new McpOutcome.Failure("InvalidAction", "unknown command " + command.GetType().Name + ".")),
    };
  }

  private Task<McpOutcome> ListAsync(IReadOnlyList<McpServerConfig> configs)
  {
    List<McpServerStatus> statuses = [];
    foreach (McpServerConfig config in configs.OrderBy(c => c.Name, StringComparer.Ordinal))
    {
      PoolEntry entry = _entries.GetOrAdd(config.Name, _ => new PoolEntry());
      statuses.Add(new McpServerStatus(config.Name, config.ApprovalState, config.Transport,
          config.CommandOrUrl, entry.State, entry.Tools, entry.Error));
    }

    return Task.FromResult<McpOutcome>(new McpOutcome.Status(statuses));
  }

  private async Task<McpOutcome> CallAsync(
      IReadOnlyList<McpServerConfig> configs, McpCommand.CallTool call, CancellationToken ct)
  {
    McpServerConfig? config = configs.FirstOrDefault(c => c.Name == call.Server);
    if (config is null)
    {
      return new McpOutcome.Failure("McpServerNotFound",
          $"no MCP server named '{call.Server}' is configured. Configured: " +
          (configs.Count == 0 ? "(none)" : string.Join(", ", configs.Select(c => c.Name))) + ".");
    }

    if (config.ApprovalState != McpApprovalState.Approved)
    {
      return new McpOutcome.Failure("McpServerNotApproved",
          $"server '{call.Server}' is {ApprovalText(config.ApprovalState)}; a server connects only " +
          "after the user approves it.");
    }

    PoolEntry entry = _entries.GetOrAdd(call.Server, _ => new PoolEntry());
    // Reconnect (issue #105, B4): a session whose server died mid-session is
    // disposed and reconnected on this dispatch - a structured reconnect, never a
    // hang or a stale-session error loop. The dead session's dispose is best-effort
    // and detached: its failure never blocks the reconnect.
    if (entry.Session is { HasExited: true } dead)
    {
      entry.Session = null;
      entry.State = McpConnectionState.NotConnected;
      entry.Tools = [];
      entry.Error = null;
      IMcpClientSession corpse = dead;
      _ = Task.Run(() => corpse.DisposeAsync().AsTask(), CancellationToken.None);
    }

    if (entry.Session is null)
    {
      McpConnectResult connect = await _pool.ConnectAsync(config, ct).ConfigureAwait(false);
      if (connect is McpConnectResult.Failure failed)
      {
        entry.State = McpConnectionState.Failed;
        entry.Error = failed.Message;
        return new McpOutcome.Failure(failed.Code,
            $"server '{call.Server}' could not connect: {failed.Message} " +
            "The failure is recorded; run mcp list to see it. The next call retries the connect.");
      }

      entry.Session = ((McpConnectResult.Success)connect).Session;
      entry.State = McpConnectionState.Connected;
      entry.Tools = await entry.Session.ListToolsAsync(ct).ConfigureAwait(false);
    }

    McpToolInfo? tool = entry.Tools.FirstOrDefault(t => t.Name == call.Tool);
    if (tool is null)
    {
      return new McpOutcome.Failure("McpToolNotFound",
          $"server '{call.Server}' advertises no tool '{call.Tool}'. Connected tools: " +
          (entry.Tools.Count == 0 ? "(none)" : string.Join(", ", entry.Tools.Select(t => t.Name))) + ".");
    }

    McpToolCallResult result = await entry.Session
        .CallToolAsync(call.Tool, call.Arguments.GetRawText(), ct).ConfigureAwait(false);
    return new McpOutcome.Called(result.Content, result.IsError);
  }

  private static string ApprovalText(McpApprovalState state) => state switch
  {
    McpApprovalState.Pending => "pending",
    McpApprovalState.Revoked => "revoked",
    McpApprovalState.Approved => "approved",
    _ => "in an unknown state",
  };

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    foreach (IMcpClientSession session in _entries.Values.Where(e => e.Session is not null)
        .Select(e => e.Session!))
    {
      await session.DisposeAsync().ConfigureAwait(false);
    }

    _entries.Clear();
  }

  /// <summary>One server's pooled state: the session once connected, the cached tool
  ///     list, and the last connect failure if any.</summary>
  private sealed class PoolEntry
  {
    public IMcpClientSession? Session { get; set; }
    public McpConnectionState State { get; set; } = McpConnectionState.NotConnected;
    public IReadOnlyList<McpToolInfo> Tools { get; set; } = [];
    public string? Error { get; set; }
  }
}
