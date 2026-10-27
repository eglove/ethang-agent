using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The workbench's Plans view is event-driven (issue #8). Two things must hold
///     for it to be INSTANT in production, and both were broken:
///     <list type="number">
///     <item>the cross-process change signal must be REGISTERED, or the view's poller
///     never starts (MainViewModel resolves it with GetService and gets null);</item>
///     <item>a plan write must publish a <see cref="PlanChangedEvent"/> onto the
///     session's event stream, because every production write is dispatched by the
///     model in the ChildHost process - the app container's PlanService never sees it,
///     so its in-process events can never fire.</item>
///     </list>
///     <para>Together these make one subscribable: the same stream a parent agent
///     watches with <c>wakeOn</c> for its subagents, so a child marking a plan step
///     done is visible to the parent AND to the view.</para></summary>
public class PlanRealtimeWiringTests
{
  private static ServiceProvider Build()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Container_RegistersTheChangeSignal()
  {
    // Without this registration MainViewModel's GetService<IChangeSignal>() returns
    // null, PlansViewViewModel.Start() skips its poller, and NOTHING re-reads the
    // plan table until the user switches workbench views.
    using ServiceProvider services = Build();

    IChangeSignal? signal = services.GetService<IChangeSignal>();

    Assert.NotNull(signal);
  }

  [Fact]
  public void Container_WiresThePlanChangePublisherOntoTheService()
  {
    // The bridge is wired INSIDE PlanService's own factory, because DI is lazy and
    // nothing resolves the publisher by name: a separate registration would never be
    // constructed, so it would subscribe to nothing. Resolving the service is
    // therefore what proves the subscription exists - and the publish test below is
    // the behavioral proof that the wiring is live.
    using ServiceProvider services = Build();

    Assert.NotNull(services.GetService<PlanService>());
  }

  [Fact]
  public async Task PlanStatusChange_PublishesAPlanChangedEvent()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    PlanService plans = services.GetRequiredService<PlanService>();
    RecordingSubscriber recorder = new();
    using IDisposable lease = events.Subscribe(recorder);

    Result<Plan> created = await plans.CreateAsync("realtime", "goal", "sess-1", null,
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected success");

    PlanChangedEvent? change = recorder.Events.OfType<PlanChangedEvent>().LastOrDefault();
    Assert.NotNull(change);
    Assert.Equal(created.Value.Id, change.PlanId);
  }

  private sealed class RecordingSubscriber : IAgentEventSubscriber
  {
    public List<ChildEvent> Events { get; } = [];

    public void OnEvent(ChildEvent evt) => Events.Add(evt);
  }
}
