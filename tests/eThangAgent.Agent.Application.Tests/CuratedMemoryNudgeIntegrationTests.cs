using System.Text.Json;
using eThangAgent.Agent.Application.Nudges;
using eThangAgent.CapabilityDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.MemoryDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>
/// Pins the write-counter seam end to end: ONE real <see cref="SessionMemoryWriteCounter"/>
/// feeds both consumers — the curated-memory capability provider bumps it on a successful
/// add, and <see cref="SendMessageCommandHandler"/> reads it at turn boundaries without
/// mutating it. This closes the hole the final review found: unit fakes inject constant
/// funcs whose reads are pure, so a composition where every read incremented stayed green
/// while nudges were dead in production. Here the real DefaultNudgePolicy runs over the
/// real counter: an add landing before a firing boundary silences it, and five tool-heavy
/// turns without adds fire the nudge on turn 5 — possible only if reads are
/// side-effect-free and the bump reaches the very instance the handler reads.
/// </summary>
public class CuratedMemoryNudgeIntegrationTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task AddDuringTurnFive_ToolHeavyBoundary_PolicyReadsOne_AndStaysSilent()
  {
    NudgeHarness harness = await RunFiveTurnsAsync(addDuringFinalTurn: true);

    // The add really happened, in the same counter instance the handler reads.
    Assert.Equal(1, harness.Counter.Count);
    _ = Assert.Single(harness.Store.Rows);

    // Turn 5 was a firing boundary (multiple of five, >= 3 tool calls) — but the
    // policy observed MemoriesWrittenTotal == 1 and stayed silent.
    Assert.DoesNotContain(harness.Conversation.Messages, m => m.Role == Role.System);
  }

  [Fact]
  public async Task FiveToolHeavyTurnsWithoutAdds_NudgeFiresOnTurnFive()
  {
    NudgeHarness harness = await RunFiveTurnsAsync(addDuringFinalTurn: false);

    // Five boundary evaluations read the counter without mutating it.
    Assert.Equal(0, harness.Counter.Count);

    // Exactly one nudge — fired by the fifth, tool-heavy, write-free turn.
    List<Message> systems = [.. harness.Conversation.Messages.Where(m => m.Role == Role.System)];
    _ = Assert.Single(systems);
    Assert.Equal(DefaultNudgePolicy.ReminderLine, systems[0].Content);
  }

  /// <summary>Runs five tool-heavy turns (three tool calls each) through a real agent,
  /// handler, DefaultNudgePolicy, and SessionMemoryWriteCounter, optionally performing
  /// a memories.add during the fifth turn's tool phase.</summary>
  private static async Task<NudgeHarness> RunFiveTurnsAsync(bool addDuringFinalTurn)
  {
    NudgeHarness harness = new();

    for (int turn = 1; turn <= 5; turn++)
    {
      List<ToolCallRequest> calls = [];
      for (int i = 0; i < 3; i++)
      {
        calls.Add(new ToolCallRequest($"c{turn}-{i}", "filler", "{}"));
      }

      if (addDuringFinalTurn && turn == 5)
      {
        calls[2] = new ToolCallRequest("c5-add", "memories",
                                 /*lang=json,strict*/
                                 """{"action":"add","content":"durable lesson","category":"insight","scope":"workspace"}""");
      }

      harness.Model.Queue(Result.Success(new ModelResponse(null, calls)));
      harness.Model.Queue(Result.Success(new ModelResponse($"turn {turn} done", [])));

      Result<string> result = await harness.Handler.Handle(new SendMessageCommand($"message {turn}")).ConfigureAwait(false);
      Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    }

    return harness;
  }

  /// <summary>The production shape in miniature: one counter, two consumers.</summary>
  private sealed class NudgeHarness
  {
    public SessionMemoryWriteCounter Counter { get; } = new();
    public InMemoryCuratedMemoryStore Store { get; } = new();
    public Conversation Conversation { get; } = new();
    public ScriptedModelProvider Model { get; } = new();

    public SendMessageCommandHandler Handler { get; }

    public NudgeHarness()
    {
      CuratedMemoryCapabilityProvider capabilities = new(
          Store,
          () => "ws-nudge",
          () => null,
          Counter.Increment,
          () => FixedNow);
      Ag agent = new(Model, Conversation,
          ModelConfig.Create("m", null, 100, 0.5f).Value!,
          new ToolRegistry([new FillerTool(), new MemoriesTool(capabilities)]));
      Handler = new SendMessageCommandHandler(
          agent,
          Conversation,
          new DefaultNudgePolicy(),
          () => Counter.Count);
    }
  }

  private sealed class ScriptedModelProvider : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _queue = new();

    public void Queue(Result<ModelResponse> response) => _queue.Enqueue(response);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(_queue.Dequeue()); // draining an empty queue fails the test loudly
  }

  /// <summary>Routes a tool call into the capability surface, extracting the action
  /// name the way the exec surface does and forwarding the remaining arguments.</summary>
  private sealed class MemoriesTool(CuratedMemoryCapabilityProvider provider) : ITool
  {
    public ToolDefinition Definition { get; } = new("memories", "curated memory actions", []);

    public async Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      using JsonDocument doc = JsonDocument.Parse(input.JsonArguments);
      string action = doc.RootElement.GetProperty("action").GetString()!;
      Dictionary<string, JsonElement> payload = new(StringComparer.Ordinal);
      foreach (JsonProperty property in doc.RootElement.EnumerateObject())
      {
        if (property.Name != "action")
        {
          payload[property.Name] = property.Value.Clone();
        }
      }

      CapabilityInvocationResult result = await provider.InvokeAsync(action, JsonSerializer.Serialize(payload), ct).ConfigureAwait(false);
      return new ToolResult(result.Content, result.IsError);
    }
  }

  private sealed class FillerTool : ITool
  {
    public ToolDefinition Definition { get; } = new("filler", "simulated work", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult("ok", false));
  }

  /// <summary>Minimal in-memory fake; only the add path is exercised here.</summary>
  private sealed class InMemoryCuratedMemoryStore : ICuratedMemoryStore
  {
    public Dictionary<Guid, CuratedMemory> Rows { get; } = [];

    public Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default)
    {
      Rows[memory.Id] = memory;
      return Task.FromResult(Result.Success(memory));
    }

    public Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(Rows.GetValueOrDefault(id)));

    public Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
        string? workspaceId, string? query, MemoryCategory? category,
        IReadOnlyList<string>? tags, int limit, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<CuratedMemory>>(
            [.. Rows.Values.Take(limit)]));

    public Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by nudge integration tests");

    public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
        => throw new NotSupportedException("not exercised by nudge integration tests");
  }
}
