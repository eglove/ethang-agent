using System.Globalization;

namespace eThangAgent.ToolDomain.FileClaims;

/// <summary>The tool-layer claim check: the seam edit/write consult before writing.
/// The gate owns the session identity, the workspace key, and the clock, so a tool
/// call needs only the resolved path. The store is the shared claim table; the gate
/// adds no state of its own.</summary>
public interface IFileClaimGate
{
  /// <summary>Checks the claim for <paramref name="path"/> before the write.
  /// Returns null when the write may proceed (claim acquired or already held by
  /// this session), or the typed refusal text when another session holds it.</summary>
  Task<string?> CheckBeforeWriteAsync(string path, CancellationToken ct = default);

  /// <summary>Renews the claim after a successful mutating call. A lapsed claim is
  /// re-acquired so the next call re-checks against the store.</summary>
  Task RenewAfterWriteAsync(string path, CancellationToken ct = default);
}

/// <summary>Session-bound <see cref="IFileClaimGate"/> over an <see cref="IFileClaimStore"/>.
/// Lazy acquisition: the first mutating call on a file acquires the claim for this
/// session; each successful mutating call renews it under
/// <see cref="FileClaimPolicy.ClaimTtl"/>, expiry computed on read, no sweeper, no
/// background timer. The refusal names the holder session and the claim expiry so the
/// model can wait, choose another file, or escalate. Shell() bypasses the file seam
/// entirely and is out of scope: claims are a cooperative guard among well-behaved
/// tool users, not a lock.</summary>
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

  public Task<string?> CheckBeforeWriteAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    FileClaimResult result = _store.Acquire(_workspaceId, path, _sessionId, _clock());
    if (result.Granted)
    {
      return Task.FromResult<string?>(null);
    }

    string holder = result.HolderSessionId ?? "unknown";
    string expiry = result.Expiry?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";
    return Task.FromResult<string?>(
        $"Error [{FileClaimErrorCodes.FileClaimHeld}]: file is claimed by session {holder} until {expiry}; wait, choose another file, or escalate.");
  }

  public Task RenewAfterWriteAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    DateTimeOffset now = _clock();
    FileClaimResult renewed = _store.Renew(_workspaceId, path, _sessionId, now);
    if (!renewed.Granted && renewed.HolderSessionId is null)
    {
      _ = _store.Acquire(_workspaceId, path, _sessionId, now);
    }

    return Task.CompletedTask;
  }
}
