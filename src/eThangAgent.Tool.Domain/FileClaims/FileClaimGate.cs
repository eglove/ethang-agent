using System.Globalization;

namespace eThangAgent.ToolDomain.FileClaims;

/// <summary>The tool-layer claim check (issue #364): the seam edit/write consult
/// before writing. The gate owns the session identity, the workspace key, and the
/// clock, so a tool call needs only the resolved path. The store is the #363
/// contract (daemon store for code workspaces, app-process store for code-less
/// workspaces); the gate adds no state of its own.</summary>
public interface IFileClaimGate
{
  /// <summary>Checks the claim for <paramref name="path"/> before the write.
  /// Returns null when the write may proceed (claim acquired or already held by
  /// this session), or the typed refusal text when another session holds it.</summary>
  string? CheckBeforeWrite(string path);

  /// <summary>Renews the claim after a successful mutating call (issue #358
  /// option (a): mutating calls only, no timer). A lapsed claim is re-acquired so
  /// the next call re-checks against the store.</summary>
  void RenewAfterWrite(string path);
}

/// <summary>Session-bound <see cref="IFileClaimGate"/> over an <see cref="IFileClaimStore"/>
/// (issue #364). Lazy acquisition: the first mutating call on a file acquires the
/// claim for this session; each successful mutating call renews it (TTL 15 minutes
/// per <see cref="FileClaimPolicy"/>, expiry computed on read, no sweeper, no
/// background timer). The refusal names the holder session and the claim expiry
/// so the model can wait, choose another file, or escalate. Shell() bypasses the
/// file seam entirely and is out of scope: claims are a cooperative guard among
/// well-behaved tool users, not a lock.</summary>
public sealed class FileClaimGate : IFileClaimGate
{
  private readonly IFileClaimStore _store;
  private readonly string _workspaceId;
  private readonly string _sessionId;
  private readonly Func<DateTimeOffset> _clock;

  public FileClaimGate(IFileClaimStore store, string workspaceId, string sessionId, Func<DateTimeOffset> clock)
  {
    _store = store ?? throw new ArgumentNullException(nameof(store));
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    _workspaceId = workspaceId;
    _sessionId = sessionId;
    _clock = clock ?? throw new ArgumentNullException(nameof(clock));
  }

  public string? CheckBeforeWrite(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    FileClaimResult result = _store.Acquire(_workspaceId, path, _sessionId, _clock());
    if (result.Granted)
    {
      return null;
    }
    string holder = result.HolderSessionId ?? "unknown";
    string expiry = result.Expiry?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";
    return $"Error [{FileClaimErrorCodes.FileClaimHeld}]: file is claimed by session {holder} until {expiry}; wait, choose another file, or escalate.";
  }

  public void RenewAfterWrite(string path)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    DateTimeOffset now = _clock();
    FileClaimResult renewed = _store.Renew(_workspaceId, path, _sessionId, now);
    if (!renewed.Granted && renewed.HolderSessionId is null)
    {
      _ = _store.Acquire(_workspaceId, path, _sessionId, now);
    }
  }
}
