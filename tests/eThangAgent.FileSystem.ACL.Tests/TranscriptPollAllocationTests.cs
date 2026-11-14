using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>ASSERTION 3 OF THE PLAN'S VERIFICATION: a poll allocates nothing proportional to the
///     image.
///     <para>THE READ IS THE MECHANISM THE PLAN EXISTS TO STOP.
///     <see cref="SqliteAgentStore.GetTranscriptAsync"/> selects role, content and meta_json for
///     EVERY message, and the desktop cell performed it twice a second. While a screenshot's
///     bytes lived in meta_json, each read materialised them as a UTF-16 string - a 7.4 MB session
///     re-allocating its pictures twice a second, the measured ~15 MB/s of pure garbage behind the
///     shell's 2.1 GB working set. Once the row holds a POINTER, the same read allocates with the
///     ROW (a media type and a path) instead of with the PICTURE.</para>
///     <para>THE CELL NO LONGER CALLS THIS ON A TICK (issue #385): a follower reads
///     <see cref="SqliteAgentStore.GetTranscriptTailAsync"/> past its cursor instead. This test
///     still measures the whole-transcript read, which is what a first paint and a restore cost.
///     </para>
///     <para>WHAT THIS MEASURES, AND WHAT IT DOES NOT. It measures ALLOCATED BYTES across the
///     read - <see cref="GC.GetTotalAllocatedBytes(bool)"/> around the call - because that is the
///     garbage the poll produces. It does NOT assert a managed heap that returns to zero: a
///     heap-to-zero check was tried in an earlier session and does not settle, and the result of
///     a read is legitimately retained by the caller. It also does not measure a :memory:
///     database: the pool and the page cache are part of what is being measured, so every
///     transcript here lives in a real file.</para>
///     <para>THE ONE CASE THAT STILL ALLOCATES IS STATED, NOT HIDDEN: a LEGACY INLINE row on a
///     database that has not been stripped carries its base64 in meta_json, so reading it costs
///     the image's size. That is what V43 removes, and the last test below pins it so the
///     contrast is a fact in the suite rather than a claim in a report.</para></summary>
public sealed class TranscriptPollAllocationTests : IDisposable
{
  /// <summary>The size the plan measured on real sessions: the heaviest recent one carried
  ///     7.4 MB of embedded images, and the largest single row was 21.6 MB. 9 MB is squarely in
  ///     that population.</summary>
  private const int ImageBytes = 9 * 1024 * 1024;

  /// <summary>How many history messages surround the image, so the measurement includes a
  ///     transcript of ordinary size rather than a single row.</summary>
  private const int SurroundingMessages = 40;

  private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly string _base = TestTempRoot.CreateDirectory("ethang-poll").FullName;
  private readonly string _session = "poll-" + Guid.NewGuid().ToString("N");

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

  private SessionImageStore Images => new(_session, _base);

  private static byte[] Png(int size)
  {
    byte[] bytes = new byte[size];
    PngSignature.CopyTo(bytes, 0);
    return bytes;
  }

  private static async Task<AgentId> SavedAgentAsync(SqliteAgentStore store)
  {
    AgentId id = AgentId.NewId();
    _ = await store.SaveAsync(AgentRecord.Started(id, "provider/model", null, "task",
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    return id;
  }

  /// <summary>Persists <see cref="SurroundingMessages"/> ordinary messages, then ONE tool message
  ///     carrying the image, through the production store. The image's form - pointer or inline -
  ///     is whatever the store writes for the sink it was given, which is the point: the same
  ///     write path decides, and the read path is measured.</summary>
  private static async Task<AgentId> TranscriptWithOneImageAsync(SqliteAgentStore store)
  {
    AgentId id = await SavedAgentAsync(store).ConfigureAwait(true);
    for (int i = 0; i < SurroundingMessages; i++)
    {
      Message chatter = new(Role.Assistant, $"working on step {i}",
          new DateTimeOffset(2026, 8, 21, 10, 0, i, TimeSpan.Zero));
      Result<string> appended = await store.AppendMessageAsync(id, chatter,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(appended.IsSuccess, appended.Error?.Message);
    }

    Message shot = new(Role.Tool, "computer: observe",
        new DateTimeOffset(2026, 8, 21, 10, 1, 0, TimeSpan.Zero), ToolCallId: "call_1",
        Parts: [new MessagePart.ImagePart("image/png", Convert.ToBase64String(Png(ImageBytes)))]);
    Result<string> persisted = await store.AppendMessageAsync(id, shot,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(persisted.IsSuccess, persisted.Error?.Message);
    return id;
  }

  /// <summary>The bytes ONE read of <paramref name="id"/>'s transcript allocates, measured over
  ///     several reads with the median taken: a single reading is at the mercy of a pooled
  ///     connection opening, a JIT tier, or a background allocation on the same thread, and the
  ///     claim is about the steady state a 500 ms poll lives in rather than about one call.</summary>
  private static async Task<long> AllocatedByOneReadAsync(SqliteAgentStore store, AgentId id,
      int expectedMessages)
  {
    // Warm: the first read pays for the connection pool, the JSON serializer's metadata and the
    // reader's buffers. The poll's SECOND read is the one the plan is about.
    Result<IReadOnlyList<Message>> warm = await store.GetTranscriptAsync(id,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(warm.IsSuccess, warm.Error?.Message);

    List<long> readings = [];
    for (int i = 0; i < 5; i++)
    {
      long before = GC.GetTotalAllocatedBytes(precise: true);
      Result<IReadOnlyList<Message>> read = await store.GetTranscriptAsync(id,
          TestContext.Current.CancellationToken).ConfigureAwait(true);
      long after = GC.GetTotalAllocatedBytes(precise: true);
      Assert.True(read.IsSuccess, read.Error?.Message);
      Assert.Equal(expectedMessages, read.Value.Count);
      readings.Add(after - before);
    }

    readings.Sort();
    return readings[readings.Count / 2];
  }

  // --- ASSERTION 3: a poll allocates nothing proportional to the image ---

  /// <summary>A transcript whose history holds a 9 MB image's POINTER, and the same transcript
  ///     with that pointer aimed at nothing, allocate comparably: the difference is a path string
  ///     against 9 MB, and the read's cost is set by the ROWS and not by the picture.</summary>
  [Fact]
  public async Task ATranscriptHoldingAStoredImage_AllocatesTheSameAsOneNamingNoFile()
  {
    SqliteAgentStore store = new(new AppDatabase(_dbPath), imagesFor: _ => Images);
    AgentId id = await TranscriptWithOneImageAsync(store).ConfigureAwait(true);

    long stored = await AllocatedByOneReadAsync(store, id, SurroundingMessages + 1)
        .ConfigureAwait(true);

    // The same transcript, its one reference pointed at a path that does not exist: the file's
    // presence must make no difference to the read, because the read never opens it.
    Assert.Equal(1, PointTheReferenceAt(id, Path.Combine(_base, "gone", "nothing.png")));
    long dangling = await AllocatedByOneReadAsync(store, id, SurroundingMessages + 1)
        .ConfigureAwait(true);

    // THE CLAIM, IN TWO PARTS. First: the file's presence is IRRELEVANT to the read, because the
    // read never opens it - a pointer at a real file and a pointer at nothing cost the same.
    Assert.True(Math.Abs(stored - dangling) < ImageBytes / 100,
        $"a stored image and a dangling reference allocate comparably: {stored} vs {dangling} bytes "
        + $"for a {ImageBytes}-byte image");

    // Second, and this is the one a regression would trip: the read does not SCALE with the
    // picture. The comparison is against the SAME transcript holding the SAME image INLINE - the
    // form the row took before this change - measured on this machine in this run. A pointer read
    // that had started carrying bytes again would land at the inline figure, not near zero, so the
    // claim is a ratio between two measurements rather than a constant that a faster machine or a
    // different runtime could invalidate.
    long inline = await AllocatedByOneReadAsync(new SqliteAgentStore(new AppDatabase(_dbPath)),
        LegacyTranscriptWithTheSameImage(), SurroundingMessages + 1).ConfigureAwait(true);
    Assert.True(stored * 10 < inline,
        $"a poll of a {ImageBytes}-byte image's POINTER allocates {stored} bytes against {inline} for "
        + "the same image inline: the pointer read must be an order of magnitude cheaper");
    Assert.True(stored < ImageBytes / 10,
        $"one poll of a transcript holding a {ImageBytes}-byte image allocates {stored} bytes, which "
        + $"is not 'nothing proportional to the image'");
  }

  /// <summary>The awkward case the plan's brief names: a session with NO images at all. Its
  ///     allocation is unchanged from today - there is nothing to strip and nothing to point at.
  ///     </summary>
  [Fact]
  public async Task ATranscriptWithNoImages_AllocatesOnlyItsText()
  {
    SqliteAgentStore store = new(new AppDatabase(_dbPath), imagesFor: _ => Images);
    AgentId id = await SavedAgentAsync(store).ConfigureAwait(true);
    for (int i = 0; i < SurroundingMessages; i++)
    {
      Message chatter = new(Role.Assistant, $"working on step {i}",
          new DateTimeOffset(2026, 8, 21, 10, 0, i, TimeSpan.Zero));
      _ = await store.AppendMessageAsync(id, chatter, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
    }

    long textOnly = await AllocatedByOneReadAsync(store, id, SurroundingMessages)
        .ConfigureAwait(true);

    Assert.True(textOnly < ImageBytes / 10,
        $"a transcript with no images allocates {textOnly} bytes; an image-proportional read would "
        + $"be at least the {ImageBytes}-byte picture");
  }

  /// <summary>THE CASE THAT STILL ALLOCATES, STATED RATHER THAN HIDDEN. A LEGACY INLINE row -
  ///     the form an older build wrote and V43 removes - carries its base64 in meta_json, so a
  ///     read materialises the picture whether or not anybody opens it. This is the cost the
  ///     strip exists to remove, and pinning it here is what makes the contrast between the two
  ///     forms a fact the suite reports instead of a claim in a report.</summary>
  [Fact]
  public async Task ALegacyInlineRow_StillAllocatesTheImage_WhichIsWhatTheStripRemoves()
  {
    // A store with NO sink writing it: the legacy wiring, whose rows keep their payload inline.
    SqliteAgentStore legacy = new(new AppDatabase(_dbPath));
    AgentId id = await TranscriptWithOneImageAsync(legacy).ConfigureAwait(true);

    long inline = await AllocatedByOneReadAsync(legacy, id, SurroundingMessages + 1)
        .ConfigureAwait(true);

    // The row really does carry the payload - asserted, not assumed, so this test cannot pass by
    // measuring a pointer and calling it legacy.
    Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM agent_messages WHERE agent_id = $id "
        + "AND meta_json LIKE '%Base64Data%';", id));
    Assert.True(inline >= ImageBytes / 2,
        $"an inline row allocates with the picture: {inline} bytes for a {ImageBytes}-byte image; "
        + "if this ever stops being true the strip is no longer the reason the read got cheaper");
  }

  /// <summary>A SECOND transcript over the same file, holding the same 9 MB image as an INLINE
  ///     payload - the shape a row took before this change, and the figure the pointer read is
  ///     compared against. Written through a store with NO sink, which is the legacy wiring whose
  ///     rows keep their bytes.</summary>
  private AgentId LegacyTranscriptWithTheSameImage()
  {
    SqliteAgentStore legacy = new(new AppDatabase(_dbPath));
    return TranscriptWithOneImageAsync(legacy).GetAwaiter().GetResult();
  }

  /// <summary>Aims the transcript's one reference at <paramref name="path"/> and reports how many
  ///     rows it rewrote. Written through raw SQL because the point is a row a PREVIOUS build could
  ///     have left: the store at this build never writes a reference to a file it did not create.
  ///     </summary>
  private int PointTheReferenceAt(AgentId id, string path)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = new(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using Microsoft.Data.Sqlite.SqliteCommand read = connection.CreateCommand();
    read.CommandText = "SELECT rowid, meta_json FROM agent_messages WHERE agent_id = $id "
        + "AND json_extract(meta_json, '$.Parts[0].Path') IS NOT NULL;";
    _ = read.Parameters.AddWithValue("$id", id.ToString());
    using Microsoft.Data.Sqlite.SqliteDataReader reader = read.ExecuteReader();
    Assert.True(reader.Read(), "the transcript holds no reference to re-point");
    long rowid = reader.GetInt64(0);
    string metaJson = reader.GetString(1);
    reader.Close();

    using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(metaJson);
    using System.Text.Json.JsonDocument repointed = System.Text.Json.JsonDocument.Parse(
        metaJson.Replace(
            document.RootElement.GetProperty("Parts")[0].GetProperty("Path").GetString()!,
            path, StringComparison.Ordinal));

    using Microsoft.Data.Sqlite.SqliteCommand write = connection.CreateCommand();
    write.CommandText = "UPDATE agent_messages SET meta_json = $meta WHERE rowid = $rowid;";
    _ = write.Parameters.AddWithValue("$meta", repointed.RootElement.GetRawText());
    _ = write.Parameters.AddWithValue("$rowid", rowid);
    return write.ExecuteNonQuery();
  }

  private long Scalar(string sql, AgentId id)
  {
    using Microsoft.Data.Sqlite.SqliteConnection connection = new(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the statement is a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    _ = command.Parameters.AddWithValue("$id", id.ToString());
    return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
  }
};
