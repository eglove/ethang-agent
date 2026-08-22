using eThangAgent.SkillDomain;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Storage.ACL.Tests;

public class SqliteLearnedSkillStoreTests : IDisposable
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
        try { File.Delete(_dbPath); } catch { }
    }

    private static SkillDefinition MakeSkill(string name)
        => new(name, $"Description for {name}.", $"Body of {name}.", 1,
            SkillSource.Learned, "session-abc123", Timestamp, Timestamp);

    private long Scalar(string sql)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public async Task Create_ThenGet_ReturnsEqualDefinition_AllFields()
    {
        var skill = MakeSkill("refactoring");

        var created = await _store.CreateAsync(skill);

        Assert.True(created.IsSuccess);
        Assert.Equal(skill, created.Value);

        var fetched = await _store.GetAsync("refactoring");

        Assert.True(fetched.IsSuccess);
        var stored = fetched.Value;
        Assert.NotNull(stored);
        Assert.Equal("refactoring", stored!.Name);
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

        var again = await _store.CreateAsync(MakeSkill("dupe"));

        Assert.False(again.IsSuccess);
        Assert.Equal("SkillExists", again.Error!.Code);
        // The original definition is untouched by the rejected create.
        var fetched = await _store.GetAsync("dupe");
        Assert.Equal(1, fetched.Value!.Version);
    }

    [Fact]
    public async Task Get_UnknownName_SucceedsWithNullValue()
    {
        var result = await _store.GetAsync("missing");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Update_WritesNewCurrent_AndHistoryRowAtEachVersion()
    {
        var v1 = MakeSkill("x");
        await _store.CreateAsync(v1);
        var v2 = v1 with
        {
            Description = "Updated description.",
            Body = "Updated body.",
            Version = 2,
            UpdatedAt = Timestamp.AddHours(1),
        };

        var updated = await _store.UpdateAsync(v2);

        Assert.True(updated.IsSuccess);
        Assert.Equal(v2, updated.Value);

        var fetched = await _store.GetAsync("x");
        Assert.True(fetched.IsSuccess);
        Assert.Equal(2, fetched.Value!.Version);
        Assert.Equal("Updated body.", fetched.Value.Body);

        // History rows carry each version's authoring time, not the skill's original creation time.
        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, body, created_at FROM skill_versions WHERE name='x' ORDER BY version;";
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(v1.Body, reader.GetString(1));
        Assert.Equal(Timestamp, DateTimeOffset.Parse(reader.GetString(2)));
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(v2.Body, reader.GetString(1));
        Assert.Equal(Timestamp.AddHours(1), DateTimeOffset.Parse(reader.GetString(2)));
        Assert.False(reader.Read());

        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM skill_versions WHERE name='x';"));
    }

    [Fact]
    public async Task Update_UnknownName_FailsWithSkillNotFound_AndWritesNothing()
    {
        var result = await _store.UpdateAsync(MakeSkill("ghost") with { Version = 2 });

        Assert.False(result.IsSuccess);
        Assert.Equal("SkillNotFound", result.Error!.Code);
        Assert.Null((await _store.GetAsync("ghost")).Value);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM learned_skills;"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM skill_versions;"));
    }

    [Fact]
    public async Task Delete_RemovesCurrentAndHistory_UnknownAndRepeatedDeletesFail()
    {
        await _store.CreateAsync(MakeSkill("doomed"));

        var deleted = await _store.DeleteAsync("doomed");

        Assert.True(deleted.IsSuccess);
        Assert.True(deleted.Value);
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM learned_skills WHERE name='doomed';"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM skill_versions WHERE name='doomed';"));
        Assert.Null((await _store.GetAsync("doomed")).Value);

        var neverExisted = await _store.DeleteAsync("never-existed");
        Assert.False(neverExisted.IsSuccess);
        Assert.Equal("SkillNotFound", neverExisted.Error!.Code);

        var secondDelete = await _store.DeleteAsync("doomed");
        Assert.False(secondDelete.IsSuccess);
        Assert.Equal("SkillNotFound", secondDelete.Error!.Code);
    }

    [Fact]
    public async Task List_ReturnsSkillsSortedByName_AndEmptyStoreSucceedsWithEmptyList()
    {
        var empty = await _store.ListAsync();
        Assert.True(empty.IsSuccess);
        Assert.Empty(empty.Value!);

        await _store.CreateAsync(MakeSkill("zeta"));
        await _store.CreateAsync(MakeSkill("alpha"));
        await _store.CreateAsync(MakeSkill("mid"));

        var listed = await _store.ListAsync();

        Assert.True(listed.IsSuccess);
        Assert.Equal(new[] { "alpha", "mid", "zeta" }, listed.Value!.Select(s => s.Name).ToArray());
        Assert.All(listed.Value!, s => Assert.Equal(SkillSource.Learned, s.Source));
    }

    [Fact]
    public async Task AppendUsage_IncrementsCount_AndRowsSurviveSkillDeletion()
    {
        await _store.CreateAsync(MakeSkill("tdd"));

        var first = await _store.AppendUsageAsync("tdd", Timestamp);
        var second = await _store.AppendUsageAsync("tdd", Timestamp.AddMinutes(5));

        Assert.True(first.IsSuccess);
        Assert.Equal(1, first.Value);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, second.Value);

        await using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT viewed_at FROM skill_usage WHERE skill_name='tdd' ORDER BY id;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(Timestamp, DateTimeOffset.Parse(reader.GetString(0)));
        Assert.True(reader.Read());
        Assert.Equal(Timestamp.AddMinutes(5), DateTimeOffset.Parse(reader.GetString(0)));
        Assert.False(reader.Read());

        var deleted = await _store.DeleteAsync("tdd");
        Assert.True(deleted.IsSuccess);
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM skill_usage WHERE skill_name='tdd';"));
    }

    [Fact]
    public void Migrations_AreIdempotent_ForLearnedSkillTables()
    {
        Assert.Equal(3L, Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('learned_skills','skill_versions','skill_usage');"));

        var exception = Record.Exception(() => new AppDatabase(_dbPath));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ReopenedDatabase_ServesPreviouslyStoredSkills()
    {
        await _store.CreateAsync(MakeSkill("durable"));

        var reopenedStore = new SqliteLearnedSkillStore(new AppDatabase(_dbPath));

        var fetched = await reopenedStore.GetAsync("durable");
        Assert.True(fetched.IsSuccess);
        Assert.Equal(MakeSkill("durable"), fetched.Value);
    }
}
