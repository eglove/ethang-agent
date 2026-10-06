using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>T4 loop wiring: the Agent owns one turn-local freshness ledger per agent,
///     binds it to ledger-capable tools at dispatch, and resets it at turn start and
///     wherever history is replaced (compaction, context shrink) — eliding content
///     the model no longer holds would be a correctness bug. All assertions run
///     end-to-end through the conversation's tool messages.</summary>
public class AgentReadFreshnessTests
{
  private static readonly DateTime Mtime = new(638000000000000000, DateTimeKind.Utc);
  private static readonly FileVersion V1 = new(100, Mtime);

  private static readonly ModelConfig Config = ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  // ---- dispatch binds the agent's ledger ----

  [Fact]
  public async Task SameTurnReRead_Elides()
  {
    Agent agent = MakeAgent(new FreshTool(new FileRead(["alpha", "beta"], 2, 2, V1)), readsPerTurn: 2);

    Result<string> hi = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(hi.IsSuccess);
    IReadOnlyList<Message> toolMessages = [.. agent.Conversation.Messages.Where(m => m.Role is Role.Tool)];
    Assert.Equal(2, toolMessages.Count);
    Assert.DoesNotContain("elided", toolMessages[0].Content, StringComparison.Ordinal);
    Assert.Contains("unchanged since your last read", toolMessages[1].Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CrossTurnRead_FullContentAgain()
  {
    Agent agent = MakeAgent(new FreshTool(new FileRead(["alpha", "beta"], 2, 2, V1)), readsPerTurn: 2);

    Result<string> hi = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);
    Result<string> again = await agent.SendMessage("again", ct: TestContext.Current.CancellationToken);

    Assert.True(hi.IsSuccess);
    Assert.True(again.IsSuccess);
    // Turn 2's FIRST read runs against a ledger reset at turn start: full content.
    Message secondUser = agent.Conversation.Messages.Where(m => m.Role is Role.User).Skip(1).First();
    Message firstReadOfTurn2 = agent.Conversation.Messages.SkipWhile(m => m != secondUser)
        .First(m => m.Role is Role.Tool);
    Message lastReadOfTurn2 = agent.Conversation.Messages.Last(m => m.Role is Role.Tool);
    Assert.DoesNotContain("elided", firstReadOfTurn2.Content, StringComparison.Ordinal);
    // ...and the turn's second read re-records and elides again.
    Assert.Contains("unchanged since your last read", lastReadOfTurn2.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task DistinctAgents_DoNotShareLedger()
  {
    Agent first = MakeAgent(new FreshTool(new FileRead(["alpha"], 1, 1, V1)), readsPerTurn: 2);
    Agent second = MakeAgent(new FreshTool(new FileRead(["alpha"], 1, 1, V1)), readsPerTurn: 2);

    Result<string> hi1 = await first.SendMessage("hi", ct: TestContext.Current.CancellationToken);
    Result<string> hi2 = await second.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(hi1.IsSuccess);
    Assert.True(hi2.IsSuccess);
    // The second agent's FIRST read runs against its own empty ledger: full content.
    Message firstReadOfSecond = second.Conversation.Messages.First(m => m.Role is Role.Tool);
    Assert.DoesNotContain("elided", firstReadOfSecond.Content, StringComparison.Ordinal);
  }

  // ---- history replacement resets the ledger ----

  [Fact]
  public async Task Compaction_ResetsLedger()
  {
    FreshTool tool = new(new FileRead(["alpha"], 1, 1, V1));
    Conversation conversation = new();
    conversation.AddUserMessage(new string('x', 8000)); // believable usage reports
    ScriptedCompactingCompactor compactor = new();
    Agent agent = new(new RepeatingReadProvider(readsPerTurn: 3), conversation, Config,
        new ToolRegistry([tool]),
        new AgentOptions
        {
          ContextMonitor = new DelayedThresholdMonitor(fullAtCall: 2),
          ContextCompactor = compactor,
        });

    Result<string> go = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(go.IsSuccess);
    IReadOnlyList<Message> toolMessages = [.. agent.Conversation.Messages.Where(m => m.Role is Role.Tool)];
    Assert.Equal(3, toolMessages.Count);
    Assert.DoesNotContain("elided", toolMessages[0].Content, StringComparison.Ordinal);
    // Read 2 hits the ledger recorded by read 1...
    Assert.Contains("unchanged since your last read", toolMessages[1].Content, StringComparison.Ordinal);
    // ...and read 3 runs AFTER the compaction reset the ledger: full content.
    Assert.DoesNotContain("elided", toolMessages[2].Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ContextShrink_ResetsLedger()
  {
    // The sentinel rides every tool result: read 1 trips the shrink path (reset),
    // so read 2 — same turn — runs against a fresh ledger.
    Agent agent = MakeAgent(new FreshTool(new FileRead(["alpha"], 1, 1, V1), sentinel: true), readsPerTurn: 2);

    Result<string> hi = await agent.SendMessage("hi", ct: TestContext.Current.CancellationToken);

    Assert.True(hi.IsSuccess);
    IReadOnlyList<Message> toolMessages = [.. agent.Conversation.Messages.Where(m => m.Role is Role.Tool)];
    Assert.Equal(2, toolMessages.Count);
    Assert.Contains("[context: shrank", toolMessages[0].Content, StringComparison.Ordinal);
    Assert.DoesNotContain("elided", toolMessages[1].Content, StringComparison.Ordinal);
  }

  // ---- helpers + fakes ----

  private static Agent MakeAgent(ITool tool, int readsPerTurn)
      => new(new RepeatingReadProvider(readsPerTurn), new Conversation(), Config, new ToolRegistry([tool]));

  /// <summary>Issues the same read on the first <paramref name="readsPerTurn"/>
  ///     iterations of a turn (tracked by assistant messages already in the
  ///     request), then answers without tools.</summary>
  private sealed class RepeatingReadProvider(int readsPerTurn) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      int lastUser = request.Messages.Select((m, i) => (m, i)).LastOrDefault(x => x.m.Role is Role.User).i;
      int assistantCount = request.Messages.Count(m => m.Role is Role.Assistant)
          - request.Messages.Take(lastUser).Count(m => m.Role is Role.Assistant);
      ModelResponse response = assistantCount < readsPerTurn
          ? new ModelResponse(null, [new ToolCallRequest("c1", "read", "{}")], Usage: new TokenUsage(10, 5))
          : new ModelResponse("done", [], Usage: new TokenUsage(10, 5));
      return Task.FromResult(Result.Success(response));
    }
  }

  private sealed class FreshTool(FileRead read, bool sentinel = false) : ITool, ILedgerBoundTool
  {
    public bool HasLedger => _ledger is not null;

    private ReadFreshnessLedger? _ledger;

    // The registry instance stays unbound; each agent's dispatch binds its own
    // ledger into a NEW tool — the same immutable shape the real ReadTool has.
    public ITool WithLedger(ReadFreshnessLedger ledger) => new FreshTool(read, sentinel) { _ledger = ledger };

    public ToolDefinition Definition { get; } = new("read", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      // Mirror the real ReadTool: Check -> format -> Record.
      FileCoverage coverage = _ledger?.Check("f", read.Version, 1, 2) ?? new FileCoverage([]);
      _ledger?.Record("f", read.Version, 1, 2);
      string content = coverage.IsEmpty
          ? $"[read f lines 1-2 of 2 total | {read.Version!.Token}]\n1→ alpha\n2→ beta"
          : $"[read f lines 1-2 of 2 total | {read.Version!.Token} | unchanged since your last read; content elided]";
      return Task.FromResult(new ToolResult(sentinel ? content + " [context: shrank 1 message]" : content, false));
    }
  }

  /// <summary>Reports 0% utilization for the first <paramref name="fullAtCall"/> - 1
  ///     requests, then 80% — so the compaction backstop fires mid-turn, after the
  ///     early reads have recorded coverage.</summary>
  private sealed class DelayedThresholdMonitor(int fullAtCall) : IContextMonitor
  {
    private int _calls;

    public ContextStatus Status { get; private set; } = new(0, 0, 0, 1000, 0.0);

    public ContextBreakdown? Breakdown => null;

    public void OnRequestUsage(TokenUsage usage, ContextComposition composition)
    {
      _calls++;
      Status = _calls >= fullAtCall
          ? new ContextStatus(800, 800, 0, 1000, 80.0)
          : new ContextStatus(0, 0, 0, 1000, 0.0);
    }
  }

  private sealed class ScriptedCompactingCompactor : IContextCompactor
  {
    public int Calls { get; private set; }

    public Task<Result<CompactionOutcome>> CompactAsync(Conversation conversation, ModelConfig servingModel, CancellationToken ct = default)
    {
      Calls++;
      Result<bool> replaced = conversation.Compact(
          [
              new Message(Role.System, "[Conversation summary - earlier messages were compacted.]\nhandoff", DateTimeOffset.UtcNow, IsSummary: true),
              ..conversation.Messages,
          ]);
      return Task.FromResult(replaced.IsSuccess
          ? Result.Success(new CompactionOutcome(1, conversation.Messages.Count - 1, new TokenUsage(50, 100)))
          : Result.Failure<CompactionOutcome>(replaced.Error));
    }
  }
}
