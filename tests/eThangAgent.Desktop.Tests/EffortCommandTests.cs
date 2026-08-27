using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

public class EffortCommandTests
{
  private static (AgentSessionViewModel Vm, List<string> SentInputs) Vm(SessionModelPreferences? preferences)
  {
    List<string> sent = [];
    TestFixtures.StubStore store = new();
    AgentSessionViewModel vm = new(
        (command, ct, onContentDelta, onReasoningDelta, onIterationEnd, onToolCall, onToolResult, onNotice) =>
        {
          sent.Add(command.Text);
          return Task.FromResult(Result.Success("done"));
        },
        new RootSessionLifecycle(store), AgentId.NewId(), new Conversation(),
        "z.ai", "glm-5.3", @"C:\work\demo", modelPreferences: preferences);
    return (vm, sent);
  }

  private static string? Notice(AgentSessionViewModel vm)
      => vm.Transcript.Entries.OfType<NoticeEntry>().LastOrDefault()?.Text;

  [Fact]
  public async Task Effort_WithValidLevel_SetsPreference_AndNotices()
  {
    SessionModelPreferences preferences = new();
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences);

    await vm.SubmitAsync("/effort high");

    Assert.Equal(ReasoningEffort.High, preferences.ReasoningEffort);
    Assert.Contains("set to High", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public async Task Effort_Bare_ShowsCurrentLevel_AndUsage()
  {
    SessionModelPreferences preferences = new() { ReasoningEffort = ReasoningEffort.Low };
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences);

    await vm.SubmitAsync("/effort");

    Assert.Equal(ReasoningEffort.Low, preferences.ReasoningEffort);
    Assert.Contains("Reasoning effort: Low", Notice(vm), StringComparison.Ordinal);
    Assert.Contains("/effort <max|xhigh|high|medium|low|minimal|none>", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Theory]
  [InlineData("/effort turbo")]
  [InlineData("/effort HIGH")]
  public async Task Effort_WithUnknownLevel_Errors_AndChangesNothing(string input)
  {
    SessionModelPreferences preferences = new() { ReasoningEffort = ReasoningEffort.Low };
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences);

    await vm.SubmitAsync(input);

    Assert.Equal(ReasoningEffort.Low, preferences.ReasoningEffort);
    Assert.Contains("Unknown effort", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public async Task Effort_WithoutPreferences_Surface_UnavailableNotice()
  {
    (AgentSessionViewModel vm, List<string> sent) = Vm(null);

    await vm.SubmitAsync("/effort high");

    Assert.Contains("unavailable", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public void Effort_Level_Tokens_ParseExactly()
  {
    string[] valid = ["max", "xhigh", "high", "medium", "low", "minimal", "none"];
    Assert.All(valid, token => Assert.True(DesktopCommands.TryParseEffortLevel(token, out _)));
    Assert.False(DesktopCommands.TryParseEffortLevel("max ", out _));
    Assert.False(DesktopCommands.TryParseEffortLevel("", out _));
  }
}
