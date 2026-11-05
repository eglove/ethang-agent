using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>Production <see cref="IAdvisoryDiagnosticsSource"/> over the daemon pipe:
/// queries the daemon's diagnostics service for one file and maps the honesty labels
/// back. The client is resolved lazily so edit/write can be registered before any
/// daemon exists; a null client yields Unavailable, never an exception, so the edit is
/// never failed by diagnostics. The factory is synchronous because the caller's budget
/// is a cancellation token: connecting is the caller's first await, so a cold daemon
/// costs the edit only what the budget allows.</summary>
public sealed class LspAdvisoryDiagnosticsAccess : IAdvisoryDiagnosticsSource
{
  private readonly Func<LspPipeClient?> _clientFactory;
  private readonly string _workspaceId;
  private readonly string _appInstanceId;

  public LspAdvisoryDiagnosticsAccess(Func<LspPipeClient?> clientFactory, string workspaceId, string appInstanceId)
  {
    _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
    _appInstanceId = appInstanceId ?? throw new ArgumentNullException(nameof(appInstanceId));
  }

  public async Task<AdvisoryDiagnosticsResult> GetAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    LspPipeClient? client = _clientFactory();
    if (client is null)
    {
      return new AdvisoryDiagnosticsResult(AdvisoryDiagnosticsState.Unavailable, []);
    }

    try
    {
      return await client.GetDiagnosticsAsync(path, knownGeneration: 0, ct).ConfigureAwait(false);
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
