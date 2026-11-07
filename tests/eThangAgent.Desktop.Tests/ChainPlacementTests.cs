using System.Diagnostics;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>A group member's cell states the steps running BESIDE it, not only where it sits
///     (parallel chain steps, D7): 'step 2 of 5 (with 1, 3)' is the difference between five
///     cells of one chain and five cells of one chain that knows they run together.
///     <para>THE ABSENCE IS ALSO ASSERTED. A sequential step's line must stay byte-identical to
///     what it was, so an empty sibling list renders no parenthetical at all rather than an
///     empty one.</para>
///     <para>THE FINAL AGENT OWNS NO STEP, so it has no siblings to name: a placement at
///     position 0 states 'chain complete' whatever its sibling list holds.</para></summary>
public class ChainPlacementTests
{
  /// <summary>A placement of a group member names the other members of its group, in the order
  ///     the group holds them, between its position and the chain's title.</summary>
  [Fact]
  public void APlacementWithSiblings_NamesThemOnItsLine()
  {
    ChainPlacement placement = new(1, "t", 2, 5) { Siblings = [1, 3] };

    Assert.Equal("step 2 of 5 (with 1, 3) \u00b7 t", placement.Line);
  }

  /// <summary>A step that runs alone states its position and the chain's title, unchanged: no
  ///     parenthetical, no separator left behind.</summary>
  [Fact]
  public void APlacementWithNoSiblings_StatesOnlyItsPosition()
  {
    ChainPlacement placement = new(1, "t", 2, 5);

    Assert.Empty(placement.Siblings);
    Assert.Equal("step 2 of 5 \u00b7 t", placement.Line);
  }

  /// <summary>The final agent executes no step, so a sibling list on it names nothing: the line
  ///     stays 'chain complete' with the chain's title.</summary>
  [Fact]
  public void AFinalPlacement_IgnoresItsSiblings()
  {
    ChainPlacement placement = new(1, "t", 0, 5) { Siblings = [1, 3] };

    Assert.True(placement.IsFinal);
    Assert.Equal("chain complete \u00b7 t", placement.Line);
  }

  /// <summary>A chain record that could not be read states the position with no total, and the
  ///     siblings still belong on the line: the group is a fact about the step, the total is a
  ///     fact about the record.</summary>
  [Fact]
  public void APlacementWithNoTotal_StillNamesItsSiblings()
  {
    ChainPlacement placement = new(1, "t", 2, null) { Siblings = [1, 3] };

    Assert.Equal("step 2 (with 1, 3) \u00b7 t", placement.Line);
  }

  /// <summary>The siblings of a group member are its group's OTHER members, in position order;
  ///     a step that runs alone has none.</summary>
  [Fact]
  public void SiblingsOf_AGroupMember_AreItsGroupsOtherMembers()
  {
    Chain chain = Grouped(1, 2);

    Assert.Equal([1, 3], ChainPlacement.SiblingsOf(chain, 2));
    Assert.Empty(ChainPlacement.SiblingsOf(chain, 4));
  }

  /// <summary>A group member's CELL states the steps running beside it, so the line is read off
  ///     the chain record the cell's placement was built from - not off the renderer alone.</summary>
  [Fact]
  public async Task AGroupMembersCell_StatesTheStepsRunningBesideIt()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(WorkspaceRoot);
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent);
    shell.Chains.Chain = WithSession(Grouped(1, 2), 2, agent);

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow, "test/model", 1));

    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.Equal("step 2 of 4 (with 1, 3) \u00b7 the chain", cell.PlacementLine);
  }

  /// <summary>A step that runs alone states its position and nothing else, so the parenthetical
  ///     is a fact about the group rather than a caption every chain step carries.</summary>
  [Fact]
  public async Task ASequentialStepsCell_StatesNoSiblings()
  {
    Shell shell = new();
    AgentTabViewModel tab = await shell.OpenAsync(WorkspaceRoot);
    AgentId agent = AgentId.NewId();
    shell.Store.Records[agent.Value] = Record(agent);
    shell.Chains.Chain = WithSession(Grouped(1, 2), 4, agent);

    shell.Events.Publish(new AgentStartedEvent(agent, DateTimeOffset.UtcNow, "test/model", 1));

    AgentCellViewModel cell = await AwaitCellAsync(tab, agent);
    Assert.Equal("step 4 of 4 \u00b7 the chain", cell.PlacementLine);
  }

  private const string WorkspaceRoot = @"C:\work\alpha";

  /// <summary>A four-step chain whose steps 1 to 3 run in <paramref name="group"/> and whose
  ///     step 4 is its reconciler, so a member and a standalone step are both present.</summary>
  private static Chain Grouped(int group, int position)
  {
    Chain chain = Chain.NewFromSeeds("ws:" + WorkspaceRoot, planId: 1, planVersion: 1,
        "the chain", sessionId: null,
        [
          new ChainStepSeed(1, ChainStepRole.Implement, group),
          new ChainStepSeed(2, ChainStepRole.Implement, group),
          new ChainStepSeed(3, ChainStepRole.Implement, group),
          new ChainStepSeed(4, ChainStepRole.Implement),
        ],
        DateTimeOffset.UtcNow) with
    { Id = 7 };
    Assert.Equal(group, chain.GroupAt(position));
    return chain;
  }

  /// <summary>The chain with the step at <paramref name="position"/> owned by
  ///     <paramref name="agent"/>, which is the link a cell's placement is read from.</summary>
  private static Chain WithSession(Chain chain, int position, AgentId agent) => chain with
  {
    Steps = [.. chain.Steps.Select(step => step.Position == position
        ? step with { SessionId = agent.ToString() }
        : step)],
  };

  private static AgentRecord Record(AgentId agent)
      => AgentRecord.Started(agent, "test/model", "step", "p",
          DateTimeOffset.UtcNow, workspaceId: "ws:" + WorkspaceRoot);

  /// <summary>Waits for the cell showing <paramref name="agentId"/> to appear, bounded by the
  ///     suite's own ceiling.</summary>
  private static async Task<AgentCellViewModel> AwaitCellAsync(AgentTabViewModel tab, AgentId agentId)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(TestCeiling.Value);
    while (DateTimeOffset.UtcNow < deadline)
    {
      if (tab.FindCell(agentId) is { } cell)
      {
        return cell;
      }

      await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }

    Assert.Fail($"no cell appeared for agent {agentId}; the tab holds {tab.Cells.Count} cell(s).");
    throw new UnreachableException();
  }

  /// <summary>A store answering the agent's record, so a cell can be built from it.</summary>
  private sealed class AgentStore : IAgentStore
  {
    public Dictionary<Guid, AgentRecord> Records { get; } = [];

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("saved"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("updated"));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Records.TryGetValue(id.Value, out AgentRecord? record)
            ? Result.Success(record)
            : Result.Failure<AgentRecord>(new DomainError("NotFound", "not found")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([.. Records.Values]));
  }

  /// <summary>A chain store answering one chain, so a cell's placement is drivable.</summary>
  private sealed class ChainStore : IChainStore
  {
    public Chain? Chain { get; set; }

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
        => Task.FromResult(Result.Success(draft));

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(Chain is { } found && found.Id == id
            ? Result.Success(found)
            : Result.Failure<Chain>(new DomainError("ChainNotFound", "not found")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainCatalog([], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
        => Task.FromResult(Result.Success(chain));

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<Chain?>(
            Chain is { } found && found.Steps.Any(s => s.SessionId == sessionId) ? found : null));
  }

  /// <summary>The composed shell plus the streams and stores it was given.</summary>
  private sealed class Shell
  {
    public InProcessAgentEvents Events { get; } = new();

    public AgentStore Store { get; } = new();

    public ChainStore Chains { get; } = new();

    public MainViewModel ViewModel { get; }

    public Shell()
    {
      ViewModel = new MainViewModel(
          (root, provider) => Task.FromResult(Result.Success(Session(root))),
          new MainViewModelOptions
          {
            UiMarshal = action => action(),
          });
    }

    public async Task<AgentTabViewModel> OpenAsync(string root)
    {
      Result<AgentTabViewModel> opened = await ViewModel.OpenAgentAsync(root, "openrouter").ConfigureAwait(true);
      Assert.True(opened.IsSuccess, opened.Error?.Message);
      return opened.Value;
    }

    private AgentSession Session(string root)
    {
      ServiceProvider services = new ServiceCollection()
          .AddSingleton<IAgentEvents>(Events)
          .AddSingleton<IAgentStore>(Store)
          .AddSingleton<IChainStore>(Chains)
          .AddSingleton<IWorkspaceContext>(new FixedWorkspaceContext("ws:" + root, root))
          .BuildServiceProvider();
      return new AgentSession(
          services,
          AgentId.NewId(),
          new Conversation(),
          Handler: null!,
          Lifecycle: new RootSessionLifecycle(Store),
          Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root,
          ProviderName: "openrouter",
          Inbox: new BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime());
    }
  }
}
