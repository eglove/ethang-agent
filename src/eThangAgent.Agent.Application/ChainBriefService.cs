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
    ArgumentNullException.ThrowIfNull(assignment);
    if (!assignment.IsExecutable)
    {
      return Result.Failure<IReadOnlyList<Message>>(new DomainError("InvalidAssignment",
          "a chain brief needs a chain id, a plan id, and a step position; " + assignment + " names none."));
    }

    Result<ChainBriefFacts> facts = await GatherAsync(assignment, ct).ConfigureAwait(false);
    if (!facts.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<Message>>(facts.Error);
    }

    Result<ChainBrief> brief = ChainBriefRenderer.Render(facts.Value);
    return brief.IsSuccess
      ? Result.Success<IReadOnlyList<Message>>([Seed(brief.Value)])
      : Result.Failure<IReadOnlyList<Message>>(brief.Error);
  }

  /// <summary>Gathers the facts for one assignment. Extracted so the refusal paths read as the
  ///     three separate reads they are rather than as one nested conditional.</summary>
  private async Task<Result<ChainBriefFacts>> GatherAsync(ChainStepAssignment assignment,
      CancellationToken ct)
  {
    Result<ChainBriefChain> chain = await _source.GetChainAsync(assignment.ChainId, ct).ConfigureAwait(false);
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
    return gate.IsSuccess
      ? Result.Success(Facts(assignment, chain.Value, plan.Value, gate.Value))
      : Result.Failure<ChainBriefFacts>(gate.Error);
  }

  /// <summary>Assembles the fact record. The ledger key is DERIVED from the chain's title
  ///     rather than stored, so the brief and the system prompt (#311) cannot disagree about
  ///     where a chain's ledger lives.</summary>
  private static ChainBriefFacts Facts(ChainStepAssignment assignment, ChainBriefChain chain,
      ChainBriefPlan plan, IReadOnlyList<string> gate) =>
      new(chain.ChainId,
          chain.Title,
          plan.PlanId,
          plan.Version,
          plan.Goal,
          assignment.Position,
          plan.Steps,
          chain.Predecessors,
          gate,
          "sci." + ChainLedgerSlug.For(chain.Title) + "/ledger");

  /// <summary>The seed message, flagged as a summary so every surface treats it as harness
  ///     context rather than as user voice.
  ///     <para>THE PREFIX IS NOT ADDED HERE: the renderer already emits it as the brief's first
  ///     line, and prepending it again would state the same provenance twice.</para></summary>
  private static Message Seed(ChainBrief brief) =>
      new(Role.System, brief.Content, DateTimeOffset.UtcNow, IsSummary: true);
}
