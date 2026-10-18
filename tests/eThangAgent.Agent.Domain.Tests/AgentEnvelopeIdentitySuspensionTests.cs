
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #124 defect 2 (2026-10-15 incident, child 631a4872): a child re-ran ONE
///     identical successful exec call 195 times because it varied the envelope's title
///     each call. The loop must feed the guard the tool's SEMANTIC call identity, not
///     the raw envelope whose transport fields (title, timeoutSeconds) vary per call —
///     the success-repetition breaker escalates to the CRITICAL error at the 13th
///     identity-identical call and ends the turn on the next one.</summary>
public class AgentEnvelopeIdentitySuspensionTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  private sealed class ScriptedModelProvider(params Result<ModelResponse>[] responses) : IModelProvider
  {
    private readonly Queue<Result<ModelResponse>> _responses = new(responses);

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(_responses.Count > 0 ? _responses.Dequeue()
            : Result.Success(new ModelResponse("fin", [])));
  }

  private sealed class RecordingExecTool : ITool, IToolCallIdentifiable
  {
    public string? CallIdentity(string jsonArguments) => ExecTool.CallIdentity(jsonArguments);

    public int _executed;

    public ToolDefinition Definition { get; } = new("exec", "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
    {
      _executed++;
      return Task.FromResult(new ToolResult("the same payload", false));
    }
  }

  private static string Envelope(int callIndex, string program) =>
      System.Text.Json.JsonSerializer.Serialize(new
      {
        timeoutSeconds = 120,
        title = "dump Task 1 test verbatim " + NumberWord(callIndex),
        program,
      });

  private static string NumberWord(int i) => i switch
  {
    1 => "one",
    2 => "two",
    3 => "three",
    4 => "four",
    5 => "five",
    6 => "six",
    7 => "seven",
    8 => "eight",
    9 => "nine",
    10 => "ten",
    11 => "eleven",
    12 => "twelve",
    13 => "thirteen",
    14 => "fourteen",
    _ => "many",
  };

  [Fact]
  public async Task ExecCalls_VaryingOnlyTitle_SameProgram_CriticalThenTurnEnd()
  {
    RecordingExecTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 14; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null,
          [new ToolCallRequest("c" + i, "exec", Envelope(i, "return 42;"))])));
    }

    responses.Add(Result.Success(new ModelResponse("never reached", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal(Agent.RepeatGuardStopCode, result.Error.Code);
    Assert.Equal(14, tool._executed); // every identity-identical call executed
    Assert.Contains(agent.Conversation.Messages,
        m => m.Role is Role.System && m.Content.StartsWith("[repeat guard] CRITICAL:", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ExecCalls_DifferentPrograms_NeverEndTurn()
  {
    RecordingExecTool tool = new();
    List<Result<ModelResponse>> responses = [];
    for (int i = 1; i <= 20; i++)
    {
      responses.Add(Result.Success(new ModelResponse(null,
          [new ToolCallRequest("c" + i, "exec", Envelope(i, "return " + i + ";"))])));
    }

    responses.Add(Result.Success(new ModelResponse("done", [])));
    ScriptedModelProvider provider = new([.. responses]);
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([tool]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(result.IsSuccess);
    Assert.Equal(20, tool._executed);
  }
}
