using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Always-successful tool returning a fixed content string.</summary>
internal sealed class FakeTool(string name, string resultContent) : ITool
{
  public ToolDefinition Definition { get; } = new ToolDefinition(name, "desc", []);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
      => Task.FromResult(new ToolResult(resultContent, false));
}

/// <summary>In-proc scripted provider: replays queued results, captures every config and request seen.</summary>
internal sealed class FakeProvider : IModelProvider
{
  private readonly Queue<Result<ModelResponse>> _responses = new();

  public System.Collections.ObjectModel.Collection<ModelConfig> ConfigsSeen { get; } = [];
  public System.Collections.ObjectModel.Collection<ModelRequest> RequestsSeen { get; } = [];

  public FakeProvider(params Result<ModelResponse>[] responses)
  {
    foreach (Result<ModelResponse> response in responses)
    {
      _responses.Enqueue(response);
    }
  }

  public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
      CancellationToken ct = default)
  {
    ConfigsSeen.Add(config);
    RequestsSeen.Add(request);
    return Task.FromResult(_responses.Count > 0
        ? _responses.Dequeue()
        : Result.Success(new ModelResponse("done", [])));
  }
}

/// <summary>Blocks until cancelled, then reports a failed call — exercises the timeout path.</summary>
internal sealed class BlockingProvider : IModelProvider
{
  public async Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
      CancellationToken ct = default)
  {
    try
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
    }
#pragma warning disable S108 // Deliberate swallow: the wait ends through ct; the Result below reports it.
    catch (OperationCanceledException)
    {
    }
#pragma warning restore S108

    return Result.Failure<ModelResponse>(new DomainError("Cancelled", "provider call was cancelled."));
  }
}

/// <summary>Throws synchronously — exercises the provider-exception path.</summary>
internal sealed class ThrowingProvider : IModelProvider
{
  public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
      CancellationToken ct = default)
      => throw new InvalidOperationException("provider exploded");
}

/// <summary>Always answers with one tool call — drives the loop until a budget stops it.</summary>
internal sealed class LoopingProvider : IModelProvider
{
  public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request,
      CancellationToken ct = default)
      => Task.FromResult(Result.Success(new ModelResponse(null,
          [new ToolCallRequest("call_1", "loop", "{}")])));
}

/// <summary>Hands out one shared provider instance and records every Create call.</summary>
internal sealed class FakeModelProviderFactory(IModelProvider provider) : IModelProviderFactory
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
internal sealed class FakeAgentStore : IAgentStore
{
  private readonly Dictionary<Guid, AgentRecord> _records = [];

  public System.Collections.ObjectModel.Collection<AgentRecord> Saved { get; } = [];
  public System.Collections.ObjectModel.Collection<AgentRecord> Updated { get; } = [];
  public System.Collections.ObjectModel.Collection<(AgentId AgentId, Message Message)> AppendedMessages { get; } = [];

  /// <summary>When set, UpdateAsync resolves to this failure and does not touch the record map.</summary>
  public Result<string>? UpdateFailure { get; set; }

  public int TotalWrites => Saved.Count + Updated.Count;

  public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
  {
    Saved.Add(record);
    _records[record.Id.Value] = record;
    return Task.FromResult(Result.Success(record.Id.ToString()));
  }

  public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
  {
    Updated.Add(record);
    if (UpdateFailure is { } failure)
    {
      return Task.FromResult(failure);
    }

    _records[record.Id.Value] = record;
    return Task.FromResult(Result.Success(record.Id.ToString()));
  }

  public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(_records.TryGetValue(id.Value, out AgentRecord? record)
          ? Result.Success(record)
          : Result.Failure<AgentRecord>(new DomainError("NotFound", $"Agent {id} was not found.")));

  public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
  {
    AppendedMessages.Add((id, message));
    return Task.FromResult(Result.Success(id.ToString()));
  }

  public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
        => Task.FromResult(Result.Success(id.ToString()));

  public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
    => Task.FromResult(Result.Success<IReadOnlyList<Message>>(
        [.. AppendedMessages.Where(a => a.AgentId == id).Select(a => a.Message)]));

  public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>(
          [.. _records.Values.Where(r => r.ParentId == parentId)]));

  public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>(
          [.. _records.Values.OrderBy(r => r.CreatedAt)]));
}
