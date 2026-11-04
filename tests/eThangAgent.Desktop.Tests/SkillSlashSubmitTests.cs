using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The closed slash registry: '/' resolves ONLY a registered session command.
///     A skill name typed after '/' is not a command, so it falls through as an ordinary
///     message and no invocation line is ever injected; leading-whitespace input is never
///     slash input; the popup lists the registry's commands and the catalog's skills, and
///     matches both by name.</summary>
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
    List<ReferenceOption> options =
    [
        .. skills.Select(s => new ReferenceOption(s.Name, s.Description, ReferenceKind.Skill, "@" + s.Name)),
        .. SessionCommandRegistry.All.Select(c => new ReferenceOption(c.Name, c.Description, ReferenceKind.Command, "/" + c.Name)),
    ];
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
          ReferenceCatalogSource = (trigger, query) => [.. options
              .Where(o => trigger == ReferenceTrigger.Command
                  ? o.Kind == ReferenceKind.Command
                  : o.Kind != ReferenceKind.Command)
              .Where(o => o.Name.Contains(query, StringComparison.OrdinalIgnoreCase))],
        });
    return (vm, conversation, turns);
  }

  [Fact]
  public async Task Slash_SkillName_IsAPlainMessage_NoInvocation()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns) = Build(Skill("deploy"));

    await vm.SubmitAsync("/deploy staging now");
    await vm.WaitForTurnAsync();

    Assert.DoesNotContain(vm.Transcript.Entries,
        e => e is SystemMessageEntry s && s.Text.StartsWith("[skill invoked", StringComparison.Ordinal));
    Assert.DoesNotContain(conversation.Messages,
        m => m.Content.StartsWith("[skill invoked", StringComparison.Ordinal));
    UserMessageEntry user = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[1]);
    Assert.Equal("/deploy staging now", user.Text);
    Assert.Equal(["/deploy staging now"], turns);
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
  public async Task Slash_ManualSkillName_IsAPlainMessage()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> turns) = Build(Skill("deploy", manual: true));

    await vm.SubmitAsync("/deploy");
    await vm.WaitForTurnAsync();

    Assert.DoesNotContain(vm.Transcript.Entries,
        e => e is SystemMessageEntry s && s.Text.StartsWith("[skill invoked", StringComparison.Ordinal));
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

    // '/dep' no longer lists the deploy SKILL: a slash token is the closed command registry,
    // so the only row it can offer is a registered command.
    ComposerReferenceViewModel popup = Assert.IsType<ComposerReferenceViewModel>(vm.ReferenceCompletions);
    vm.UpdateReferenceCompletions("/ne", 3);
    Assert.True(popup.IsOpen);
    Assert.Equal("new", popup.Options[0].Name);
    vm.UpdateReferenceCompletions("/dep", 4);
    Assert.False(popup.IsOpen);
    vm.UpdateReferenceCompletions("plain", 5);
    Assert.False(popup.IsOpen);
  }

  [Fact]
  public void Popup_AtTrigger_NeverOffersTheNewCommand()
  {
    // '@new' is not a reference: the command registry is a disjoint list reached only by '/'.
    (AgentSessionViewModel vm, Conversation _, List<string> _) = Build(Skill("deploy"));

    ComposerReferenceViewModel popup = Assert.IsType<ComposerReferenceViewModel>(vm.ReferenceCompletions);
    vm.UpdateReferenceCompletions("@new", 4);
    Assert.DoesNotContain(popup.Options, o => o.Kind == ReferenceKind.Command);
    Assert.False(popup.IsOpen);
  }

  [Fact]
  public void Popup_SlashTrigger_OffersCommandsOnly()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> _) = Build(Skill("deploy"));

    ComposerReferenceViewModel popup = Assert.IsType<ComposerReferenceViewModel>(vm.ReferenceCompletions);
    vm.UpdateReferenceCompletions("/", 1);
    Assert.True(popup.IsOpen);
    Assert.All(popup.Options, o => Assert.Equal(ReferenceKind.Command, o.Kind));
    Assert.Equal("new", Assert.Single(popup.Options).Name);
  }

  [Fact]
  public void Popup_ListsTheRegisteredCommand_AndMatchesItByName()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> _) = Build(Skill("deploy"));

    ComposerReferenceViewModel popup = Assert.IsType<ComposerReferenceViewModel>(vm.ReferenceCompletions);
    vm.UpdateReferenceCompletions("/ne", 3);
    Assert.True(popup.IsOpen);
    ReferenceOption option = Assert.Single(popup.Options);
    Assert.Equal("new", option.Name);
    Assert.Equal(ReferenceKind.Command, option.Kind);
    Assert.Equal("/new", option.InsertText);
  }
}
