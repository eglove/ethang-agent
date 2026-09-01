using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>web_fetch must be wired into the root agent's tool list with its real
///     ACL dependencies, and must be safe for sub-agents (not excluded like clarify).</summary>
public class WebFetchWiringTests
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
            new AgentHostOptions(new SilentClarifyChannel(),
                new FixedWorkspaceContext("app"), new UnrootedPathResolver()))
        .BuildServiceProvider();
  }

  private sealed class SilentClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => throw new NotSupportedException("No test should reach the human.");
  }

  [Fact]
  public void WebFetch_IsRegistered_ForRootAgents()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    Assert.Contains(tools.Actions, a => a.Name == "web_fetch");
  }

  [Fact]
  public void WebFetch_IsAvailableToChildAgents()
  {
    using ServiceProvider services = Build();
    AgentToolsProvider tools = services.GetRequiredService<AgentToolsProvider>();
    AgentToolsProvider childTools = tools.Except("clarify");
    Assert.Contains(childTools.Actions, a => a.Name == "web_fetch");
  }
}
