using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>Production <see cref="IAdvisoryDiagnosticsSource"/> over the daemon pipe
/// (issue #356): queries the daemon's diagnostics service for one file and maps the
/// honesty labels back. The pipe client is resolved lazily so edit/write can be
/// registered before any daemon exists; a null client yields Unavailable, never an
/// exception, so the edit is never failed by diagnostics. The factory receives the
/// caller's token, which the edit's own budget supplies: connecting to a daemon is
/// bounded by the SAME budget as the query it serves.</summary>
public sealed class LspAdvisoryDiagnosticsAccess : IAdvisoryDiagnosticsSource
{
  private readonly Func<CancellationToken, LspPipeClient?> _clientFactory;
  private readonly string _workspaceId;
  private readonly string _appInstanceId;

  public LspAdvisoryDiagnosticsAccess(Func<CancellationToken, LspPipeClient?> clientFactory, string workspaceId, string appInstanceId)
  {
    _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
    _appInstanceId = appInstanceId ?? throw new ArgumentNullException(nameof(appInstanceId));
  }

  public async Task<AdvisoryDiagnosticsResult> GetAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    LspPipeClient? client = _clientFactory(ct);
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
    catch (Exception)
    {
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Degraded, []);
    }
#pragma warning restore CA1031 // Do not catch general exception types
  }
}
