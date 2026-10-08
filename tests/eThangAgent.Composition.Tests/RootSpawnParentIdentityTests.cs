using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Phantom-parent regression, spawn path (2026-10-08): the composition's
///     parent-context fallback minted a FRESH random AgentId for the root record, so
///     every child the ROOT spawned pointed at a parent id that had no row (grandchildren
///     parented correctly because RunningChild carries the real child id). The spawn
///     path must read the persisted RootSessionIdentity, exactly as RootAgentHolder
///     does for the agent's request identity.</summary>
public class RootSpawnParentIdentityTests
{
  private sealed class CapturingSpawnCommand : IAgentSpawnCommand
  {
    public AgentRecord? CapturedParent { get; private set; }

    public Task<Result<AgentId>> Execute(AgentRecord parent, SpawnRequest request, CancellationToken ct = default)
    {
      CapturedParent = parent;
      return Task.FromResult(Result.Success(AgentId.NewId()));
    }
  }

  [Fact]
  public async Task RootSpawn_ParentRecord_Carries_The_Persisted_Session_Id()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    ServiceCollection services = new();
    _ = services.AddEThangAgentCore(settings, Providers.OpenRouter,
        ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
        new AgentHostOptions(new FixedWorkspaceContext("app"), new UnrootedPathResolver()), db.Database);
    CapturingSpawnCommand capture = new();
    _ = services.AddSingleton<IAgentSpawnCommand>(capture); // last registration wins
    using ServiceProvider provider = services.BuildServiceProvider();

    AgentId persistedRootId = new(Guid.NewGuid());
    provider.GetRequiredService<RootSessionIdentity>().Id = persistedRootId;

    AgentCapabilityProvider surface = provider.GetRequiredService<AgentCapabilityProvider>();
    CapabilityInvocationResult result = await surface.InvokeAsync("spawn",
        /*lang=json,strict*/ """{"taskPrompt":"summarize"}""",
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsError);
    Assert.NotNull(capture.CapturedParent);
    Assert.Equal(persistedRootId, capture.CapturedParent.Id);
    Assert.Null(capture.CapturedParent.ParentId);
    Assert.Equal(0, capture.CapturedParent.Depth);
  }
}
