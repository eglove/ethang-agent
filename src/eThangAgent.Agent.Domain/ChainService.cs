using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The fields a new chain is created from (issue #300). A parameter record rather
///     than eight positional arguments: the create call sites name what they mean, and the
///     record is the one place the creation contract is stated.</summary>
/// <param name="WorkspaceId">The workspace the chain belongs to.</param>
/// <param name="PlanId">The plan the chain executes; the chain references it and never copies it.</param>
/// <param name="PlanVersion">The plan version the chain started at; a later revision is
///     visible at brief time but never retroactive.</param>
/// <param name="Title">A human label for the chain, normally the plan's title.</param>
/// <param name="SessionId">The session that created the chain, if any. The harness owns the
///     execution regardless — a creating session may close immediately.</param>
/// <param name="Steps">The steps this chain executes, in any order; the aggregate orders and
///     de-duplicates them. Each names a plan position and the role assigned to the session
///     that will run it (issue #313 D2).</param>
public sealed record ChainCreation(string WorkspaceId, int PlanId, int PlanVersion, string Title,
    string? SessionId, IReadOnlyList<ChainStepSeed> Steps)
{
  /// <summary>The position-only shape: every step is an implementer step. Stated once so the
  ///     many call sites that assign no role do not each re-derive the default.</summary>
  /// <param name="workspaceId">The workspace the chain belongs to.</param>
  /// <param name="planId">The plan the chain executes.</param>
  /// <param name="planVersion">The plan version the chain starts at.</param>
  /// <param name="title">A human label for the chain.</param>
  /// <param name="sessionId">The session that created the chain, if any.</param>
  /// <param name="stepPositions">The plan step positions this chain executes.</param>
  public static ChainCreation Implementing(string workspaceId, int planId, int planVersion,
      string title, string? sessionId, IReadOnlyList<int> stepPositions) =>
      new(workspaceId, planId, planVersion, title, sessionId,
          [.. (stepPositions ?? throw new ArgumentNullException(nameof(stepPositions)))
              .Select(ChainStepSeed.Implementing)]);
}

/// <summary>
/// Application service over <see cref="IChainStore"/>: builds chains, applies aggregate
/// mutations, and CAS-saves with the version it loaded. The shape mirrors
/// <c>PlanService</c> deliberately — the chain is the plan's sibling, so a second
/// persistence idiom would be a second thing to get wrong.
/// <para>Aggregate <see cref="ChainInputException"/>s map to the InvalidTransition
/// DomainError; store failures (ChainNotFound, VersionConflict) pass through verbatim.
/// Events are raised in-process on success and never persisted.</para>
/// <para>Every mutation takes a <see cref="ChainActor"/>: the harness advancing a chain has
/// no session, and the record must be able to say so (issue #300 D6).</para>
/// </summary>
public sealed class ChainService(IChainStore store)
{
  private readonly IChainStore _store = store ?? throw new ArgumentNullException(nameof(store));

  // Action<T> events follow the PlanService precedent; CA1003's EventHandler<TEventArgs>
  // preference would change the advertised API shape for no gain.
#pragma warning disable CA1003
  public event Action<ChainCreated>? Created;
  public event Action<ChainStepChanged>? StepChanged;
  public event Action<ChainStatusChanged>? StatusChanged;
#pragma warning restore CA1003

  public async Task<Result<Chain>> CreateAsync(ChainCreation creation, DateTimeOffset now,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(creation);
    Chain chain;
    try
    {
      chain = Chain.NewFromSeeds(creation.WorkspaceId, creation.PlanId, creation.PlanVersion,
          creation.Title, creation.SessionId, creation.Steps, now);
    }
    catch (ChainInputException ex)
    {
      return Result.Failure<Chain>(new DomainError("InvalidTransition", ex.Message));
    }

    Result<Chain> created = await _store.CreateAsync(chain, ct).ConfigureAwait(false);
    if (created.IsSuccess)
    {
      Created?.Invoke(new ChainCreated(created.Value.Id, creation.PlanId, creation.PlanVersion,
          creation.SessionId, now));
    }

    return created;
  }

  public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default) => _store.GetAsync(id, ct);

  public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default) =>
    _store.ListAsync(status, ct);

  /// <summary>Claims the step at <paramref name="position"/> for a session.</summary>
  public Task<Result<Chain>> ClaimStepAsync(int id, int position, string sessionId, DateTimeOffset now,
      CancellationToken ct = default)
    => MutateAsync(id, chain => chain.ClaimStep(position, sessionId, now), now,
        (_, _) => StepChanged?.Invoke(
            new ChainStepChanged(id, position, "claimed", ChainActor.Session(sessionId), now)), ct);

  /// <summary>Settles the step at <paramref name="position"/> with an outcome.</summary>
  public Task<Result<Chain>> SettleStepAsync(int id, int position, ChainStepStatus status,
      ChainActor actor, string? outcome, DateTimeOffset now, CancellationToken ct = default)
    => MutateAsync(id, chain => chain.SettleStep(position, status, actor, outcome, now), now,
        (_, _) => StepChanged?.Invoke(new ChainStepChanged(id, position, KindOf(status), actor, now)), ct);

  /// <summary>The chain claiming a step for <paramref name="sessionId"/>, or null when this
  ///     session is executing none (issue #301).</summary>
  public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
    => _store.FindBySessionAsync(sessionId, ct);

  /// <summary>Moves a live claim to a different session without spending an attempt (issue
  ///     #301): the harness claims before the new session's id exists.</summary>
  public Task<Result<Chain>> ReassignStepAsync(int id, int position, string sessionId,
      DateTimeOffset now, CancellationToken ct = default)
    => MutateAsync(id, chain => chain.ReassignStep(position, sessionId, now), now,
        (_, _) => StepChanged?.Invoke(
            new ChainStepChanged(id, position, "claimed", ChainActor.Session(sessionId), now)), ct);

  /// <summary>Re-enters a FAILED step for the session that ran it (issue #303 D1): a retry
  ///     resumes, it does not restart. The attempt count is untouched - see
  ///     <see cref="Chain.RetryStep"/>.</summary>
  public Task<Result<Chain>> RetryStepAsync(int id, int position, string sessionId,
      DateTimeOffset now, CancellationToken ct = default)
    => MutateAsync(id, chain => chain.RetryStep(position, sessionId, now), now,
        (_, _) => StepChanged?.Invoke(
            new ChainStepChanged(id, position, "retried", ChainActor.Session(sessionId), now)), ct);

  /// <summary>Releases a claim whose launch failed, leaving the step available (issue #301).
  ///     The attempt count is kept: the launch was an attempt even though nothing ran.</summary>
  public Task<Result<Chain>> ReleaseStepAsync(int id, int position, ChainActor actor,
      DateTimeOffset now, CancellationToken ct = default)
    => MutateAsync(id, chain => chain.ReleaseStep(position, actor, now), now,
        (_, _) => StepChanged?.Invoke(new ChainStepChanged(id, position, "pending", actor, now)), ct);

  /// <summary>Parks the chain, naming why and which step stopped it.</summary>
  public Task<Result<Chain>> ParkAsync(int id, string reason, int? position, ChainActor actor,
      DateTimeOffset now, CancellationToken ct = default)
    => MutateAsync(id, chain => chain.Park(reason, position, actor, now), now,
        (before, after) => StatusChanged?.Invoke(new ChainStatusChanged(
            id, before.Status, ChainStatus.Parked, actor, after.ParkReason, now)), ct);

  /// <summary>Un-parks the chain (Parked -> Running).</summary>
  public Task<Result<Chain>> ResumeAsync(int id, ChainActor actor, DateTimeOffset now,
      CancellationToken ct = default)
    => MutateAsync(id, chain => chain.Resume(actor, now), now,
        (before, _) => StatusChanged?.Invoke(new ChainStatusChanged(
            id, before.Status, ChainStatus.Running, actor, null, now)), ct);

  /// <summary>Moves the chain's status through the aggregate's state machine.</summary>
  public Task<Result<Chain>> SetStatusAsync(int id, ChainStatus target, ChainActor actor,
      DateTimeOffset now, CancellationToken ct = default)
    => MutateAsync(id, chain => chain.SetStatus(target, actor, now), now,
        (before, after) => StatusChanged?.Invoke(new ChainStatusChanged(
            id, before.Status, target, actor, after.ParkReason, now)), ct);

  /// <summary>Loads, applies the mutation, and CAS-saves at the version it loaded. A version
  ///     drift between the load and the save surfaces as VersionConflict with the current
  ///     version named, so the caller can re-read and retry.</summary>
  private async Task<Result<Chain>> MutateAsync(int id, Func<Chain, Chain> mutate, DateTimeOffset now,
      Action<Chain, Chain> onSaved, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(mutate);
    Result<Chain> loaded = await _store.GetAsync(id, ct).ConfigureAwait(false);
    if (!loaded.IsSuccess)
    {
      return loaded;
    }

    Chain mutated;
    try
    {
      mutated = mutate(loaded.Value);
    }
    catch (ChainInputException ex)
    {
      return Result.Failure<Chain>(new DomainError("InvalidTransition", ex.Message));
    }

    Result<Chain> saved = await _store
        .SaveAsync(mutated with { UpdatedAt = now }, loaded.Value.Version, ct)
        .ConfigureAwait(false);
    if (!saved.IsSuccess)
    {
      return saved;
    }

    onSaved(loaded.Value, saved.Value);
    return saved;
  }

  /// <summary>The event kind a settled status reports. The settled set is closed, so this
  ///     switch is exhaustive and no fallback can invent a kind.</summary>
  private static string KindOf(ChainStepStatus status) => status switch
  {
    ChainStepStatus.Done => "done",
    ChainStepStatus.Failed => "failed",
    ChainStepStatus.Skipped => "skipped",
    ChainStepStatus.Parked => "parked",
    ChainStepStatus.Pending => "pending",
    ChainStepStatus.Running => "running",
    _ => throw new ArgumentOutOfRangeException(nameof(status), status,
        "unknown chain step status; the status set is closed."),
  };
}
