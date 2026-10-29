using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Implements the Agent Domain's brief port over the real stores (issue #302).
///     <para>WHY HERE AND NOT IN THE AGENT DOMAIN: the facts live in the Plan Domain, the
///     chain record, and the workspace's state keys, and the Agent Domain references none of
///     them. The composition root is the one place allowed to know all three.</para>
///     <para>EVERY READ IS A READ. Nothing here writes, and nothing here renders: the brief's
///     text is the Agent Domain's, so a second renderer cannot drift from the first.</para></summary>
internal sealed class CompositionChainBriefSource(ChainService chains, PlanService plans,
    IStateService state, ISkillCatalog skills) : IChainBriefSource
{
  /// <summary>The state key holding the project's own check commands, as a JSON array of
  ///     strings. The same key the completion gate reads, so the brief and the gate can never
  ///     disagree about what this workspace requires.</summary>
  private const string GoalCheckKey = "goal/check";

  /// <summary>The embedded skill that carries the reviewer's rubric (issue #217). Named here
  ///     rather than assumed present: until #217 ships, a review brief says plainly that no
  ///     rubric is installed instead of inventing one.</summary>
  private const string ReviewRubricSkill = "reviewing-plans";

  private readonly ChainService _chains = chains ?? throw new ArgumentNullException(nameof(chains));
  private readonly PlanService _plans = plans ?? throw new ArgumentNullException(nameof(plans));
  private readonly IStateService _state = state ?? throw new ArgumentNullException(nameof(state));
  private readonly ISkillCatalog _skills = skills ?? throw new ArgumentNullException(nameof(skills));

  /// <inheritdoc />
  public async Task<Result<ChainBriefChain>> GetChainAsync(int chainId, int position,
      CancellationToken ct = default)
  {
    Result<Chain> chain = await _chains.GetAsync(chainId, ct).ConfigureAwait(false);
    return chain.IsSuccess
      ? Result.Success(new ChainBriefChain(chain.Value.Id, chain.Value.Title, chain.Value.PlanId,
          Predecessors(chain.Value), RoleOf(chain.Value, position)))
      : Result.Failure<ChainBriefChain>(chain.Error);
  }

  /// <summary>Whether the reviewer's rubric skill is installed (issue #306). The catalog is the
  ///     same one this session's listing is built from, so the brief and the skill listing
  ///     cannot disagree about whether a rubric exists.</summary>
  public async Task<Result<bool>> GetReviewRubricAvailableAsync(CancellationToken ct = default)
  {
    Result<IReadOnlyList<SkillDefinition>> listed = await _skills.ListAsync(ct).ConfigureAwait(false);
    return listed.IsSuccess
      ? Result.Success(listed.Value.Any(skill =>
          string.Equals(skill.Name, ReviewRubricSkill, StringComparison.OrdinalIgnoreCase)))
      : Result.Failure<bool>(listed.Error);
  }

  /// <summary>The role of the chain step at <paramref name="position"/>, or null when the chain
  ///     holds no step there.
  ///     <para>THE RECORD IS THE SOURCE, not the assignment: a resumed or restarted launch reads
  ///     the row, and the row is what #218's separation rule reads too.</para></summary>
  private static ChainStepRole? RoleOf(Chain chain, int position) =>
      chain.Steps.FirstOrDefault(step => step.Position == position)?.Role;

  /// <inheritdoc />
  public async Task<Result<ChainBriefPlan>> GetPlanAsync(int planId, CancellationToken ct = default)
  {
    Result<Plan> plan = await _plans.GetAsync(planId, ct).ConfigureAwait(false);
    return plan.IsSuccess
      ? Result.Success(new ChainBriefPlan(plan.Value.Id, plan.Value.Version, plan.Value.Goal,
          [.. plan.Value.Steps.Select(step => new ChainBriefPlanStep(step.Position, step.Title, step.Detail))]))
      : Result.Failure<ChainBriefPlan>(plan.Error);
  }

  /// <inheritdoc />
  public async Task<Result<IReadOnlyList<string>>> GetGateCommandsAsync(CancellationToken ct = default)
  {
    Result<string> stored = await _state.GetAsync(GoalCheckKey, ct).ConfigureAwait(false);
    // An UNSET gate is an ordinary answer, not a failure: the completion gate treats it the
    // same way, and a brief that refused to render because a workspace declared no gate would
    // block every chain in that workspace.
    return Result.Success(stored.IsSuccess ? ParseCommands(stored.Value) : []);
  }

  /// <summary>The chain steps that already ran, in position order. A step that is still
  ///     Pending or Running is not a predecessor: it has concluded nothing yet, and listing it
  ///     would tell this session about work that is happening rather than work that happened.</summary>
  private static IReadOnlyList<ChainBriefPredecessor> Predecessors(Chain chain) =>
      [.. chain.Steps
          .Where(step => step.Status is ChainStepStatus.Done or ChainStepStatus.Failed
              or ChainStepStatus.Skipped or ChainStepStatus.Parked)
          .OrderBy(step => step.Position)
          .Select(step => new ChainBriefPredecessor(step.Position, step.Status, step.Outcome))];

  /// <summary>The commands in the stored array. A payload that is not a JSON array of strings
  ///     yields NO commands rather than an error: the completion gate reports a malformed
  ///     <c>goal/check</c> in its own words, and duplicating that judgement here would give the
  ///     workspace two answers about whether it has a gate.</summary>
  private static IReadOnlyList<string> ParseCommands(string stored)
  {
    try
    {
      return JsonSerializer.Deserialize<string[]>(stored) is { Length: > 0 } commands
        ? [.. commands.Where(command => !string.IsNullOrWhiteSpace(command))]
        : [];
    }
    // Named decision (CA1031): a malformed goal/check is the completion gate's to report.
    // Failing the brief here would block a chain over a value it does not own.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      return [];
    }
  }
}
