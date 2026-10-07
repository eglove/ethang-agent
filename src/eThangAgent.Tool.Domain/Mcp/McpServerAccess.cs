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
    : IMcpServerAccess, IAsyncDisposable, IDisposable
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
          config.CommandOrUrl, entry.State, entry.Tools, entry.Error, entry.Stderr));
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
      // Issue #106: the crash's diagnosis outlives the corpse - the dead session's
      // stderr tail stays on the entry until a fresh session connects.
      entry.Stderr = dead.StderrTail ?? entry.Stderr;
      entry.Session = null;
      entry.State = McpConnectionState.NotConnected;
      entry.Tools = [];
      entry.Error = null;
      IMcpClientSession corpse = dead;
      _ = Task.Run(() => corpse.DisposeAsync().AsTask(), CancellationToken.None);
    }

    // Trust staleness (issue #107): the pooled session was established under a
    // particular launch (command, args, env, headers, pinned version). A
    // trust-relevant config change - even re-approved - means the RUNNING process
    // is not the one the user's current trust decision produced: dispose it and
    // reconnect under the new launch.
    string launchStamp = StampOf(config);
    if (entry.Session is not null && !string.Equals(entry.LaunchStamp, launchStamp, StringComparison.Ordinal))
    {
      IMcpClientSession stale = entry.Session;
      entry.Session = null;
      entry.State = McpConnectionState.NotConnected;
      entry.Tools = [];
      entry.Error = null;
      _ = Task.Run(() => stale.DisposeAsync().AsTask(), CancellationToken.None);
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
      entry.LaunchStamp = launchStamp;
      entry.Stderr = entry.Session.StderrTail;
      entry.Tools = await entry.Session.ListToolsAsync(ct).ConfigureAwait(false);
    }

    McpToolInfo? tool = entry.Tools.FirstOrDefault(t => t.Name == call.Tool);
    if (tool is null)
    {
      return new McpOutcome.Failure("McpToolNotFound",
          $"server '{call.Server}' advertises no tool '{call.Tool}'. Connected tools: " +
          (entry.Tools.Count == 0 ? "(none)" : string.Join(", ", entry.Tools.Select(t => t.Name))) + ".");
    }

    // The per-call gate (issue #109): a mutating call on a gated server is refused
    // with a structured, logged refusal - the dialog's toggle is the human decision
    // path; the harness has no mid-turn approval surface to wait on. Classification
    // is policy over the server's declared hints: destructive, undeclared, or
    // explicitly non-read-only all gate; only a declared read-only tool passes.
    if (config.GateMode == McpGateMode.Mutating && IsMutating(tool))
    {
      string detail = $"'{call.Tool}' is a mutating call on gated server '{call.Server}'";
      _ = await _store.AppendDecisionAsync(config.Id, "gate-denied", detail, ct).ConfigureAwait(false);
      return new McpOutcome.Failure("McpCallGated",
        $"server '{call.Server}' gates mutating calls: '{call.Tool}' is mutating (no read-only declaration). " +
        "The user can lift the gate in the MCP Servers dialog; the denial is logged.");
    }

    McpToolCallResult result = await entry.Session
        .CallToolAsync(call.Tool, call.Arguments.GetRawText(), ct).ConfigureAwait(false);
    return new McpOutcome.Called(result.Content, result.IsError);
  }

  /// <summary>Whether the tool is mutating under the gate's classification (issue #109):
  ///     a destructive hint, a false read-only hint, or NO hint at all is mutating -
  ///     deny-by-default over an untyped world.</summary>
  private static bool IsMutating(McpToolInfo tool)
      => tool.DestructiveHint == true
          || tool.ReadOnlyHint is null or false;

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

  /// <summary>Sync dispose for the DI container's shutdown path: the same reaping
  ///     as <see cref="DisposeAsync"/> without the await (the SDK session factory's
  ///     precedent). Sessions dispose their transports; a UI-thread container close
  ///     must not deadlock on them.</summary>
  public void Dispose()
  {
    foreach (IMcpClientSession session in _entries.Values.Where(e => e.Session is not null)
        .Select(e => e.Session!))
    {
      session.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    /// <summary>The captured stderr tail of the current (or last dead) session.</summary>
    public string? Stderr { get; set; }

    /// <summary>The launch stamp the CURRENT session was established under (issue #107):
    ///     a config whose stamp differs means the session predates the user's current
    ///     trust decision and must be re-established.</summary>
    public string? LaunchStamp { get; set; }
  }

  /// <summary>The trust-relevant launch identity of one config (issue #107): the fields
  ///     whose change re-opens approval in the dialog - command, args, env, headers,
  ///     pinned version - joined so a change in any of them changes the stamp.</summary>
  private static string StampOf(McpServerConfig config)
      => string.Join("|", config.CommandOrUrl, config.ArgsJson, config.EnvJson,
          config.HeadersJson, config.PinnedVersion ?? "");
}
