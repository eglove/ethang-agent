using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition;

/// <summary>Repairs a chain step's claim when its session opens, which is the moment the
///     placeholder can finally be replaced by the fact.
///     <para>THE HARNESS CLAIMS A STEP BEFORE THE SESSION'S ID EXISTS, so every launch writes
///     the claim twice: once under a provisional id to hold the step during the launch, once
///     as the real id when the launch answers. The second write is a CAS save, and when it is
///     lost the step stays on the placeholder - the session that did the work cannot complete
///     it, and nothing else can move the chain.</para>
///     <para>WHY OPENING IS THE RIGHT MOMENT. The session's own id is exactly the fact the
///     placeholder stood in for, and it is known here without trusting anything the session
///     says. A repair that waited for a completion would never run: the completion is the very
///     write the stranded claim refuses.</para>
///     <para>IT NEVER BLOCKS THE OPEN. A chain that cannot be read, or a repair that is
///     refused, leaves the session usable - a session that could not open because a bookkeeping
///     write failed would lose the work the write exists to protect.</para></summary>
public sealed class ChainClaimAdoptionService(IChainStore chains, ChainService service)
{
  private readonly ChainService _service = service ?? throw new ArgumentNullException(nameof(service));
  private readonly IChainStore _chains = chains ?? throw new ArgumentNullException(nameof(chains));

  /// <summary>Adopts <paramref name="position"/> for <paramref name="sessionId"/> when the
  ///     claim is a placeholder, and reports what it did.</summary>
  /// <param name="chainId">The chain the step belongs to.</param>
  /// <param name="position">The step the session was started for.</param>
  /// <param name="sessionId">The session that is opening.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainClaimAdoption>> AdoptAsync(int chainId, int position,
      string sessionId, DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    Result<Chain> loaded = await _chains.GetAsync(chainId, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return Result.Failure<ChainClaimAdoption>(loaded.Error);
    }

    ChainClaimAdoption decision = ChainClaimAdoptionPolicy.Decide(loaded.Value, position, sessionId);
    if (decision.Kind != ChainClaimAdoptionKind.Adopt)
    {
      return Result.Success(decision);
    }

    // THE SAME AGGREGATE RULE THE POLICY READ, applied through the service so the CAS
    // contract and the step-change event are the ordinary ones.
    Result<Chain> adopted = await _service
        .AdoptStepAsync(chainId, position, sessionId, now, ct).ConfigureAwait(false);
    return adopted.IsSuccess
      ? Result.Success(ChainClaimAdoption.Adopt())
      : Result.Failure<ChainClaimAdoption>(adopted.Error);
  }
}
