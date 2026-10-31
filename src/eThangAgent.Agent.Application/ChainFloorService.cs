using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application;

/// <summary>What a floor trip did to the chain (issue #304).</summary>
/// <param name="Parked">True when the chain was parked for a human.</param>
/// <param name="ChainId">The chain that was parked, or null when the session holds no chain step.
///     </param>
/// <param name="Reason">The park reason, or why nothing was parked.</param>
public sealed record ChainFloorOutcome(bool Parked, int? ChainId, string Reason);

/// <summary>Enacts the universal floor's consequence: an act on the floor PARKED the chain
///     (issue #304).
///     <para>WHY THIS IS A SERVICE AND NOT PART OF THE REFUSAL. The refusal happens deep inside
///     an exec program, in the Tool Domain, which knows nothing about chains - and it must not,
///     because a file-tool refusal is not a chain concept. This class is the seam: the Tool
///     Domain records the trip in an ambient ledger, and the Application layer (which does know
///     about chains) reads it once per turn and parks.
///     </para>
///     <para>ONCE PER TURN, NOT ONCE PER TRIP. A program that trips three rules parks the chain
///     one time and names the FIRST act, because a chain that stopped because of one act should
///     report that act rather than the queue behind it.</para>
///     <para>AN ORDINARY SESSION PARKS NOTHING. A session outside a chain, and a session whose
///     step was already settled, resolve to no chain - and the refusal the model already read is
///     the whole consequence. That is the same answer the advance and the lifecycle give a
///     session in no chain: not an error, just nothing to do.</para>
///     <para>THE LEDGER IS CLEARED WHATEVER HAPPENS, so one turn's refusal never parks the next
///     turn. Clearing after the park attempt rather than before means a park that fails on an
///     infrastructure fault does not re-park on the following turn either - the model has the
///     refusal, and a repeated park would be noise on top of a real fault.</para></summary>
/// <param name="chains">The chain application service.</param>
/// <param name="floor">The turn's trip ledger.</param>
public sealed class ChainFloorService(ChainService chains, ChainFloorLedger floor)
{
  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly ChainFloorLedger _floor = floor ?? throw new ArgumentNullException(nameof(floor));

  /// <summary>Parks the chain for the first act this turn's floor refused, if any.</summary>
  /// <param name="sessionId">The session whose turn just ran.</param>
  /// <param name="now">The clock.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainFloorOutcome>> OnTurnEndAsync(string sessionId, DateTimeOffset now,
      CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
    if (_floor.First is not { } trip)
    {
      return Result.Success(new ChainFloorOutcome(false, null,
          "no act on the universal floor was attempted this turn."));
    }

    // Clear before the park attempt: the refusal has already reached the model, and a park that
    // faulted must not re-park on the next turn on top of a real infrastructure failure.
    _floor.Reset();
    Result<Chain?> found = await _chains.FindBySessionAsync(sessionId, ct).ConfigureAwait(false);
    return !found.IsSuccess
        ? Result.Failure<ChainFloorOutcome>(found.Error)
        : await ParkForChainAsync(found.Value, trip, sessionId, now, ct).ConfigureAwait(false);
  }

  /// <summary>Parks the chain the session holds, or reports that it holds none. Extracted so the
  ///     store failure and the no-chain case stay separate statements (S3358).</summary>
  private async Task<Result<ChainFloorOutcome>> ParkForChainAsync(Chain? chain, ChainFloorTrip trip,
      string sessionId, DateTimeOffset now, CancellationToken ct) =>
      chain is { } found
          ? await ParkAsync(found, trip, now, ct).ConfigureAwait(false)
          : Result.Success(new ChainFloorOutcome(false, null,
              $"session {sessionId} is not assigned to any chain step, so the refused act parked nothing."));

  /// <summary>Parks the chain, naming the refused act and what tripped it.</summary>
  private async Task<Result<ChainFloorOutcome>> ParkAsync(Chain chain, ChainFloorTrip trip,
      DateTimeOffset now, CancellationToken ct)
  {
    string reason = $"chain #{chain.Id} stopped on the universal floor: {trip.Refusal}";
    Result<Chain> parked = await _chains
        .ParkAsync(chain.Id, reason, null, ChainActor.Harness, now, ct).ConfigureAwait(false);
    return parked.IsSuccess
        ? Result.Success(new ChainFloorOutcome(true, chain.Id, reason))
        : Result.Failure<ChainFloorOutcome>(parked.Error);
  }
}
