using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>A group member's session runs in its own checkout: the advance provisions the
///     member's worktree, records it on the step, and hands it to the launch - and a
///     sequential step provisions nothing.</summary>
public class ChainAdvanceGroupTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

  private const string ProvisionedPath = "C:\\ws\\.worktrees\\provisioned";

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
    public HashSet<string> Dirty { get; } = new(StringComparer.Ordinal);

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
      return Task.FromResult(ProvisionResult
          ?? Result.Success(new WorktreeProvision(name, ProvisionedPath, name)));
    }

    public Task<bool> WorktreeIsDirtyAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Dirty.Contains(path));

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
    Assert.Equal(["chain-1-step-2"], fixture.Host.Provisioned);
    ChainStepAssignment started = Assert.Single(fixture.Host.Started);
    Assert.Equal(2, started.Position);
    Assert.Equal(ProvisionedPath, started.WorktreePath);
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
    Assert.Equal(ProvisionedPath, stored.StepAt(2)!.WorktreePath);
    // The branch the provisioner checked out is recorded beside the path: the reconciler merges
    // by branch, and the member's session is closed by the time it reads this.
    Assert.Equal("chain-1-step-2", stored.StepAt(2)!.WorktreeBranch);
    Assert.Null(stored.StepAt(3)!.WorktreePath);
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

    Assert.Empty(fixture.Host.Provisioned);
    Assert.Equal("C:\\ws\\.worktrees\\chain-1-step-2",
        Assert.Single(fixture.Host.Started).WorktreePath);
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
    _ = fixture.Host.Dirty.Add(ProvisionedPath);

    Result<ChainAdvance> second = await fixture.Service.AdvanceAsync(afterFirst, 2, "session-2", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(second.IsSuccess, second.Error?.Message ?? "a refusal is a value, not a failure");
    Assert.Equal(ChainAdvanceKind.Refused, second.Value.Kind);
    Assert.Equal("ChainWorktreeDirty", second.Value.Code);
    Assert.Contains(ProvisionedPath, second.Value.Reason!, StringComparison.Ordinal);
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
}
