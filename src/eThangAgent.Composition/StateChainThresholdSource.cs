using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>The fixed thresholds, read from workspace state (issue #309 D5).
///     <para>WHY AN ADAPTER AND NOT A DIRECT STATE READ IN THE DOMAIN: the State Domain is a
///     separate bounded context, and the Agent Domain reaching into its service would be the
///     boundary violation <c>IChainBriefSource</c> exists to avoid — the brief already reads the
///     workspace's gate commands through a port for exactly this reason.</para>
///     <para>AN ABSENT KEY IS A NAMED REFUSAL, NOT A DEFAULT. The whole point of D5 is that the
///     thresholds existed before the measurement; defaulting them would produce a report whose
///     bounds nobody chose, which is precisely the unfalsifiable claim the issue opens with. The
///     refusal names the key to write and the metrics it must state.</para>
///     <para>IT READS THE VALUE, NOT THE ROW, so the artifact stays a plain state key a human can
///     inspect with <c>state.get</c> — the ordering evidence is that key's own <c>updated_at</c>,
///     which only the state store records.</para></summary>
public sealed class StateChainThresholdSource(IStateService state) : IChainThresholdSource
{
  private readonly IStateService _state = state ?? throw new ArgumentNullException(nameof(state));

  public async Task<Result<ChainTelemetryThresholds>> ReadAsync(string key,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(key);
    Result<string> stored = await _state.GetAsync(key, ct).ConfigureAwait(false);
    return stored.IsSuccess
        ? ChainTelemetryThresholds.Parse(stored.Value)
        : Result.Failure<ChainTelemetryThresholds>(new DomainError(ChainTelemetryThresholds.Code,
            $"no thresholds artifact exists at '{key}', so this report would have no bounds fixed "
            + "before the measurement. Write it first, one line per metric (see "
            + "ChainTelemetryThresholds.RequiredKeys): " + stored.Error.Message));
  }
}
