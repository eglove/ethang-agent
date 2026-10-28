using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests.Sessions;

/// <summary>The Sessions catalog's DEPTH FILTER, reconciled with #305's one-process-per-agent model.
///     <para>The owner's inventory comment asked for this explicitly: once every agent is its own
///     host process, is <c>record.Depth == 0</c> still the right listing rule, and the answer
///     "follows from whether the chain record supplies the parent link that parent_id used to".</para>
///     <para>THE ANSWER IS YES, IT STAYS, and the reason is that #305 changed WHERE an agent runs,
///     not what a session IS. An agent's <c>Depth</c> is a property of the RECORD (who spawned it),
///     not of the process it runs in, so a depth-1 child that now has its own host is still a
///     depth-1 child and still not a resumable session. The chain record's link (#300's
///     <c>chains</c>/<c>chain_steps</c>, whose session assignment arrives in #301/#302) is what will
///     make a chain step findable from its plan — it is not a substitute for the depth column, and
///     listing children here would offer a user rows whose continuation is the chain's business,
///     not a tab's.</para>
///     <para>These tests pin both halves so a later change to either the depth rule or the chain
///     link has to confront the other: a child with its own host is still omitted, and a completed
///     chain step that IS a root session is still listed (because a step session is depth 0).</para></summary>
public class SessionCatalogDepthReconciliationTests
{
  private static readonly DateTimeOffset Base = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

  private static DateTimeOffset At(double minutes) => Base.AddMinutes(minutes);

  private readonly FakeAgentStore _store = new();
  private readonly SessionCatalogQueryHandler _handler;

  public SessionCatalogDepthReconciliationTests()
      => _handler = new SessionCatalogQueryHandler(_store);

  [Fact]
  public async Task AChildInItsOwnHost_IsStillNotAListedSession()
  {
    // #305 gives this child its own host process. It is still a CHILD: the catalog lists
    // sessions, and a spawned agent's continuation belongs to whoever spawned it.
    _ = await _store.SaveAsync(
        AgentRecord.Spawned(AgentId.NewId(), AgentId.NewId(), depth: 1, modelUsed: "m",
            label: "child", taskPrompt: "task", createdAt: At(0), workspaceId: "C:/ws/a") with
        { Status = AgentStatus.Completed },
        ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    Assert.Empty(listed.Value);
  }

  [Fact]
  public async Task AChainStepSession_IsListed_BecauseItIsADepthZeroSession()
  {
    // The half that makes the rule correct rather than merely convenient: under #305 a chain step
    // runs as its OWN SESSION, so it is a depth-0 record like any other conversation — and the
    // catalog must list it, or a completed step would be exactly the session a user cannot resume.
    AgentRecord stepSession = AgentRecord.Root(AgentId.NewId(), At(0), "C:/ws/a", "openrouter") with
    {
      Status = AgentStatus.Completed,
      CompletedAt = At(30),
    };
    _ = await _store.SaveAsync(stepSession, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<SessionCatalogEntry>> listed = await _handler.ListAsync(TestContext.Current.CancellationToken);

    Assert.True(listed.IsSuccess);
    SessionCatalogEntry entry = Assert.Single(listed.Value);
    Assert.Equal(stepSession.Id, entry.Id);
    Assert.Equal(AgentStatus.Completed, entry.Status);
  }
}
