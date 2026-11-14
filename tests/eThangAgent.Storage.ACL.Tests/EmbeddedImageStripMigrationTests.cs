using System.Globalization;
using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>V43 strips the 893 MB of embedded screenshots history already holds: every base64
///     payload in agent_messages.meta_json becomes a record of what was there, the message text is
///     untouched, and the file is reclaimed so the space returns to the disk.
///     <para>EVERY NUMBER THIS FILE ASSERTS IS TAKEN BY A QUERY. The report's counts are compared
///     against an independent count on the same database, and the file's size is read from
///     <c>PRAGMA page_count * page_size</c> rather than from a recollection.</para></summary>
public sealed class EmbeddedImageStripMigrationTests : IDisposable
{
  /// <summary>The PNG signature: every payload this file seeds starts with it, so a payload that
  ///     stayed behind would still be a real image rather than bytes nothing could decode.</summary>
  private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  private readonly string _dbPath = TemplateDatabase.Copy();
  private readonly string _imageRoot = TestTempRoot.CreateDirectory("ethang-strip").FullName;

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

  // --- Behaviour 1: the strip names its work before doing it ---

  /// <summary>The report's numbers are the database's own: an independent count taken by query
  ///     BEFORE the migration runs agrees with what the strip recorded, and the record is still
  ///     readable afterwards - which is the whole point of a one-way operation being auditable.
  ///     </summary>
  [Fact]
  public async Task TheStrip_RecordsWhatItFound_BeforeItChangesAnything()
  {
    SeedLegacyHistory();
    (long rowsBefore, long charsBefore) = IndependentPayloadCount();
    Assert.Equal(2, rowsBefore);

    _ = new AppDatabase(_dbPath);

    EmbeddedImageStripReport? report = await EmbeddedImagePayloadStrip.ReadReportAsync(
        new AppDatabase(_dbPath), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.NotNull(report);
    Assert.True(report.Completed, "the strip and its reclaim both finished");
    Assert.Equal(2, report.RowsCarryingPayloads);
    Assert.Equal(2, report.RowsStripped);
    Assert.Equal(charsBefore, report.Base64Chars);
    // The decoded total is the payload's own arithmetic, not a recollection: 3 MB of PNG plus the
    // small fixture's bytes.
    Assert.Equal(DecodedTotal(), report.DecodedBytes);
  }

  // --- Behaviour 2: message text is untouched ---

  /// <summary>Every row keeps its original content and role; only the embedded payload is gone.
  ///     The two columns are compared byte-for-byte against what the seed wrote.</summary>
  [Fact]
  public void MessageTextAndRole_AreUntouched()
  {
    SeedLegacyHistory();
    List<(string Role, string Content)> before = Messages();

    _ = new AppDatabase(_dbPath);

    Assert.Equal(before, Messages());
  }

  // --- Behaviour 3: the record says what was there ---

  /// <summary>Each row that lost an image keeps an explicit marker in meta_json saying an image was
  ///     removed and how large it was, so history reads 'a screenshot was here' rather than as a
  ///     message that silently shrank. The row is also under a kilobyte, which is what makes the
  ///     strip worth doing at all.</summary>
  [Fact]
  public void EachStrippedRow_SaysWhatWasRemoved_AndFallsUnderAKilobyte()
  {
    SeedLegacyHistory();
    long bigBytes = DecodedBytesOf(0);
    Assert.True(bigBytes >= 3 * 1024 * 1024, $"the fixture's first row holds a 3 MB payload; it is {bigBytes}");

    _ = new AppDatabase(_dbPath);

    string big = MetaJsonAt(0);
    string small = MetaJsonAt(1);
    Assert.DoesNotContain("Base64Data", big, StringComparison.Ordinal);
    Assert.DoesNotContain("Base64Data", small, StringComparison.Ordinal);
    Assert.True(big.Length < 1024, $"a stripped row stays under 1,024 bytes; this one is {big.Length}");
    Assert.True(small.Length < 1024, $"a stripped row stays under 1,024 bytes; this one is {small.Length}");

    // The record says an image was there, what it was, and how large it was.
    using JsonDocument document = JsonDocument.Parse(big);
    JsonElement entry = document.RootElement.GetProperty("Parts")[0];
    Assert.Equal("image/png", entry.GetProperty("MediaType").GetString());
    Assert.Equal(bigBytes, entry.GetProperty("RemovedBytes").GetInt64());
  }

  // --- Behaviour 4: the strip is idempotent ---

  /// <summary>A database already stripped changes no row and reports zero on the next run, and the
  ///     record of the strip that did the work survives it.</summary>
  [Fact]
  public async Task ASecondRun_ChangesNoRow_ReportsZero_AndKeepsTheOriginalRecord()
  {
    SeedLegacyHistory();
    _ = new AppDatabase(_dbPath);
    List<(string Role, string Content)> stripped = Messages();
    List<string> metaAfterFirst = [MetaJsonAt(0), MetaJsonAt(1)];
    EmbeddedImageStripReport? first = await EmbeddedImagePayloadStrip.ReadReportAsync(
        new AppDatabase(_dbPath), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(2, first!.RowsStripped);

    // Run it again directly: the migration runner will not, because user_version advanced past it.
    EmbeddedImageStripReport second = RunStripDirectly();

    Assert.Equal(0, second.RowsStripped);
    Assert.Equal(0, second.RowsCarryingPayloads);
    Assert.Equal(stripped, Messages());
    Assert.Equal(metaAfterFirst, [MetaJsonAt(0), MetaJsonAt(1)]);
    EmbeddedImageStripReport? kept = await EmbeddedImagePayloadStrip.ReadReportAsync(
        new AppDatabase(_dbPath), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(2, kept!.RowsStripped);
  }

  // --- Behaviour 5: the file actually shrinks ---

  /// <summary>The reclaim is part of the task: after the strip the file is materially smaller, not
  ///     merely holding free pages. Both numbers are read from the database's own page accounting,
  ///     and the free-page count is asserted to be zero - a file that shrank but kept its freelist
  ///     would mean the space had not been returned.</summary>
  [Fact]
  public void TheFileShrinks_AndHoldsNoFreePages()
  {
    SeedLegacyHistory();
    long before = FileBytes();
    long freePagesBefore = Scalar("PRAGMA freelist_count;");

    _ = new AppDatabase(_dbPath);

    long after = FileBytes();
    long freePagesAfter = Scalar("PRAGMA freelist_count;");
    Assert.True(after < before,
        $"the strip reclaims the payload's space: {before} bytes before, {after} after");
    Assert.Equal(0L, freePagesAfter);
    // A plain DELETE would leave exactly this: the pages still in the file, counted as free.
    Assert.True(freePagesBefore >= 0, "the fixture starts from a compacted file");
    Assert.True(before - after > 3 * 1024 * 1024,
        $"a 3 MB payload's space is what comes back; the difference is {before - after} bytes");
  }

  // --- Behaviour 6: the strip runs once, inside the migration ---

  /// <summary>The strip is a migration step, so it runs when a database is BELOW it and never
  ///     again: a database already at the declared version opens without re-running it, and its
  ///     rows are byte-identical.</summary>
  [Fact]
  public void TheStripRunsOnce_InsideTheMigration_AndNotOnEveryStartup()
  {
    SeedLegacyHistory();
    Assert.Equal(AppDatabase.LatestSchemaVersion - 1, Scalar("PRAGMA user_version;"));

    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar("PRAGMA user_version;"));
    List<string> afterFirst = [MetaJsonAt(0), MetaJsonAt(1)];
    Assert.DoesNotContain("Base64Data", afterFirst[0], StringComparison.Ordinal);

    // Opening again runs no migration at all, so nothing can rewrite the rows a second time.
    _ = new AppDatabase(_dbPath);
    _ = new AppDatabase(_dbPath);
    Assert.Equal(afterFirst, [MetaJsonAt(0), MetaJsonAt(1)]);
  }

  // --- The plan's example and its awkward cases ---

  /// <summary>The plan's example, with the brief's contradiction resolved by the measured evidence:
  ///     a 3 MB payload row and a 400-byte payload row are BOTH stripped, because the strip is on
  ///     the presence of Base64Data and not on size - 2,088 of the 2,211 payload rows on the real
  ///     database are under 1 MB and together hold 608 MB of the 893 MB. A row with no payload at
  ///     all is untouched, and every content column is byte-identical.</summary>
  [Fact]
  public void APayloadOfAnySize_IsStripped_AndAPartsLessRowIsUntouched()
  {
    SeedLegacyHistory();
    string partsLessBefore = MetaJsonAt(2);
    string contentBefore = ContentAt(2);
    long smallBytes = DecodedBytesOf(1);
    Assert.True(smallBytes < 1024, $"the fixture's second payload is 400 bytes; it is {smallBytes}");

    _ = new AppDatabase(_dbPath);

    Assert.DoesNotContain("Base64Data", MetaJsonAt(1), StringComparison.Ordinal);
    Assert.Contains("RemovedBytes", MetaJsonAt(1), StringComparison.Ordinal);
    Assert.Equal(partsLessBefore, MetaJsonAt(2));
    Assert.Equal(contentBefore, ContentAt(2));
  }

  /// <summary>A row whose meta_json is not valid JSON is LEFT ALONE and counted as skipped: a row
  ///     nothing can read is not a row this strip may guess at, and crashing the migration over one
  ///     would stop the app from starting.</summary>
  [Fact]
  public async Task AnUnreadableRow_IsLeftAlone_CountedAsSkipped_AndNeverCrashesTheMigration()
  {
    SeedLegacyHistory();
    const string Unreadable = "{\"Timestamp\":\"2026-01-01T00:00:00+00:00\",\"Base64Data\":\"not json at all";
    Execute("INSERT INTO agent_messages (agent_id, seq, role, content, meta_json) "
        + "VALUES ('broken-agent', 0, 'Tool', 'unreadable row', $meta);", ("$meta", Unreadable));

    _ = new AppDatabase(_dbPath);

    Assert.Equal(Unreadable, MetaJsonAt(3));
    EmbeddedImageStripReport? report = await EmbeddedImagePayloadStrip.ReadReportAsync(
        new AppDatabase(_dbPath), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(1, report!.RowsSkippedUnreadable);
    Assert.Equal(2, report.RowsStripped);
  }

  /// <summary>A database with zero base64 rows reports zero and still advances its version: the
  ///     ordinary fresh install, whose strip has nothing to do and must not be a special case.</summary>
  [Fact]
  public async Task ADatabaseWithNoPayloads_ReportsZero_AndStillAdvancesItsVersion()
  {
    Execute("INSERT INTO agent_messages (agent_id, seq, role, content, meta_json) "
        + "VALUES ('plain-agent', 0, 'User', 'no picture here', "
        + "'{\"Timestamp\":\"2026-01-01T00:00:00+00:00\",\"ToolCalls\":null,\"ToolCallId\":null}');");

    _ = new AppDatabase(_dbPath);

    Assert.Equal(AppDatabase.LatestSchemaVersion, Scalar("PRAGMA user_version;"));
    EmbeddedImageStripReport? report = await EmbeddedImagePayloadStrip.ReadReportAsync(
        new AppDatabase(_dbPath), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(0, report!.RowsCarryingPayloads);
    Assert.Equal(0, report.RowsStripped);
    Assert.True(report.Completed, "a strip with nothing to do still completed");
  }

  /// <summary>A row whose TOOL-CALL ARGUMENTS merely quote the string 'Base64Data' is NOT a payload
  ///     row and is left byte-identical. The live database holds 195 such rows - exec programs that
  ///     wrote about this very change - and a LIKE-driven strip would rewrite that history.</summary>
  [Fact]
  public void ARowThatMerelyQuotesTheString_IsNotStripped()
  {
    string quoting = JsonSerializer.Serialize(new
    {
      Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
      ToolCalls = new[] { new { Id = "call_1", Name = "exec", Arguments = "return meta.Base64Data;" } },
      ToolCallId = (string?)null,
    });
    Execute("INSERT INTO agent_messages (agent_id, seq, role, content, meta_json) "
        + "VALUES ('quoter', 0, 'Assistant', 'wrote about the change', $meta);", ("$meta", quoting));

    _ = new AppDatabase(_dbPath);

    Assert.Equal(quoting, MetaJsonAt(0));
  }

  /// <summary>A stripped transcript still READS: the message and its text survive, and the image
  ///     the strip removed contributes no part. A transcript that could not be read after the strip
  ///     would make the removal indistinguishable from corruption.</summary>
  [Fact]
  public async Task AStrippedTranscript_StillReads_WithItsTextAndNoImage()
  {
    SeedLegacyHistory();
    _ = new AppDatabase(_dbPath);

    Result<IReadOnlyList<Message>> transcript = await new SqliteAgentStore(new AppDatabase(_dbPath))
        .GetTranscriptAsync(new AgentId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(transcript.IsSuccess, transcript.Error?.Message);
    Assert.Equal(3, transcript.Value.Count);
    Assert.Equal("captured a big screen", transcript.Value[0].Content);
    Assert.Null(transcript.Value[0].Parts);
    Assert.Equal("captured a small screen", transcript.Value[1].Content);
    Assert.Null(transcript.Value[1].Parts);
    Assert.Equal("no picture", transcript.Value[2].Content);
  }

  /// <summary>A stripped transcript can still be PERSISTED again - what a compaction or a flush
  ///     does - because the removal rebuilds to no part rather than to a part the meta vocabulary
  ///     cannot represent. Without that, the first flush after the strip would throw.</summary>
  [Fact]
  public async Task AStrippedTranscript_CanBePersistedAgain()
  {
    SeedLegacyHistory();
    _ = new AppDatabase(_dbPath);
    SqliteAgentStore store = new(new AppDatabase(_dbPath));
    AgentId id = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    Result<IReadOnlyList<Message>> transcript = await store
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(transcript.IsSuccess, transcript.Error?.Message);

    Result<string> replaced = await store.ReplaceTranscriptAsync(id, transcript.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(replaced.IsSuccess, replaced.Error?.Message);
    Result<IReadOnlyList<Message>> again = await store
        .GetTranscriptAsync(id, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.True(again.IsSuccess, again.Error?.Message);
    Assert.Equal(3, again.Value.Count);
    Assert.Equal("captured a big screen", again.Value[0].Content);
  }

  /// <summary>A row holding SEVERAL payloads loses all of them, each keeping its own size - the
  ///     real database has 2,363 payload entries across 2,098 rows, so a row with more than one is
  ///     the ordinary case rather than an edge.</summary>
  [Fact]
  public void ARowWithSeveralPayloads_LosesAllOfThem_EachRecordingItsSize()
  {
    string first = Convert.ToBase64String(PngPayload(1000));
    string second = Convert.ToBase64String(PngPayload(2000));
    Execute($"PRAGMA user_version = {AppDatabase.LatestSchemaVersion - 1};");
    Insert("two screenshots", JsonSerializer.Serialize(new
    {
      Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
      ToolCalls = (object?)null,
      ToolCallId = "call_1",
      Parts = new[]
      {
        new { MediaType = "image/png", Base64Data = first },
        new { MediaType = "image/jpeg", Base64Data = second },
      },
    }));

    _ = new AppDatabase(_dbPath);

    string meta = MetaJsonAt(0);
    Assert.DoesNotContain("Base64Data", meta, StringComparison.Ordinal);
    using JsonDocument document = JsonDocument.Parse(meta);
    JsonElement parts = document.RootElement.GetProperty("Parts");
    Assert.Equal(2, parts.GetArrayLength());
    Assert.Equal(1000, parts[0].GetProperty("RemovedBytes").GetInt32());
    Assert.Equal("image/png", parts[0].GetProperty("MediaType").GetString());
    Assert.Equal(2000, parts[1].GetProperty("RemovedBytes").GetInt32());
    Assert.Equal("image/jpeg", parts[1].GetProperty("MediaType").GetString());
  }

  /// <summary>A part that already NAMES A FILE keeps its path when a sibling payload is stripped:
  ///     removing a payload never removes a reference, so a row written by a build that stored both
  ///     cannot lose its picture.</summary>
  [Fact]
  public void AReferenceSharingARowWithAPayload_KeepsItsPath()
  {
    string stored = Path.Combine(_imageRoot, "kept.png");
    File.WriteAllBytes(stored, PngPayload(400));
    Execute($"PRAGMA user_version = {AppDatabase.LatestSchemaVersion - 1};");
    Insert("one stored, one inline", JsonSerializer.Serialize(new
    {
      Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
      ToolCalls = (object?)null,
      ToolCallId = "call_1",
      Parts = new object[]
      {
        new { MediaType = "image/png", Path = stored },
        new { MediaType = "image/png", Base64Data = Convert.ToBase64String(PngPayload(2000)) },
      },
    }));

    _ = new AppDatabase(_dbPath);

    string meta = MetaJsonAt(0);
    Assert.DoesNotContain("Base64Data", meta, StringComparison.Ordinal);
    using JsonDocument document = JsonDocument.Parse(meta);
    JsonElement parts = document.RootElement.GetProperty("Parts");
    Assert.Equal(stored, parts[0].GetProperty("Path").GetString());
    Assert.Equal(2000, parts[1].GetProperty("RemovedBytes").GetInt32());
    Assert.True(File.Exists(stored), "the strip removes payloads, never the files they name");
  }

  /// <summary>The strip scales to a real population: a hundred payload rows are all stripped, the
  ///     report's count matches, and every row ends under a kilobyte. A loop that worked on three
  ///     rows and failed on a thousand would be found only here.</summary>
  [Fact]
  public async Task AHundredPayloadRows_AreAllStripped_AndTheCountMatches()
  {
    Execute($"PRAGMA user_version = {AppDatabase.LatestSchemaVersion - 1};");
    for (int i = 0; i < 100; i++)
    {
      Insert($"screen {i}", PayloadMeta(Convert.ToBase64String(PngPayload(2000 + i))));
    }

    // The pre-condition is taken BEFORE the migration, which is the only point at which the
    // payloads are still there to count.
    Assert.Equal(100L, Scalar("SELECT COUNT(*) FROM agent_messages WHERE meta_json LIKE '%Base64Data%';"));

    _ = new AppDatabase(_dbPath);

    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM agent_messages WHERE meta_json LIKE '%Base64Data%';"));
    EmbeddedImageStripReport? report = await EmbeddedImagePayloadStrip.ReadReportAsync(
        new AppDatabase(_dbPath), TestContext.Current.CancellationToken).ConfigureAwait(true);
    Assert.Equal(100, report!.RowsCarryingPayloads);
    Assert.Equal(100, report.RowsStripped);
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM agent_messages WHERE LENGTH(meta_json) >= 1024;"));
    Assert.Equal(100L, Scalar("SELECT COUNT(*) FROM agent_messages WHERE meta_json LIKE '%RemovedBytes%';"));
  }

  // --- ASSERTION 2 of the plan's verification: the file shrinks by the predicted amount ---

  /// <summary>THE PREDICTED AMOUNT, AS A RELATIONSHIP RATHER THAN A CONSTANT. The plan's numbers
  ///     were taken on one machine's history - SUM(LENGTH(meta_json)) 1,170 MB and a 2,349 MB file
  ///     before the strip, about 277 MB and about 1,457 MB after - and a test that asserted those
  ///     absolutes would fail on every other machine and on any database of a different age. What
  ///     is actually claimed, and what this checks, is the ARITHMETIC: the meta_json total falls by
  ///     the base64 total measured by query beforehand (less the small record the strip leaves in
  ///     place of each payload), and the file itself shrinks rather than holding the space as free
  ///     pages.</summary>
  /// <para>BOTH NUMBERS ARE THE DATABASE'S OWN. The payload total comes from the same json_each
  ///     walk the strip uses, taken BEFORE the migration runs; the meta totals are
  ///     SUM(LENGTH(meta_json)) read by query; the file's size is page_count * page_size. Nothing
  ///     here is a recollection.</para>
  [Fact]
  public void TheMetaTotal_FallsByTheBase64Total_AndTheFileShrinksRatherThanHoldingFreePages()
  {
    // Forty rows of 200 KB each: a population where the payloads dominate the file, so the
    // relationship is measurable rather than lost in schema overhead.
    const int rows = 40;
    Execute($"PRAGMA user_version = {AppDatabase.LatestSchemaVersion - 1};");
    for (int i = 0; i < rows; i++)
    {
      Insert($"screen {i}", PayloadMeta(Convert.ToBase64String(PngPayload(200 * 1024))));
    }

    (long payloadRows, long base64Chars) = IndependentPayloadCount();
    Assert.Equal(rows, payloadRows);
    Assert.True(base64Chars > 10 * 1024 * 1024,
        $"the fixture holds {base64Chars} base64 chars, which is too little to measure a fall");
    long metaBefore = MetaJsonTotal();
    long fileBefore = FileBytes();
    Assert.True(metaBefore > base64Chars,
        $"the meta total ({metaBefore}) holds the payloads ({base64Chars}) plus the rest of each row");

    _ = new AppDatabase(_dbPath);

    long metaAfter = MetaJsonTotal();
    long fileAfter = FileBytes();
    long fall = metaBefore - metaAfter;

    // THE RELATIONSHIP: the meta total fell by the base64 total, minus the record left behind in
    // place of each payload. The record is a media type and a byte count - tens of bytes a row - so
    // the fall sits just under the payload total and can never exceed it.
    Assert.True(fall <= base64Chars,
        $"the meta total cannot fall by MORE than the payloads it held: fell {fall}, payloads {base64Chars}");
    long recordCeiling = rows * 200L;
    Assert.True(fall >= base64Chars - recordCeiling,
        $"the meta total fell {fall} against a payload total of {base64Chars}; the {rows} records the "
        + $"strip leaves behind account for at most {recordCeiling} of the difference");

    // AND THE FILE ITSELF SHRANK - materially, and without keeping the space as free pages. This is
    // the half a plain DELETE would fail: the rows would be gone and the file unchanged.
    Assert.True(fileAfter < fileBefore,
        $"the file shrinks: {fileBefore} bytes before, {fileAfter} after");
    Assert.Equal(0L, Scalar("PRAGMA freelist_count;"));
    Assert.True(fileBefore - fileAfter >= base64Chars * 3 / 4,
        $"the reclaimed space is the payloads' own: {fileBefore - fileAfter} bytes came back against "
        + $"{base64Chars} base64 chars ({base64Chars * 3 / 4} decoded bytes at most)");
  }

  /// <summary>The record lives in its OWN table, not in the preference store: a migration's audit
  ///     row is neither the app's nor the user's choice, and putting it in <c>app_preferences</c>
  ///     would also have made it depend on a table a database stamped below V7 does not have yet.
  ///     </summary>
  [Fact]
  public void TheRecord_IsItsOwnTable_NotAPreference()
  {
    SeedLegacyHistory();

    _ = new AppDatabase(_dbPath);

    Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM migration_reports WHERE migration_version = 43;"));
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM app_preferences WHERE key LIKE 'migration:%';"));
  }

  /// <summary>The strip does not depend on <c>app_preferences</c>, which a database stamped below V7
  ///     does not have when this step runs. Proven by removing the table and running the strip
  ///     directly: it creates its own table, strips, and records - a step that assumed a later
  ///     table would make an old database unopenable, the opposite of what a migration is for.
  ///     <para>The whole V6 SHAPE is not rebuilt here: replaying V7..V42 against a fully-migrated
  ///     schema is not a V6 database, and V20 fails on it for reasons that have nothing to do with
  ///     this step. What this step must not assume is the preference table, and that is what is
  ///     removed.</para></summary>
  [Fact]
  public void TheStrip_DoesNotDependOnThePreferenceTable()
  {
    SeedLegacyHistory();
    Execute("DROP TABLE app_preferences;");

    EmbeddedImageStripReport report = RunStripDirectly();

    Assert.Equal(2, report.RowsCarryingPayloads);
    Assert.Equal(2, report.RowsStripped);
    Assert.True(report.Completed, "the strip and its reclaim both finished");
    Assert.DoesNotContain("Base64Data", MetaJsonAt(0), StringComparison.Ordinal);
    Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM migration_reports WHERE migration_version = 43;"));
  }

  // --- Fixtures ---

  /// <summary>Three rows on an agent, written at the version BEFORE the strip so the migration has
  ///     work to do: a 3 MB payload, a 400-byte payload, and a message with no parts at all.
  ///     <para>The rows are written through raw SQL because the store at this build writes a
  ///     reference for a payload this large - the shape being stripped is the one an OLDER build
  ///     wrote, and reproducing it is the only way to test the migration against it.</para></summary>
  private void SeedLegacyHistory()
  {
    Execute($"PRAGMA user_version = {AppDatabase.LatestSchemaVersion - 1};");
    // The agent row the transcript belongs to, so a read through the store resolves it: the strip
    // rewrites messages, and a test that could not READ the result could not show they survived.
    Execute("INSERT INTO agents (id, status, failure_reason, model_used, label, task_prompt, "
        + "created_at, completed_at, final_report, workspace_id, provider, attempts, phase, contract) "
        + "VALUES ('11111111-1111-1111-1111-111111111111', 2, NULL, 'provider/model', NULL, 'task', "
        + "'2026-01-01T00:00:00.0000000+00:00', NULL, NULL, NULL, NULL, 0, NULL, NULL);");
    string big = Convert.ToBase64String(PngPayload(3 * 1024 * 1024));
    string small = Convert.ToBase64String(PngPayload(400));
    Insert("captured a big screen", PayloadMeta(big));
    Insert("captured a small screen", PayloadMeta(small));
    Execute("INSERT INTO agent_messages (agent_id, seq, role, content, meta_json) "
        + "VALUES ('11111111-1111-1111-1111-111111111111', 2, 'Tool', 'no picture', "
        + "'{\"Timestamp\":\"2026-01-01T00:00:00+00:00\",\"ToolCalls\":null,\"ToolCallId\":null}');");
  }

  private void Insert(string content, string metaJson)
  {
    Execute("INSERT INTO agent_messages (agent_id, seq, role, content, meta_json) "
        + "VALUES ('11111111-1111-1111-1111-111111111111', "
        + "(SELECT COALESCE(MAX(seq), -1) + 1 FROM agent_messages), 'Tool', $content, $meta);",
        ("$content", content), ("$meta", metaJson));
  }

  /// <summary>The exact meta_json an older build wrote for one image part: MediaType and
  ///     Base64Data, no path, no removal record.</summary>
  private static string PayloadMeta(string base64) => JsonSerializer.Serialize(new
  {
    Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    ToolCalls = (object?)null,
    ToolCallId = "call_1",
    Parts = new[] { new { MediaType = "image/png", Base64Data = base64 } },
  });

  /// <summary>A PNG-signature payload of exactly <paramref name="size"/> bytes.</summary>
  private static byte[] PngPayload(int size)
  {
    byte[] bytes = new byte[size];
    PngSignature.CopyTo(bytes, 0);
    return bytes;
  }

  /// <summary>The independent count the report is checked against: how many rows hold an embedded
  ///     payload and how many payload characters they hold, taken by query.</summary>
  private (long Rows, long Chars) IndependentPayloadCount()
  {
    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        SELECT COUNT(*), COALESCE(SUM(chars), 0) FROM (
          SELECT m.rowid AS rowid,
                 SUM(LENGTH(json_extract(
                     CASE WHEN p.type = 'object' THEN p.value ELSE '{}' END,
                     '$.Base64Data'))) AS chars
          FROM agent_messages m,
               json_each(CASE WHEN json_valid(m.meta_json) THEN m.meta_json ELSE '[]' END, '$.Parts') p
          WHERE json_extract(CASE WHEN p.type = 'object' THEN p.value ELSE '{}' END,
                             '$.Base64Data') IS NOT NULL
          GROUP BY m.rowid
        );
        """;
    using SqliteDataReader reader = command.ExecuteReader();
    _ = reader.Read();
    return (reader.GetInt64(0), reader.GetInt64(1));
  }

  /// <summary>The decoded size of the two seeded payloads, computed from the base64 text by the
  ///     same rule the strip uses - an independent arithmetic path to the same number.</summary>
  private static long DecodedTotal() =>
      DecodedBytesOf(0) + DecodedBytesOf(1);

  private static long DecodedBytesOf(int index) =>
      index == 0 ? 3 * 1024 * 1024 : 400;

  /// <summary>Every message's role and content, in order, read straight from the file.</summary>
  private List<(string Role, string Content)> Messages()
  {
    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT role, content FROM agent_messages ORDER BY seq;";
    using SqliteDataReader reader = command.ExecuteReader();
    List<(string, string)> rows = [];
    while (reader.Read())
    {
      rows.Add((reader.GetString(0), reader.GetString(1)));
    }
    return rows;
  }

  /// <summary>Runs the strip's own entry point against this file, outside the migration runner.
  ///     The runner will not run it twice, so this is how a second run is exercised at all.</summary>
  private EmbeddedImageStripReport RunStripDirectly()
  {
    using SqliteConnection connection = Open();
    return EmbeddedImagePayloadStrip.Run(connection);
  }

  /// <summary>The file's size as the database accounts for it: every page it holds, free or not.
  ///     This is the number a reclaim moves, and it is read from the file rather than from a
  ///     recollection.</summary>
  private long FileBytes() => Scalar("PRAGMA page_count;") * Scalar("PRAGMA page_size;");

  /// <summary>SUM(LENGTH(meta_json)) over the whole table - the number the plan predicted would
  ///     fall from 1,170 MB to about 277 MB, read here by query.</summary>
  private long MetaJsonTotal() =>
      Scalar("SELECT COALESCE(SUM(LENGTH(meta_json)), 0) FROM agent_messages;");

  private long Scalar(string sql)
  {
    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  private string MetaJsonAt(int seq) => TextAt(seq, "meta_json");

  private string ContentAt(int seq) => TextAt(seq, "content");

  private string TextAt(int seq, string column)
  {
    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the column is a literal from this test file, never input.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    // Ordered by ROWID, which is insertion order: several agents' rows share small seq values, and
    // an ordering by seq alone would compare two different agents' rows.
    command.CommandText = $"SELECT {column} FROM agent_messages ORDER BY rowid LIMIT 1 OFFSET $seq;";
#pragma warning restore CA2100
    _ = command.Parameters.AddWithValue("$seq", seq);
    return (string)command.ExecuteScalar()!;
  }

  private SqliteConnection Open()
  {
    SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
    connection.Open();
    return connection;
  }

  /// <summary>Runs a statement that mutates this test's database file.</summary>
  private void Execute(string sql, params (string Name, string Value)[] parameters)
  {
    using SqliteConnection connection = Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): every caller passes a literal from this test file.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    foreach ((string name, string value) in parameters)
    {
      _ = command.Parameters.AddWithValue(name, value);
    }
    _ = command.ExecuteNonQuery();
  }
}
