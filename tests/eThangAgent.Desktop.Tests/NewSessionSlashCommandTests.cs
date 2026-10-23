using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The /new slash command (issue #234): a shell command resolved BEFORE the
///     skill lookup — with a wired shell seam and an idle session it invokes the
///     command with the rest of the input as the prompt and announces the start;
///     it refuses while busy, without a prompt, or unwired (typed notices, never a
///     coerced turn); it is case-insensitive; unknown slash names still fall through
///     as ordinary messages; and a resolvable skill named 'new' loses to the command.</summary>
public class NewSessionSlashCommandTests
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

  private sealed class RecordingCommand
  {
    public List<string> Prompts { get; } = [];

    public Task Invoke(string prompt)
    {
      Prompts.Add(prompt);
      return Task.CompletedTask;
    }
  }

  private static (AgentSessionViewModel Vm, Conversation Conversation, List<string> Turns, RecordingCommand Command)
      Build(params SkillDefinition[] skills)
  {
    SkillInvocationService invocation = new(new FakeCatalog(skills));
    Conversation conversation = new();
    List<string> turns = [];
    RecordingCommand command = new();
    StubStore store = new();
    RecordingLifecycle lifecycle = new(store);
    List<SkillOption> options = [.. skills.Select(s => new SkillOption(s.Name, s.Description, s.Manual))];
    AgentSessionViewModel vm = new(
        (cmd, _, _, _) =>
        {
          turns.Add(cmd.Text);
          return Task.FromResult(Result.Success("ok"));
        },
        lifecycle, AgentId.NewId(), conversation,
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          SkillInvocation = invocation,
          SkillCatalogSource = () => options,
          NewSessionCommand = command.Invoke,
          UiMarshal = action => action(),
        });
    return (vm, conversation, turns, command);
  }

  [Fact]
  public async Task New_WithPrompt_InvokesCommand_NoSkillLookup_AnnouncesStart()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns, RecordingCommand command) =
        Build(Skill("deploy"), Skill("other"), Skill("manual-skill", manual: true));

    await vm.SubmitAsync("/new explore the auth module").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Equal(["explore the auth module"], command.Prompts);
    Assert.Empty(turns);
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is SystemMessageEntry s && s.Text.StartsWith("[skill invoked", StringComparison.Ordinal));
    Assert.DoesNotContain(conversation.Messages, m => m.Content.StartsWith("[skill invoked", StringComparison.Ordinal));
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is UserMessageEntry);
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[1]);
    Assert.Contains("new session", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task New_WhileBusy_Refused_CommandNotInvoked_NoTurnStarted()
  {
    TestFixtures.ParkingRunner park = new();
    SkillInvocationService invocation = new(new FakeCatalog(Skill("deploy")));
    RecordingCommand command = new();
    Conversation conversation = new();
    List<string> turns = [];
    AgentSessionViewModel vm = new(
        park.RunAsync, new RecordingLifecycle(new StubStore()), AgentId.NewId(), conversation,
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          SkillInvocation = invocation,
          SkillCatalogSource = () => [],
          NewSessionCommand = command.Invoke,
          UiMarshal = action => action(),
        });

    // The turn-runner idiom: the turn task is NOT awaited — SubmitAsync awaits the
    // whole turn, and a parking turn would never return. Start it, wait for the
    // runner's own started signal, then submit the command while busy.
    Task turn = vm.SubmitAsync("start a long turn");
    await park.Started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(vm.IsBusy);

    await vm.SubmitAsync("/new explore the auth module").ConfigureAwait(true);

    Assert.Empty(command.Prompts);
    Assert.Empty(turns);
    // The refusal adds NO user entry: the only user message is the first turn's own.
    Assert.DoesNotContain(vm.Transcript.Entries,
        e => e is UserMessageEntry u && u.Text.StartsWith("/new", StringComparison.Ordinal));
    Assert.Contains(vm.Transcript.Notices,
        n => n.Contains("A turn is running", StringComparison.Ordinal));
    park.Release();
    await turn.ConfigureAwait(true);
  }

  [Fact]
  public async Task New_WithoutPrompt_Refused_CommandNotInvoked()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> turns, RecordingCommand command) = Build(Skill("deploy"));

    await vm.SubmitAsync("/new").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Empty(command.Prompts);
    Assert.Empty(turns);
    Assert.Contains(vm.Transcript.Notices,
        n => n.Contains("Give the new session its first prompt", StringComparison.Ordinal));
  }

  [Fact]
  public async Task New_WhitespaceOnlyPrompt_Refused_CommandNotInvoked()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> _, RecordingCommand command) = Build(Skill("deploy"));

    await vm.SubmitAsync("/new    ").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Empty(command.Prompts);
    Assert.Contains(vm.Transcript.Notices,
        n => n.Contains("Give the new session its first prompt", StringComparison.Ordinal));
  }

  [Fact]
  public async Task New_WithoutCommandWired_UnavailableNotice()
  {
    SkillInvocationService invocation = new(new FakeCatalog(Skill("deploy")));
    Conversation conversation = new();
    List<string> turns = [];
    AgentSessionViewModel vm = new(
        (cmd, _, _, _) =>
        {
          turns.Add(cmd.Text);
          return Task.FromResult(Result.Success("ok"));
        },
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), conversation,
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          SkillInvocation = invocation,
          SkillCatalogSource = () => [],
          UiMarshal = action => action(),
        });

    await vm.SubmitAsync("/new explore").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Contains(vm.Transcript.Notices,
        n => n.Contains("The new-session command is unavailable", StringComparison.Ordinal));
    Assert.Empty(turns);
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is UserMessageEntry);
  }

  [Fact]
  public async Task New_IsCaseInsensitive()
  {
    (AgentSessionViewModel vm, Conversation _, List<string> _, RecordingCommand command) = Build(Skill("deploy"));

    await vm.SubmitAsync("/NEW explore the auth module").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Equal(["explore the auth module"], command.Prompts);
  }

  [Fact]
  public async Task UnknownSlashName_WithCommandWired_StillFallsThroughAsPlainMessage()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns, RecordingCommand command) =
        Build(Skill("deploy"));

    await vm.SubmitAsync("/nope x").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Empty(command.Prompts);
    Assert.Equal(["/nope x"], turns);
    UserMessageEntry user = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[1]);
    Assert.Equal("/nope x", user.Text);
    Assert.DoesNotContain(conversation.Messages, m => m.Content.StartsWith("[skill invoked", StringComparison.Ordinal));
  }

  [Fact]
  public async Task SkillNamedNew_TheCommandWins()
  {
    (AgentSessionViewModel vm, Conversation conversation, List<string> turns, RecordingCommand command) =
        Build(Skill("new"));

    await vm.SubmitAsync("/new explore the auth module").ConfigureAwait(true);
    await vm.WaitForTurnAsync().ConfigureAwait(true);

    Assert.Equal(["explore the auth module"], command.Prompts);
    Assert.Empty(turns);
    Assert.DoesNotContain(vm.Transcript.Entries, e => e is SystemMessageEntry s && s.Text.StartsWith("[skill invoked", StringComparison.Ordinal));
    Assert.DoesNotContain(conversation.Messages, m => m.Content.StartsWith("[skill invoked", StringComparison.Ordinal));
  }
}
