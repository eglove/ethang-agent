using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>What a repair did, stated so the caller can report both halves: which repair it was,
///     which step it was about, and the session it started when it started one.</summary>
/// <param name="ChainId">The chain that was repaired.</param>
/// <param name="Kind">The repair enacted.</param>
/// <param name="Position">The step it was about, or null.</param>
/// <param name="SessionId">The session started, or null when the repair started none.</param>
/// <param name="Reason">Why the chain parked, or null.</param>
public sealed record ChainRepairOutcome(int ChainId, ChainRepairKind Kind, int? Position,
    string? SessionId, string? Reason);

/// <summary>Repairs a chain that cannot move on its own (issue #313).
///     <para>THE PROBLEM THIS EXISTS FOR: a Running chain whose cursor has nothing to start sits
///     there forever. <c>resume-parked</c> refuses it - it is not parked - and no session can
///     complete a step it does not hold, so the only exit was <c>abandon</c>, which throws the
///     work away. A chain that got into that state needed a human to read the record and work out
///     what happened, which is precisely the investigation a chain should not require.</para>
///     <para>THE RULE IS <see cref="ChainStuckPolicy"/>; this class only performs what the rule
///     decided, so there is one place the rule lives and one place the side effects live - the
///     <see cref="ChainAdvanceService"/> split, applied to recovery instead of progress.</para>
///     <para>IT REFUSES A HEALTHY CHAIN BY NAME. A repair that silently did nothing would leave
///     a caller believing it had fixed something, and the chain's own record is the only thing
///     that would disagree.</para></summary>
/// <param name="chains">The chain application service.</param>
/// <param name="launch">Starts the session a step repair needs.</param>
public sealed class ChainRepairService(ChainService chains, IChainResumeLaunchPort launch)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly IChainResumeLaunchPort _launch = launch
      ?? throw new ArgumentNullException(nameof(launch));

  /// <summary>Repairs the chain at <paramref name="chainId"/>, or refuses naming why there is
  ///     nothing to repair.
  ///     <para>THE RECORD IS RE-READ AFTER EVERY WRITE, so the step the launch is claimed for is
  ///     the step as it now stands: a repair runs against a chain something else may have moved,
  ///     and a claim computed from a stale copy would target a position that has since settled.
  ///     </para></summary>
  /// <param name="chainId">The chain to repair.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainRepairOutcome>> RepairAsync(int chainId, DateTimeOffset now,
      CancellationToken ct = default)
  {
    Result<Chain> loaded = await _chains.GetAsync(chainId, ct).ConfigureAwait(false);
    return !loaded.IsSuccess
        ? Result.Failure<ChainRepairOutcome>(loaded.Error)
        : await RepairLoadedAsync(loaded.Value, now, ct).ConfigureAwait(false);
  }

  /// <summary>Repairs a chain the caller already loaded, or refuses naming why it needs none.
  ///     <para>SPLIT OUT so the store read and the decision read as separate statements: the
  ///     failure path is about the record, and the decision is about the chain.</para></summary>
  /// <param name="chain">The chain as the record stands.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainRepairOutcome>> RepairLoadedAsync(Chain chain, DateTimeOffset now,
      CancellationToken ct) =>
      ChainStuckPolicy.RepairFor(chain) is { } repair
          ? await EnactAsync(chain, repair, now, ct).ConfigureAwait(false)
          : Result.Failure<ChainRepairOutcome>(NotStuck(chain));

  /// <summary>Why the chain needs no repair, stated as the fact that made it healthy.
  ///     <para>EACH SHAPE IS A DIFFERENT ANSWER, so collapsing them would tell a caller 'no'
  ///     without saying whether the chain is working, parked, or finished.</para></summary>
  /// <param name="chain">The chain as the record stands.</param>
  private static DomainError NotStuck(Chain chain) =>
      chain.Status != ChainStatus.Running
          ? new DomainError(ChainRepairCode.NotRunning,
              $"chain #{chain.Id} is {chain.Status}, not Running, so there is nothing to repair. "
              + "A parked chain is resumed with chain.resume-parked; a finished one is done.")
          : new DomainError(ChainRepairCode.AlreadyMoving,
              $"chain #{chain.Id} is Running with step(s) "
              + string.Join(", ", chain.Steps
                  .Where(step => step.Status == ChainStepStatus.Running)
                  .Select(step => step.Position.ToString(System.Globalization.CultureInfo.InvariantCulture)))
              + " running, so a live session holds the work and its completion is what advances "
              + "the chain. Repairing now would start a second session for work somebody is "
              + "already doing.");

  /// <summary>Performs the repair the policy named.</summary>
  /// <param name="chain">The chain as the record stands.</param>
  /// <param name="repair">What to do.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainRepairOutcome>> EnactAsync(Chain chain, ChainRepair repair,
      DateTimeOffset now, CancellationToken ct) =>
      repair.Kind switch
      {
        ChainRepairKind.StartStep =>
            await StartAsync(chain, repair.Position!.Value, now, ct).ConfigureAwait(false),
        ChainRepairKind.CompleteChain =>
            await CompleteAsync(chain, now, ct).ConfigureAwait(false),
        ChainRepairKind.ParkChain =>
            await ParkAsync(chain, repair, now, ct).ConfigureAwait(false),
        _ => Result.Failure<ChainRepairOutcome>(new DomainError("UnknownRepairKind",
            $"{repair.Kind} is not a chain repair kind; the set is closed.")),
      };

  /// <summary>Claims the step, launches a session for it, and corrects the claim to the real id,
  ///     releasing the claim when the launch fails so the step is available to the next attempt.
  ///     <para>THE ORDER IS THE ADVANCE'S, and for the same reason: the claim is what makes the
  ///     new session's ownership check pass, and a launch that fails must not leave the step held
  ///     by a session that never existed.</para></summary>
  /// <param name="chain">The chain being repaired.</param>
  /// <param name="position">The step to start.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainRepairOutcome>> StartAsync(Chain chain, int position,
      DateTimeOffset now, CancellationToken ct)
  {
    string provisional = "pending-" + Guid.NewGuid().ToString("N");
    Result<Chain> claimed = await _chains.ClaimStepAsync(chain.Id, position, provisional, now, ct)
        .ConfigureAwait(false);
    if (!claimed.IsSuccess)
    {
      return Result.Failure<ChainRepairOutcome>(claimed.Error);
    }

    ChainStep step = claimed.Value.StepAt(position)!;
    Result<string> started = await _launch
        .StartResumedSessionAsync(new ChainStepAssignment(chain.Id, chain.PlanId, position, null)
            .WithRole(step.Role), ct).ConfigureAwait(false);
    if (!started.IsSuccess)
    {
      _ = await _chains.ReleaseStepAsync(chain.Id, position, ChainActor.Harness, now, ct)
          .ConfigureAwait(false);
      return Result.Failure<ChainRepairOutcome>(started.Error);
    }

    Result<Chain> reassigned = await _chains
        .ReassignStepAsync(chain.Id, position, started.Value, now, ct).ConfigureAwait(false);
    return reassigned.IsSuccess
        ? Result.Success(new ChainRepairOutcome(chain.Id, ChainRepairKind.StartStep, position,
            started.Value, null))
        : Result.Failure<ChainRepairOutcome>(new DomainError(ChainAdvanceCode.ClaimNotCorrected,
            $"chain #{chain.Id} step {position} was started as session {started.Value} but its "
            + $"claim could not be corrected to it ({reassigned.Error.Code}: "
            + $"{reassigned.Error.Message}); the step is still held by a placeholder and will be "
            + "adopted when that session opens."));
  }

  /// <summary>Marks an all-settled chain Completed: the work finished and the last write did not
  ///     land, so the record is what needs correcting.</summary>
  /// <param name="chain">The chain being repaired.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainRepairOutcome>> CompleteAsync(Chain chain, DateTimeOffset now,
      CancellationToken ct)
  {
    Result<Chain> completed = await _chains
        .SetStatusAsync(chain.Id, ChainStatus.Completed, ChainActor.Harness, now, ct)
        .ConfigureAwait(false);
    return completed.IsSuccess
        ? Result.Success(new ChainRepairOutcome(chain.Id, ChainRepairKind.CompleteChain, null, null,
            null))
        : Result.Failure<ChainRepairOutcome>(completed.Error);
  }

  /// <summary>Parks a chain blocked on a step that cannot settle by itself, naming it.
  ///     <para>THE PARK IS THE REPAIR: it converts a state nothing can act on into one both
  ///     <c>resume-parked</c> and <c>abandon</c> can act on.</para></summary>
  /// <param name="chain">The chain being repaired.</param>
  /// <param name="repair">The park decision, which names the step and the reason.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  private async Task<Result<ChainRepairOutcome>> ParkAsync(Chain chain, ChainRepair repair,
      DateTimeOffset now, CancellationToken ct)
  {
    string reason = repair.Reason ?? $"chain #{chain.Id} can no longer advance.";
    Result<Chain> parked = await _chains
        .ParkAsync(chain.Id, reason, repair.Position, ChainActor.Harness, now, ct).ConfigureAwait(false);
    return parked.IsSuccess
        ? Result.Success(new ChainRepairOutcome(chain.Id, ChainRepairKind.ParkChain, repair.Position,
            null, reason))
        : Result.Failure<ChainRepairOutcome>(parked.Error);
  }
}

/// <summary>The named refusals a repair produces.</summary>
public static class ChainRepairCode
{
  /// <summary>The chain is not Running, so there is nothing to repair.</summary>
  public const string NotRunning = "ChainNotRunning";

  /// <summary>A live session holds a step, so the chain is working rather than stuck.</summary>
  public const string AlreadyMoving = "ChainAlreadyMoving";
}
