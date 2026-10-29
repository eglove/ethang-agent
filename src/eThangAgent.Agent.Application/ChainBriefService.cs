using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application;

/// <summary>Builds the seed a chain step's session starts with (issue #302): gather the facts,
///     render the brief, wrap it as a summary message.
///     <para>THE SEED SHAPE IS THE HANDOFF'S, deliberately. A chain brief is a System message
///     flagged <see cref="Message.IsSummary"/> under its own prefix, exactly as
///     <c>SessionHandoffSeed</c> does for a compaction handoff — the factory already persists
///     a seeded prefix explicitly, and every surface that understands a summary message
///     understands this one. A different shape would need a different persistence path, and a
///     second path is a second thing to get wrong.</para>
///     <para>A REFUSAL IS A VALUE. A chain step whose plan step is gone returns the named
///     error rather than an empty seed: starting a session with nothing to do produces work
///     nobody asked for (D5).</para></summary>
public sealed class ChainBriefService(IChainBriefSource source)
{
  private readonly IChainBriefSource _source = source ?? throw new ArgumentNullException(nameof(source));

  /// <summary>The seed for <paramref name="assignment"/>, or the named refusal.
  ///     <para>THE ASSIGNMENT IS THE ONLY INPUT: it names the chain and the position, and
  ///     everything else is read from the records. Passing the plan or the step in would let a
  ///     caller hand the brief facts that disagree with the chain it is describing.</para></summary>
  /// <param name="assignment">The chain step the session is being started for.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<IReadOnlyList<Message>>> BuildSeedAsync(ChainStepAssignment assignment,
      CancellationToken ct = default)
  {
    Result<ChainBriefLaunch> launch = await BuildLaunchAsync(assignment, ct).ConfigureAwait(false);
    return launch.IsSuccess
      ? Result.Success(launch.Value.Seed)
      : Result.Failure<IReadOnlyList<Message>>(launch.Error);
  }

  /// <summary>The seed AND the contract for <paramref name="assignment"/> (issue #311): one
  ///     gather, both artifacts.
  ///     <para>WHY ONE CALL: the contract's ledger key and gate ARE the brief's facts, and a
  ///     caller that built them separately would read the chain twice and could render a
  ///     prompt describing a step the seed does not. The two artifacts are also always wanted
  ///     together — a session started for a chain step needs both, and a caller that had only
  ///     one would be a caller that forgot the other.</para>
  ///     <para>A REFUSAL IS STILL A VALUE: the same named errors <see cref="BuildSeedAsync"/>
  ///     reports (ChainStepMissing, ChainNotFound, ...) come back here, because the refusal is
  ///     about the facts and the facts are shared.</para></summary>
  /// <param name="assignment">The chain step the session is being started for.</param>
  /// <param name="ct">Cancellation.</param>
  public async Task<Result<ChainBriefLaunch>> BuildLaunchAsync(ChainStepAssignment assignment,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(assignment);
    if (!assignment.IsExecutable)
    {
      return Result.Failure<ChainBriefLaunch>(new DomainError("InvalidAssignment",
          "a chain brief needs a chain id, a plan id, and a step position; " + assignment + " names none."));
    }

    Result<ChainBriefFacts> facts = await GatherAsync(assignment, ct).ConfigureAwait(false);
    if (!facts.IsSuccess)
    {
      return Result.Failure<ChainBriefLaunch>(facts.Error);
    }

    Result<ChainBrief> brief = ChainBriefRenderer.Render(facts.Value);
    return brief.IsSuccess
      ? Result.Success(new ChainBriefLaunch([Seed(brief.Value)], ChainContract.For(facts.Value)))
      : Result.Failure<ChainBriefLaunch>(brief.Error);
  }

  /// <summary>Gathers the facts for one assignment. Extracted so the refusal paths read as the
  ///     three separate reads they are rather than as one nested conditional.</summary>
  private async Task<Result<ChainBriefFacts>> GatherAsync(ChainStepAssignment assignment,
      CancellationToken ct)
  {
    Result<ChainBriefChain> chain = await _source
        .GetChainAsync(assignment.ChainId, assignment.Position, ct).ConfigureAwait(false);
    if (!chain.IsSuccess)
    {
      return Result.Failure<ChainBriefFacts>(chain.Error);
    }

    Result<ChainBriefPlan> plan = await _source.GetPlanAsync(assignment.PlanId, ct).ConfigureAwait(false);
    if (!plan.IsSuccess)
    {
      return Result.Failure<ChainBriefFacts>(plan.Error);
    }

    Result<IReadOnlyList<string>> gate = await _source.GetGateCommandsAsync(ct).ConfigureAwait(false);
    if (!gate.IsSuccess)
    {
      return Result.Failure<ChainBriefFacts>(gate.Error);
    }

    // THE RUBRIC IS ASKED FOR ONLY WHEN SOMETHING USES IT (issue #306): an implement step never
    // reads the skill catalog, so a chain with no review step costs exactly what it did before.
    Result<bool> rubric = RoleOf(chain.Value, assignment).IsReview
      ? await _source.GetReviewRubricAvailableAsync(ct).ConfigureAwait(false)
      : Result.Success(false);
    return rubric.IsSuccess
      ? Result.Success(Facts(assignment, chain.Value, plan.Value, gate.Value, rubric.Value))
      : Result.Failure<ChainBriefFacts>(rubric.Error);
  }

  /// <summary>The role this brief is for (issue #306): the ASSIGNMENT's when the launcher read
  ///     it, else the RECORD's. Both are the same value in production — the launcher reads it
  ///     from the record — but a caller that did not load the chain yet still gets the record's
  ///     answer rather than a silent 'implement'.
  ///     <para>FALLING BACK TO IMPLEMENT IS THE LAST RESORT and only when the chain holds no step
  ///     at that position, which <see cref="ChainBriefRenderer.RefusalFor"/> already refuses.</para></summary>
  private static ChainStepRole RoleOf(ChainBriefChain chain, ChainStepAssignment assignment) =>
      assignment.Role
      ?? chain.Role
      ?? ChainStepRole.Implement;

  /// <summary>Assembles the fact record. The ledger key is DERIVED from the chain's title
  ///     rather than stored, so the brief and the system prompt (#311) cannot disagree about
  ///     where a chain's ledger lives.</summary>
  private static ChainBriefFacts Facts(ChainStepAssignment assignment, ChainBriefChain chain,
      ChainBriefPlan plan, IReadOnlyList<string> gate, bool rubricAvailable) =>
      new(chain.ChainId,
          chain.Title,
          plan.PlanId,
          plan.Version,
          plan.Goal,
          assignment.Position,
          plan.Steps,
          chain.Predecessors,
          gate,
          ChainLedgerSlug.KeyFor(chain.Title),
          RoleOf(chain, assignment),
          rubricAvailable);

  /// <summary>The seed message, flagged as a summary so every surface treats it as harness
  ///     context rather than as user voice.
  ///     <para>THE PREFIX IS NOT ADDED HERE: the renderer already emits it as the brief's first
  ///     line, and prepending it again would state the same provenance twice.</para></summary>
  private static Message Seed(ChainBrief brief) =>
      new(Role.System, brief.Content, DateTimeOffset.UtcNow, IsSummary: true);
}
