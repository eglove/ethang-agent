using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

public class ModelCommandTests
{
  private static readonly string[] ZaiChoices = ["glm-5.3", "glm-5.3-flash"];

  private static (AgentSessionViewModel Vm, List<string> SentInputs) Vm(
      SessionModelPreferences? preferences, bool hasSelectableLineup = true)
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
        "z.ai", "glm-5.3-flash", @"C:\work\demo",
        modelPreferences: preferences, selectableModels: hasSelectableLineup ? ZaiChoices : null);
    return (vm, sent);
  }

  private static string? Notice(AgentSessionViewModel vm)
      => vm.Transcript.Entries.OfType<NoticeEntry>().LastOrDefault()?.Text;

  [Theory]
  [InlineData("glm-5.3")]
  [InlineData("glm-5.3-flash")]
  public async Task Model_WithValidChoice_SetsPreference_AndNotices(string choice)
  {
    SessionModelPreferences preferences = new();
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences);

    await vm.SubmitAsync($"/model {choice}");

    Assert.Equal(choice, preferences.ModelId);
    Assert.Contains($"Model set to {choice}", Notice(vm), StringComparison.Ordinal);
    Assert.Contains("applies from the next turn", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public async Task Model_Bare_ShowsCurrentModel_AndUsage()
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences);

    await vm.SubmitAsync("/model");

    Assert.Equal("glm-5.3", preferences.ModelId);
    Assert.Contains("Session model: glm-5.3", Notice(vm), StringComparison.Ordinal);
    Assert.Contains("/model <glm-5.3|glm-5.3-flash>", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public async Task Model_Bare_WithoutChoice_ShowsBootstrapDefault()
  {
    SessionModelPreferences preferences = new();
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences);

    await vm.SubmitAsync("/model");

    Assert.Null(preferences.ModelId);
    Assert.Contains("Session model: glm-5.3-flash", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Theory]
  [InlineData("/model glm-4.6")]
  [InlineData("/model GLM-5.3")]
  [InlineData("/model auto")]
  public async Task Model_WithUnknownChoice_Errors_AndChangesNothing(string input)
  {
    SessionModelPreferences preferences = new() { ModelId = "glm-5.3" };
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences);

    await vm.SubmitAsync(input);

    Assert.Equal("glm-5.3", preferences.ModelId);
    Assert.Contains("Unknown model", Notice(vm), StringComparison.Ordinal);
    Assert.Contains("glm-5.3, glm-5.3-flash", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public async Task Model_OnAutomaticallySelectingProvider_IsUnavailable()
  {
    SessionModelPreferences preferences = new();
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences, hasSelectableLineup: false);

    await vm.SubmitAsync("/model glm-5.3");

    Assert.Null(preferences.ModelId);
    Assert.Contains("unavailable", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public async Task Model_WithoutPreferences_IsUnavailable()
  {
    (AgentSessionViewModel vm, List<string> sent) = Vm(preferences: null);

    await vm.SubmitAsync("/model glm-5.3");

    Assert.Contains("unavailable", Notice(vm), StringComparison.Ordinal);
    Assert.Empty(sent);
  }

  [Fact]
  public void Model_Command_IsListed_ForHelpAndAutocomplete()
      => Assert.Contains(DesktopCommands.All, c => c.Name == "/model");
}
