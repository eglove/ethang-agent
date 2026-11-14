using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The sweep's storage half: which stored-image paths a PERSISTED transcript names.
///     A sweep told the wrong set deletes the wrong files, so the two facts this suite pins are
///     the ones that make the answer safe to act on.
///     <para>IT READS THE ROWS, NOT THE CONVERSATION. A path that reached a row is what survives
///     a restart, so a path named only by an in-memory message is not a reference - it is exactly
///     the orphan the sweep is for.</para>
///     <para>A ROW THIS CODE CANNOT PARSE IS A FAILURE, NEVER AN EMPTY SET. An empty set is the
///     instruction 'delete everything', which is the only answer here that cannot be undone.
///     </para></summary>
public sealed class StoredImageReferenceSetTests : IDisposable
{
  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly string _imageRoot = TestTempRoot.CreateDirectory("ethang-refset").FullName;
  private readonly SqliteAgentStore _store;

  public StoredImageReferenceSetTests() => _store = new SqliteAgentStore(new AppDatabase(_dbPath));

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

  /// <summary>Writes a PNG-signature file under the run root and returns its absolute path.</summary>
  private string StoredFile(string extension = "png")
  {
    string path = Path.Combine(_imageRoot, $"image-{Guid.NewGuid():N}.{extension}");
    byte[] bytes = new byte[64];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
    File.WriteAllBytes(path, bytes);
    return path;
  }

  private async Task<AgentId> SavedAgentAsync()
  {
    AgentId id = AgentId.NewId();
    _ = await _store.SaveAsync(AgentRecord.Started(id, "provider/model", null, "task",
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    return id;
  }

  private Task<Result<string>> AppendAsync(AgentId id, string content, params string[] paths)
  {
    MessagePart[] parts = [.. paths.Select(path =>
        (MessagePart)new MessagePart.ImageReferencePart(path, "image/png"))];
    Message message = new(Role.Tool, content, DateTimeOffset.UtcNow, ToolCallId: "call_1",
        Parts: parts);
    return _store.AppendMessageAsync(id, message, TestContext.Current.CancellationToken);
  }

  /// <summary>The reference set is exactly what the persisted rows name, across every message - so
  ///     a path any message names survives the sweep.</summary>
  [Fact]
  public async Task TheReferenceSet_IsEveryPathAnyPersistedMessageNames()
  {
    AgentId id = await SavedAgentAsync();
    string first = StoredFile();
    string second = StoredFile();
    _ = await AppendAsync(id, "one", first).ConfigureAwait(true);
    _ = await AppendAsync(id, "two", second).ConfigureAwait(true);
    // A message with no parts at all must not confuse the read.
    _ = await _store.AppendMessageAsync(id, new Message(Role.User, "plain", DateTimeOffset.UtcNow),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Result<IReadOnlySet<string>> paths = await _store
        .GetStoredImagePathsAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(paths.IsSuccess, paths.Error?.Message);
    Assert.Equal(2, paths.Value.Count);
    Assert.Contains(first, paths.Value);
    Assert.Contains(second, paths.Value);
  }

  /// <summary>Case is not identity: two spellings of one Windows path are one file, and a set
  ///     that disagreed would let the sweep delete a picture its own transcript still names.</summary>
  [Fact]
  public async Task TheReferenceSet_ComparesPathsCaseInsensitively()
  {
    AgentId id = await SavedAgentAsync();
    string path = StoredFile();
    _ = await AppendAsync(id, "one", path).ConfigureAwait(true);

    Result<IReadOnlySet<string>> paths = await _store
        .GetStoredImagePathsAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(paths.IsSuccess, paths.Error?.Message);
    Assert.Contains(path.ToUpperInvariant(), paths.Value);
  }

  /// <summary>A session id with no rows names nothing - and 'nothing' is a FACT here, not a
  ///     failed read, which is why the sweep reports a missing agent as its own typed failure
  ///     rather than handing an empty set to the folder.</summary>
  [Fact]
  public async Task AnUnknownSession_FailsWithNotFound_RatherThanReportingAnEmptySet()
  {
    Result<IReadOnlySet<string>> paths = await _store
        .GetStoredImagePathsAsync(AgentId.NewId(), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.False(paths.IsSuccess);
    Assert.Equal("NotFound", paths.Error?.Code);
  }

  /// <summary>A row whose meta_json will not parse fails the read: a partial reference set is
  ///     a set that deletes the picture that row's own message is about.</summary>
  [Fact]
  public async Task AnUnparseableRow_FailsTheRead_NeverReportsAPartialSet()
  {
    AgentId id = await SavedAgentAsync();
    _ = await AppendAsync(id, "one", StoredFile()).ConfigureAwait(true);
    await CorruptAsync(id).ConfigureAwait(true);

    Result<IReadOnlySet<string>> paths = await _store
        .GetStoredImagePathsAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(paths.IsSuccess);
    Assert.Equal("TranscriptUnreadable", paths.Error?.Code);
  }

  /// <summary>Rewrites every row's meta_json to something this code cannot parse, the way a
  ///     corrupted or hand-edited database would read.</summary>
  private async Task CorruptAsync(AgentId id)
  {
    // Named decision (CA2007): a test's own seeding connection has no caller to starve.
#pragma warning disable CA2007
    await using Microsoft.Data.Sqlite.SqliteConnection connection =
        new($"Data Source={_dbPath}");
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    command.CommandText = "UPDATE agent_messages SET meta_json = '{ not json' WHERE agent_id=@id;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    int corrupted = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.Equal(1, corrupted);
  }
}
