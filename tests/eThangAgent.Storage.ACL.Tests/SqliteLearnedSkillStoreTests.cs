using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

public sealed class SqliteLearnedSkillStoreTests : IDisposable
{
  private static readonly DateTimeOffset Timestamp
      = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private readonly string _dbPath = Path.Combine(
      Path.GetTempPath(), $"ethang-skills-{Guid.NewGuid():N}.db");
  private readonly AppDatabase _database;
  private readonly SqliteLearnedSkillStore _store;

  public SqliteLearnedSkillStoreTests()
  {
    _database = new AppDatabase(_dbPath);
    _store = new SqliteLearnedSkillStore(_database);
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

  private static SkillDefinition MakeSkill(string name)
      => new(name, $"Description for {name}.", $"Body of {name}.", 1,
          SkillSource.Learned, "session-abc123", Timestamp, Timestamp);

  private long Scalar(string sql)
  {
    using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): test helper runs test-authored constant SQL only.
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities
    command.CommandText = sql;
#pragma warning restore CA2100
    return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
  }

  [Fact]
  public async Task Create_ThenGet_ReturnsEqualDefinition_AllFields()
  {
    SkillDefinition skill = MakeSkill("refactoring");

    Result<SkillDefinition> created = await _store.CreateAsync(skill);

    Assert.True(created.IsSuccess);
    Assert.Equal(skill, created.Value);

    Result<SkillDefinition?> fetched = await _store.GetAsync("refactoring");

    Assert.True(fetched.IsSuccess);
    SkillDefinition? stored = fetched.Value;
    Assert.NotNull(stored);
    Assert.Equal("refactoring", stored.Name);
    Assert.Equal("Description for refactoring.", stored.Description);
    Assert.Equal("Body of refactoring.", stored.Body);
    Assert.Equal(1, stored.Version);
    Assert.Equal(SkillSource.Learned, stored.Source);
    Assert.Equal("session-abc123", stored.ProvenanceSessionId);
    Assert.Equal(Timestamp, stored.CreatedAt);
    Assert.Equal(Timestamp, stored.UpdatedAt);
  }

  [Fact]
  public async Task Create_DuplicateName_FailsWithSkillExists()
  {
    Assert.True((await _store.CreateAsync(MakeSkill("dupe"))).IsSuccess);

    Result<SkillDefinition> again = await _store.CreateAsync(MakeSkill("dupe"));

    Assert.False(again.IsSuccess);
    Assert.Equal("SkillExists", again.Error.Code);
    // The original definition is untouched by the rejected create.
    Result<SkillDefinition?> fetched = await _store.GetAsync("dupe");
    Assert.Equal(1, fetched.Value!.Version);
  }

  [Fact]
  public async Task Get_UnknownName_SucceedsWithNullValue()
  {
    Result<SkillDefinition?> result = await _store.GetAsync("missing");

    Assert.True(result.IsSuccess);
    Assert.Null(result.Value);
  }

  [Fact]
  public async Task Update_WritesNewCurrent_AndHistoryRowAtEachVersion()
  {
    SkillDefinition v1 = MakeSkill("x");
    _ = await _store.CreateAsync(v1);
    SkillDefinition v2 = v1 with
    {
      Description = "Updated description.",
      Body = "Updated body.",
      Version = 2,
      UpdatedAt = Timestamp.AddHours(1),
    };

    Result<SkillDefinition> updated = await _store.UpdateAsync(v2);

    Assert.True(updated.IsSuccess);
    Assert.Equal(v2, updated.Value);

    Result<SkillDefinition?> fetched = await _store.GetAsync("x");
    Assert.True(fetched.IsSuccess);
    Assert.Equal(2, fetched.Value.Version);
    Assert.Equal("Updated body.", fetched.Value.Body);

    // History rows carry each version's authoring time, not the skill's original creation time.
    await using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT version, body, created_at FROM skill_versions WHERE name='x' ORDER BY version;";
#pragma warning disable CA2007 // await using cannot carry ConfigureAwait (disposition type)
    await using SqliteDataReader reader = await command.ExecuteReaderAsync();
#pragma warning restore CA2007
    Assert.True(await reader.ReadAsync().ConfigureAwait(true));
    Assert.Equal(1, reader.GetInt64(0));
    Assert.Equal(v1.Body, reader.GetString(1));
    Assert.Equal(Timestamp, DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
    Assert.True(await reader.ReadAsync().ConfigureAwait(true));
    Assert.Equal(2, reader.GetInt64(0));
    Assert.Equal(v2.Body, reader.GetString(1));
    Assert.Equal(Timestamp.AddHours(1), DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
    Assert.False(await reader.ReadAsync().ConfigureAwait(true));

    Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM skill_versions WHERE name='x';"));
  }

  [Fact]
  public async Task Update_UnknownName_FailsWithSkillNotFound_AndWritesNothing()
  {
    Result<SkillDefinition> result = await _store.UpdateAsync(MakeSkill("ghost") with { Version = 2 });

    Assert.False(result.IsSuccess);
    Assert.Equal("SkillNotFound", result.Error.Code);
    Assert.Null((await _store.GetAsync("ghost")).Value);
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM learned_skills;"));
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM skill_versions;"));
  }

  [Fact]
  public async Task Delete_RemovesCurrentAndHistory_UnknownAndRepeatedDeletesFail()
  {
    _ = await _store.CreateAsync(MakeSkill("doomed"));

    Result<bool> deleted = await _store.DeleteAsync("doomed");

    Assert.True(deleted.IsSuccess);
    Assert.True(deleted.Value);
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM learned_skills WHERE name='doomed';"));
    Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM skill_versions WHERE name='doomed';"));
    Assert.Null((await _store.GetAsync("doomed")).Value);

    Result<bool> neverExisted = await _store.DeleteAsync("never-existed");
    Assert.False(neverExisted.IsSuccess);
    Assert.Equal("SkillNotFound", neverExisted.Error.Code);

    Result<bool> secondDelete = await _store.DeleteAsync("doomed");
    Assert.False(secondDelete.IsSuccess);
    Assert.Equal("SkillNotFound", secondDelete.Error.Code);
  }

  [Fact]
  public async Task List_ReturnsSkillsSortedByName_AndEmptyStoreSucceedsWithEmptyList()
  {
    Result<IReadOnlyList<SkillDefinition>> empty = await _store.ListAsync();
    Assert.True(empty.IsSuccess);
    Assert.Empty(empty.Value);

    _ = await _store.CreateAsync(MakeSkill("zeta"));
    _ = await _store.CreateAsync(MakeSkill("alpha"));
    _ = await _store.CreateAsync(MakeSkill("mid"));

    Result<IReadOnlyList<SkillDefinition>> listed = await _store.ListAsync();

    Assert.True(listed.IsSuccess);
    Assert.Equal(["alpha", "mid", "zeta"], listed.Value.Select(s => s.Name).ToList());
    Assert.All(listed.Value, s => Assert.Equal(SkillSource.Learned, s.Source));
  }

  [Fact]
  public async Task AppendUsage_IncrementsCount_AndRowsSurviveSkillDeletion()
  {
    _ = await _store.CreateAsync(MakeSkill("tdd"));

    Result<int> first = await _store.AppendUsageAsync("tdd", Timestamp);
    Result<int> second = await _store.AppendUsageAsync("tdd", Timestamp.AddMinutes(5));

    Assert.True(first.IsSuccess);
    Assert.Equal(1, first.Value);
    Assert.True(second.IsSuccess);
    Assert.Equal(2, second.Value);

    await using SqliteConnection connection = _database.Open();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT viewed_at FROM skill_usage WHERE skill_name='tdd' ORDER BY id;";
#pragma warning disable CA2007 // await using cannot carry ConfigureAwait (disposition type)
    await using SqliteDataReader reader = await command.ExecuteReaderAsync();
#pragma warning restore CA2007
    Assert.True(await reader.ReadAsync().ConfigureAwait(true));
    Assert.Equal(Timestamp, DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
    Assert.True(await reader.ReadAsync().ConfigureAwait(true));
    Assert.Equal(Timestamp.AddMinutes(5), DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture));
    Assert.False(await reader.ReadAsync().ConfigureAwait(true));

    Result<bool> deleted = await _store.DeleteAsync("tdd");
    Assert.True(deleted.IsSuccess);
    Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM skill_usage WHERE skill_name='tdd';"));
  }

  [Fact]
  public void Migrations_AreIdempotent_ForLearnedSkillTables()
  {
    Assert.Equal(3L, Scalar(
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('learned_skills','skill_versions','skill_usage');"));

    Exception exception = Record.Exception(() => new AppDatabase(_dbPath));
    Assert.Null(exception);
  }

  [Fact]
  public async Task ReopenedDatabase_ServesPreviouslyStoredSkills()
  {
    _ = await _store.CreateAsync(MakeSkill("durable"));

    SqliteLearnedSkillStore reopenedStore = new(new AppDatabase(_dbPath));

    Result<SkillDefinition?> fetched = await reopenedStore.GetAsync("durable");
    Assert.True(fetched.IsSuccess);
    Assert.Equal(MakeSkill("durable"), fetched.Value);
  }
}
