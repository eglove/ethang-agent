using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Fake shell seam: records the anchor directory and command, returns a
///     canned result.</summary>
internal sealed class FakeShellCommandAccess : IShellCommandAccess
{
  public string? AnchorDirectory { get; private set; }
  public string? Command { get; private set; }
  public Result<ShellRun> NextResult { get; set; } = Result.Success(new ShellRun(0, "", false));

  public Task<Result<ShellRun>> RunAsync(string workingDirectory, string command, TimeSpan timeout, CancellationToken ct = default)
  {
    AnchorDirectory = workingDirectory;
    Command = command;
    return Task.FromResult(NextResult);
  }
}

/// <summary>Fake store: assigns ids 1..n in call order.</summary>
internal sealed class FakeCommandRunStore : ICommandRunStore
{
  private readonly List<CommandRun> _runs = [];
  public int NextId { get; set; } = 1;

  public IReadOnlyList<CommandRun> GetRunsForTests() => _runs;

  public Task<Result<CommandRun>> AddAsync(CommandRun run, CancellationToken ct = default)
  {
    CommandRun assigned = run with { Id = NextId };
    _runs.Add(assigned);
    NextId++;
    return Task.FromResult(Result.Success(assigned));
  }

  public Task<Result<CommandRun>> GetAsync(int id, CancellationToken ct = default)
      => Task.FromResult(_runs.FirstOrDefault(r => r.Id == id) is { } run
          ? Result.Success(run)
          : Result.Failure<CommandRun>(new DomainError("CommandRunNotFound", "nope")));

  public Task<Result<CommandRun>> GetLatestAsync(CancellationToken ct = default)
      => Task.FromResult(_runs.Count > 0
          ? Result.Success(_runs[^1])
          : Result.Failure<CommandRun>(new DomainError("CommandRunNotFound", "nope")));
}

public sealed class UserCommandRunnerTests
{
  private static (UserCommandRunner Runner, FakeShellCommandAccess Shell, FakeCommandRunStore Store, Conversation Conversation)
      Make(string workspaceRoot = @"C:\ws\demo")
  {
    FakeShellCommandAccess shell = new();
    FakeCommandRunStore store = new();
    Conversation conversation = new();
    return (new UserCommandRunner(workspaceRoot, shell, store, conversation), shell, store, conversation);
  }

  [Fact]
  public async Task Run_AnchorsCommandAtWorkspaceRoot()
  {
    (UserCommandRunner runner, FakeShellCommandAccess shell, FakeCommandRunStore _, Conversation _) = Make(@"C:\ws\demo");

    _ = await runner.RunAsync("git status", TestContext.Current.CancellationToken);

    Assert.Equal(@"C:\ws\demo", shell.AnchorDirectory);
    Assert.Equal("git status", shell.Command);
  }

  [Fact]
  public async Task Run_PersistsRunWithAssignedId_AndReturnsIt()
  {
    (UserCommandRunner runner, FakeShellCommandAccess _, FakeCommandRunStore store, Conversation _) = Make();
    _ = await runner.RunAsync("echo hi", TestContext.Current.CancellationToken);

    _ = Assert.Single(store.GetRunsForTests());
  }

  [Fact]
  public async Task Run_AppendsSystemMessageToConversation_WithCommandIdAndExitCode()
  {
    (UserCommandRunner runner, FakeShellCommandAccess _, FakeCommandRunStore _, Conversation conversation) = Make();

    _ = await runner.RunAsync("git status", TestContext.Current.CancellationToken);

    Message message = Assert.Single(conversation.Messages);
    Assert.Equal(Role.System, message.Role);
    Assert.Contains("git status", message.Content, StringComparison.Ordinal);
    Assert.Contains("id: 1", message.Content, StringComparison.Ordinal);
    Assert.Contains("exit code 0", message.Content, StringComparison.Ordinal);
    Assert.Contains("command_output", message.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Run_TimedOutRun_MentionsTimeoutInSystemMessage()
  {
    (UserCommandRunner runner, FakeShellCommandAccess shell, FakeCommandRunStore _, Conversation conversation) = Make();
    shell.NextResult = Result.Success(new ShellRun(-1, "partial", TimedOut: true));

    _ = await runner.RunAsync("long-running", TestContext.Current.CancellationToken);

    Assert.Contains("timed out", Assert.Single(conversation.Messages).Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Run_NonzeroExit_MentionsExitCode()
  {
    (UserCommandRunner runner, FakeShellCommandAccess shell, FakeCommandRunStore _, Conversation conversation) = Make();
    shell.NextResult = Result.Success(new ShellRun(7, "err", false));

    _ = await runner.RunAsync("failing", TestContext.Current.CancellationToken);

    Assert.Contains("exit code 7", Assert.Single(conversation.Messages).Content, StringComparison.Ordinal);
  }
}
