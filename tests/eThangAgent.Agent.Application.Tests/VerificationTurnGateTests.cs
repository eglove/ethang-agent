using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Verification;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Pins gate B: the turn-end verification nudge. One nudge per turn,
///     never on fresh ledgers, never on failed turns, and byte-identical legacy
///     behavior when the gate or resolver is absent.</summary>
// Named decision (CA1812): the analyzer cannot see the test-harness instantiations.
#pragma warning disable CA1812
public class VerificationTurnGateTests
#pragma warning restore CA1812
{
  private static readonly DateTimeOffset Base = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

  private sealed class FakeLedger : IVerificationLedger
  {
    public List<ShellExecutionRecord> Records { get; } = [];
    public void Append(ShellExecutionRecord record) => Records.Add(record);
    public IReadOnlyList<ShellExecutionRecord> Snapshot() => [.. Records];
  }



  private sealed class ScriptedModelProvider : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _queue = new();

    public void Queue(Result<ModelResponse> response) => _queue.Enqueue(response);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_queue.Dequeue()); // draining an empty queue fails the test loudly
  }

  private sealed class FillerTool : ITool
  {
    public ToolDefinition Definition { get; } = new("filler", "", [], ["timeoutSeconds"]);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult("ok", false));
  }

  private sealed class Harness
  {
    public Conversation Conversation { get; } = new();
    public ScriptedModelProvider Model { get; } = new();
    public FakeLedger Ledger { get; } = new();

    public SendMessageCommandHandler Handler(bool enabled = true)
    {
      VerificationGate gate = new(
          Ledger,
          new VerificationFreshnessSpecification(new VerificationCommandSpecification(["dotnet test"])),
          enabled);
      Ag agent = new(Model, Conversation,
          ModelConfig.Create("m", null, 100, 0.5f, 8192).Value!,
          new ToolRegistry([new FillerTool()]));
      return new SendMessageCommandHandler(
          agent,
          Conversation,
          changedFilesResolver: () => [("a.cs", Base)],
          verificationGate: gate);
    }
  }

  [Fact]
  public async Task DirtyTurn_StaleLedger_AppendsNudgeOnce()
  {
    Harness h = new();
    SendMessageCommandHandler handler = h.Handler();
    h.Model.Queue(Result.Success(new ModelResponse("working", [new ToolCallRequest("c1", "filler", "{}")])));
    h.Model.Queue(Result.Success(new ModelResponse("done", [])));

    Result<string> result = await handler.Handle(new SendMessageCommand("go"), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    List<Message> systems = [.. h.Conversation.Messages.Where(m => m.Role == Role.System)];
    _ = Assert.Single(systems);
    Assert.StartsWith("[verification gate]", systems[0].Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FreshLedger_NoNudge()
  {
    Harness h = new();
    h.Ledger.Append(new ShellExecutionRecord(["dotnet", "test"], 0, Base.AddMinutes(5), Base.AddMinutes(6)));
    SendMessageCommandHandler handler = h.Handler();
    h.Model.Queue(Result.Success(new ModelResponse("done", [])));

    Result<string> result = await handler.Handle(new SendMessageCommand("go"), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.DoesNotContain(h.Conversation.Messages, m => m.Role == Role.System);
  }

  [Fact]
  public async Task FailedTurn_NoNudge()
  {
    Harness h = new();
    SendMessageCommandHandler handler = h.Handler();
    h.Model.Queue(Result.Failure<ModelResponse>(new DomainError("ModelDown", "down")));

    Result<string> result = await handler.Handle(new SendMessageCommand("go"), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    // The gate never fires on failed turns; the only System message is the loop's
    // own turn-failure marker, not a verification-gate line.
    Assert.DoesNotContain(h.Conversation.Messages,
        m => m.Role == Role.System && m.Content.Contains("[verification gate]", StringComparison.Ordinal));
  }

  [Fact]
  public async Task DisabledGate_NoNudge()
  {
    Harness h = new();
    SendMessageCommandHandler handler = h.Handler(enabled: false);
    h.Model.Queue(Result.Success(new ModelResponse("done", [])));

    Result<string> result = await handler.Handle(new SendMessageCommand("go"), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.DoesNotContain(h.Conversation.Messages, m => m.Role == Role.System);
  }
}
