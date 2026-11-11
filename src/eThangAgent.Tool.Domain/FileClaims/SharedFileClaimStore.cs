namespace eThangAgent.ToolDomain.FileClaims;

/// <summary>In-memory shared claim store (issue #363): one table per workspace,
/// keyed by file path. The daemon hosts one instance for code workspaces and
/// the app process hosts one for code-less workspaces; both share this contract
/// and this implementation, so out-of-process hosts see one table per workspace
/// either way. Expiry is computed on read; there is no sweeper.</summary>
public sealed class SharedFileClaimStore : IFileClaimStore
{
  private readonly Lock _gate = new();
  private readonly Dictionary<string, Dictionary<string, FileClaim>> _byWorkspace = [with(StringComparer.Ordinal)];

  public FileClaimResult Acquire(string workspaceId, string path, string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrEmpty(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    lock (_gate)
    {
      FileClaim? live = LiveLocked(workspaceId, path, now);
      if (live is not null && !string.Equals(live.SessionId, sessionId, StringComparison.Ordinal))
      {
        return new FileClaimResult(false, live.SessionId, live.Expiry);
      }
      DateTimeOffset expiry = now + FileClaimPolicy.ClaimTtl;
      SetLocked(workspaceId, path, new FileClaim(sessionId, expiry));
      return new FileClaimResult(true, sessionId, expiry);
    }
  }

  public FileClaimResult Renew(string workspaceId, string path, string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrEmpty(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    lock (_gate)
    {
      FileClaim? live = LiveLocked(workspaceId, path, now);
      if (live is null)
      {
        return new FileClaimResult(false, null, null);
      }
      if (!string.Equals(live.SessionId, sessionId, StringComparison.Ordinal))
      {
        return new FileClaimResult(false, live.SessionId, live.Expiry);
      }
      DateTimeOffset expiry = now + FileClaimPolicy.ClaimTtl;
      SetLocked(workspaceId, path, new FileClaim(sessionId, expiry));
      return new FileClaimResult(true, sessionId, expiry);
    }
  }

  public bool Release(string workspaceId, string path, string sessionId, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrEmpty(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    ArgumentException.ThrowIfNullOrEmpty(sessionId);
    lock (_gate)
    {
      FileClaim? live = LiveLocked(workspaceId, path, now);
      if (live is null || !string.Equals(live.SessionId, sessionId, StringComparison.Ordinal))
      {
        return false;
      }
      _ = _byWorkspace[workspaceId].Remove(path);
      return true;
    }
  }

  public FileClaim? Peek(string workspaceId, string path, DateTimeOffset now)
  {
    ArgumentException.ThrowIfNullOrEmpty(workspaceId);
    ArgumentException.ThrowIfNullOrEmpty(path);
    lock (_gate)
    {
      return LiveLocked(workspaceId, path, now);
    }
  }

  private FileClaim? LiveLocked(string workspaceId, string path, DateTimeOffset now)
  {
    if (!_byWorkspace.TryGetValue(workspaceId, out Dictionary<string, FileClaim>? table))
    {
      return null;
    }
    if (!table.TryGetValue(path, out FileClaim? claim))
    {
      return null;
    }
    if (claim.Expiry <= now)
    {
      _ = table.Remove(path);
      return null;
    }
    return claim;
  }

  private void SetLocked(string workspaceId, string path, FileClaim claim)
  {
    if (!_byWorkspace.TryGetValue(workspaceId, out Dictionary<string, FileClaim>? table))
    {
      table = [with(StringComparer.Ordinal)];
      _byWorkspace[workspaceId] = table;
    }
    table[path] = claim;
  }
}
