using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>In-memory IAgentStore recording every write so tests can assert side effects and call order.</summary>
public sealed class FakeAgentStore(List<string>? callLog = null) : IAgentStore
{
    private readonly Dictionary<Guid, AgentRecord> _records = [];

    public List<AgentRecord> Saved { get; } = [];
    public List<AgentRecord> Updated { get; } = [];
    public int TotalWrites => Saved.Count + Updated.Count;

    /// <summary>When set, the next SaveAsync fails with this error instead of persisting.</summary>
    public Error? SaveFailure { get; set; }

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
    {
        if (SaveFailure is { } failure)
            return Task.FromResult(Result<string>.Failure(failure));

        Saved.Add(record);
        _records[record.Id.Value] = record;
        callLog?.Add($"save:{record.Id}");
        return Task.FromResult(Result<string>.Success(record.Id.ToString()));
    }

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
    {
        Updated.Add(record);
        _records[record.Id.Value] = record;
        callLog?.Add($"update:{record.Id}");
        return Task.FromResult(Result<string>.Success(record.Id.ToString()));
    }

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(_records.TryGetValue(id.Value, out var record)
            ? Result<AgentRecord>.Success(record)
            : Result<AgentRecord>.Failure(new Error("NotFound", $"Agent {id} was not found.")));

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
    {
        callLog?.Add($"append:{id}");
        return Task.FromResult(Result<string>.Success(id.ToString()));
    }

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result<IReadOnlyList<Message>>.Success([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
        => Task.FromResult(Result<IReadOnlyList<AgentRecord>>.Success(
            _records.Values.Where(r => r.ParentId == parentId).ToList()));
}

/// <summary>Fake IAgentRuntime capturing started records; returns a scripted outcome when set.</summary>
public sealed class FakeAgentRuntime(List<string>? callLog = null) : IAgentRuntime
{
    public List<AgentRecord> Started { get; } = [];

    /// <summary>When set, Start returns this outcome instead of success.</summary>
    public Result<AgentId>? StartOutcome { get; set; }

    public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
    {
        Started.Add(record);
        callLog?.Add($"start:{record.Id}");
        return Task.FromResult(StartOutcome ?? Result<AgentId>.Success(record.Id));
    }
}
