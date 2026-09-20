using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Message parts persist inside agent_messages.meta_json - no schema
///     migration. Old rows without a parts key read back with Parts null, and
///     part-bearing messages round-trip through both append and transcript
///     replacement.</summary>
public sealed class SqliteAgentStorePartsTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-agents-{Guid.NewGuid():N}.db");
  private readonly SqliteAgentStore _store;

  public SqliteAgentStorePartsTests()
      => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private async Task<AgentId> SavedAgentAsync()
  {
    AgentId id = AgentId.NewId();
    _ = await _store.SaveAsync(AgentRecord.Spawned(id, null, 1, "provider/model", null, "task",
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    return id;
  }

  private static DateTimeOffset At(int seconds) => new(2026, 8, 21, 10, 0, seconds, TimeSpan.Zero);

  [Fact]
  public async Task AppendAndGetTranscript_RoundTripsImagePart()
  {
    AgentId id = await SavedAgentAsync();
    Message user = new(Role.User, "screenshot the page", At(1));
    Message tool = new(Role.Tool, "screenshot captured", At(2), ToolCallId: "call_1",
        Parts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")]);
    _ = await _store.AppendMessageAsync(id, user, ct: TestContext.Current.CancellationToken);
    _ = await _store.AppendMessageAsync(id, tool, ct: TestContext.Current.CancellationToken);

    Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(transcript.IsSuccess);
    Assert.Null(transcript.Value[0].Parts); // parts-less message unchanged
    Message loaded = transcript.Value[1];
    Assert.Equal(Role.Tool, loaded.Role);
    Assert.Equal("screenshot captured", loaded.Content);
    Assert.Equal("call_1", loaded.ToolCallId);
    Assert.NotNull(loaded.Parts);
    _ = Assert.Single(loaded.Parts);
    MessagePart.ImagePart image = Assert.IsType<MessagePart.ImagePart>(loaded.Parts[0]);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal("aGVsbG8=", image.Base64Data);
  }

  [Fact]
  public async Task LegacyRowWithoutPartsKey_DeserializesPartsNull()
  {
    AgentId id = await SavedAgentAsync();
    _ = await _store.AppendMessageAsync(id,
        new Message(Role.User, "legacy prompt", At(1)), ct: TestContext.Current.CancellationToken);

    RewriteMetaToLegacyShape(id);

    Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(id, ct: TestContext.Current.CancellationToken);

    Assert.True(transcript.IsSuccess);
    _ = Assert.Single(transcript.Value);
    Assert.Equal("legacy prompt", transcript.Value[0].Content);
    Assert.Null(transcript.Value[0].Parts);
  }

  /// <summary>Overwrites the only message row's meta_json with the exact legacy
  ///     shape: timestamp/toolCalls/toolCallId and NO parts key at all.</summary>
  private void RewriteMetaToLegacyShape(AgentId id)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "UPDATE agent_messages SET meta_json=@m WHERE agent_id=@id;";
    _ = command.Parameters.AddWithValue("@m", JsonSerializer.Serialize(new
    {
      Timestamp = At(1),
      ToolCalls = (object?)null,
      ToolCallId = (string?)null,
    }));
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    _ = command.ExecuteNonQuery();
  }

  [Fact]
  public async Task ReplaceTranscript_RoundTripsParts()
  {
    AgentId id = await SavedAgentAsync();
    List<Message> replacement =
    [
      new(Role.System, "summary", At(0)),
      new(Role.User, "kept prompt", At(1)),
      new(Role.Tool, "kept screenshot", At(2), ToolCallId: "call_1",
          Parts: [new MessagePart.ImagePart("image/jpeg", "aGVsbG8=")]),
    ];

    Result<string> replaced = await _store.ReplaceTranscriptAsync(id, replacement,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(replaced.IsSuccess);
    Result<IReadOnlyList<Message>> transcript = await _store.GetTranscriptAsync(id, ct: TestContext.Current.CancellationToken);
    Assert.Equal(3, transcript.Value!.Count);
    Message loaded = transcript.Value[2];
    Assert.NotNull(loaded.Parts);
    MessagePart.ImagePart image = Assert.IsType<MessagePart.ImagePart>(loaded.Parts[0]);
    Assert.Equal("image/jpeg", image.MediaType);
    Assert.Equal("aGVsbG8=", image.Base64Data);
    Assert.Null(transcript.Value[1].Parts);
  }
}
