using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>An image is a FILE PLUS A POINTER, and the two halves are written by two different
///     owners at two different moments. These tests are about the gap between them: a file that
///     was written but never named (an ORPHAN) and a name with no file behind it (a DANGLING
///     REFERENCE). Both mechanisms are closed here, and both are observed on the REAL store and
///     the REAL database rather than on a fake, so what is asserted is what a later process would
///     actually find on disk after a restart.</summary>
/// <para>THE SWEEP NEVER RUNS ON THE WRITE PATH: it is an entry point a caller invokes where no
///     turn can be in flight, so every test states the rest point it simulates.</para>
public sealed class StoredImageSweepTests : IDisposable
{
  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly string _base = TestTempRoot.CreateDirectory("ethang-sweep").FullName;
  private readonly string _session = "sweep-" + Guid.NewGuid().ToString("N");

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

  /// <summary>The real store, over the run root, in this session's own folder.</summary>
  private SessionImageStore Images => new(_session, _base);

  private string SessionFolder => Path.Combine(SessionImageStore.ArtifactRootUnder(_base), _session);

  private string[] StoredFiles() =>
      Directory.Exists(SessionFolder) ? Directory.GetFiles(SessionFolder) : [];

  private SqliteAgentStore StoreWithImages => new(new AppDatabase(_dbPath), imagesFor: _ => Images);

  /// <summary>A PNG-shaped payload of exactly "size" bytes: the signature is what the admission
  ///     rule reads, and the filler is the rest of a screenshot nobody decodes.</summary>
  private static byte[] Png(int size)
  {
    byte[] bytes = new byte[size];
    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
    return bytes;
  }

  private static DateTimeOffset At(int seconds) => new(2026, 8, 21, 10, 0, seconds, TimeSpan.Zero);

  private async Task<AgentId> SavedAgentAsync()
  {
    AgentId id = AgentId.NewId();
    Result<string> saved = await StoreWithImages.SaveAsync(
        AgentRecord.Started(id, "provider/model", null, "task", At(0)),
        ct: TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(saved.IsSuccess, saved.Error?.Message);
    return id;
  }

  /// <summary>Reads the last row's meta_json, content and the meta_json's stored length straight
  ///     out of the file, so an assertion about the ROW is about what SQLite holds rather than
  ///     about an object this code kept.</summary>
  private async Task<(string MetaJson, string Content, long MetaLength)> ReadLastRowAsync(AgentId id)
  {
    // Named decision (CA2007): a test's own reading connection has no caller to starve.
#pragma warning disable CA2007
    await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
    {
      DataSource = _dbPath,
    }.ToString());
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT meta_json, content, LENGTH(meta_json) FROM agent_messages WHERE agent_id=@id ORDER BY seq DESC LIMIT 1;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    // Named decision (CA2007): the reader below is disposed before this method returns.
#pragma warning disable CA2007
    await using SqliteDataReader reader = await command
        .ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
#pragma warning restore CA2007
    _ = await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
  }


  /// <summary>Persists one tool message whose part is ALREADY a reference to "path" - the shape
  ///     a restored history re-persisted through the drain carries.</summary>
  private async Task PersistMessageNamingAsync(AgentId id, string path)
  {
    Message message = new(Role.Tool, "computer: observe", At(2), ToolCallId: "call_1",
        Parts: [new MessagePart.ImageReferencePart(path, "image/png")]);
    Result<string> appended = await StoreWithImages
        .AppendMessageAsync(id, message, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(appended.IsSuccess, appended.Error?.Message);
  }

  // ── Behaviour 1: a reference is never persisted before its file exists ────────────────

  /// <summary>A sink that cannot write leaves NO reference row and does NOT fall back to
  ///     bytes in the row: the transcript keeps its text and records, in that text, that the
  ///     image did not land. The other two answers are both wrong - a dangling reference names a
  ///     file nothing wrote, and an inline payload reintroduces exactly the bytes this form
  ///     exists to keep out of the row.</summary>
  [Fact]
  public async Task ASinkThatCannotWrite_WritesNoReference_KeepsNoBytes_AndSaysTheImageDidNotLand()
  {
    AgentId id = await SavedAgentAsync();
    // A path the store cannot create: an existing FILE where the session folder would go.
    string blocked = Path.Combine(_base, "blocked-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(blocked);
    await File.WriteAllTextAsync(Path.Combine(blocked, "ethang-agent"), "not a directory",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    SqliteAgentStore store = new(new AppDatabase(_dbPath),
        imagesFor: _ => new SessionImageStore(_session, blocked));

    Message message = new(Role.Tool, "computer: observe", At(2), ToolCallId: "call_1",
        Parts: [new MessagePart.ImagePart("image/png", Convert.ToBase64String(Png(400 * 1024)))]);
    Result<string> appended = await store
        .AppendMessageAsync(id, message, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(appended.IsSuccess, appended.Error?.Message);

    (string metaJson, string content, long metaLength) = await ReadLastRowAsync(id)
        .ConfigureAwait(true);
    // No reference was written, because no file exists for one to name.
    Assert.False(metaJson.Contains("Path", StringComparison.Ordinal), metaJson);
    // And the payload did not fall back into the row: this form exists to keep it out.
    Assert.DoesNotContain("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.True(metaLength < 1024, $"the row stays small; this one is {metaLength} bytes: {metaJson}");
    // The transcript says an image was here and did not land, so the record is not silent.
    Assert.StartsWith("computer: observe", content, StringComparison.Ordinal);
    Assert.Contains("[image not stored: image/png, ", content, StringComparison.Ordinal);
    Assert.Contains(" bytes]", content, StringComparison.Ordinal);

    // And the message still reads: the picture is what is missing, not the message.
    Result<IReadOnlyList<Message>> transcript = await store
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    Message stored = Assert.Single(transcript.Value);
    Assert.Equal(content, stored.Content);
    Assert.Null(stored.Parts);
  }

  /// <summary>The READ side of the same fact: a null meta entry contributes no part, and the
  ///     message's own text still says what happened. Recorded on a legacy-shaped store with no
  ///     sink, whose payload therefore STAYS inline - the two outcomes are deliberately different,
  ///     and this test pins that a wired sink's failure is not the no-sink case.</summary>
  [Fact]
  public async Task WithoutASink_ARefusedOversizeImageKeepsItsInlinePayload()
  {
    AgentId id = await SavedAgentAsync();
    SqliteAgentStore store = new(new AppDatabase(_dbPath));
    Message message = new(Role.Tool, "computer: observe", At(2), ToolCallId: "call_1",
        Parts: [new MessagePart.ImagePart("image/png", Convert.ToBase64String(Png(400 * 1024)))]);
    _ = await store.AppendMessageAsync(id, message, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    (string metaJson, string content, _) = await ReadLastRowAsync(id).ConfigureAwait(true);
    Assert.Contains("Base64Data", metaJson, StringComparison.Ordinal);
    Assert.Equal("computer: observe", content);
  }

  // ── Behaviour 3, and the plan's example: one referenced file survives, two orphans go ──

  [Fact]
  public async Task TheSweep_RemovesExactlyTheUnreferencedFiles_AndTheSurvivorStillResolves()
  {
    AgentId id = await SavedAgentAsync();
    byte[] referenced = Png(3 * 1024);
    StoredImage kept = Must(await Images.PersistAsync("image/png", referenced,
        TestContext.Current.CancellationToken));
    _ = Must(await Images.PersistAsync("image/png", Png(2 * 1024), TestContext.Current.CancellationToken));
    _ = Must(await Images.PersistAsync("image/png", Png(4 * 1024), TestContext.Current.CancellationToken));
    await PersistMessageNamingAsync(id, kept.Path);

    Result<int> swept = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(swept.IsSuccess, swept.Error?.Message);
    Assert.Equal(2, swept.Value);
    string survivor = Assert.Single(StoredFiles());
    Assert.Equal(kept.Path, survivor);
    // The surviving reference resolves to its ORIGINAL bytes: the sweep removed files, not data.
    Result<StoredImageContent> resolved = await Images
        .ResolveAsync(kept.Path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(resolved.IsSuccess, resolved.Error?.Message);
    Assert.Equal(referenced, resolved.Value.Bytes.ToArray());
  }

  // ── Behaviour 2: a crash between the write and the flush leaves a SWEEPABLE ORPHAN ────

  /// <summary>A REAL PROCESS writes an image through the real store and exits with nothing
  ///     naming it. That is exactly the state a process killed between the file write and the
  ///     transcript flush leaves behind, and it is the case the sweep exists for: the file is
  ///     real, on disk, and referenced by no row.</summary>
  [Fact]
  [Trait("Category", "Process")]
  public async Task ACrashBetweenTheFileWriteAndTheFlush_LeavesAFileTheSweepRemoves_AndEverySurvivingReferenceResolves()
  {
    AgentId id = await SavedAgentAsync();
    // A file a completed turn persisted and named: it must survive the sweep.
    byte[] referenced = Png(3 * 1024);
    StoredImage kept = Must(await Images.PersistAsync("image/png", referenced,
        TestContext.Current.CancellationToken));
    await PersistMessageNamingAsync(id, kept.Path);

    // The crashed writer: a separate process that wrote one image and died before anything
    // could name it.
    string orphan = await WriteAnImageAndDieAsync().ConfigureAwait(true);
    Assert.True(File.Exists(orphan), "the crashed writer left no file, so there is no orphan to sweep");
    Assert.NotEqual(kept.Path, orphan);

    Result<int> swept = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(swept.IsSuccess, swept.Error?.Message);
    Assert.Equal(1, swept.Value);
    Assert.False(File.Exists(orphan), "the un-referenced file must be gone");
    // Every persisted reference still resolves: the sweep took the orphan, not the picture.
    Assert.Equal(kept.Path, Assert.Single(StoredFiles()));
    Result<StoredImageContent> resolved = await Images
        .ResolveAsync(kept.Path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(resolved.IsSuccess, resolved.Error?.Message);
    Assert.Equal(referenced, resolved.Value.Bytes.ToArray());
  }

  /// <summary>Builds (once per process) and runs the in-repo writer, and returns the path it
  ///     wrote. The build is the same shape every in-repo stub host in this repository uses: a
  ///     project beside the tests, built on demand, cached for the run.</summary>
  private async Task<string> WriteAnImageAndDieAsync()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    string project = OrphanCrashHost.Project(AppContext.BaseDirectory);
    string exe = OrphanHostBuilder.Build(project);
    System.Diagnostics.ProcessStartInfo start = new(exe)
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    foreach (string argument in new[] { OrphanCrashHost.WriteOnly, _base, _session, "2048" })
    {
      start.ArgumentList.Add(argument);
    }

    using System.Diagnostics.Process writer = System.Diagnostics.Process.Start(start)!;
    string output = await writer.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(true);
    string error = await writer.StandardError.ReadToEndAsync(ct).ConfigureAwait(true);
    await writer.WaitForExitAsync(ct).ConfigureAwait(true);

    Assert.True(writer.ExitCode == 0, $"the writer failed ({writer.ExitCode}): {error}");
    string path = output.Trim();
    Assert.True(Path.IsPathRooted(path), $"the writer reported no absolute path: '{output}'");
    return path;
  }

  // ── The store's own tests run the sweep, so the plan's named test file covers it ────────

  /// <summary>The plan's example, stated end to end on the REAL store: three files persisted
  ///     for one session, a transcript naming one, and a sweep that reports two removed and
  ///     leaves the named file holding its original bytes.</summary>
  [Fact]
  public async Task PersistThree_FreezeOneInTheTranscript_Sweep_ReportsTwoRemoved_AndTheRestResolves()
  {
    AgentId id = await SavedAgentAsync();
    byte[] payload = Png(64 * 1024);
    StoredImage named = Must(await Images.PersistAsync("image/png", payload,
        TestContext.Current.CancellationToken));
    _ = Must(await Images.PersistAsync("image/png", Png(4096), TestContext.Current.CancellationToken));
    _ = Must(await Images.PersistAsync("image/png", Png(8192), TestContext.Current.CancellationToken));
    Assert.Equal(3, StoredFiles().Length);
    await PersistMessageNamingAsync(id, named.Path);

    Result<int> swept = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(swept.IsSuccess, swept.Error?.Message);
    Assert.Equal(2, swept.Value);
    string survivor = Assert.Single(StoredFiles());
    Assert.Equal(named.Path, survivor);
    byte[] onDisk = await File.ReadAllBytesAsync(survivor, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.Equal(payload, onDisk);
  }

  // ── Behaviour 4: safe to run twice ────────────────────────────────────────────────────

  [Fact]
  public async Task TheSweep_IsSafeToRunTwice_AndTheSecondRunReportsZero()
  {
    AgentId id = await SavedAgentAsync();
    StoredImage kept = Must(await Images.PersistAsync("image/png", Png(3 * 1024),
        TestContext.Current.CancellationToken));
    _ = Must(await Images.PersistAsync("image/png", Png(2 * 1024), TestContext.Current.CancellationToken));
    await PersistMessageNamingAsync(id, kept.Path);

    Result<int> first = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<int> second = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(1, first.Value);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal(0, second.Value);
    _ = Assert.Single(StoredFiles());
  }

  // ── The awkward case: a session folder that does not exist ────────────────────────────

  [Fact]
  public async Task SweepingASessionFolderThatDoesNotExist_ReportsZero_AndDoesNotThrow()
  {
    AgentId id = await SavedAgentAsync();
    Assert.Empty(StoredFiles());
    Assert.False(Directory.Exists(SessionFolder));

    Result<int> swept = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(swept.IsSuccess, swept.Error?.Message);
    Assert.Equal(0, swept.Value);
  }

  // ── The awkward case: a session with no transcript at all ─────────────────────────────

  [Fact]
  public async Task ASessionWithNoTranscriptRow_HasEveryFileRemoved_ThroughTheEmptyReferenceSet()
  {
    _ = Must(await Images.PersistAsync("image/png", Png(2 * 1024),
        TestContext.Current.CancellationToken));
    _ = Must(await Images.PersistAsync("image/png", Png(3 * 1024),
        TestContext.Current.CancellationToken));

    Result<int> swept = await SessionImageSweep
        .SweepSessionWithNoTranscriptAsync(Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(swept.IsSuccess, swept.Error?.Message);
    Assert.Equal(2, swept.Value);
    Assert.Empty(StoredFiles());
  }

  // ── Behaviour 5: a deleted file is not an exception ───────────────────────────────────

  /// <summary>A file deleted out from under a persisted reference cannot be swept back into
  ///     existence: the message still reads, the reference still names the path, and the sweep
  ///     reports what it found. The sweep's job is deletion, and it never invents a replacement.
  ///     </summary>
  [Fact]
  public async Task AReferenceWhoseFileIsGone_StillReads_AndTheSweepDoesNotInventIt()
  {
    AgentId id = await SavedAgentAsync();
    StoredImage gone = Must(await Images.PersistAsync("image/png", Png(3 * 1024),
        TestContext.Current.CancellationToken));
    await PersistMessageNamingAsync(id, gone.Path);
    File.Delete(gone.Path);

    Result<int> swept = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(swept.IsSuccess, swept.Error?.Message);
    Assert.Equal(0, swept.Value);
    Assert.False(File.Exists(gone.Path));

    // The message survives and still names the path: what is missing is the picture.
    Result<IReadOnlyList<Message>> transcript = await StoreWithImages
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    MessagePart.ImageReferencePart reference = Assert.IsType<MessagePart.ImageReferencePart>(
        Assert.Single(Assert.Single(transcript.Value).Parts!));
    Assert.Equal(gone.Path, reference.Path);
    // And resolving it is the typed failure step 2 defined, not an exception.
    Result<StoredImageContent> resolved = await Images
        .ResolveAsync(gone.Path, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.False(resolved.IsSuccess);
    Assert.Equal("ImageNotFound", resolved.Error?.Code);
  }

  /// <summary>THE SWEEP DELETES FILES ONLY. A referenced file survives whatever else sits in the
  ///     folder, and an unrelated file adjacent to it is never treated as a reference.</summary>
  [Fact]
  public async Task TheSweep_NeverDeletesAFileSomePersistedMessageNames()
  {
    AgentId id = await SavedAgentAsync();
    StoredImage first = Must(await Images.PersistAsync("image/png", Png(2 * 1024),
        TestContext.Current.CancellationToken));
    StoredImage second = Must(await Images.PersistAsync("image/png", Png(3 * 1024),
        TestContext.Current.CancellationToken));
    await PersistMessageNamingAsync(id, first.Path);
    await PersistMessageNamingAsync(id, second.Path);

    Result<int> swept = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(0, swept.Value);
    Assert.Equal(2, StoredFiles().Length);
  }

  /// <summary>Sweeping against a set this code could not read is a FAILURE, never an empty set:
  ///     an empty set deletes every picture the session has.</summary>
  [Fact]
  public async Task AnUnreadableTranscript_FailsTheSweep_AndRemovesNothing()
  {
    AgentId id = await SavedAgentAsync();
    _ = Must(await Images.PersistAsync("image/png", Png(2 * 1024),
        TestContext.Current.CancellationToken));
    // One persisted message, so a row exists for the sweep to try to read.
    Result<string> appended = await StoreWithImages.AppendMessageAsync(id,
        new Message(Role.Tool, "computer: observe", At(2), ToolCallId: "call_1"),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(appended.IsSuccess, appended.Error?.Message);
    await CorruptTheOnlyRowsMetaJsonAsync(id);

    Result<int> swept = await SessionImageSweep
        .SweepAsync(StoreWithImages, id, Images, TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(swept.IsSuccess);
    Assert.Equal("TranscriptUnreadable", swept.Error?.Code);
    // Nothing was deleted: the sweep never ran.
    _ = Assert.Single(StoredFiles());
  }

  /// <summary>Rewrites the ONLY row's meta_json to something this code cannot parse, the way a
  ///     corrupted or hand-edited database would read.</summary>
  private async Task CorruptTheOnlyRowsMetaJsonAsync(AgentId id)
  {
    // Named decision (CA2007): a test's own seeding connection has no caller to starve.
#pragma warning disable CA2007
    await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
    {
      DataSource = _dbPath,
    }.ToString());
#pragma warning restore CA2007
    await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "UPDATE agent_messages SET meta_json = '{ not json' WHERE agent_id=@id;";
    _ = command.Parameters.AddWithValue("@id", id.ToString());
    int corrupted = await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.Equal(1, corrupted);
  }


  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }
}
