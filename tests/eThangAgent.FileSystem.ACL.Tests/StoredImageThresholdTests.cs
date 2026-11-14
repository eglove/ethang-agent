using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>The size that lands in a row is decided at PERSISTENCE, and only there: an image
///     payload below <see cref="ImageLimits.InlineThresholdBytes"/> keeps the inline form it always
///     had, and one at or above it becomes a FILE PLUS A POINTER. The message the store is handed
///     is never rewritten, which is what lets the turn that produced the image still carry its
///     bytes to the provider: what changes is the row, not the picture anybody is looking at.
///     <para>The store here is the real one (<see cref="SessionImageStore"/>) under the run's own
///     temp root, and the database is a real migrated AppDatabase, so every assertion is about
///     what the two actually wrote.</para></summary>
public sealed class StoredImageThresholdTests : IDisposable
{
  // A real 3x2 PNG: the store SNIFFS the bytes, so a signature-only fixture would not do.
  private const string Png3x2Base64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAFElEQVR4nGP4z8DAAMZMEOo/AwMAO+wF/S5FekgAAAAASUVORK5CYII=";

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly string _base = TestTempRoot.CreateDirectory("ethang-threshold").FullName;
  private readonly string _session = "threshold-" + Guid.NewGuid().ToString("N");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private SessionImageStore Images => new(_session, _base);

  /// <summary>The store with NO image sink attached: the shape every unit test and every legacy
  ///     container uses.</summary>
  private SqliteAgentStore StoreWithoutImages => new(new AppDatabase(_dbPath));

  /// <summary>The store with the real sink attached, as production wires it.</summary>
  private SqliteAgentStore StoreWithImages => new(new AppDatabase(_dbPath), imagesFor: _ => Images);

  /// <summary>A PNG-shaped payload of exactly <paramref name="size"/> bytes. The signature is
  ///     what the admission rule reads, so a payload that big is admitted; the filler is the
  ///     rest of a screenshot nobody decodes.</summary>
  private static byte[] Png(int size)
  {
    byte[] bytes = new byte[size];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
    return bytes;
  }

  private string SessionFolder => Path.Combine(SessionImageStore.ArtifactRootUnder(_base), _session);

  private string[] StoredFiles() =>
      Directory.Exists(SessionFolder) ? Directory.GetFiles(SessionFolder) : [];

  private async Task<AgentId> SavedAgentAsync()
  {
    AgentId id = AgentId.NewId();
    _ = await StoreWithImages.SaveAsync(AgentRecord.Started(id, "provider/model", null, "task",
        new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    return id;
  }

  private static DateTimeOffset At(int seconds) => new(2026, 8, 21, 10, 0, seconds, TimeSpan.Zero);

  /// <summary>Persists one tool message carrying <paramref name="base64"/> and reads the row's
  ///     meta_json and content straight out of the file.</summary>
  private async Task<(string MetaJson, string Content)> PersistAsync(SqliteAgentStore store,
      AgentId id, string base64, string tool = "computer")
  {
    Message message = new(Role.Tool, $"{tool}: observe", At(2), ToolCallId: "call_1",
        Parts: [new MessagePart.ImagePart("image/png", base64)]);
    Result<string> appended = await store.AppendMessageAsync(id, message,
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(appended.IsSuccess, appended.Error?.Message);
    return ReadLastRow(id);
  }

  private (string MetaJson, string Content) ReadLastRow(AgentId id)
  {
    using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT meta_json, content FROM agent_messages WHERE agent_id=@id ORDER BY seq DESC LIMIT 1;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    using SqliteDataReader reader = command.ExecuteReader();
    _ = reader.Read();
    return (reader.GetString(0), reader.GetString(1));
  }

  // --- Behaviour 1: a small image keeps its inline form ---

  [Fact]
  public async Task SmallImage_PersistsInline_ExactlyAsBefore_AndWritesNoFile()
  {
    AgentId id = await SavedAgentAsync();

    (string metaJson, string content) = await PersistAsync(StoreWithImages, id, Png3x2Base64);

    Assert.Contains("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.DoesNotContain("\"Path\"", metaJson, StringComparison.Ordinal);
    // Nothing was written: a payload under the threshold never becomes a file.
    Assert.Empty(StoredFiles());
    // The content is untouched, so the message reads exactly as it always did.
    Assert.Equal("computer: observe", content);
  }

  // --- Behaviour 2: a large image becomes a reference at persistence time ---

  [Fact]
  public async Task LargeImage_PersistsAsAReference_UnderAKilobyte_WithItsFileOnDisk()
  {
    AgentId id = await SavedAgentAsync();
    byte[] big = Png(400 * 1024);

    (string metaJson, string content) = await PersistAsync(StoreWithImages, id,
        Convert.ToBase64String(big));

    Assert.DoesNotContain("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.True(metaJson.Length < 1024,
        $"a reference row stays under 1,024 bytes; this one is {metaJson.Length}: {metaJson}");

    string path = Assert.Single(StoredFiles());
    Assert.Equal(big, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    // The content NAMES the path, so the text of the transcript says where the picture went.
    Assert.Contains(path, content, StringComparison.Ordinal);
  }

  // --- The awkward cases: exactly at the threshold, and one byte under ---

  [Fact]
  public async Task TheThreshold_IsInclusive_AndOneByteUnderStaysInline()
  {
    AgentId id = await SavedAgentAsync();
    int threshold = ImageLimits.InlineThresholdBytes;

    (string atThreshold, _) = await PersistAsync(StoreWithImages, id,
        Convert.ToBase64String(Png(threshold)));
    (string oneUnder, _) = await PersistAsync(StoreWithImages, id,
        Convert.ToBase64String(Png(threshold - 1)));

    // AT the threshold is a reference: the constant states the inclusive side, and this pins it.
    Assert.DoesNotContain("Base64Data", atThreshold, StringComparison.Ordinal);
    Assert.Contains("Path", atThreshold, StringComparison.Ordinal);
    // ONE BYTE UNDER is still inline.
    Assert.Contains("Base64Data", oneUnder, StringComparison.Ordinal);
    Assert.DoesNotContain("\"Path\"", oneUnder, StringComparison.Ordinal);
    _ = Assert.Single(StoredFiles());
  }

  // --- Behaviours 3 and 4: the same turn, two sizes, and nothing shown changes ---

  [Fact]
  public async Task OneTurnWithTwoImages_PersistsEachByItsOwnRule_AndRewritesNeitherMessage()
  {
    AgentId id = await SavedAgentAsync();
    string small = Png3x2Base64;
    string big = Convert.ToBase64String(Png(9 * 1024 * 1024));

    Message shotSmall = new(Role.Tool, "computer: observe", At(2), ToolCallId: "call_1",
        Parts: [new MessagePart.ImagePart("image/png", small)]);
    Message shotBig = new(Role.Tool, "computer: observe", At(3), ToolCallId: "call_2",
        Parts: [new MessagePart.ImagePart("image/png", big)]);

    foreach (Message message in new[] { shotSmall, shotBig })
    {
      Result<string> appended = await StoreWithImages.AppendMessageAsync(id, message,
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(appended.IsSuccess, appended.Error?.Message);
    }

    // Behaviour 4: the message each producer built still carries the payload it built. The
    // conversion happens on the ROW, never on the object the model's turn is holding.
    Assert.Equal(small, Assert.IsType<MessagePart.ImagePart>(Assert.Single(shotSmall.Parts!)).Base64Data);
    Assert.Equal(big, Assert.IsType<MessagePart.ImagePart>(Assert.Single(shotBig.Parts!)).Base64Data);

    // Each persisted by its own rule: one file, and it is the big one.
    string path = Assert.Single(StoredFiles());
    Assert.Equal(9 * 1024 * 1024, new FileInfo(path).Length);

    // Behaviour 3: what the next turn hydrates is the SAME bytes for the small image, and for
    // the large one a reference whose file holds those exact bytes.
    Result<IReadOnlyList<Message>> transcript = await StoreWithoutImages
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    Assert.Equal(2, transcript.Value.Count);
    Assert.Equal(small,
        Assert.IsType<MessagePart.ImagePart>(Assert.Single(transcript.Value[0].Parts!)).Base64Data);
    MessagePart.ImageReferencePart reference = Assert.IsType<MessagePart.ImageReferencePart>(
        Assert.Single(transcript.Value[1].Parts!));
    Assert.Equal(path, reference.Path);
    Assert.Equal(big, Convert.ToBase64String(
        await File.ReadAllBytesAsync(reference.Path, TestContext.Current.CancellationToken)
            .ConfigureAwait(true)));
  }

  // --- The awkward case: a sink that cannot write leaves the row correct, never a dangling reference ---

  [Fact]
  public async Task SinkFailure_LeavesThePayloadInline_RatherThanNamingAFileThatWasNeverWritten()
  {
    AgentId id = await SavedAgentAsync();
    byte[] big = Png(400 * 1024);
    // A path the store cannot create: an existing FILE where the session folder would go.
    string blocked = Path.Combine(_base, "blocked-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(blocked);
    string asFile = Path.Combine(blocked, "ethang-agent");
    await File.WriteAllTextAsync(asFile, "not a directory", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    SqliteAgentStore store = new(new AppDatabase(_dbPath),
        imagesFor: _ => new SessionImageStore(_session, blocked));

    (string metaJson, string content) = await PersistAsync(store, id, Convert.ToBase64String(big));

    // The write failed, so the payload stays inline: the row still reads and still renders, and
    // no reference names a file that does not exist.
    Assert.Contains("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.DoesNotContain("\"Path\"", metaJson, StringComparison.Ordinal);
    Assert.Equal("computer: observe", content);
  }

  // --- A store with no sink attached is the legacy shape ---

  [Fact]
  public async Task WithoutASink_EveryPayloadStaysInline_WhateverItsSize()
  {
    AgentId id = await SavedAgentAsync();

    (string metaJson, _) = await PersistAsync(StoreWithoutImages, id,
        Convert.ToBase64String(Png(400 * 1024)));

    Assert.Contains("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.Empty(StoredFiles());
  }

  /// <summary>A reference written by an earlier step reads back and resolves through the same
  ///     store, so the two sides agree about the file's identity.</summary>
  [Fact]
  public async Task APersistedReference_ResolvesThroughTheStoreThatWroteIt()
  {
    AgentId id = await SavedAgentAsync();
    byte[] big = Png(400 * 1024);
    _ = await PersistAsync(StoreWithImages, id, Convert.ToBase64String(big));

    Result<IReadOnlyList<Message>> transcript = await StoreWithoutImages
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    Message only = Assert.Single(transcript.Value);
    MessagePart.ImageReferencePart reference = Assert.IsType<MessagePart.ImageReferencePart>(
        Assert.Single(only.Parts!));

    Result<StoredImageContent> resolved = await Images
        .ResolveAsync(reference.Path, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(resolved.IsSuccess, resolved.Error?.Message);
    Assert.Equal("image/png", resolved.Value.MediaType);
    Assert.Equal(big, resolved.Value.Bytes.ToArray());
  }

  /// <summary>The threshold is ONE number, declared in bytes on the shared image limits: the
  ///     row's shape and the persisted content are both decided by it, so a reader can confirm
  ///     the boundary is the same one the test above pins.</summary>
  [Fact]
  public void TheThreshold_IsDeclaredOnce_WithAUnit()
  {
    Assert.InRange(ImageLimits.InlineThresholdBytes, 1, ImageLimits.MaxBytes);
    // A document switch: changing the constant alone moves the boundary in the test above.
    Assert.Equal(64 * 1024, ImageLimits.InlineThresholdBytes);
  }
}
