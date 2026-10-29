namespace eThangAgent.AgentDomain;

/// <summary>The standing facts of the chain a session is executing (issue #311 D1): what it is
///     assigned, where the chain's ledger lives, and what the workspace's gate is.
///     <para>WHY THIS IS NOT <see cref="ChainBriefFacts"/>: the brief is an ASSIGNMENT — it names
///     the plan's goal, the step's title and detail, and every predecessor, because it is the
///     session's first message and has to be self-contained. The contract is an IDENTITY — it
///     says what the session IS, and it sits in every request, so anything it restated from the
///     brief would be paid for on every iteration and could drift from the record. Four facts
///     and the gate are the whole of it.</para>
///     <para>DERIVED FROM THE SAME FACTS AS THE BRIEF (<see cref="For"/>), so the ledger key the
///     prompt names and the key the brief names are one value rather than two derivations that
///     happen to agree today.</para>
///     <para>THE WIRE SHAPE. This record rides the host settings JSON (the app resolves it, the
///     host renders the prompt), so it must serialize plainly: positional members, no behavior
///     that a round trip could lose.</para></summary>
/// <param name="ChainId">The chain the session's step belongs to.</param>
/// <param name="PlanId">The plan the chain executes.</param>
/// <param name="Position">The 1-based chain step position this session owns.</param>
/// <param name="LedgerKey">Where the chain's ledger lives — <c>sci.&lt;slug&gt;/ledger</c>.</param>
/// <param name="GateCommands">The workspace's <c>goal/check</c> commands as stored; null when
///     the payload carried none. Read <see cref="Gates"/> instead, which is never null.</param>
public sealed record ChainContract(int ChainId, int PlanId, int Position, string LedgerKey,
    IReadOnlyList<string>? GateCommands = null)
{
  /// <summary>The gate commands, never null. An absent list and an empty list are the same
  ///     fact — this workspace declares no gate — which is exactly how the completion gate
  ///     reads an unset <c>goal/check</c>, so the prompt and the gate cannot disagree about
  ///     whether a gate applies.
  ///     <para>NOT ON THE WIRE: this is a view over <see cref="GateCommands"/>, so serializing
  ///     it would put the same list in the payload twice under two names.</para></summary>
  [System.Text.Json.Serialization.JsonIgnore]
  public IReadOnlyList<string> Gates => GateCommands ?? [];

  /// <summary>The contract for a chain step's gathered brief facts.
  ///     <para>THE ONE DERIVATION SITE: the brief (#302) and the contract (#311) both come from
  ///     <see cref="ChainBriefFacts"/>, so the ledger key, the plan id, and the gate are the same
  ///     values in both. A second derivation would be a second answer to one question.</para></summary>
  /// <param name="facts">The facts the brief was rendered from.</param>
  public static ChainContract For(ChainBriefFacts facts)
  {
    ArgumentNullException.ThrowIfNull(facts);
    return new ChainContract(facts.ChainId, facts.PlanId, facts.AssignedPosition, facts.LedgerKey,
        facts.GateCommands);
  }

  public override string ToString() =>
      string.Create(System.Globalization.CultureInfo.InvariantCulture,
          $"chain #{ChainId} step {Position} of plan #{PlanId}");
}
