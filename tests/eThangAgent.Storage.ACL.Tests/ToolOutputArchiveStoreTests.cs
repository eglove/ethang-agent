using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>The V15 tool_output_archive store: migration, content-addressed dedup,
///     workspace isolation, excerpt rendering, paged read-back, and FTS5
///     searchability (lexical memory recall finds archived content after it left
///     the context).</summary>
public sealed class ToolOutputArchiveStoreTests : IDisposable
{
  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-arch-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqliteToolOutputArchive _archive;

  public ToolOutputArchiveStoreTests()
  {
    _database = new AppDatabase(_dbPath);
    _archive = new SqliteToolOutputArchive(_database, "C:\\ws\\alpha");
  }

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

  // ---- Migration V15 ----

  [Fact]
  public void FreshDatabase_MigratesToVersion15_WithArchiveTableAndFts()
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand version = connection.CreateCommand();
    version.CommandText = "PRAGMA user_version;";
    Assert.Equal(15, Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture));
    Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tool_output_archive';"));
    Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tool_output_archive_fts';"));
    Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('tool_output_ai','tool_output_ad','tool_output_au');"));
  }

  // ---- Archive + dedup ----

  [Fact]
  public async Task Archive_StoresContent_AndReturnsStableHandle()
  {
    string content = Repeat('x', 10_000);

    Result<string> first = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);
    Result<string> second = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.Equal(first.Value, second.Value);
    Assert.StartsWith(ToolOutputArchiveFormat.HandlePrefix, first.Value, StringComparison.Ordinal);
    Assert.True(ToolOutputArchiveFormat.IsWellFormedHandle(first.Value));
    // Dedup: identical content stored once.
    Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM tool_output_archive;"));
  }

  [Fact]
  public async Task Archive_DistinctContent_DistinctHandles()
  {
    Result<string> a = await _archive.ArchiveAsync(Repeat('a', 10_000), TestContext.Current.CancellationToken);
    Result<string> b = await _archive.ArchiveAsync(Repeat('b', 10_000), TestContext.Current.CancellationToken);

    Assert.NotEqual(a.Value, b.Value);
    Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM tool_output_archive;"));
  }

  [Fact]
  public async Task Archive_EmptyContent_FailsTyped()
  {
    Result<string> r = await _archive.ArchiveAsync("", TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
  }

  // ---- Workspace isolation ----

  [Fact]
  public async Task ReadBack_OtherWorkspace_HandleFailsNotFound()
  {
    string content = Repeat('x', 10_000);
    Result<string> archived = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);

    SqliteToolOutputArchive other = new(_database, "C:\\ws\\beta");
    Result<ArchivePage> cross = await other.ReadBackAsync(archived.Value!, 0, 100, TestContext.Current.CancellationToken);

    Assert.False(cross.IsSuccess);
    Assert.Equal("ArchiveNotFound", cross.Error.Code);
  }

  // ---- Excerpt ----

  [Fact]
  public void Excerpt_LargeContent_CarriesMarkerHeadTail()
  {
    string content = Repeat('h', 5000) + Repeat('m', 8000) + Repeat('t', 2000);

    string excerpt = _archive.Excerpt(content, 4000, 1000);

    Assert.StartsWith("[tool-output archived: arch:", excerpt, StringComparison.Ordinal);
    Assert.Contains("10000 chars omitted | read back with the tool_output_read tool]", excerpt, StringComparison.Ordinal);
    Assert.Equal(content[..4000], excerpt.Split('\n')[1][..4000]);
    Assert.EndsWith(content[^1000..], excerpt, StringComparison.Ordinal);
    Assert.Contains(ToolOutputArchiveFormat.Separator, excerpt, StringComparison.Ordinal);
    Assert.Contains(ToolOutputArchiveFormat.Separator, excerpt, StringComparison.Ordinal);
  }

  [Fact]
  public void Excerpt_SmallContent_PassesThroughUnchanged()
  {
    string content = "short result";

    string excerpt = _archive.Excerpt(content, 4000, 1000);

    Assert.Equal(content, excerpt);
  }

  // ---- Read-back paging ----

  [Fact]
  public async Task ReadBack_FirstPage_CarriesGutterFacts()
  {
    string content = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line-{i:000}"));
    Result<string> archived = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);

    Result<ArchivePage> page = await _archive.ReadBackAsync(archived.Value!, 0, 1000, TestContext.Current.CancellationToken);

    Assert.True(page.IsSuccess, page.Error?.Message);
    Assert.Equal(archived.Value, page.Value.Handle);
    Assert.Equal(content.Length, page.Value.TotalChars);
    Assert.Equal(0, page.Value.Offset);
    Assert.Equal(1000, page.Value.Text.Length);
    Assert.Equal(1, page.Value.StartLine);
    Assert.True(page.Value.HasMore);
    Assert.Equal(content[..1000], page.Value.Text);
  }

  [Fact]
  public async Task ReadBack_SecondPage_ContinuesExactly()
  {
    string content = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line-{i:000}"));
    Result<string> archived = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);

    Result<ArchivePage> first = await _archive.ReadBackAsync(archived.Value!, 0, 1000, TestContext.Current.CancellationToken);
    Result<ArchivePage> second = await _archive.ReadBackAsync(archived.Value!, 1000, 1000, TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess, first.Error?.Message);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal(content.Substring(1000, 1000), second.Value.Text);
    Assert.Equal(1000, second.Value.Offset);
    Assert.Equal(112, second.Value.StartLine); // the 1000-char boundary splits a line
    Assert.Equal(first.Value.EndLine, second.Value.StartLine);
  }

  [Fact]
  public async Task ReadBack_LastPage_ReportsNoMore()
  {
    string content = Repeat('x', 2500);
    Result<string> archived = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);

    Result<ArchivePage> last = await _archive.ReadBackAsync(archived.Value!, 2000, 1000, TestContext.Current.CancellationToken);

    Assert.True(last.IsSuccess, last.Error?.Message);
    Assert.Equal(500, last.Value.Text.Length);
    Assert.False(last.Value.HasMore);
  }

  [Fact]
  public async Task ReadBack_OffsetAtOrBeyondLength_FailsTyped()
  {
    Result<string> archived = await _archive.ArchiveAsync("abc", TestContext.Current.CancellationToken);

    Result<ArchivePage> r = await _archive.ReadBackAsync(archived.Value!, 3, 100, TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
  }

  [Fact]
  public async Task ReadBack_UnknownHandle_FailsArchiveNotFound()
  {
    Result<ArchivePage> r = await _archive.ReadBackAsync("arch:0000000000000000", 0, 100, TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("ArchiveNotFound", r.Error.Code);
  }

  [Fact]
  public async Task ReadBack_MalformedHandle_FailsTyped()
  {
    Result<ArchivePage> r = await _archive.ReadBackAsync("not-a-handle", 0, 100, TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
  }

  // ---- FTS5 searchability ----

  [Fact]
  public async Task ArchivedContent_IsLexicallySearchable_ViaFts()
  {
    string needle = "quartz tuning fork calibration table";
    string content = Repeat('p', 7000) + "\n" + needle + "\n" + Repeat('q', 7000);
    _ = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);

    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
            SELECT COUNT(*) FROM tool_output_archive_fts
            WHERE tool_output_archive_fts MATCH @q;
            """;
    _ = command.Parameters.AddWithValue("@q", "quartz");
    Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture));
  }

  /// <summary>Repeats a character. A helper, not inline <c>new(c, n)</c>: the
  ///     target-typed form trips IDE0090 inside typed local declarations.</summary>
  private static string Repeat(char c, int count) => new(c, count);

  private long Scalar(string sql)
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): test helper runs test-authored constant SQL only.
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100 // Review SQL queries for security vulnerabilities
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }
}
