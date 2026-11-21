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
      McpCommand.AddServer add => AddAsync(add.Spec, ct),
      McpCommand.UpdateServer update => UpdateAsync(configs, update, ct),
      McpCommand.RemoveServer remove => RemoveAsync(configs, remove, ct),
      _ => Task.FromResult<McpOutcome>(
          new McpOutcome.Failure("InvalidAction", "unknown command " + command.GetType().Name + ".")),
    };
  }

  // Reconnect (issue #105, B4): a session whose server died mid-session is
  // disposed and reconnected on this dispatch - a structured reconnect, never a
  // hang or a stale-session error loop. The dead session's dispose is best-effort
  // and detached: its failure never blocks the reconnect.
  private static void DiscardDeadSession(PoolEntry entry)
  {
    if (entry.Session is not { HasExited: true } dead)
    {
      return;
    }

    // Issue #106: the crash's diagnosis outlives the corpse - the dead session's
    // stderr tail stays on the entry until a fresh session connects.
    entry.Stderr = dead.StderrTail ?? entry.Stderr;
    ResetEntry(entry, dead);
  }

  // Trust staleness (issue #107): the pooled session was established under a
  // particular launch (command, args, env, headers, pinned version). A
  // trust-relevant config change - even re-approved - means the RUNNING process
  // is not the one the user's current trust decision produced: dispose it and
  // reconnect under the new launch.
  private static void DiscardStaleSession(PoolEntry entry, McpServerConfig config)
  {
    if (entry.Session is null)
    {
      return;
    }

    string launchStamp = StampOf(config);
    if (string.Equals(entry.LaunchStamp, launchStamp, StringComparison.Ordinal))
    {
      return;
    }

    ResetEntry(entry, entry.Session);
  }

  private static void ResetEntry(PoolEntry entry, IMcpClientSession session)
  {
    entry.Session = null;
    entry.State = McpConnectionState.NotConnected;
    entry.Tools = [];
    entry.Error = null;
    _ = Task.Run(() => session.DisposeAsync().AsTask(), CancellationToken.None);
  }

  private Task<McpOutcome> ListAsync(IReadOnlyList<McpServerConfig> configs)
  {
    List<McpServerStatus> statuses = [];
    foreach (McpServerConfig config in configs.OrderBy(c => c.Name, StringComparer.Ordinal))
    {
      PoolEntry entry = _entries.GetOrAdd(config.Name, _ => new PoolEntry());
      statuses.Add(new McpServerStatus(config.Name, config.ApprovalState, config.Transport,
          config.CommandOrUrl, entry.State, entry.Tools, entry.Error, entry.Stderr,
          config.WorkspaceId is null ? McpServerScope.Global : McpServerScope.Workspace,
          config.GateMode));
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
    DiscardDeadSession(entry);
    DiscardStaleSession(entry, config);

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
      entry.LaunchStamp = StampOf(config);
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

  // ---- configuration: add, update, remove ----

  /// <summary>Stores a new server and verifies its launch. The row is written first so
  ///     the verification runs against exactly what the store will hand the pool later;
  ///     a launch that cannot connect is removed again, so a failed add leaves nothing
  ///     behind. The stored row is approved: configuring a server is the trust event,
  ///     and the dialog's revoke is the way to withdraw it.</summary>
  private async Task<McpOutcome> AddAsync(McpServerSpec spec, CancellationToken ct)
  {
    McpServerConfig candidate = spec.ToConfig(_workspaceId, McpApprovalState.Approved);
    Result<McpServerConfig> stored = await Store(() => _store.AddAsync(candidate, ct)).ConfigureAwait(false);
    if (!stored.IsSuccess)
    {
      return new McpOutcome.Failure(stored.Error.Code, stored.Error.Message);
    }

    McpServerConfig row = stored.Value;
    Verified verified = await VerifyAsync(row, ct).ConfigureAwait(false);
    if (verified.Failure is { } failure)
    {
      _ = await Store(() => _store.DeleteAsync(row.Id, _workspaceId, ct)).ConfigureAwait(false);
      return new McpOutcome.Failure(failure.Code,
          $"server '{row.Name}' was not stored: its launch could not be verified. {failure.Message} "
          + "Nothing was added; correct the launch and run add again.");
    }

    return new McpOutcome.Added(row.Name, ScopeOf(row), row.ApprovalState, verified.Tools);
  }

  /// <summary>Applies a partial change to a stored server and verifies the resulting
  ///     launch. The stored row is restored when the verification fails, so a rejected
  ///     edit leaves the server exactly as it was. A change to the launch identity
  ///     re-opens approval; a scope or gate change keeps the user's approval.</summary>
  private async Task<McpOutcome> UpdateAsync(
      IReadOnlyList<McpServerConfig> configs, McpCommand.UpdateServer update, CancellationToken ct)
  {
    McpServerConfig? existing = configs.FirstOrDefault(c => c.Name == update.Server);
    if (existing is null)
    {
      return UnknownServer(update.Server, configs);
    }

    McpServerConfig candidate = update.Edit.AppliedTo(existing, _workspaceId, existing.ApprovalState);
    McpServerConfig withApproval = candidate with
    {
      ApprovalState = LaunchChanged(existing, candidate) ? McpApprovalState.Pending : existing.ApprovalState,
    };
    Result<McpServerConfig> saved = await Store(() => _store.UpdateAsync(withApproval, ct)).ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return new McpOutcome.Failure(saved.Error.Code, saved.Error.Message);
    }

    McpServerConfig row = saved.Value;
    ForgetSession(existing.Name);
    Verified verified = await VerifyAsync(row, ct).ConfigureAwait(false);
    if (verified.Failure is { } failure)
    {
      _ = await Store(() => _store.UpdateAsync(existing, ct)).ConfigureAwait(false);
      return new McpOutcome.Failure(failure.Code,
          $"server '{row.Name}' was not changed: the resulting launch could not be verified. {failure.Message} "
          + "The stored configuration is unchanged; correct the change and run update again.");
    }

    return new McpOutcome.Updated(row.Name, ScopeOf(row), row.ApprovalState, verified.Tools);
  }

  /// <summary>Deletes a stored server and forgets its pooled session, so a later add of
  ///     the same name starts from a fresh connect rather than a stale one.</summary>
  private async Task<McpOutcome> RemoveAsync(
      IReadOnlyList<McpServerConfig> configs, McpCommand.RemoveServer remove, CancellationToken ct)
  {
    McpServerConfig? existing = configs.FirstOrDefault(c => c.Name == remove.Server);
    if (existing is null)
    {
      return UnknownServer(remove.Server, configs);
    }

    Result<bool> deleted = await Store(() => _store.DeleteAsync(existing.Id, _workspaceId, ct)).ConfigureAwait(false);
    if (!deleted.IsSuccess)
    {
      return new McpOutcome.Failure(deleted.Error.Code, deleted.Error.Message);
    }

    ForgetSession(existing.Name);
    return deleted.Value
        ? new McpOutcome.Removed(existing.Name)
        : new McpOutcome.Failure("McpServerNotFound",
            $"server '{existing.Name}' was already gone; nothing was removed.");
  }

  /// <summary>The outcome of one verification connect: the tools the server answered
  ///     with, or the failure that stopped it.</summary>
  private sealed record Verified(IReadOnlyList<McpToolInfo> Tools, McpOutcome.Failure? Failure);

  /// <summary>Connects one stored row once, lists its tools, and disposes the session:
  ///     the mechanical proof that a launch works. Nothing is pooled and no tool is
  ///     called - a server that answers with its tool list is reachable, and calling a
  ///     tool to find out would run the server's side effects to test its wiring.</summary>
  private async Task<Verified> VerifyAsync(McpServerConfig config, CancellationToken ct)
  {
    McpConnectResult connect = await _pool.ConnectAsync(config, ct).ConfigureAwait(false);
    if (connect is McpConnectResult.Failure failed)
    {
      return new Verified([], new McpOutcome.Failure(failed.Code, failed.Message));
    }

    IMcpClientSession session = ((McpConnectResult.Success)connect).Session;
    try
    {
      IReadOnlyList<McpToolInfo> tools = await session.ListToolsAsync(ct).ConfigureAwait(false);
      return new Verified(tools, null);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return new Verified([], new McpOutcome.Failure("McpConnectFailed",
          "the server connected but did not answer with its tool list: " + ex.Message));
    }
    finally
    {
      await session.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Drops a server's pooled entry, disposing any live session off the
  ///     dispatch path. The next dispatch to that name connects afresh.</summary>
  private void ForgetSession(string name)
  {
    if (_entries.TryRemove(name, out PoolEntry? entry) && entry.Session is { } session)
    {
      _ = Task.Run(() => session.DisposeAsync().AsTask(), CancellationToken.None);
    }
  }

  /// <summary>Runs one store write, turning an implementation fault into a typed
  ///     failure: a store that throws must fail the dispatch, never the turn.</summary>
  private static async Task<Result<T>> Store<T>(Func<Task<Result<T>>> write)
  {
    try
    {
      return await write().ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
    {
      return Result.Failure<T>(new DomainError("StorageUnavailable",
          "the MCP server config store could not complete the write: " + ex.Message));
    }
  }

  /// <summary>Whether a stored row's launch identity differs from the candidate's - the
  ///     fields the user's approval was about. Scope and gate are not launch identity.</summary>
  private static bool LaunchChanged(McpServerConfig existing, McpServerConfig candidate)
  {
    return !string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal)
        || existing.Transport != candidate.Transport
        || !string.Equals(existing.CommandOrUrl, candidate.CommandOrUrl, StringComparison.Ordinal)
        || !string.Equals(existing.ArgsJson, candidate.ArgsJson, StringComparison.Ordinal)
        || !string.Equals(existing.EnvJson, candidate.EnvJson, StringComparison.Ordinal)
        || !string.Equals(existing.HeadersJson, candidate.HeadersJson, StringComparison.Ordinal)
        || !string.Equals(existing.PinnedVersion ?? "", candidate.PinnedVersion ?? "", StringComparison.Ordinal);
  }

  private static McpOutcome.Failure UnknownServer(string name, IReadOnlyList<McpServerConfig> configs) =>
      new("McpServerNotFound",
          $"no MCP server named '{name}' is configured. Configured: "
          + (configs.Count == 0 ? "(none)" : string.Join(", ", configs.Select(c => c.Name))) + ".");

  private static McpServerScope ScopeOf(McpServerConfig config) =>
      config.WorkspaceId is null ? McpServerScope.Global : McpServerScope.Workspace;

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
