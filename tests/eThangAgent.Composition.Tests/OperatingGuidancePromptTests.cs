using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The three always-on operating rules the harness supplies itself: computed
///     values are run rather than reasoned about, every session closes with a
///     retrospective that names a mechanical replacement, and the user is addressed as
///     the stakeholder rather than as a fellow implementer. Each rides the composite
///     prompt of a real composition, so a provider dropped from the wiring fails here.</summary>
public class OperatingGuidancePromptTests
{
  private static ServiceProvider BuildCore()
  {
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    using TestAppDatabase db = TestAppDatabase.Create();
    return new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath("app"),
                new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();
  }

  private static string SessionPrompt()
  {
    using ServiceProvider services = BuildCore();
    return services.GetRequiredService<ISystemPromptProvider>().Build();
  }

  [Fact]
  public void ComputedValues_ForbidsReasoningAndRequiresARun()
  {
    string guidance = ComputedValuesPromptProvider.Guidance;

    Assert.Contains("Never compute a number or a date by reasoning", guidance, StringComparison.Ordinal);
    Assert.Contains("running", guidance, StringComparison.Ordinal);
    Assert.Contains("unverified", guidance, StringComparison.Ordinal);
  }

  [Fact]
  public void ComputedValues_ReachesTheSessionPrompt() =>
      Assert.Contains(ComputedValuesPromptProvider.Guidance, SessionPrompt(), StringComparison.Ordinal);

  [Fact]
  public void Retrospective_AsksForMechanicalReplacements()
  {
    string guidance = RetrospectivePromptProvider.Guidance;

    Assert.Contains("retrospective", guidance, StringComparison.Ordinal);
    Assert.Contains("script", guidance, StringComparison.Ordinal);
    Assert.Contains("lint", guidance, StringComparison.Ordinal);
    Assert.Contains("test", guidance, StringComparison.Ordinal);
    Assert.Contains("by hand", guidance, StringComparison.Ordinal);
  }

  [Fact]
  public void Retrospective_ReachesTheSessionPrompt() =>
      Assert.Contains(RetrospectivePromptProvider.Guidance, SessionPrompt(), StringComparison.Ordinal);

  [Fact]
  public void StakeholderVoice_AddressesTheStakeholderAndOwnsImplementation()
  {
    string guidance = StakeholderVoicePromptProvider.Guidance;

    Assert.Contains("stakeholder", guidance, StringComparison.Ordinal);
    Assert.Contains("Legibility outranks", guidance, StringComparison.Ordinal);
    Assert.Contains("responsibility for the implementation", guidance, StringComparison.Ordinal);
  }

  [Fact]
  public void StakeholderVoice_ReachesTheSessionPrompt() =>
      Assert.Contains(StakeholderVoicePromptProvider.Guidance, SessionPrompt(), StringComparison.Ordinal);

  // The voice guidance qualifies the identity, so it must be read after the identity and
  // before the operating guidance that follows it.
  [Fact]
  public void StakeholderVoice_SitsBetweenTheIdentityAndTheOperatingGuidance()
  {
    string prompt = SessionPrompt();

    int identity = prompt.IndexOf("You are eThang Agent", StringComparison.Ordinal);
    int voice = prompt.IndexOf(StakeholderVoicePromptProvider.Guidance, StringComparison.Ordinal);
    int operating = prompt.IndexOf(ContextHygienePromptProvider.Guidance, StringComparison.Ordinal);
    Assert.True(identity >= 0 && voice > identity && operating > voice,
        $"identity={identity} voice={voice} operating={operating}");
  }
}
