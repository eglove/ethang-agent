using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The static context-hygiene guidance must always ride the system prompt
/// (root and children alike): the model is the primary janitor via context_trim,
/// and the harness's deep clean is only the backstop.</summary>
public class ContextHygienePromptTests
{
  private static ServiceProvider BuildCore(AgentHostOptions host)
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new SubAgentOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!, host, db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Guidance_IsStaticAndShort()
  {
    Assert.Contains("context_trim", ContextHygienePromptProvider.Guidance, StringComparison.Ordinal);
    Assert.Contains("Clean as you go", ContextHygienePromptProvider.Guidance, StringComparison.Ordinal);
    Assert.True(ContextHygienePromptProvider.Guidance.Length < 600); // a few dozen tokens
  }

  [Fact]
  public void SessionPrompt_CarriesGuidance()
  {
    // Pattern: HostPromptProviderCompositionTests - resolve the composite provider
    // from a built container and assert the composed prompt carries the guidance.
    using ServiceProvider services = BuildCore(new AgentHostOptions(
        new FixedWorkspaceContext("app"),
        new UnrootedPathResolver()));

    string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

    Assert.Contains("context_trim", prompt, StringComparison.Ordinal);
    Assert.Contains("Clean as you go", prompt, StringComparison.Ordinal);
  }
}
