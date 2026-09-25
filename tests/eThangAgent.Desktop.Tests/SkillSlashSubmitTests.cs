using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Desktop slash skill invocation (plan #30 task 5): a raw-input
///     leading '/' resolves through the session's SkillInvocationService —
///     found injects the System line then forwards the FULL original input as
///     the ordinary user message; unknown names stay plain messages; ambiguous
///     names send nothing and surface a notice listing the matches; manual
///     skills resolve; leading-whitespace input is never slash input.</summary>
public class SkillSlashSubmitTests
{
  private static SkillDefinition Skill(string name, bool manual = false) => new(
      name, "Does " + name + " things", "Body of " + name, 1,
      manual ? SkillSource.File : SkillSource.BuiltIn,
      null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, manual, null);

  private sealed class FakeCatalog(params SkillDefinition[] skills) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([.. skills]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", name)));
  }

  private static (AgentSessionViewModel Vm, Conversation Conversation, List<string> Turns)
      Build(params SkillDefinition[] skills)
  {
    SkillInvocationService invocation = new(new FakeCatalog(skills));
    Conversation conversation = new();
    List<string> turns = [];
    StubStore store = new();
    RecordingLifecycle lifecycle = new(store);
    List<SkillOption> options = [.. skills.Select(s => new SkillOption(s.Name, s.Description, s.Manual))];
    AgentSessionViewModel vm = new(
        (command, _, _, _) =>
        {
          turns.Add(command.Text);
          return Task.FromResult(Result.Success("ok"));
        },
        lifecycle, AgentId.NewId(), conversation,
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          SkillInvocation = invocation,
          SkillCatalogSource = () => options,
        });
    return (vm, conversation, turns);
  }

  [Fact]
  public async Task Slash_Found_InjectsSystem_Then_ForwardsFullInput()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns) = Build(Skill("deploy"));

    await vm.SubmitAsync("/deploy staging now");
    await vm.WaitForTurnAsync();

    SystemMessageEntry system = Assert.IsType<SystemMessageEntry>(vm.Transcript.Entries[1]);
    Assert.StartsWith("[skill invoked: deploy]", system.Text, StringComparison.Ordinal);
    UserMessageEntry user = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[2]);
    Assert.Equal("/deploy staging now", user.Text);
    Assert.Equal(["/deploy staging now"], turns);
    _ = Assert.Single(conversation.Messages, m =>
        m.Role == Role.System && m.Content.StartsWith("[skill invoked: deploy]", StringComparison.Ordinal));
    Assert.Contains("arguments: staging now", system.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Slash_Unknown_SendsPlainMessage()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns) = Build(Skill("deploy"));

    await vm.SubmitAsync("/nope x");
    await vm.WaitForTurnAsync();

    Assert.Equal(["/nope x"], turns);
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is SystemMessageEntry s && s.Text.StartsWith("[skill invoked", StringComparison.Ordinal));
    Assert.DoesNotContain(conversation.Messages, m => m.Content.StartsWith("[skill invoked", StringComparison.Ordinal));
    UserMessageEntry user = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[1]);
    Assert.Equal("/nope x", user.Text);
  }

  [Fact]
  public async Task Slash_Ambiguous_NothingSent_NoticeListsMatches()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns) = Build(Skill("deploy"), Skill("Deploy"));

    await vm.SubmitAsync("/deploy");
    await vm.WaitForTurnAsync();

    Assert.Empty(turns);
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is UserMessageEntry);
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is SystemMessageEntry s && s.Text.StartsWith("[skill invoked", StringComparison.Ordinal));
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("deploy", notice.Text, StringComparison.Ordinal);
    Assert.DoesNotContain(conversation.Messages, m => m.Content.StartsWith("[skill invoked", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Slash_ManualSkill_Resolves()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> turns) = Build(Skill("deploy", manual: true));

    await vm.SubmitAsync("/deploy");
    await vm.WaitForTurnAsync();

    SystemMessageEntry system = Assert.IsType<SystemMessageEntry>(vm.Transcript.Entries[1]);
    Assert.StartsWith("[skill invoked: deploy]", system.Text, StringComparison.Ordinal);
    Assert.Equal(["/deploy"], turns);
  }

  [Fact]
  public async Task LeadingSpace_Slash_IsPlainMessage()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns) = Build(Skill("deploy"));

    await vm.SubmitAsync(" /deploy");
    await vm.WaitForTurnAsync();

    Assert.DoesNotContain(vm.Transcript.Entries, e => e is SystemMessageEntry s && s.Text.StartsWith("[skill invoked", StringComparison.Ordinal));
    Assert.DoesNotContain(conversation.Messages, m => m.Content.StartsWith("[skill invoked", StringComparison.Ordinal));
    UserMessageEntry user = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[1]);
    Assert.Equal("/deploy", user.Text);
    Assert.Equal(["/deploy"], turns);
  }

  [Fact]
  public void Popup_HeldBySessionVm_And_FedOnUpdate()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> _) = Build(Skill("deploy"));

    Assert.NotNull(vm.Autocomplete);
    vm.UpdateAutocomplete("/dep");
    Assert.True(vm.Autocomplete.IsOpen);
    Assert.Equal("deploy", vm.Autocomplete.Options[0].Name);
    vm.UpdateAutocomplete("plain");
    Assert.False(vm.Autocomplete.IsOpen);
  }
}
