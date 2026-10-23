using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>The composition adapter for <c>INewSessionPort</c> (issue #234): emits a
///     session-request through the live turn's channel and awaits the app's answer.
///     The app-side shell does the actual launch (the Desktop task); this port only
///     carries the request over the wire and translates the outcome. The port does NOT
///     know the workspace — the app resolves it from the requesting tab (the Tool
///     Domain cannot reference the workspace seam).</summary>
public sealed class WireNewSessionPort(NewSessionRequestChannel channel) : INewSessionPort
{
  private readonly NewSessionRequestChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));

  /// <summary>Carries one launch request to the app over the live turn's wire and
  ///     returns its answer: the new session's id, or the app's typed failure
  ///     (unreachable, cancelled, or the app's own refusal) verbatim.</summary>
  public async Task<Result<NewSessionPortResult>> StartAsync(string prompt, string? handoffSummary,
      CancellationToken ct = default)
  {
    string requestId = Guid.NewGuid().ToString("N");
    Result<string> answered = await _channel.RequestAsync(requestId, prompt, handoffSummary, ct).ConfigureAwait(false);
    return answered.IsSuccess
        ? Result.Success(new NewSessionPortResult(answered.Value))
        : Result.Failure<NewSessionPortResult>(answered.Error);
  }
}
