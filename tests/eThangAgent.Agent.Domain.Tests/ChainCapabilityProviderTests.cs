using System.Globalization;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #313: the plan-to-execution transition against FAKE collaborators - the
///     one action that converts a plan into a chain, its total validation, and the claim
///     bookkeeping a failed launch must not leak. No database, no tab, no host process.</summary>
public class ChainCapabilityProviderTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

  private sealed class FakeStore : IChainStore
  {
    private readonly Dictionary<int, Chain> _chains = [];
    private int _next = 1;

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
    {
      Chain saved = draft with { Id = _next++, Version = 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(_chains.TryGetValue(id, out Chain? chain)
            ? Result.Success(chain)
            : Result.Failure<Chain>(new DomainError("ChainNotFound", $"no chain #{id}")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainCatalog(
            [.. _chains.Values.Where(c => status is null || c.Status == status)], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
    {
      if (!_chains.TryGetValue(chain.Id, out Chain? current))
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("ChainNotFound", "gone")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("VersionConflict", "stale")));
      }

      Chain saved = chain with { Version = current.Version + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));
  }

  private sealed class FakePlans(params int[] positions) : IChainPlanSource
  {
    public Result<ChainPlanFacts>? Override { get; set; }

    public Task<Result<ChainPlanFacts>> GetPlanAsync(int planId, CancellationToken ct = default)
        => Task.FromResult(Override ?? Result.Success(new ChainPlanFacts(planId, 4,
            "Ship the dashboard", "C:\\ws", positions)));
  }

  private sealed class FakeStart : IChainStartPort
  {
    public List<ChainStepAssignment> Started { get; } = [];

    public Result<string>? Outcome { get; set; }

    public Task<Result<string>> StartFirstStepAsync(ChainStepAssignment assignment,
        CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(Outcome ?? Result.Success("session-step-1"));
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static Task<CapabilityInvocationResult> InvokeAsync(ChainCapabilityProvider provider,
      string json)
      => provider.InvokeAsync("start", json, TestContext.Current.CancellationToken);

  /// <summary>A create call's arguments, built rather than written as an escaped literal.
  ///     <para>WHY A BUILDER: an escaped JSON string in a C# source file is a JSON string the
  ///     analyzer cannot tell from a real one (JSON002), and every test that spells one out
  ///     re-derives the same quoting. Building it states the SHAPE under test and leaves the
  ///     escaping to one place.</para></summary>
  /// <param name="planId">The plan to execute.</param>
  /// <param name="steps">The steps array body, already rendered.</param>
  /// <param name="extraMember">An extra top-level member, for the unknown-parameter case.</param>
  private static string Plan(int planId, string? steps = null, string? extraMember = null)
  {
    string members = $"\"planId\": {planId.ToString(CultureInfo.InvariantCulture)}";
    if (steps is not null)
    {
      members += $", \"steps\": [{steps}]";
    }

    if (extraMember is not null)
    {
      members += ", " + extraMember;
    }

    return "{" + members + "}";
  }

  /// <summary>One step object: the position, and optionally a role, a parallel group, and an
  ///     extra member the case states.</summary>
  /// <param name="position">The 1-based plan step position.</param>
  /// <param name="role">The role token, when the case states one.</param>
  /// <param name="group">The parallel group id, when the case states one.</param>
  /// <param name="extraMember">An extra member's rendered body, for the refused-member cases.</param>
  private static string Step(int position, string? role = null, int? group = null,
      string? extraMember = null)
  {
    string members = $"\"position\": {position.ToString(CultureInfo.InvariantCulture)}";
    if (role is not null)
    {
      members += $", \"role\": \"{role}\"";
    }

    if (group is { } id)
    {
      members += $", \"group\": {id.ToString(CultureInfo.InvariantCulture)}";
    }

    if (extraMember is not null)
    {
      members += ", " + extraMember;
    }

    return "{" + members + "}";
  }

  private static (ChainCapabilityProvider Provider, FakeStore Store, FakePlans Plans, FakeStart Start)
      New(params int[] planSteps)
  {
    FakeStore store = new();
    FakePlans plans = new(planSteps);
    FakeStart start = new();
    ChainService chains = new(store);
    return (new ChainCapabilityProvider(chains, plans, start, () => "session-creator",
        new FixedClock(T0)), store, plans, start);
  }

  /// <summary>The git seam as the creation asks it: whether the workspace is a repository.
  ///     <para>ONLY THE LIST IS ANSWERED, because that is the one call that distinguishes a
  ///     repository from a plain directory - the other two would have to create or destroy
  ///     something to answer at all.</para></summary>
  private sealed class FakeWorktrees(bool isRepository) : IGitWorktreeAccess
  {
    public Task<Result<IReadOnlyList<WorktreeInfo>>> ListAsync(string repoRoot,
        CancellationToken ct = default)
        => Task.FromResult(isRepository
            ? Result.Success<IReadOnlyList<WorktreeInfo>>([])
            : Result.Failure<IReadOnlyList<WorktreeInfo>>(new DomainError("GitFailed",
                $"not a git repository: {repoRoot}")));

    public Task<Result<WorktreeInfo>> CreateAsync(string repoRoot, string name,
        CancellationToken ct = default)
        => throw new NotSupportedException("creation does not provision a worktree");

    public Task<Result<bool>> RemoveAsync(string repoRoot, string name, bool force,
        CancellationToken ct = default)
        => throw new NotSupportedException("creation does not remove a worktree");
  }

  private static (ChainCapabilityProvider Provider, FakeStore Store, FakeStart Start)
      NewWithGit(bool isRepository, params int[] planSteps)
  {
    FakeStore store = new();
    FakeStart start = new();
    ChainService chains = new(store);
    return (new ChainCapabilityProvider(chains, new FakePlans(planSteps), start,
        () => "session-creator", new FixedClock(T0),
        workspaceGit: new ChainWorkspaceGit(new FakeWorktrees(isRepository), "C:\\ws",
            new FakeProvisioner())),
        store, start);
  }

  /// <summary>The provisioner a group's members are made by: one checkout per worktree name,
  ///     under the workspace's own <c>.worktrees</c> directory.</summary>
  private sealed class FakeProvisioner : IWorktreeProvisioner
  {
    public Task<Result<WorktreeProvision>> CreateAsync(string repoRoot, string name,
        CancellationToken ct = default)
        => Task.FromResult(Result.Success(new WorktreeProvision(name,
            repoRoot + "\\.worktrees\\" + name, name)));
  }

  [Fact]
  public async Task Create_WithAGroupInANonRepository_IsRefusedBeforeAnythingIsCreated()
  {
    (ChainCapabilityProvider provider, FakeStore store, FakeStart start) =
        NewWithGit(isRepository: false, 1, 2, 3);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1, group: 1) + ", " + Step(2, group: 1) + ", " + Step(3)))
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains(ChainCreationCode.GroupNeedsRepository, result.Content,
        StringComparison.Ordinal);
    Assert.Empty(start.Started);
    ChainCatalog listed = Must(await store.ListAsync(null, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Empty(listed.Chains);
  }

  [Fact]
  public async Task Create_WithoutAGroupInANonRepository_IsCreated()
  {
    // Only a group needs worktrees, so a chain that runs one step at a time is unaffected by
    // a workspace that is not a repository.
    (ChainCapabilityProvider provider, _, FakeStart start) =
        NewWithGit(isRepository: false, 1, 2);

    CapabilityInvocationResult result = await InvokeAsync(provider, Plan(7)).ConfigureAwait(true);

    Assert.False(result.IsError);
    _ = Assert.Single(start.Started);
  }

  [Fact]
  public async Task Create_WithAGroupInARepository_IsCreated()
  {
    (ChainCapabilityProvider provider, _, FakeStart start) = NewWithGit(isRepository: true, 1, 2, 3);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1, group: 1) + ", " + Step(2, group: 1) + ", " + Step(3)))
        .ConfigureAwait(true);

    Assert.False(result.IsError);
    // BOTH MEMBERS START, not only the first: a group's members run at the same moment, so
    // creation launches every one of them.
    Assert.Equal([1, 2], [.. start.Started.Select(assignment => assignment.Position)]);
  }

  [Fact]
  public async Task Create_ConvertsThePlanAndStartsStepOne()
  {
    (ChainCapabilityProvider provider, _, _, FakeStart start) = New(1, 2);

    CapabilityInvocationResult result = await InvokeAsync(provider, Plan(7)).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.Contains("[chain] created #1 from plan #7 v4: 2 step(s), step 1 started as session",
        result.Content, StringComparison.Ordinal);
    ChainStepAssignment assignment = Assert.Single(start.Started);
    Assert.Equal(1, assignment.Position);
    Assert.Equal(7, assignment.PlanId);
    Assert.Equal(ChainStepRoleKind.Implement, assignment.Role?.Kind);
  }

  [Fact]
  public async Task Create_ClaimsTheFirstStepForTheRealSession_WithoutASecondAttempt()
  {
    (ChainCapabilityProvider provider, FakeStore store, _, _) = New(1, 2);

    _ = await InvokeAsync(provider, Plan(7)).ConfigureAwait(true);

    Chain stored = Must(await store.GetAsync(1, TestContext.Current.CancellationToken).ConfigureAwait(true));
    ChainStep first = Assert.Single(stored.Steps, s => s.Position == 1);
    Assert.Equal("session-step-1", first.SessionId);
    Assert.Equal(1, first.Attempts);
    Assert.Equal(ChainStepStatus.Running, first.Status);
  }

  [Fact]
  public async Task Create_WithNoExecutableStep_IsRefusedAndCreatesNothing()
  {
    (ChainCapabilityProvider provider, FakeStore store, _, FakeStart start) = New();

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, steps: string.Empty)).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ChainNoExecutableSteps", result.Content, StringComparison.Ordinal);
    Assert.Empty(start.Started);
    ChainCatalog listed = Must(await store.ListAsync(null, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Empty(listed.Chains);
  }

  [Fact]
  public async Task Create_NamingAPositionThePlanDoesNotHave_IsRefused()
  {
    (ChainCapabilityProvider provider, _, _, FakeStart start) = New(1, 2);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(9))).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ChainStepMissing", result.Content, StringComparison.Ordinal);
    Assert.Empty(start.Started);
  }

  [Fact]
  public async Task Create_WithAnUnreadablePlan_IsRefusedNamingTheUnderlyingCause()
  {
    (ChainCapabilityProvider provider, _, FakePlans plans, FakeStart start) = New(1);
    plans.Override = Result.Failure<ChainPlanFacts>(new DomainError("PlanNotFound", "no plan #7"));

    CapabilityInvocationResult result = await InvokeAsync(provider, Plan(7)).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ChainPlanUnreadable", result.Content, StringComparison.Ordinal);
    Assert.Contains("no plan #7", result.Content, StringComparison.Ordinal);
    Assert.Empty(start.Started);
  }

  [Fact]
  public async Task Create_FromASessionExecutingAChainStep_IsRefusedAsNested()
  {
    (ChainCapabilityProvider provider, FakeStore store, _, _) = New(1);
    Chain existing = Must(await new ChainService(store).CreateAsync(
        ChainCreation.Implementing("C:\\ws", 3, 1, "outer", "session-creator", [1]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await store.SaveAsync(existing.ClaimStep(1, "session-creator", T0), existing.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    CapabilityInvocationResult result = await InvokeAsync(provider, Plan(7)).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("ChainStepCannotCreateChain", result.Content, StringComparison.Ordinal);
    Assert.Contains("chain #1", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_WhenTheLaunchFails_ReleasesTheClaimAndKeepsTheAttempt()
  {
    (ChainCapabilityProvider provider, FakeStore store, _, FakeStart start) = New(1, 2);
    start.Outcome = Result.Failure<string>(new DomainError("SessionLaunchFailed", "no host"));

    CapabilityInvocationResult result = await InvokeAsync(provider, Plan(7)).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("SessionLaunchFailed", result.Content, StringComparison.Ordinal);
    // The CHAIN SURVIVES: the record is a fact about the plan, the launch is a fact about
    // this process. The step is available again rather than parked behind a session that
    // never existed, and the attempt the launch spent is the only one.
    Chain stored = Must(await store.GetAsync(1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    ChainStep first = Assert.Single(stored.Steps, s => s.Position == 1);
    Assert.Equal(ChainStepStatus.Pending, first.Status);
    Assert.Null(first.SessionId);
    Assert.Equal(1, first.Attempts);
  }

  [Fact]
  public async Task Create_DeclaresRolesPerStep_AndDefaultsTheRestToImplement()
  {
    (ChainCapabilityProvider provider, FakeStore store, _, _) = New(1, 2);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1) + ", " + Step(2, "review"))).ConfigureAwait(true);

    Assert.False(result.IsError);
    Chain stored = Must(await store.GetAsync(1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStepRoleKind.Implement, stored.Steps[0].Role.Kind);
    Assert.Equal(ChainStepRoleKind.Review, stored.Steps[1].Role.Kind);
  }

  [Fact]
  public async Task Create_WithAnUnknownRoleToken_IsRefusedNamingTheVocabulary()
  {
    (ChainCapabilityProvider provider, _, _, FakeStart start) = New(1);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1, "integrate"))).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("implement|review", result.Content, StringComparison.Ordinal);
    Assert.Empty(start.Started);
  }

  [Fact]
  public async Task Create_WithAModelOnAStep_IsRefusedByName()
  {
    // A STEP CARRIES NO MODEL. Refusing the member by name is the only honest answer: silently
    // dropping it would leave the caller believing a model was recorded for the step, and a
    // step's session runs on the workspace's model choice like every other session's.
    (ChainCapabilityProvider provider, _, _, FakeStart start) = New(1);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1, "review", extraMember: "\"model\": \"vendor/x\""))).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("model", result.Content, StringComparison.Ordinal);
    Assert.Empty(start.Started);
  }

  [Fact]
  public async Task Create_WithAnUnknownParameter_IsRefusedByName()
  {
    (ChainCapabilityProvider provider, _, _, _) = New(1);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, extraMember: "\"title\": \"x\"")).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter 'title'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Invoke_WithAnUnknownAction_IsRefused()
  {
    (ChainCapabilityProvider provider, _, _, _) = New(1);

    CapabilityInvocationResult result = await provider
        .InvokeAsync("advance", "{}", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("UnknownAction", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_WithAGroup_RecordsTheGroupOnEveryMemberAndStartsStepOne()
  {
    // THE GIT FIXTURE, because a group's members need checkouts: creation makes one per member
    // before it launches them, so a container with no provisioner cannot start a group.
    (ChainCapabilityProvider provider, FakeStore store, _) = NewWithGit(isRepository: true, 1, 2, 3);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1, group: 4) + ", " + Step(2, group: 4) + ", " + Step(3)))
        .ConfigureAwait(true);

    Assert.False(result.IsError);
    Chain stored = Must(await store.GetAsync(1, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    // EVERY MEMBER OF THE OPENING GROUP IS STARTED, and the reconciler is not: creation
    // launches the members together and leaves the merge to the step after them.
    Assert.Equal(ChainStepStatus.Running, stored.Steps.Single(step => step.Position == 1).Status);
    Assert.Equal(ChainStepStatus.Running, stored.Steps.Single(step => step.Position == 2).Status);
    Assert.Equal(ChainStepStatus.Pending, stored.Steps.Single(step => step.Position == 3).Status);
    Assert.Equal(4, stored.Steps.Single(step => step.Position == 1).Group);
    Assert.Equal(4, stored.Steps.Single(step => step.Position == 2).Group);
    Assert.Null(stored.Steps.Single(step => step.Position == 3).Group);
  }

  [Fact]
  public async Task Create_WithAGroupOfOne_IsRefusedBeforeAnythingIsCreated()
  {
    (ChainCapabilityProvider provider, FakeStore store, _, FakeStart start) = New(1, 2);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1, group: 1) + ", " + Step(2))).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains(ChainCreationCode.GroupTooSmall, result.Content, StringComparison.Ordinal);
    Assert.Empty(start.Started);
    ChainCatalog listed = Must(await store.ListAsync(null, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Empty(listed.Chains);
  }

  [Fact]
  public async Task Create_WithAGroupBelowOne_IsRefusedByName()
  {
    (ChainCapabilityProvider provider, _, _, FakeStart start) = New(1, 2);

    CapabilityInvocationResult result = await InvokeAsync(provider,
        Plan(7, Step(1, group: 0) + ", " + Step(2))).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("'steps[1].group' must be an integer >= 1", result.Content,
        StringComparison.Ordinal);
    Assert.Empty(start.Started);
  }

  [Fact]
  public void ActionNames_MatchTheActualActions()
  {
    (ChainCapabilityProvider provider, _, _, _) = New(1);

    Assert.Equal(
        [.. provider.Actions.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal)],
        [.. ChainCapabilityProvider.ActionNames.OrderBy(n => n, StringComparer.Ordinal)]);
  }

  /// <summary>A clock pinned to T0: the claim window is measured against it, and a test that
  ///     read the wall clock would drift across the TTL on a slow machine.</summary>
  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
