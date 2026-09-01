using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Watchdog seams must exist per container: a singleton heartbeat, the shared
///     SQLite event store, and the heartbeat passed into SubAgentServices so child runs
///     actually beat. Two containers never share a heartbeat (per-session isolation).</summary>
public class WatchdogWiringTests
{
  private static ServiceProvider Build() => new ServiceCollection()
      .AddEThangAgentCore(
          new AgentSettings(
              new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
              new ZaiSettings(null, new Uri("https://zai.test")),
              new SubAgentOptions(null, 2)),
          Providers.OpenRouter,
          ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
          new AgentHostOptions(new SilentClarifyChannel(),
              new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
      .BuildServiceProvider();

  private sealed class SilentClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => throw new NotSupportedException("No test should reach the human.");
  }

  [Fact]
  public void Container_ResolvesSingletonHeartbeatAndEventStore()
  {
    using ServiceProvider services = Build();
    IAgentHeartbeat heartbeat = services.GetRequiredService<IAgentHeartbeat>();
    Assert.Same(heartbeat, services.GetRequiredService<IAgentHeartbeat>());
    _ = Assert.IsType<SqliteWatchdogEventStore>(services.GetRequiredService<IWatchdogEventStore>());
  }

  [Fact]
  public void Container_SubAgentServices_CarriesTheContainerHeartbeat()
  {
    using ServiceProvider services = Build();
    SubAgentServices svc = services.GetRequiredService<SubAgentServices>();
    IAgentHeartbeat heartbeat = services.GetRequiredService<IAgentHeartbeat>();
    Assert.Same(heartbeat, svc.Heartbeat);
  }

  [Fact]
  public void TwoContainers_GetDistinctHeartbeats()
  {
    using ServiceProvider first = Build();
    using ServiceProvider second = Build();
    Assert.NotSame(first.GetRequiredService<IAgentHeartbeat>(),
        second.GetRequiredService<IAgentHeartbeat>());
  }
}
