using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Recording subclass — overrides AppendExchangeAsync to count calls
/// without executing real persistence (the conversation in these tests
/// has no messages, so calling base would throw an index-out-of-range).
/// RootSessionLifecycle persistence semantics are covered in
/// eThangAgent.Composition.Tests.
/// </summary>
internal sealed class RecordingLifecycle(IAgentStore store) : RootSessionLifecycle(store)
{
  public int _exchanges;

  public override Task AppendExchangeAsync(
      AgentId rootId, Conversation conversation, int messageCountBefore,
      Result<string> result, Action<string> reportError)
  {
    _exchanges++;
    return Task.CompletedTask;
  }
}

/// <summary>
/// Lifecycle override whose persistence step fails through the reportError
/// callback — mirroring how RootSessionLifecycle surfaces store failures —
/// while still recording that the exchange was booked.
/// </summary>
internal sealed class PersistenceErroringLifecycle(IAgentStore store)
    : RootSessionLifecycle(store)
{
  public int _exchanges;

  public override Task AppendExchangeAsync(
      AgentId rootId, Conversation conversation, int messageCountBefore,
      Result<string> result, Action<string> reportError)
  {
    ArgumentNullException.ThrowIfNull(reportError);
    _exchanges++;
    reportError("Error [DbDown]: nope");
    return Task.CompletedTask;
  }
}

public class AgentSessionViewModelTests
{
  private static (AgentSessionViewModel Vm, List<string> Errors, RecordingLifecycle Lifecycle)
      Build(TurnRunner runner)
  {
    StubStore store = new();
    RecordingLifecycle lifecycle = new(store);
    List<string> errors = [];
    AgentSessionViewModel vm = new(
        runner, lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model", workspaceRoot: @"C:\work\demo");
    return (vm, errors, lifecycle);
  }

  // ── 1. /help ──────────────────────────────────────────────────────────────

  [Fact]
  public async Task Help_Prints_Command_List_Not_Sent_To_Model()
  {
    int sent = 0;
    (AgentSessionViewModel? vm, List<string> _, RecordingLifecycle _) = Build((_, _, _, _, _, _, _, _) =>
    {
      sent++;
      return Task.FromResult(Result.Success(""));
    });

    await vm.SubmitAsync("/help");

    Assert.Equal(0, sent);
    Assert.False(vm.IsBusy);
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("/help", notice.Text, StringComparison.Ordinal);
    Assert.Contains("/exit", notice.Text, StringComparison.Ordinal);
    Assert.Contains("/quit", notice.Text, StringComparison.Ordinal);
  }

  // ── 2. /exit, /quit ───────────────────────────────────────────────────────

  [Theory]
  [InlineData("/exit")]
  [InlineData("/quit")]
  public async Task Quit_Commands_Request_Close_Without_Model_Call(string cmd)
  {
    int sent = 0;
    bool closed = false;
    StubStore store = new();
    AgentSessionViewModel vm = new(
        (_, _, _, _, _, _, _, _) =>
        {
          sent++;
          return Task.FromResult(Result.Success(""));
        },
        new RecordingLifecycle(store), AgentId.NewId(), new Conversation(), "OpenRouter", "m",
        workspaceRoot: @"C:\work\demo");
    vm.CloseRequested += (_, _) => closed = true;

    await vm.SubmitAsync(cmd);

    Assert.True(closed);
    Assert.Equal(0, sent);
  }

  // ── 3. Normal turn ────────────────────────────────────────────────────────

  [Fact]
  public async Task Normal_Turn_Appends_User_Entry_Disables_Input_And_Books_Exchange()
  {
    (AgentSessionViewModel? vm, List<string> _, RecordingLifecycle? lifecycle) = Build(async (_, _, onContent, _, _, _, _, _) =>
    {
      onContent!("hel");
      onContent!("lo");
      await Task.Yield();
      return Result.Success("hello");
    });

    Task turnTask = vm.SubmitAsync("hi");
    await turnTask.ConfigureAwait(true);
    await vm.WaitForTurnAsync();

    _ = Assert.IsType<UserMessageEntry>(vm.Transcript.Entries[0]);
    AssistantTextEntry last = Assert.IsType<AssistantTextEntry>(vm.Transcript.Entries[^1]);
    Assert.Equal("hello", last.Text);
    Assert.False(vm.IsBusy);
    Assert.Equal(1, lifecycle._exchanges);
    Assert.Equal(1, vm.MessageCount);
  }

  // ── 3a. Failure produces error notice ─────────────────────────────────────

  [Fact]
  public async Task Failure_Produces_Error_Notice_With_Code()
  {
    (AgentSessionViewModel? vm, List<string> _, RecordingLifecycle _) = Build((_, _, _, _, _, _, _, _) =>
        Task.FromResult(Result.Failure<string>(new DomainError("RateLimited", "slow down"))));

    await vm.SubmitAsync("go");
    await vm.WaitForTurnAsync();

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [RateLimited]: slow down", notice.Text, StringComparison.Ordinal);
  }

  // ── 3b. Success with no deltas falls back to notice ───────────────────────

  [Fact]
  public async Task Success_Without_Streamed_Deltas_Falls_Back_To_Final_Text_Notice()
  {
    (AgentSessionViewModel? vm, List<string> _, RecordingLifecycle _) = Build((_, _, _, _, _, _, _, _) =>
        Task.FromResult(Result.Success("plain answer")));

    await vm.SubmitAsync("q");
    await vm.WaitForTurnAsync();

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("plain answer", notice.Text, StringComparison.Ordinal);
  }

  // ── 4. Busy submissions ignored ───────────────────────────────────────────

  [Fact]
  public async Task Submission_While_Busy_Is_Ignored()
  {
    TaskCompletionSource release = new();
    (AgentSessionViewModel? vm, List<string> _, RecordingLifecycle _) = Build((_, _, _, _, _, _, _, _) =>
        release.Task.ContinueWith(_ => Result.Success("done"),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));

    Task first = vm.SubmitAsync("one");
    Assert.True(vm.IsBusy);

    await vm.SubmitAsync("two"); // ignored — no second user entry

    release.SetResult();
    await first.ConfigureAwait(true);
    await vm.WaitForTurnAsync();

    _ = Assert.Single(vm.Transcript.Entries.OfType<UserMessageEntry>());
  }

  // ── 5. Persistence errors route through reportError → notice entries ─────

  [Fact]
  public async Task Persistence_Error_Routes_Through_ReportError_To_Notice_Entry()
  {
    PersistenceErroringLifecycle lifecycle = new(new StubStore());
    AgentSessionViewModel vm = new(
        (_, _, _, _, _, _, _, _) =>
            Task.FromResult(Result.Success("answer")),
        lifecycle, AgentId.NewId(), new Conversation(), "OpenRouter", "test/model",
        workspaceRoot: @"C:\work\demo");

    await vm.SubmitAsync("hi");
    await vm.WaitForTurnAsync();

    Assert.Contains(vm.Transcript.Entries.OfType<NoticeEntry>(),
        n => n.Text.Contains("Error [DbDown]", StringComparison.Ordinal));
    Assert.Equal(1, lifecycle._exchanges);
  }

  // ── 6. Blank input ignored ────────────────────────────────────────────────

  [Fact]
  public async Task Blank_Input_Is_Ignored()
  {
    (AgentSessionViewModel? vm, List<string> _, RecordingLifecycle _) = Build((_, _, _, _, _, _, _, _) =>
        Task.FromResult(Result.Success("x")));

    await vm.SubmitAsync("   ");

    Assert.Empty(vm.Transcript.Entries);
  }

  // ── 7. Model picker choice ────────────────────────────────────────────────

  [Fact]
  public void ApplyModelChoice_Pins_Session_Model_Updates_Status_And_Announces()
  {
    SessionModelPreferences preferences = new();
    AgentSessionViewModel vm = new(
        (_, _, _, _, _, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "openrouter/auto", workspaceRoot: @"C:\work\demo",
        modelPreferences: preferences);

    vm.ApplyModelChoice("anthropic/claude");

    Assert.Equal("anthropic/claude", preferences.ModelId);
    Assert.Equal("anthropic/claude", vm.Status.ModelId);
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("anthropic/claude", notice.Text, StringComparison.Ordinal);
    Assert.Contains("next turn", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void ApplyModelChoice_Auto_Clears_Choice_And_Restores_Session_Default()
  {
    SessionModelPreferences preferences = new() { ModelId = "anthropic/claude" };
    AgentSessionViewModel vm = new(
        (_, _, _, _, _, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "openrouter/auto", workspaceRoot: @"C:\work\demo",
        modelPreferences: preferences);

    vm.ApplyModelChoice(null);

    Assert.Null(preferences.ModelId);
    Assert.Equal("openrouter/auto", vm.Status.ModelId); // the session's bootstrap model
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("automatic", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void ApplyModelChoice_Without_Preferences_Notices_Unavailable()
  {
    AgentSessionViewModel vm = new(
        (_, _, _, _, _, _, _, _) => Task.FromResult(Result.Success("")),
        new RecordingLifecycle(new StubStore()), AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model", workspaceRoot: @"C:\work\demo");

    vm.ApplyModelChoice("anthropic/claude");

    Assert.Equal("test/model", vm.Status.ModelId); // untouched
    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("unavailable", notice.Text, StringComparison.Ordinal);
  }
}
