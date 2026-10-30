using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The workbench's Chains view is event-driven (issue #312). Two things must hold for
///     it to be INSTANT in production, and the plan view's wiring proved both can be silently
///     absent:
///     <list type="number">
///     <item>a chain write must publish a <see cref="ChainChangedEvent"/> onto the session's
///     event stream, because the chain a model advances is advanced in the ChildHost process -
///     the app container's <see cref="ChainService"/> events can never fire for it;</item>
///     <item>the publisher must be WIRED, and DI is lazy, so nothing resolving it by name would
///     mean it subscribes to nothing and every chain event fires into no listener.</item>
///     </list>
///     <para>The second is proven the way the plan view's is: resolving the service is what
///     constructs the publisher, and the publish test below is the behavioral proof that the
///     subscription is live.</para></summary>
public class ChainRealtimeWiringTests
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
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();
  }

  /// <summary>Resolving the chain service constructs the publisher inside its own factory, which
  ///     is the only ordering that cannot be forgotten (a separate registration is never
  ///     resolved, because nothing asks for it by name).</summary>
  [Fact]
  public void Container_WiresTheChainChangePublisherOntoTheService()
  {
    using ServiceProvider services = Build();

    Assert.NotNull(services.GetService<ChainService>());
  }

  /// <summary>The behavioral proof: a chain created through the container's own service reaches
  ///     the session's stream as a <see cref="ChainChangedEvent"/>, so a view subscribing that
  ///     stream sees it without polling.</summary>
  [Fact]
  public async Task ChainCreation_PublishesAChainChangedEvent()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    ChainService chains = services.GetRequiredService<ChainService>();
    RecordingSubscriber recorder = new();
    using IDisposable lease = events.Subscribe(recorder);

    Result<Chain> created = await chains.CreateAsync(
        ChainCreation.Implementing("app", 1, 1, "realtime", "sess-1", [1]),
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected success");

    ChainChangedEvent? change = recorder.Events.OfType<ChainChangedEvent>().LastOrDefault();
    Assert.NotNull(change);
    Assert.Equal(created.Value.Id, change.ChainId);
    Assert.Equal("created", change.Change);
  }

  /// <summary>A PARK publishes the new status, because the park/completion distinction is the
  ///     whole point of the fact reaching a surface at all (issue #312 D2).</summary>
  [Fact]
  public async Task ChainPark_PublishesTheNewStatus()
  {
    using ServiceProvider services = Build();
    IAgentEvents events = services.GetRequiredService<IAgentEvents>();
    ChainService chains = services.GetRequiredService<ChainService>();
    Result<Chain> created = await chains.CreateAsync(
        ChainCreation.Implementing("app", 1, 1, "parked", "sess-1", [1]),
        DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(created.IsSuccess, created.Error?.Message ?? "expected success");
    RecordingSubscriber recorder = new();
    using IDisposable lease = events.Subscribe(recorder);

    Result<Chain> parked = await chains.ParkAsync(created.Value.Id, "needs a human", 1,
        ChainActor.Harness, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(parked.IsSuccess, parked.Error?.Message ?? "expected success");

    ChainChangedEvent change = Assert.Single(recorder.Events.OfType<ChainChangedEvent>());
    Assert.Equal("status", change.Change);
    Assert.Equal("Parked", change.Status);
  }

  private sealed class RecordingSubscriber : IAgentEventSubscriber
  {
    public List<ChildEvent> Events { get; } = [];

    public void OnEvent(ChildEvent evt) => Events.Add(evt);
  }
}
