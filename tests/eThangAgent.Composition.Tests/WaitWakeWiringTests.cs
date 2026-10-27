using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The wait wake bus must exist per container: the hub singleton, the
///     child-event adapter connected, the state bus publishing through the state
///     service, the signaling inbox decorating the root mailbox, and the provider's
///     wake parsing live through DI.</summary>
public class WaitWakeWiringTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
      .AddEThangAgentCore(
          new AgentSettings(
              new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
              new SubAgentOptions(null, 2)),
          Providers.OpenRouter,
          ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
          new AgentHostOptions(
              new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
      .BuildServiceProvider();
  }

  [Fact]
  public void Container_ResolvesWaitWakeHub_AsSingleton()
  {
    using ServiceProvider services = Build();
    WaitWakeHub hub = services.GetRequiredService<WaitWakeHub>();
    Assert.Same(hub, services.GetRequiredService<WaitWakeHub>());
  }

  [Fact]
  public void Container_ResolvesSignalingInbox_DecoratingBoundedMailbox()
  {
    using ServiceProvider services = Build();
    IAgentInbox inbox = services.GetRequiredService<IAgentInbox>();
    _ = Assert.IsType<SignalingInbox>(inbox);

    WaitWakeHub hub = services.GetRequiredService<WaitWakeHub>();
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    inbox.Post("hello");

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.UserMessage, signal.Reason);
  }

  [Fact]
  public async Task Container_StateServicePublishesThroughTheBus()
  {
    using ServiceProvider services = Build();
    IStateChangeBus bus = services.GetRequiredService<IStateChangeBus>();
    List<StateChange> seen = [];
    using IDisposable lease = bus.Subscribe(seen.Add);
    IStateService state = services.GetRequiredService<IStateService>();

    _ = await state.SetAsync("current/head", "done", null, ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    StateChange change = Assert.Single(seen);
    Assert.Equal(StateChangeKind.Set, change.Kind);
    Assert.Equal("current/head", change.Key);
  }

  [Fact]
  public void Container_ChildEventAdapter_Connected()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    WaitWakeHub hub = services.GetRequiredService<WaitWakeHub>();
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = AgentId.NewId();

    events.Publish(new ChildIdleAlertEvent(child, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(3), "ToolExec"));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.Idle, signal.Reason);
    Assert.Equal(child, signal.ChildId);
  }

  [Fact]
  public void Container_ChildEventAdapter_IgnoresProgress()
  {
    // Progress beats are not wake-worthy: a wait that woke on them would never rest.
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    WaitWakeHub hub = services.GetRequiredService<WaitWakeHub>();
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);

    events.Publish(new ChildProgressEvent(AgentId.NewId(), DateTimeOffset.UtcNow, ChildPhase.ToolExec, "tool:read"));

    Assert.Empty(seen);
  }

  [Fact]
  public async Task Provider_WakeParsingLiveThroughDI()
  {
    using ServiceProvider services = Build();
    // The provider resolves through the capability registry surface; the wake parsing
    // is live when an invalid wakeOn token fails InvalidActionInput (not UnknownAction
    // and not a silent pass-through).
    ICapabilityRegistry registry = services.GetRequiredService<ICapabilityRegistry>();
    Result<ResolvedCapability> resolved = registry.Resolve("agent.wait");
    Assert.True(resolved.IsSuccess);

    CapabilityInvocationResult result = await registry.InvokeAsync(resolved.Value,
        $"{{\"id\":\"{AgentId.NewId()}\",\"wakeOn\":\"banana\"}}", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsError);
    Assert.Contains("banana", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Container_ProgressLedger_SubscribedToTheEventStream()
  {
    // Issue #93: the ledger is constructed eagerly at container build (nothing else
    // resolves it, so a lazy registration would never subscribe).
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    ChildProgressLedger ledger = services.GetRequiredService<ChildProgressLedger>();
    AgentId child = AgentId.NewId();

    events.Publish(new ChildProgressEvent(child, DateTimeOffset.UtcNow, ChildPhase.ToolExec, "tool:read"));

    Assert.Equal("tool:read", ledger.Find(child)!.Label);
  }

  [Fact]
  public void Container_ProgressLedger_IsNotAWakeSource()
  {
    // The ledger is a READ surface only: subscribing it must not make progress
    // wake-worthy (a wait that woke on every tool call would never rest).
    using ServiceProvider services = Build();
    _ = services.GetRequiredService<ChildProgressLedger>();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    WaitWakeHub hub = services.GetRequiredService<WaitWakeHub>();
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);

    events.Publish(new ChildProgressEvent(AgentId.NewId(), DateTimeOffset.UtcNow, ChildPhase.ToolExec, "tool:read"));

    Assert.Empty(seen);
  }

  [Fact]
  public void SubAgentServices_SupervisorFor_ResolvesRegisteredChild()
  {
    using ServiceProvider services = Build();
    SubAgentServices svc = services.GetRequiredService<SubAgentServices>();
    Assert.NotNull(svc.SupervisorFor);

    ChildSupervisorRegistry registry = services.GetRequiredService<ChildSupervisorRegistry>();
    AgentId child = AgentId.NewId();
    ChildSupervisor supervisor = new(child, NullAgentEvents.Instance, TimeProvider.System, ceilings: null);
    registry.Register(child, supervisor);

    Assert.Same(supervisor, svc.SupervisorFor!(child));
  }
}
