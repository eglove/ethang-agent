using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Fake runner: records RunAsync calls, returns a canned result.</summary>
internal sealed class FakeUserCommandRunner : IUserCommandRunner
{
  public string? LastCommand { get; private set; }
  public Result<CommandRun> NextResult { get; set; } =
      Result.Success(new CommandRun(1, "echo hi", 0, false, "hi", DateTimeOffset.UtcNow));

  public Task<Result<CommandRun>> RunAsync(string command, CancellationToken ct = default)
  {
    LastCommand = command;
    return Task.FromResult(NextResult);
  }
}

public class CommandSubmissionTests
{
  private static (AgentSessionViewModel Vm, FakeUserCommandRunner Runner) Build(
      TurnRunner? runner = null)
  {
    StubStore store = new();
    RecordingLifecycle lifecycle = new(store);
    FakeUserCommandRunner commandRunner = new();
    AgentSessionViewModel vm = new(
        runner ?? ((_, _, _, _) => Task.FromResult(Result.Success("ok"))),
        lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions
        {
          WorkspaceRoot = @"C:\work\demo",
          CommandRunner = commandRunner,
        });
    return (vm, commandRunner);
  }

  // ---- ! interception ----

  [Fact]
  public async Task ExclamationSubmission_RunsCommandInsteadOfTurn()
  {
    (AgentSessionViewModel vm, FakeUserCommandRunner runner) = Build();

    await vm.SubmitAsync("! git status");

    Assert.Equal("git status", runner.LastCommand);
    Assert.False(vm.IsBusy);
  }

  [Fact]
  public async Task ExclamationSubmission_AddsCommandEntryAndResultEntry()
  {
    (AgentSessionViewModel vm, FakeUserCommandRunner _) = Build();

    await vm.SubmitAsync("! echo hi");

    CommandRunEntry run = Assert.IsType<CommandRunEntry>(vm.Transcript.Entries[1]);
    Assert.Equal("echo hi", run.Command);
    _ = Assert.IsType<CommandResultEntry>(vm.Transcript.Entries[2]);
  }

  [Fact]
  public async Task ExclamationSubmission_DoesNotAppendUserOrAssistantMessages()
  {
    (AgentSessionViewModel vm, FakeUserCommandRunner _) = Build();

    await vm.SubmitAsync("! echo hi");

    Assert.Empty(vm.Transcript.Entries.OfType<UserMessageEntry>());
    Assert.Empty(vm.Transcript.Entries.OfType<AssistantTextEntry>());
  }

  [Fact]
  public async Task BareExclamation_ShowsNotice_RunsNothing()
  {
    (AgentSessionViewModel vm, FakeUserCommandRunner runner) = Build();

    await vm.SubmitAsync("!");

    Assert.Null(runner.LastCommand);
    _ = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
  }

  [Fact]
  public async Task CommandFailure_ShowsErrorNotice()
  {
    (AgentSessionViewModel vm, FakeUserCommandRunner runner) = Build();
    runner.NextResult = Result.Failure<CommandRun>(new DomainError("ShellStartFailed", "no shell"));

    await vm.SubmitAsync("! boom");

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("Error [ShellStartFailed]", notice.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CommandSubmission_WorksWithNoRunnerWired_ShowsNotice()
  {
    StubStore store = new();
    RecordingLifecycle lifecycle = new(store);
    AgentSessionViewModel vm = new(
        (_, _, _, _) => Task.FromResult(Result.Success("ok")),
        lifecycle, AgentId.NewId(), new Conversation(),
        "OpenRouter", "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo" });

    await vm.SubmitAsync("! echo hi");

    NoticeEntry notice = Assert.IsType<NoticeEntry>(vm.Transcript.Entries[^1]);
    Assert.Contains("unavailable", notice.Text, StringComparison.Ordinal);
  }

  // ---- Mid-turn: runs the command, never steers ----

  [Fact]
  public async Task CommandSubmission_WhileTurnRunning_RunsCommandNotSteer()
  {
    TaskCompletionSource release = new();
    (AgentSessionViewModel vm, FakeUserCommandRunner runner) = Build((_, _, _, _) =>
        release.Task.ContinueWith(_ => Result.Success("done"),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));

    Task turn = vm.SubmitAsync("hello");
    Assert.True(vm.IsBusy);

    await vm.SubmitAsync("! git status");

    Assert.Equal("git status", runner.LastCommand);

    release.SetResult();
    await turn.ConfigureAwait(true);
    await vm.WaitForTurnAsync();
  }
}
