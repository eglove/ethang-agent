using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>In-memory IAgentStore recording every write so tests can assert side effects and call order.</summary>
internal sealed class FakeAgentStore(List<string>? callLog = null) : IAgentStore
{
  private readonly Dictionary<Guid, AgentRecord> _records = [];
  private readonly Dictionary<Guid, List<Message>> _messages = [];

  public List<AgentRecord> Saved { get; } = [];
  public List<AgentRecord> Updated { get; } = [];
  public int TotalWrites => Saved.Count + Updated.Count;

  /// <summary>When set, the next SaveAsync fails with this error instead of persisting.</summary>
  public DomainError? SaveFailure { get; set; }

  public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
  {
    if (SaveFailure is { } failure)
    {
      return Task.FromResult(Result.Failure<string>(failure));
    }

    Saved.Add(record);
    _records[record.Id.Value] = record;
    callLog?.Add($"save:{record.Id}");
    return Task.FromResult(Result.Success<string>(record.Id.ToString()));
  }

  public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
  {
    Updated.Add(record);
    _records[record.Id.Value] = record;
    callLog?.Add($"update:{record.Id}");
    return Task.FromResult(Result.Success<string>(record.Id.ToString()));
  }

  public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(_records.TryGetValue(id.Value, out AgentRecord? record)
          ? Result.Success<AgentRecord>(record)
          : Result.Failure<AgentRecord>(new DomainError("NotFound", $"Agent {id} was not found.")));

  public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
  {
    callLog?.Add($"append:{id}");
    if (!_messages.TryGetValue(id.Value, out List<Message>? transcript))
    {
      _messages[id.Value] = transcript = [];
    }

    transcript.Add(message);
    return Task.FromResult(Result.Success<string>(id.ToString()));
  }

  public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<Message>>(
          _messages.TryGetValue(id.Value, out List<Message>? transcript)
              ? transcript.ToList()
              : []));

  public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>(
          [.. _records.Values.Where(r => r.ParentId == parentId)]));

  public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>(
          [.. _records.Values.OrderBy(r => r.CreatedAt)]));
}

/// <summary>Fake IAgentRuntime capturing started records; returns a scripted outcome when set.</summary>
internal sealed class FakeAgentRuntime(List<string>? callLog = null) : IAgentRuntime
{
  public List<AgentRecord> Started { get; } = [];

  /// <summary>When set, Start returns this outcome instead of success.</summary>
  public Result<AgentId>? StartOutcome { get; set; }

  public Task<Result<AgentId>> Start(AgentRecord record, CancellationToken ct = default)
  {
    Started.Add(record);
    callLog?.Add($"start:{record.Id}");
    return Task.FromResult(StartOutcome ?? Result.Success<AgentId>(record.Id));
  }

  /// <summary>Interrupts observed by tests; never throws.</summary>
  public void Interrupt(AgentId? childId = null) => Interrupted.Add(childId);

  public List<AgentId?> Interrupted { get; } = [];
}
