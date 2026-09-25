using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Skill registry wiring (plan #29 task 11): service, adapters, and
///     both tools resolve in a container; a container with NO configured skill
///     directories still builds and the service reports NoTarget at use time —
///     never a composition failure.</summary>
public class SkillRegistryWiringTests
{
  private static ServiceProvider Build()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new ZaiSettings(null, new Uri("https://zai.test")),
        new SubAgentOptions(null, 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
        .BuildServiceProvider();
  }

  [Fact]
  public void Service_And_Adapters_Resolve()
  {
    using ServiceProvider services = Build();
    Assert.Same(services.GetRequiredService<SkillRegistryService>(), services.GetRequiredService<SkillRegistryService>());
    Assert.NotNull(services.GetRequiredService<ISkillRegistryAccess>());
    Assert.NotNull(services.GetRequiredService<ISkillsShAccess>());
  }

  [Fact]
  public void BothTools_Advertise()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.Actions, a => a.Name == "skill_search");
    Assert.Contains(tools.Actions, a => a.Name == "skill_registry");
  }

  [Fact]
  public async Task Container_WithoutDirectories_InstallReportsNoTarget()
  {
    using ServiceProvider services = Build();
    SkillRegistryService service = services.GetRequiredService<SkillRegistryService>();
    Result<SkillAddress> address = SkillAddress.Create("o/r");
    Assert.True(address.IsSuccess);
    Result<SkillInstallReport> r = await service.InstallAsync(address.Value, null, false, false, TestContext.Current.CancellationToken);
    Assert.False(r.IsSuccess);
    Assert.Equal("NoTarget", r.Error.Code);
  }
}