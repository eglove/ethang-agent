using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The three always-on operating rules the harness supplies itself: computed
///     values are run rather than reasoned about, what a session trips over is fixed at
///     its cause in the same session, and the user is addressed as the stakeholder rather
///     than as a fellow implementer. Each rides the composite prompt of a real
///     composition, so a provider dropped from the wiring fails here.</summary>
[Trait("Category", "Process")]
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
  public void Hardening_NamesTheMechanismsThatCloseAGap()
  {
    string guidance = HardeningPromptProvider.Guidance;

    Assert.Contains("test", guidance, StringComparison.Ordinal);
    Assert.Contains("lint", guidance, StringComparison.Ordinal);
    Assert.Contains("script", guidance, StringComparison.Ordinal);
    Assert.Contains("gate", guidance, StringComparison.Ordinal);
    Assert.Contains("in the same session", guidance, StringComparison.Ordinal);
  }

  [Fact]
  public void Hardening_ForbidsDeferringTheFix()
  {
    // The rule is a fix, not a report: a session that writes the gap up as a suggestion
    // for later has done nothing about it.
    string guidance = HardeningPromptProvider.Guidance;

    Assert.Contains("do not write it up as a suggestion for later", guidance, StringComparison.Ordinal);
    Assert.Contains("one line in your summary", guidance, StringComparison.Ordinal);
    Assert.DoesNotContain("retrospective", guidance, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Hardening_ReachesTheSessionPrompt() =>
      Assert.Contains(HardeningPromptProvider.Guidance, SessionPrompt(), StringComparison.Ordinal);

  [Fact]
  public void StakeholderVoice_AddressesTheStakeholderAndOwnsImplementation()
  {
    string guidance = StakeholderVoicePromptProvider.Guidance;

    Assert.Contains("stakeholder", guidance, StringComparison.Ordinal);
    Assert.Contains("Legibility outranks", guidance, StringComparison.Ordinal);
    Assert.Contains("responsibility for the implementation", guidance, StringComparison.Ordinal);
  }

  [Fact]
  public void StakeholderVoice_FinishesTheWorkInsteadOfDisclosingIt()
  {
    // A gap the agent could have closed is work it skipped. The guidance must forbid
    // handing one back as a caveat, and must require evidence before a claim of
    // necessity - an unverified claim presented as fact survives review.
    string guidance = StakeholderVoicePromptProvider.Guidance;

    Assert.Contains("FINISH THE WORK BEFORE REPORTING IT", guidance, StringComparison.Ordinal);
    Assert.Contains("not a limitation to disclose", guidance, StringComparison.Ordinal);
    Assert.Contains("if you can name the fix, make the fix", guidance, StringComparison.Ordinal);
    Assert.Contains("READ ITS ACTUAL CONTRACT", guidance, StringComparison.Ordinal);
    Assert.Contains("hypothesis until a test or a measurement confirms it", guidance, StringComparison.Ordinal);
  }

  [Fact]
  public void StakeholderVoice_ReachesTheSessionPrompt() =>
      Assert.Contains(StakeholderVoicePromptProvider.Guidance, SessionPrompt(), StringComparison.Ordinal);

  [Fact]
  public void References_NameAPointerAndLeaveTheFetchToTheModel()
  {
    // A reference is a name the user wrote, not content the harness fetched - for a file AND
    // for a skill. The guidance must not read as an instruction to load either one, because
    // the model may already hold it.
    string guidance = ReferencesPromptProvider.Guidance;

    Assert.Contains("@name", guidance, StringComparison.Ordinal);
    Assert.Contains("[references]", guidance, StringComparison.Ordinal);
    Assert.Contains("WITHOUT loading it", guidance, StringComparison.Ordinal);
    Assert.Contains("Fetch one only when you need it", guidance, StringComparison.Ordinal);
    Assert.Contains("may already hold the file or the skill", guidance, StringComparison.Ordinal);
    Assert.Contains("skill_view", guidance, StringComparison.Ordinal);
  }

  [Fact]
  public void References_ReachesTheSessionPrompt() =>
      Assert.Contains(ReferencesPromptProvider.Guidance, SessionPrompt(), StringComparison.Ordinal);

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
