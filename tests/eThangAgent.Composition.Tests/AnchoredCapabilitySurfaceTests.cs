using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

public class AnchoredCapabilitySurfaceTests
{
  private static ServiceProvider Build(string workspaceRoot)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new ZaiSettings(null, new Uri("https://zai.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext(workspaceRoot), new UnrootedPathResolver()))
        .BuildServiceProvider();
  }

  private static AgentRecord AnchoredChild(string anchor) => AgentRecord.Spawned(
      AgentId.NewId(), new AgentId(Guid.NewGuid()), 1, "test/model", "anchored",
      "task", DateTimeOffset.UtcNow, new SpawnContract(WorkspaceRoot: anchor));

  [Fact]
  public async Task AnchoredChildSurface_ResolvesReadAndWriteAtAnchor_AndRefusesEscape()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-anchor-surface");
    string anchor = Path.Combine(ws.FullName, "anchored");
    _ = Directory.CreateDirectory(anchor);
    string probe = Path.Combine(anchor, "probe.txt");
    await File.WriteAllTextAsync(probe, "anchor-surface-probe", TestContext.Current.CancellationToken);
    try
    {
      using ServiceProvider services = Build(ws.FullName);
      Func<ICapabilityRegistry> factory = services.GetRequiredService<Func<ICapabilityRegistry>>();

      using IDisposable ambient = SubAgentSpawner.PushRunningChildForTests(AnchoredChild(anchor));
      ICapabilityRegistry surface = factory();

      Result<ResolvedCapability> read = surface.Resolve("read");
      Assert.True(read.IsSuccess);
      CapabilityInvocationResult hit = await surface.InvokeAsync(read.Value,
                               /*lang=json,strict*/
                               """{"timeoutSeconds":60,"path":"probe.txt","startLine":1,"endLine":1}""", TestContext.Current.CancellationToken);
      Assert.False(hit.IsError);
      Assert.Contains("anchor-surface-probe", hit.Content, StringComparison.Ordinal);

      Result<ResolvedCapability> write = surface.Resolve("write");
      Assert.True(write.IsSuccess);
      CapabilityInvocationResult put = await surface.InvokeAsync(write.Value,
                               /*lang=json,strict*/
                               """{"timeoutSeconds":60,"path":"roundtrip.txt","content":"anchored-roundtrip"}""", TestContext.Current.CancellationToken);
      Assert.False(put.IsError);
      Assert.Equal("anchored-roundtrip",
          await File.ReadAllTextAsync(Path.Combine(anchor, "roundtrip.txt"), TestContext.Current.CancellationToken));

      CapabilityInvocationResult refused = await surface.InvokeAsync(read.Value,
                               /*lang=json,strict*/
                               """{"timeoutSeconds":60,"path":"../../../../outside.txt","startLine":1,"endLine":1}""", TestContext.Current.CancellationToken);
      Assert.True(refused.IsError);
      Assert.Contains("Error [PathOutsideWorkspace]:", refused.Content, StringComparison.Ordinal);
    }
    finally
    {
      ws.Delete(true);
    }
  }

  [Fact]
  public void RootSurface_WithoutRunningChild_StillResolvesAllActions()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-root-surface");
    try
    {
      using ServiceProvider services = Build(ws.FullName);
      Func<ICapabilityRegistry> factory = services.GetRequiredService<Func<ICapabilityRegistry>>();
      ICapabilityRegistry surface = factory();
      Assert.True(surface.Resolve("read").IsSuccess);
      Assert.True(surface.Resolve("cycle_check").IsSuccess);
      Assert.True(surface.Resolve("agent.spawn").IsSuccess);
    }
    finally
    {
      ws.Delete(true);
    }
  }

  [Fact]
  public void AnchoredSurface_IsMemoized_PerAnchor()
  {
    DirectoryInfo ws = Directory.CreateTempSubdirectory("ethang-anchor-memo");
    string anchor = Path.Combine(ws.FullName, "anchored");
    _ = Directory.CreateDirectory(anchor);
    try
    {
      using ServiceProvider services = Build(ws.FullName);
      Func<ICapabilityRegistry> factory = services.GetRequiredService<Func<ICapabilityRegistry>>();
      using IDisposable ambient = SubAgentSpawner.PushRunningChildForTests(AnchoredChild(anchor));
      Assert.Same(factory(), factory());
    }
    finally
    {
      ws.Delete(true);
    }
  }
}
