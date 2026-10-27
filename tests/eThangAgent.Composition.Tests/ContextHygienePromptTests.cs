using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;
using static eThangAgent.AgentDomain.Agent;

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
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!, host, db.Database)
        .BuildServiceProvider();
  }

  [Fact]
  public void Guidance_IsStaticAndShort()
  {
    Assert.Contains("context_trim", ContextHygienePromptProvider.Guidance, StringComparison.Ordinal);
    Assert.Contains("Clean as you go", ContextHygienePromptProvider.Guidance, StringComparison.Ordinal);
    Assert.True(ContextHygienePromptProvider.Guidance.Length < 600); // a few dozen tokens
  }

  /// <summary>THE GUIDANCE MUST NAME A REACHABLE DOOR (issue #288 regression).
  ///     <para>Since #288 the loop registry holds exactly ONE tool - exec - and every
  ///     other capability is reached as <c>Tools.*</c> inside an exec program. The
  ///     hygiene prompt and the loop's soft reminder were written before that collapse
  ///     and still say "trim it with the context_trim tool", which reads as a TOP-LEVEL
  ///     tool call. A model that follows the sentence literally gets
  ///     <c>Error [UnknownTool]</c> for the one verb the harness just asked it to use -
  ///     observed 2026-10-26 in a session that then guessed at the tool's parameters
  ///     instead of using it.</para>
  ///     <para>The assertion is deliberately about REACHABILITY, not wording: whatever
  ///     the sentence says, it must not present context_trim as a call the model can make
  ///     without going through exec.</para></summary>
  [Fact]
  public void Guidance_PointsAtADoorTheModelActuallyHas()
  {
    string guidance = ContextHygienePromptProvider.Guidance;

    Assert.True(
        guidance.Contains("Tools.context_trim", StringComparison.Ordinal)
        || guidance.Contains("exec", StringComparison.Ordinal),
        "the hygiene guidance must say HOW to reach context_trim (Tools.context_trim inside "
        + "exec); since #288 it is not a top-level tool. Guidance was: " + guidance);

    // The bare-tool phrasing is the defect: it reads as a top-level call.
    Assert.DoesNotContain("with the context_trim tool", guidance, StringComparison.Ordinal);
  }

  /// <summary>The loop's one-time soft reminder has the same contract: it nudges the model
  ///     to trim, so it must name the same reachable door the prompt names.</summary>
  [Fact]
  public void SoftReminder_NamesTheSameReachableDoor()
  {
    string reminder = SoftReminderText(88, 80);

    Assert.True(
        reminder.Contains("Tools.context_trim", StringComparison.Ordinal)
        || reminder.Contains("exec", StringComparison.Ordinal),
        "the soft reminder must say how to reach context_trim. Reminder was: " + reminder);
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
