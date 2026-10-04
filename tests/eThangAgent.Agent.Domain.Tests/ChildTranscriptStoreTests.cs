using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Records the cancellation tokens every append receives — the store contract
///     under test is that persistence NEVER observes a run's cancellation token.</summary>
internal sealed class CtRecordingStore : IAgentStore
{
  public System.Collections.ObjectModel.Collection<CancellationToken> AppendTokens { get; } = [];
  public System.Collections.ObjectModel.Collection<(AgentId AgentId, Message Message)> AppendedMessages { get; } = [];
  public System.Collections.ObjectModel.Collection<(AgentId AgentId, IReadOnlyList<Message> Messages)> ReplacedTranscripts { get; } = [];

  public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
      => Task.FromResult(Result.Success(record.Id.ToString()));

  public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
      => Task.FromResult(Result.Success(record.Id.ToString()));

  public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Failure<AgentRecord>(new DomainError("NotFound", "no records")));

  public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
  {
    AppendTokens.Add(ct);
    AppendedMessages.Add((id, message));
    return Task.FromResult(Result.Success(id.ToString()));
  }

  public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages, CancellationToken ct = default)
  {
    ReplacedTranscripts.Add((id, messages));
    return Task.FromResult(Result.Success(id.ToString()));
  }

  public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

  public Task<Result<IReadOnlyList<AgentRecord>>> ListChildrenAsync(AgentId parentId, CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));

  public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
      => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
}

public class ChildTranscriptStoreTests
{
  [Fact]
  public async Task AppendAsync_PersistsEachMessageImmediately()
  {
    CtRecordingStore store = new();
    AgentId id = new(Guid.NewGuid());
#pragma warning disable CA2007
    await using ChildTranscriptStore sink = new(store, id);
#pragma warning restore CA2007

    Message first = new(Role.User, "hello", DateTimeOffset.UtcNow);
    await sink.AppendAsync(first).ConfigureAwait(true);

    _ = Assert.Single(store.AppendedMessages);
    Assert.Equal("hello", store.AppendedMessages[0].Message.Content);
  }

  [Fact]
  public async Task AppendAsync_NeverObservesARunCancellationToken()
  {
    CtRecordingStore store = new();
#pragma warning disable CA2007
    await using ChildTranscriptStore sink = new(store, new AgentId(Guid.NewGuid()));
#pragma warning restore CA2007
    await sink.AppendAsync(new Message(Role.User, "m", DateTimeOffset.UtcNow)).ConfigureAwait(true);

    _ = Assert.Single(store.AppendTokens);
    Assert.False(store.AppendTokens[0].IsCancellationRequested);
    Assert.Equal(CancellationToken.None, store.AppendTokens[0]);
  }

  [Fact]
  public async Task FlushedCount_TracksPersistedMessages()
  {
    CtRecordingStore store = new();
#pragma warning disable CA2007
    await using ChildTranscriptStore sink = new(store, new AgentId(Guid.NewGuid()));
#pragma warning restore CA2007
    Assert.Equal(0, await sink.FlushedCount().ConfigureAwait(true));
    await sink.AppendAsync(new Message(Role.User, "one", DateTimeOffset.UtcNow)).ConfigureAwait(true);
    await sink.AppendAsync(new Message(Role.Assistant, "two", DateTimeOffset.UtcNow)).ConfigureAwait(true);
    Assert.Equal(2, await sink.FlushedCount().ConfigureAwait(true));
  }

  [Fact]
  public async Task FlushAsync_WhenClean_IsNoOp()
  {
    CtRecordingStore store = new();
#pragma warning disable CA2007
    await using ChildTranscriptStore sink = new(store, new AgentId(Guid.NewGuid()));
#pragma warning restore CA2007
    await sink.AppendAsync(new Message(Role.User, "one", DateTimeOffset.UtcNow)).ConfigureAwait(true);
    int before = store.AppendedMessages.Count;
    await sink.FlushAsync().ConfigureAwait(true);
    Assert.Equal(before, store.AppendedMessages.Count);
  }

  [Fact]
  public async Task ReplaceAsync_DelegatesToStoreAndResetsCount()
  {
    CtRecordingStore store = new();
#pragma warning disable CA2007
    await using ChildTranscriptStore sink = new(store, new AgentId(Guid.NewGuid()));
#pragma warning restore CA2007
    await sink.AppendAsync(new Message(Role.User, "old", DateTimeOffset.UtcNow)).ConfigureAwait(true);
    Message[] replacement = [new Message(Role.System, "summary", DateTimeOffset.UtcNow, IsSummary: true)];

    await sink.ReplaceAsync(replacement).ConfigureAwait(true);

    _ = Assert.Single(store.ReplacedTranscripts);
    Assert.Equal(1, await sink.FlushedCount().ConfigureAwait(true));
  }
}
