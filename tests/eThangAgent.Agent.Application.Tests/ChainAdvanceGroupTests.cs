using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>A group member's session runs in its own checkout: the advance provisions the
///     member's worktree, records it on the step, and hands it to the launch - and a
///     sequential step provisions nothing.</summary>
public class ChainAdvanceGroupTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

  /// <summary>What happened, in the order it happened: the claim and the provisioning are
  ///     two writes whose order decides whether a member can be claimed by nobody while its
  ///     checkout is being made.</summary>
  private sealed class Journal
  {
    public List<string> Entries { get; } = [];

    public void Add(string entry) => Entries.Add(entry);

    public int IndexOf(string entry) => Entries.IndexOf(entry);
  }

  private sealed class FakeStore(Journal journal) : IChainStore
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

      foreach (ChainStep step in chain.Steps)
      {
        ChainStep? before = current.Steps.FirstOrDefault(s => s.Position == step.Position);
        if (step.SessionId is { } claimed && claimed.StartsWith("pending-", StringComparison.Ordinal)
            && !string.Equals(before?.SessionId, claimed, StringComparison.Ordinal))
        {
          journal.Add("claim:" + step.Position);
        }
      }

      Chain saved = chain with { Version = current.Version + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));
  }

  private sealed class FakeHost(Journal journal) : IChainAdvanceHost
  {
    public List<string> Provisioned { get; } = [];

    public List<ChainStepAssignment> Started { get; } = [];

    public List<string> Closed { get; } = [];

    /// <summary>The checkouts that hold uncommitted work.</summary>
    public HashSet<string> Dirty { get; } = [with(StringComparer.Ordinal)];

    /// <summary>The worktrees the host was asked to remove, in the order it was asked.</summary>
    public List<string> Removed { get; } = [];

    public Result<WorktreeProvision>? ProvisionResult { get; set; }

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
      Closed.Add(sessionId);
      return Task.CompletedTask;
    }

    public Task<Result<WorktreeProvision>> ProvisionWorktreeAsync(string name, CancellationToken ct = default)
    {
      Provisioned.Add(name);
      journal.Add("provision:" + name);
      // EACH CHECKOUT GETS ITS OWN DIRECTORY, named after the worktree: a group's members are
      // provisioned together, and one shared path would make them indistinguishable.
      return Task.FromResult(ProvisionResult
          ?? Result.Success(new WorktreeProvision(name, PathFor(name), name)));
    }

    /// <summary>The directory this host reports for a worktree name.</summary>
    /// <param name="name">The worktree name.</param>
    public static string PathFor(string name) => "C:\\ws\\.worktrees\\" + name;

    public Task<bool> WorktreeIsDirtyAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Dirty.Contains(path));

    public Task RemoveWorktreeAsync(string path, CancellationToken ct = default)
    {
      Removed.Add(path);
      return Task.CompletedTask;
    }

    public Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
        string predecessorSessionId, CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(Result.Success("session-" + assignment.Position));
    }

    public Task<Result<string>> StartFinalSessionAsync(Chain chain, string predecessorSessionId,
        CancellationToken ct = default) => Task.FromResult(Result.Success("session-final"));
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>Steps 1 and 4 run alone; 2 and 3 are one group, with 4 as its reconciler.</summary>
  private static ChainCreation Grouped() => new("C:\\ws", 3, 1, "t", "session-root",
      [
        ChainStepSeed.Implementing(1),
        new ChainStepSeed(2, ChainStepRole.Implement, Group: 1),
        new ChainStepSeed(3, ChainStepRole.Implement, Group: 1),
        ChainStepSeed.Implementing(4),
      ]);

  private sealed record Fixture(ChainAdvanceService Service, FakeStore Store, FakeHost Host, Chain Chain);

  private static async Task<Fixture> NewAsync(ChainCreation? creation = null)
  {
    Journal journal = new();
    FakeStore store = new(journal);
    FakeHost host = new(journal);
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(creation ?? Grouped(), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    return new Fixture(new ChainAdvanceService(chains, host), store, host, chain);
  }

  /// <summary>Claims step 1 for a session and settles it, so the next advance starts step 2.</summary>
  private static async Task<Chain> SettleStepOneAsync(Fixture fixture)
  {
    Chain claimed = fixture.Chain.ClaimStep(1, "session-a", T0);
    return Must(await fixture.Store.SaveAsync(claimed, fixture.Chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task AdvanceIntoAGroup_ProvisionsTheMembersWorktree()
  {
    Fixture fixture = await NewAsync();
    Chain claimed = await SettleStepOneAsync(fixture);

    Result<ChainAdvance> advance = await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(advance.IsSuccess, advance.Error?.Message ?? "expected success");
    // THE WHOLE GROUP STARTS, not only the member the cursor offered: a group whose members
    // waited for one another would be a sequential chain.
    Assert.Equal(["chain-1-step-2", "chain-1-step-3"], fixture.Host.Provisioned);
    Assert.Equal([2, 3], [.. fixture.Host.Started.Select(started => started.Position)]);
    Assert.Equal(FakeHost.PathFor("chain-1-step-2"), fixture.Host.Started[0].WorktreePath);
    Assert.Equal(FakeHost.PathFor("chain-1-step-3"), fixture.Host.Started[1].WorktreePath);
  }

  [Fact]
  public async Task AdvanceIntoAGroup_RecordsTheWorktreeOnTheStep()
  {
    Fixture fixture = await NewAsync();
    Chain claimed = await SettleStepOneAsync(fixture);

    _ = Must(await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Chain stored = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(FakeHost.PathFor("chain-1-step-2"), stored.StepAt(2)!.WorktreePath);
    // The branch the provisioner checked out is recorded beside the path: the reconciler merges
    // by branch, and the member's session is closed by the time it reads this.
    Assert.Equal("chain-1-step-2", stored.StepAt(2)!.WorktreeBranch);
    // EVERY MEMBER OF THE GROUP HAS ITS OWN CHECKOUT, and the reconciler has none.
    Assert.Equal(FakeHost.PathFor("chain-1-step-3"), stored.StepAt(3)!.WorktreePath);
    Assert.Null(stored.StepAt(4)!.WorktreePath);
    Assert.Null(stored.StepAt(1)!.WorktreePath);
  }

  [Fact]
  public async Task AdvanceIntoAGroup_ClaimsTheStepBeforeItProvisions()
  {
    Journal journal = new();
    FakeStore store = new(journal);
    FakeHost host = new(journal);
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(Grouped(), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain claimed = chain.ClaimStep(1, "session-a", T0);
    _ = Must(await store.SaveAsync(claimed, chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    journal.Entries.Clear();

    _ = Must(await new ChainAdvanceService(chains, host).AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.True(journal.IndexOf("claim:2") >= 0, string.Join(",", journal.Entries));
    Assert.True(journal.IndexOf("claim:2") < journal.IndexOf("provision:chain-1-step-2"),
        string.Join(",", journal.Entries));
  }

  [Fact]
  public async Task AdvanceIntoAGroup_WhenProvisioningFails_ReleasesTheClaimAndStartsNothing()
  {
    Fixture fixture = await NewAsync();
    fixture.Host.ProvisionResult = Result.Failure<WorktreeProvision>(
        new DomainError("WorktreeExists", "chain-1-step-2 already exists"));
    Chain claimed = await SettleStepOneAsync(fixture);

    Result<ChainAdvance> advance = await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(advance.IsSuccess, advance.Error?.Message ?? "a refusal is a value, not a failure");
    Assert.Equal("WorktreeExists", advance.Value.Code);
    Assert.Empty(fixture.Host.Started);
    Chain stored = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Pending, stored.StepAt(2)!.Status);
    Assert.Null(stored.StepAt(2)!.SessionId);
    Assert.Null(stored.StepAt(2)!.WorktreePath);
  }

  [Fact]
  public async Task AdvanceIntoASequentialStep_ProvisionsNothing()
  {
    Fixture fixture = await NewAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root", [1, 2, 3]));
    Chain claimed = await SettleStepOneAsync(fixture);

    _ = Must(await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Empty(fixture.Host.Provisioned);
    ChainStepAssignment started = Assert.Single(fixture.Host.Started);
    Assert.Null(started.WorktreePath);
  }

  [Fact]
  public async Task AdvanceIntoAGroup_ReusesAWorktreeAlreadyRecorded()
  {
    Fixture fixture = await NewAsync();
    Chain claimed = await SettleStepOneAsync(fixture);
    Chain stamped = claimed.WithWorktree(2, "C:\\ws\\.worktrees\\chain-1-step-2",
        "chain-1-step-2", T0);
    _ = Must(await fixture.Store.SaveAsync(stamped, claimed.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    _ = Must(await fixture.Service.AdvanceAsync(stamped, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    // THE RECORDED CHECKOUT IS REUSED and only the sibling is provisioned: a retried launch must
    // land in the directory that already holds its work.
    Assert.Equal(["chain-1-step-3"], fixture.Host.Provisioned);
    Assert.Equal("C:\\ws\\.worktrees\\chain-1-step-2", fixture.Host.Started[0].WorktreePath);
  }

  [Fact]
  public async Task AdvanceFromAMember_WithADirtyWorktree_IsRefusedAndSettlesNothing()
  {
    Fixture fixture = await NewAsync();
    Chain claimed = await SettleStepOneAsync(fixture);
    _ = Must(await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain afterFirst = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    string memberPath = FakeHost.PathFor("chain-1-step-2");
    _ = fixture.Host.Dirty.Add(memberPath);

    Result<ChainAdvance> second = await fixture.Service.AdvanceAsync(afterFirst, 2, "session-2", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(second.IsSuccess, second.Error?.Message ?? "a refusal is a value, not a failure");
    Assert.Equal(ChainAdvanceKind.Refused, second.Value.Kind);
    Assert.Equal("ChainWorktreeDirty", second.Value.Code);
    Assert.Contains(memberPath, second.Value.Reason!, StringComparison.Ordinal);
    Assert.Contains("git commit -m", second.Value.Reason!, StringComparison.Ordinal);
    Chain stored = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Running, stored.StepAt(2)!.Status);
    Assert.Empty(fixture.Host.Closed);
  }

  [Fact]
  public async Task AdvanceFromAMember_WithACleanWorktree_SettlesTheStep()
  {
    Fixture fixture = await NewAsync();
    Chain claimed = await SettleStepOneAsync(fixture);
    _ = Must(await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain afterFirst = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    _ = Must(await fixture.Service.AdvanceAsync(afterFirst, 2, "session-2", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Chain stored = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Done, stored.StepAt(2)!.Status);
  }

  [Fact]
  public async Task AdvanceFromOneMember_ProvisionsTheSiblingsWorktree()
  {
    Fixture fixture = await NewAsync();
    Chain claimed = await SettleStepOneAsync(fixture);
    _ = Must(await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain afterFirst = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Result<ChainAdvance> second = await fixture.Service.AdvanceAsync(afterFirst, 2, "session-2", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(second.IsSuccess, second.Error?.Message ?? "expected success");
    Assert.Equal(["chain-1-step-2", "chain-1-step-3"], fixture.Host.Provisioned);
    Assert.Equal(3, fixture.Host.Started[^1].Position);
  }

  [Fact]
  public async Task SettleAMember_WithAParkedSibling_StartsTheReconciler()
  {
    Fixture fixture = await NewAsync();
    Chain prepared = fixture.Chain
        .ClaimStep(1, "session-1", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-1"), null, T0)
        .ClaimStep(2, "session-2", T0)
        .WithWorktree(2, MemberOnePath, "worktree/chain-1-step-2", T0)
        .SettleStep(2, ChainStepStatus.Parked, ChainActor.Session("session-2"), null, T0)
        .WithWorktree(3, MemberTwoPath, "worktree/chain-1-step-3", T0)
        .ClaimStep(3, "session-3", T0);
    Chain saved = Must(await fixture.Store.SaveAsync(prepared, fixture.Chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    // The rule, read directly on the chain as it stands once step 3 has settled: a group whose
    // members have all settled offers the step after it, even when one of them parked. Work that
    // passed is not stranded because a sibling failed.
    ChainAdvance next = ChainAdvancePolicy.Next(
        saved.SettleStep(3, ChainStepStatus.Done, ChainActor.Session("session-3"), null, T0));
    Assert.Equal(ChainAdvanceKind.StartStep, next.Kind);
    Assert.Equal(4, next.Position);

    Result<ChainAdvance> advance = await fixture.Service.AdvanceAsync(saved, 3, "session-3", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(advance.IsSuccess, advance.Error?.Message ?? "expected success");
    Assert.Equal(4, Assert.Single(fixture.Host.Started).Position);
    // The reconciler runs in the workspace checkout, so it owns no worktree of its own.
    Assert.Empty(fixture.Host.Removed);
  }

  [Fact]
  public async Task ReconcilerDone_WithAParkedMember_ParksNamingThatMember()
  {
    Fixture fixture = await NewAsync();
    Chain prepared = await ReadyToReconcileAsync(fixture, memberOneParked: true);

    Result<ChainAdvance> advance = await fixture.Service.AdvanceAsync(prepared, 4, "session-4", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(advance.IsSuccess, advance.Error?.Message ?? "a park is a value, not a failure");
    Assert.Equal(ChainAdvanceKind.Blocked, advance.Value.Kind);
    Assert.Contains("2 (Parked)", advance.Value.Reason!, StringComparison.Ordinal);
    Chain stored = Must(await fixture.Store.GetAsync(prepared.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(ChainStatus.Parked, stored.Status);
    Assert.Equal(ChainStepStatus.Done, stored.StepAt(4)!.Status);
  }

  [Fact]
  public async Task ReconcilerDone_RemovesEveryMembersWorktree()
  {
    Fixture fixture = await NewAsync();
    Chain prepared = await ReadyToReconcileAsync(fixture, memberOneParked: false);

    _ = Must(await fixture.Service.AdvanceAsync(prepared, 4, "session-4", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal([MemberOnePath, MemberTwoPath], fixture.Host.Removed);
    // The record stops naming checkouts that no longer exist, so a later reader cannot launch a
    // session into a directory the landing removed.
    Chain stored = Must(await fixture.Store.GetAsync(prepared.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Null(stored.StepAt(2)!.WorktreePath);
    Assert.Null(stored.StepAt(3)!.WorktreePath);
    Assert.Null(stored.StepAt(2)!.WorktreeBranch);
  }

  [Fact]
  public async Task MemberDone_RemovesNothing()
  {
    Fixture fixture = await NewAsync();
    Chain claimed = await SettleStepOneAsync(fixture);
    _ = Must(await fixture.Service.AdvanceAsync(claimed, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain afterFirst = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    // A member's completion lands nothing: only the reconciler merges, so only the reconciler's
    // completion may remove a checkout.
    _ = Must(await fixture.Service.AdvanceAsync(afterFirst, 2, "session-2", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Empty(fixture.Host.Removed);
    Chain stored = Must(await fixture.Store.GetAsync(claimed.Id,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(FakeHost.PathFor("chain-1-step-2"), stored.StepAt(2)!.WorktreePath);
  }

  [Fact]
  public async Task ReconcilerDone_OnAChainWithNoWorktrees_RemovesNothing()
  {
    Fixture fixture = await NewAsync(
        ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root", [1, 2, 3]));
    Chain prepared = fixture.Chain
        .ClaimStep(1, "session-1", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-1"), null, T0)
        .ClaimStep(2, "session-2", T0)
        .SettleStep(2, ChainStepStatus.Done, ChainActor.Session("session-2"), null, T0)
        .ClaimStep(3, "session-3", T0);
    Chain saved = Must(await fixture.Store.SaveAsync(prepared, fixture.Chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    _ = Must(await fixture.Service.AdvanceAsync(saved, 3, "session-3", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Empty(fixture.Host.Removed);
  }

  private const string MemberOnePath = "C:\\ws\\.worktrees\\chain-1-step-2";

  private const string MemberTwoPath = "C:\\ws\\.worktrees\\chain-1-step-3";

  /// <summary>A chain whose group has settled and whose reconciler (step 4) is running: steps 2
  ///     and 3 carry their checkouts, and step 2 either succeeded or parked.</summary>
  private static async Task<Chain> ReadyToReconcileAsync(Fixture fixture, bool memberOneParked)
  {
    Chain chain = fixture.Chain
        .ClaimStep(1, "session-1", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-1"), null, T0)
        .ClaimStep(2, "session-2", T0)
        .WithWorktree(2, MemberOnePath, "worktree/chain-1-step-2", T0)
        .SettleStep(2, memberOneParked ? ChainStepStatus.Parked : ChainStepStatus.Done,
            ChainActor.Session("session-2"), null, T0)
        .ClaimStep(3, "session-3", T0)
        .WithWorktree(3, MemberTwoPath, "worktree/chain-1-step-3", T0)
        .SettleStep(3, ChainStepStatus.Done, ChainActor.Session("session-3"), null, T0)
        .ClaimStep(4, "session-4", T0);
    return Must(await fixture.Store.SaveAsync(chain, fixture.Chain.Version,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }
}
