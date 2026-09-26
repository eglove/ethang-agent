using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Ag = eThangAgent.AgentDomain.Agent;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Root-agent wiring: the session's root id rides the holder into every
///     agent the holder builds, so each provider call keys OpenRouter sticky
///     routing on the persisted session id. Null id (legacy wiring) means no id
///     on the request — byte-identical legacy behavior.</summary>
public class RootAgentHolderSessionIdTests
{
  private sealed class RecordingProvider : IModelProvider
  {
    public System.Collections.ObjectModel.Collection<ModelRequest> RequestsSeen { get; } = [];

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
    {
      RequestsSeen.Add(request);
      return Task.FromResult(Result.Success(new ModelResponse("ok", [])));
    }

    public Task<Result<ModelResponse>> SendStreamingAsync(ModelConfig config, ModelRequest request,
        Action<string>? onContentDelta = null, Action<string>? onReasoningDelta = null, CancellationToken ct = default)
        => SendAsync(config, request, ct);
  }

  private static readonly IToolRegistry Tools = new ToolRegistry([]);

  [Fact]
  public async Task Build_WithSessionId_AgentStampsItOnRequests()
  {
    RecordingProvider provider = new();
    Conversation conversation = new();
    AgentId sessionId = AgentId.NewId();
    RootAgentHolder holder = new(provider, conversation, Tools, sessionId: sessionId.ToString());
    ModelConfig config = ModelConfig.Create("first/model", null, 512, 0.5f, 8192).Value!;

    Ag agent = holder.Build(existing: null, config);
    _ = await agent.SendMessage("Hi", ct: TestContext.Current.CancellationToken);

    ModelRequest request = Assert.Single(provider.RequestsSeen);
    Assert.Equal(sessionId.ToString(), request.SessionId);
  }

  [Fact]
  public async Task Build_WithoutSessionId_RequestsCarryNull()
  {
    RecordingProvider provider = new();
    RootAgentHolder holder = new(provider, new Conversation(), Tools);
    ModelConfig config = ModelConfig.Create("first/model", null, 512, 0.5f, 8192).Value!;

    Ag agent = holder.Build(existing: null, config);
    _ = await agent.SendMessage("Hi", ct: TestContext.Current.CancellationToken);

    ModelRequest request = Assert.Single(provider.RequestsSeen);
    Assert.Null(request.SessionId);
  }
}
