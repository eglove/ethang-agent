using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Frontends may append their own prompt providers (e.g. workspace instructions)
/// via AgentHostOptions; the composition must merge them into the composite system prompt.</summary>
public class HostPromptProviderCompositionTests
{
  private static ServiceProvider BuildCore(AgentHostOptions host)
  {
    AgentSettings settings = new("sk-or-test", new Uri("https://openrouter.test"),
        new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));
    return new ServiceCollection()
        .AddEThangAgentCore(settings, settings.ApiKey!,
            ModelConfig.Create("test/model", null, 512, 0.5f).Value!, host)
        .BuildServiceProvider();
  }

  private sealed class StubClarifyChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }

  [Fact]
  public void Extra_Providers_Are_Merged_Into_The_Composite_System_Prompt()
  {
    using ServiceProvider services = BuildCore(new AgentHostOptions(
        new StubClarifyChannel(),
        new FixedWorkspaceContext("app"),
        new UnrootedPathResolver(),
        [new StaticPromptProvider("EXTRA-PROMPT-MARKER-123")]));

    string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

    Assert.Contains("EXTRA-PROMPT-MARKER-123", prompt, StringComparison.Ordinal);
  }

  [Fact]
  public void Core_Providers_Precede_Frontend_Providers()
  {
    using ServiceProvider services = BuildCore(new AgentHostOptions(
        new StubClarifyChannel(),
        new FixedWorkspaceContext("app"),
        new UnrootedPathResolver(),
        [new StaticPromptProvider("FRONTEND-TAIL-MARKER")]));

    string prompt = services.GetRequiredService<ISystemPromptProvider>().Build();

    int core = prompt.IndexOf("eThang Agent", StringComparison.Ordinal);
    int tail = prompt.IndexOf("FRONTEND-TAIL-MARKER", StringComparison.Ordinal);
    Assert.True(core >= 0 && tail > core,
        $"core={core} tail={tail}: frontend provider must come after the core identity prompt");
  }

  [Fact]
  public void Default_Options_Carry_No_Extra_Providers()
  {
    AgentHostOptions options = new(
        new StubClarifyChannel(),
        new FixedWorkspaceContext("app"),
        new UnrootedPathResolver());

    Assert.Empty(options.ExtraPromptProviders);
  }
}
