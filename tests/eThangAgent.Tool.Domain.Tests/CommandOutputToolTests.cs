using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Fake store: canned latest/id lookups, records queried ids.</summary>
internal sealed class FakeCommandRunStore : ICommandRunStore
{
  public CommandRun? Latest { get; set; }
  private readonly Dictionary<int, CommandRun> _runs = [];
  public List<int> QueriedIds { get; } = [];

  public void Seed(params CommandRun[] runs)
  {
    foreach (CommandRun run in runs)
    {
      _runs[run.Id] = run;
      Latest = run;
    }
  }

  public Task<Result<CommandRun>> AddAsync(CommandRun run, CancellationToken ct = default)
      => Task.FromResult(Result.Success(run));

  public Task<Result<CommandRun>> GetAsync(int id, CancellationToken ct = default)
  {
    QueriedIds.Add(id);
    return Task.FromResult(_runs.TryGetValue(id, out CommandRun? run)
        ? Result.Success(run)
        : Result.Failure<CommandRun>(new DomainError("CommandRunNotFound", $"no run {id}")));
  }

  public Task<Result<CommandRun>> GetLatestAsync(CancellationToken ct = default)
      => Task.FromResult(Latest is { } run
          ? Result.Success(run)
          : Result.Failure<CommandRun>(new DomainError("CommandRunNotFound", "no runs")));
}

public sealed class CommandOutputToolTests
{
  private static CommandRun Run(int id, string output) => new(id, "git status", 0, false, output, DateTimeOffset.UtcNow);

  private static (CommandOutputTool Tool, FakeCommandRunStore Store) Make()
  {
    FakeCommandRunStore store = new();
    return (new CommandOutputTool(store), store);
  }

  // ---- Resolution ----

  [Fact]
  public async Task NoId_ReturnsLatestRun()
  {
    (CommandOutputTool tool, FakeCommandRunStore store) = Make();
    store.Seed(Run(1, "first"), Run(2, "second"));

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60}"), ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.Contains("second", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WithId_ReturnsThatRun()
  {
    (CommandOutputTool tool, FakeCommandRunStore store) = Make();
    store.Seed(Run(1, "first"), Run(2, "second"));

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60,\"id\":1}"), ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.Contains("first", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownId_SurfacesNotFoundVerbatim()
  {
    (CommandOutputTool tool, FakeCommandRunStore store) = Make();
    store.Seed(Run(1, "first"));

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60,\"id\":99}"), ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsError);
    Assert.Contains("Error [CommandRunNotFound]", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoRunsAtAll_SurfacesNotFound()
  {
    (CommandOutputTool tool, FakeCommandRunStore _) = Make();

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60}"), ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsError);
    Assert.Contains("Error [CommandRunNotFound]", r.Content, StringComparison.Ordinal);
  }

  // ---- Output contract ----

  [Fact]
  public async Task Output_CarriesAnnotationLineWithCommandAndExitCode()
  {
    (CommandOutputTool tool, FakeCommandRunStore store) = Make();
    store.Seed(new CommandRun(3, "dotnet build", 2, false, "error CS1002", DateTimeOffset.UtcNow));

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60}"), ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.StartsWith("[command-run 3] command: dotnet build | exit code 2", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Output_CarriesTimeoutMarker_WhenRunTimedOut()
  {
    (CommandOutputTool tool, FakeCommandRunStore store) = Make();
    store.Seed(new CommandRun(4, "hang", -1, true, "partial", DateTimeOffset.UtcNow));

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60}"), ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.Contains("TIMED OUT (partial output)", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TailLines_CapsTheReturnedTail_AndSaysSo()
  {
    (CommandOutputTool tool, FakeCommandRunStore store) = Make();
    string[] lines = [.. Enumerable.Range(1, 50).Select(i => $"line {i}")];
    store.Seed(Run(5, string.Join("\n", lines)));

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60,\"tailLines\":10}"), ct: TestContext.Current.CancellationToken);

    Assert.False(r.IsError);
    Assert.Contains("line 50", r.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("line 1\n", r.Content, StringComparison.Ordinal);
    Assert.Contains("showing last 10", r.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_IsRejected()
  {
    (CommandOutputTool tool, FakeCommandRunStore _) = Make();

    ToolResult r = await tool.ExecuteAsync(new RawToolInput("command_output", /*lang=json,strict*/ "{\"timeoutSeconds\":60,\"wat\":1}"), ct: TestContext.Current.CancellationToken);

    Assert.True(r.IsError);
    Assert.Contains("Unknown parameter", r.Content, StringComparison.Ordinal);
  }
}
