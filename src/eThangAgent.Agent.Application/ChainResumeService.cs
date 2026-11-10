using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Enacts a resume of a PARKED chain: un-park it, re-arm the step that stopped it, and
///     start a fresh session for whatever step is now available.
///     <para>WHY A SERVICE AND NOT JUST THE DOMAIN MUTATION. Un-parking the chain and re-arming
///     the step makes the work AVAILABLE again, but nothing starts it: the advance is driven by
///     a completion fact, and a parked chain produces none - the step that would complete is the
///     one that stopped. Without this, resuming a chain parked on a spent attempt budget leaves
///     a Running chain with an available step and no session, which looks exactly as stuck as
///     before.</para>
///     <para>THE RULE IS THE DOMAIN'S (<see cref="Chain.ResumeAndRearm"/>) and the launch is the
///     port's; this class only sequences them, so there is one place each lives - the
///     <see cref="ChainAdvanceService"/> split, applied to the exit instead of the advance.</para>
///     <para>ORDER IS LOAD-BEARING, the advance's rule: the record is written BEFORE the launch,
///     so a launch that fails leaves an available step for the next attempt rather than a claim
///     held by a session that never existed.</para></summary>
/// <param name="chains">The chain application service.</param>
/// <param name="launch">Starts the resumed step's session.</param>
public sealed class ChainResumeService(ChainService chains, IChainResumeLaunchPort launch)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly IChainResumeLaunchPort _launch = launch ?? throw new ArgumentNullException(nameof(launch));

  /// <summary>Resumes the parked chain and starts what it can.
  ///     <para>A CHAIN THAT IS NOT PARKED IS REFUSED BY NAME, not treated as a no-op: a caller
  ///     told 'resumed' about a chain nothing happened to would report work that did not
  ///     occur.</para>
  ///     <para>THE RE-ARMED STEP IS THE ONE THE PARK NAMED, and it is read from the record
  ///     before the resume so the caller learns which position it was. A park that named no
  ///     step (a chain that parked because nothing was available) re-arms nothing and simply
  ///     resumes.</para></summary>
  /// <param name="chainId">The chain to resume.</param>
  /// <param name="actor">Who resumed it - the user, or a session the user asked.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainResumeOutcome>> ResumeAsync(int chainId, ChainActor actor,
      DateTimeOffset now, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(actor);
    Result<Chain> loaded = await _chains.GetAsync(chainId, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return Result.Failure<ChainResumeOutcome>(loaded.Error);
    }

    if (loaded.Value.Status != ChainStatus.Parked)
    {
      return Result.Failure<ChainResumeOutcome>(new DomainError(ChainResumeCode.NotParked,
          $"chain #{chainId} is {loaded.Value.Status}, not Parked; only a parked chain can be resumed."));
    }

    int? rearmed = loaded.Value.ParkedAtPosition;
    Result<Chain> resumed = await _chains
        .ResumeAndRearmAsync(chainId, actor, now, ct).ConfigureAwait(false);
    return resumed.IsSuccess
        ? await StartAvailableAsync(resumed.Value, rearmed, now, ct).ConfigureAwait(false)
        : Result.Failure<ChainResumeOutcome>(resumed.Error);
  }

  /// <summary>Starts the first step the resumed chain offers, or reports that nothing was
  ///     available.
  ///     <para>A STEP ALREADY RUNNING IS NOT STARTED AGAIN. A group's other members may still be
  ///     working when the chain parks, and the resume must not launch a second session for work
  ///     a live one holds - the cursor already refuses it, and this reports the same fact rather
  ///     than launching anyway.</para></summary>
  /// <param name="chain">The chain as it stands after the resume.</param>
  /// <param name="rearmed">The position put back to Pending, or null.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainResumeOutcome>> StartAvailableAsync(Chain chain, int? rearmed,
      DateTimeOffset now, CancellationToken ct)
  {
    return chain.NextAvailableStep() is { } step
        ? await StartStepAsync(chain, step, rearmed, now, ct).ConfigureAwait(false)
        : Result.Success(new ChainResumeOutcome(chain.Id, rearmed, null, null));
  }

  /// <summary>Claims, launches, and reassigns one resumed step, releasing the claim when the
  ///     launch fails so the step is available to the next attempt.</summary>
  /// <param name="chain">The chain as it stands after the resume.</param>
  /// <param name="step">The step the cursor offered.</param>
  /// <param name="rearmed">The position put back to Pending, or null.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainResumeOutcome>> StartStepAsync(Chain chain, ChainStep step,
      int? rearmed, DateTimeOffset now, CancellationToken ct)
  {


    string provisional = "pending-" + Guid.NewGuid().ToString("N");
    Result<Chain> claimed = await _chains
        .ClaimStepAsync(chain.Id, step.Position, provisional, now, ct).ConfigureAwait(false);
    if (!claimed.IsSuccess)
    {
      return Result.Failure<ChainResumeOutcome>(claimed.Error);
    }

    ChainStep claimedStep = claimed.Value.StepAt(step.Position)
        ?? throw new InvalidOperationException($"chain #{chain.Id} lost step {step.Position} while it was being claimed.");
    Result<string> started = await _launch.StartResumedSessionAsync(
        new ChainStepAssignment(chain.Id, chain.PlanId, step.Position, null).WithRole(claimedStep.Role), ct)
        .ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      _ = await _chains.ReleaseStepAsync(chain.Id, step.Position, ChainActor.Harness, now, ct)
          .ConfigureAwait(false);
      return Result.Failure<ChainResumeOutcome>(started.Error);
    }

    _ = await _chains.ReassignStepAsync(chain.Id, step.Position, started.Value, now, ct)
        .ConfigureAwait(false);
    return Result.Success(new ChainResumeOutcome(chain.Id, rearmed, step.Position, started.Value));
  }
}
