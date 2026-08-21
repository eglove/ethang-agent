using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Always-successful tool returning a fixed content string.</summary>
public sealed class FakeTool(string name, string resultContent) : ITool
{
    public ToolDefinition Definition { get; } = new ToolDefinition(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(new ToolResult(resultContent, false));
}

/// <summary>In-proc scripted provider: replays queued results, captures every config and request seen.</summary>
public sealed class FakeProvider : IModelProvider
{
    private readonly Queue<Result<ModelResponse>> _responses = new();

    public List<ModelConfig> ConfigsSeen { get; } = [];
    public List<ModelRequest> RequestsSeen { get; } = [];

    public FakeProvider(params Result<ModelResponse>[] responses)
    {
        foreach (var response in responses) _responses.Enqueue(response);
    }

    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
        ConfigsSeen.Add(config);
        RequestsSeen.Add(request);
        return Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : Result<ModelResponse>.Success(new ModelResponse("done", [])));
    }
}

/// <summary>Blocks until cancelled, then reports a failed call — exercises the timeout path.</summary>
public sealed class BlockingProvider : IModelProvider
{
    public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
        }

        return Result<ModelResponse>.Failure(new Error("Cancelled", "provider call was cancelled."));
    }
}

/// <summary>Throws synchronously — exercises the provider-exception path.</summary>
public sealed class ThrowingProvider : IModelProvider
{
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => throw new InvalidOperationException("provider exploded");
}

/// <summary>Always answers with one tool call — drives the loop to MaxToolIterations.</summary>
public sealed class LoopingProvider : IModelProvider
{
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
        CancellationToken ct = default)
        => Task.FromResult(Result<ModelResponse>.Success(new ModelResponse(null,
            [new ToolCallRequest("call_1", "loop", "{}")])));
}

/// <summary>Hands out one shared provider instance and records every Create call.</summary>
public sealed class FakeModelProviderFactory(IModelProvider provider) : IModelProviderFactory
{
    public int CreateCount { get; private set; }
    public ModelConfig? LastConfig { get; private set; }

    public IModelProvider Create(ModelConfig config)
    {
        CreateCount++;
        LastConfig = config;
        return provider;
    }
}

/// <summary>In-memory IAgentStore recording every write so tests can assert side effects.</summary>
public sealed class FakeAgentStore : IAgentStore
{
    private readonly Dictionary<Guid, AgentRecord> _records = new();

    public List<AgentRecord> Saved { get; } = [];
    public List<AgentRecord> Updated { get; } = [];
    public List<(AgentId AgentId, Message Message)> AppendedMessages { get; } = [];

    public int TotalWrites => Saved.Count + Updated.Count;

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    {
        Saved.Add(record);
        _records[record.Id.Value] = record;
        return Task.FromResult(Result<string>.Success(record.Id.ToString()));
    }

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
        Updated.Add(record);
        _records[record.Id.Value] = record;
        return Task.FromResult(Result<string>.Success(record.Id.ToString()));
    }

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(_records.TryGetValue(id.Value, out var record)
            ? Result<AgentRecord>.Success(record)
            : Result<AgentRecord>.Failure(new Error("NotFound", $"Agent {id} was not found.")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    {
        AppendedMessages.Add((id, message));
        return Task.FromResult(Result<string>.Success(id.ToString()));
    }

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result<IReadOnlyList<Message>>.Success(
            AppendedMessages.Where(a => a.AgentId == id).Select(a => a.Message).ToList()));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result<IReadOnlyList<AgentRecord>>.Success(
            _records.Values.Where(r => r.ParentId == parentId).ToList()));
}
