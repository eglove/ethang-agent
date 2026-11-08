using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>Production <see cref="IAdvisoryDiagnosticsSource"/> over the daemon pipe:
/// queries the daemon's diagnostics service for one file and maps the honesty labels
/// back. The client is resolved through an async factory, so a cold workspace connects
/// the daemon on the first edit rather than reporting Unavailable forever; a null client
/// yields Unavailable, never an exception, so the edit is never failed by diagnostics.</summary>
public sealed class LspAdvisoryDiagnosticsAccess : IAdvisoryDiagnosticsSource
{
  private readonly Func<CancellationToken, Task<LspPipeClient?>> _clientFactory;

  public LspAdvisoryDiagnosticsAccess(Func<CancellationToken, Task<LspPipeClient?>> clientFactory)
    => _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));

  public async Task<AdvisoryDiagnosticsResult> GetAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    LspPipeClient? client = await _clientFactory(ct).ConfigureAwait(false);
    if (client is null)
    {
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Unavailable, []);
    }

    try
    {
      return await client.GetDiagnosticsAsync(path, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    // Named decision (CA1031): a diagnostics fault must never fail the edit that
    // produced it - the edit result is worth more than the append.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Degraded, [], ex.Message);
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }
}
