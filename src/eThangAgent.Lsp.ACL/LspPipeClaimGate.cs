using System.Globalization;
using eThangAgent.ToolDomain.FileClaims;

namespace eThangAgent.Lsp.ACL;

/// <summary>The tool-layer claim check over the daemon's shared claim table. Every
/// host in a workspace asks the same daemon process, so a claim taken by one session
/// is visible to its siblings. An unreachable daemon grants the write: the claim is a
/// cooperative guard, and losing the guard must never block editing.</summary>
public sealed class LspPipeClaimGate : IFileClaimGate
{
  private readonly Func<CancellationToken, Task<LspPipeClient?>> _clientFactory;
  private readonly string _sessionId;

  public LspPipeClaimGate(Func<CancellationToken, Task<LspPipeClient?>> clientFactory, string sessionId)
  {
    _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    _sessionId = sessionId;
  }

  public async Task<string?> CheckBeforeWriteAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    LspPipeClient? client = await _clientFactory(ct).ConfigureAwait(false);
    if (client is null)
    {
      return null;
    }

    FileClaimResult result = await client.AcquireClaimAsync(path, _sessionId, ct).ConfigureAwait(false);
    if (result.Granted)
    {
      return null;
    }

    string holder = result.HolderSessionId ?? "unknown";
    string expiry = result.Expiry?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown";
    return $"Error [{FileClaimErrorCodes.FileClaimHeld}]: file is claimed by session {holder} until {expiry}; wait, choose another file, or escalate.";
  }

  public async Task RenewAfterWriteAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    LspPipeClient? client = await _clientFactory(ct).ConfigureAwait(false);
    if (client is null)
    {
      return;
    }

    FileClaimResult renewed = await client.RenewClaimAsync(path, _sessionId, ct).ConfigureAwait(false);
    if (!renewed.Granted && renewed.HolderSessionId is null)
    {
      _ = await client.AcquireClaimAsync(path, _sessionId, ct).ConfigureAwait(false);
    }
  }
}
