namespace eThangAgent.AgentDomain;

/// <summary>The named refusals the advance produces (issue #301). Each is a distinct answer
///     a caller acts on differently, so collapsing them into one code would tell a session
///     'wrong step' when its claim had merely expired.</summary>
public static class ChainAdvanceCode
{
  /// <summary>The completion named a position the chain does not have.</summary>
  public const string UnknownStep = "ChainStepUnknown";

  /// <summary>D3: the completion named a step this session was not seeded for. The message
  ///     names the assigned session, because a session told only 'no' retries.</summary>
  public const string ForeignStep = "ChainStepNotAssigned";

  /// <summary>The step is the right one for this session but is not Done.</summary>
  public const string StepNotDone = "ChainStepNotDone";

  /// <summary>The chain cannot advance because steps are neither Done nor Skipped - the
  ///     lifecycle question #303 owns, reported rather than guessed.</summary>
  public const string StepsUnsettled = "ChainStepsUnsettled";

  /// <summary>The launch succeeded but its claim could not be corrected to the new session, so
  ///     the step is still held by the placeholder the harness claimed it under. The step is
  ///     recoverable - the session adopts it when it opens - but a caller that ignores this
  ///     reports an advance whose record is wrong.</summary>
  public const string ClaimNotCorrected = "ChainClaimNotCorrected";

  /// <summary>An act on the universal floor was attempted, so the chain was PARKED (issue #304).
  ///     Distinct from every other park: no gate could have licensed the act, so the remedy is a
  ///     human's decision rather than a re-run.</summary>
  public const string FloorTripped = "ChainFloorTripped";
}

/// <summary>What the advance decided.</summary>
public enum ChainAdvanceKind
{
  /// <summary>Start a session for the step at <see cref="ChainAdvance.Position"/>.</summary>
  StartStep,

  /// <summary>Every step is Done or Skipped: the chain may move to Completed.</summary>
  CompleteChain,

  /// <summary>Nothing to start and nothing to complete; <see cref="ChainAdvance.Reason"/> says why.</summary>
  Blocked,

  /// <summary>The completion was refused; <see cref="ChainAdvance.Code"/> names the refusal.</summary>
  Refused,

  /// <summary>The reporting session executes no chain step, so there is nothing to advance.
  ///     <para>DISTINCT FROM <see cref="Refused"/> ON PURPOSE. Every hand-started session writes
  ///     plans, and every such write reaches the advance as a completion fact. A refusal is
  ///     something a caller must act on, and reporting one for each ordinary write trained both
  ///     the user and the model to ignore the notice that actually matters. Nothing was refused
  ///     here - there was simply no chain.</para></summary>
  NotInAChain,
}

/// <summary>The advance decision (issue #301 D1): a kind plus the facts the enactment needs.
///     <para><see cref="Position"/> is set only for <see cref="ChainAdvanceKind.StartStep"/>,
///     because it is the step to START - a refusal carries the step the completion NAMED in
///     its message instead, so a caller cannot mistake a refused position for a next one.</para>
///     <para>A refused advance is a normal outcome, never an exception: the model receives it
///     as a tool result and can correct itself.</para></summary>
public sealed record ChainAdvance(ChainAdvanceKind Kind, int? Position, string? Code, string? Reason)
{
  /// <summary>True when the harness has work to do (start a step, or complete the chain).</summary>
  public bool IsActionable => Kind is ChainAdvanceKind.StartStep or ChainAdvanceKind.CompleteChain;

  /// <summary>A refusal carrying its named code and the sentence explaining it.</summary>
  public static ChainAdvance Refused(string code, string reason)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(code);
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    return new ChainAdvance(ChainAdvanceKind.Refused, null, code, reason);
  }

  /// <summary>The answer for a session that executes no chain step: no code, no reason, and
  ///     nothing for a caller to report.</summary>
  public static ChainAdvance NoChain() => new(ChainAdvanceKind.NotInAChain, null, null, null);

  public override string ToString()
  {
    if (Kind == ChainAdvanceKind.StartStep)
    {
      return $"start step {Position}";
    }

    string code = Code is null ? string.Empty : " [" + Code + "]";
    string reason = Reason is null ? string.Empty : ": " + Reason;
    return $"{Kind}{code}{reason}";
  }
}
