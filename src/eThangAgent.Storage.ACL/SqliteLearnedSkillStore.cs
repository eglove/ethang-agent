using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite persistence for learned skills: current rows, version history,
/// and usage analytics. Global scope — rows are not keyed by workspace.</summary>
public sealed class SqliteLearnedSkillStore : ILearnedSkillStore
{
    private readonly AppDatabase _database;

    public SqliteLearnedSkillStore(AppDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

            using (var exists = connection.CreateCommand())
            {
                exists.Transaction = transaction;
                exists.CommandText = "SELECT name FROM learned_skills WHERE name=@n;";
                Add(exists, "@n", skill.Name);
                using var reader = await exists.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    return Result<SkillDefinition>.Failure(
                        new Error("SkillExists", $"A learned skill named '{skill.Name}' already exists."));
            }

            using (var insertCurrent = connection.CreateCommand())
            {
                insertCurrent.Transaction = transaction;
                insertCurrent.CommandText = """
                    INSERT INTO learned_skills (name, description, body, version, provenance_session, created_at, updated_at)
                    VALUES (@n, @d, @b, @v, @p, @c, @u);
                    """;
                FillDefinition(insertCurrent, skill);
                await insertCurrent.ExecuteNonQueryAsync(ct);
            }

            using (var insertHistory = connection.CreateCommand())
            {
                insertHistory.Transaction = transaction;
                insertHistory.CommandText = """
                    INSERT INTO skill_versions (name, version, description, body, created_at)
                    VALUES (@n, @v, @d, @b, @c);
                    """;
                FillHistory(insertHistory, skill);
                await insertHistory.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return Result<SkillDefinition>.Success(skill);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<SkillDefinition>.Failure(new Error("StorageError", ex.Message));
        }
    }

    public async Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""{SelectColumns} WHERE name=@n;""";
            Add(command, "@n", name);
            using var reader = await command.ExecuteReaderAsync(ct);
            return Result<SkillDefinition?>.Success(
                await reader.ReadAsync(ct) ? MapRow(reader) : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<SkillDefinition?>.Failure(new Error("StorageError", ex.Message));
        }
    }

    public async Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""{SelectColumns} ORDER BY name;""";
            var skills = new List<SkillDefinition>();
            using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                skills.Add(MapRow(reader));
            return Result<IReadOnlyList<SkillDefinition>>.Success(skills);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<IReadOnlyList<SkillDefinition>>.Failure(new Error("StorageError", ex.Message));
        }
    }

    public async Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

            int changed;
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE learned_skills
                    SET description=@d, body=@b, version=@v, provenance_session=@p, created_at=@c, updated_at=@u
                    WHERE name=@n;
                    """;
                FillDefinition(update, updated);
                changed = await update.ExecuteNonQueryAsync(ct);
            }

            if (changed == 0)
                return Result<SkillDefinition>.Failure(
                    new Error("SkillNotFound", $"No learned skill named '{updated.Name}' exists."));

            using (var insertHistory = connection.CreateCommand())
            {
                insertHistory.Transaction = transaction;
                insertHistory.CommandText = """
                    INSERT INTO skill_versions (name, version, description, body, created_at)
                    VALUES (@n, @v, @d, @b, @c);
                    """;
                FillHistory(insertHistory, updated);
                await insertHistory.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return Result<SkillDefinition>.Success(updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<SkillDefinition>.Failure(new Error("StorageError", ex.Message));
        }
    }

    public async Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

            int deleted;
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM learned_skills WHERE name=@n;";
                Add(delete, "@n", name);
                deleted = await delete.ExecuteNonQueryAsync(ct);
            }

            if (deleted == 0)
                return Result<bool>.Failure(
                    new Error("SkillNotFound", $"No learned skill named '{name}' exists."));

            using (var deleteHistory = connection.CreateCommand())
            {
                deleteHistory.Transaction = transaction;
                deleteHistory.CommandText = "DELETE FROM skill_versions WHERE name=@n;";
                Add(deleteHistory, "@n", name);
                await deleteHistory.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return Result<bool>.Success(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<bool>.Failure(new Error("StorageError", ex.Message));
        }
    }

    public async Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default)
    {
        try
        {
            await using var connection = _database.Open();
            using (var insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO skill_usage (skill_name, viewed_at) VALUES (@n, @t);";
                Add(insert, "@n", name);
                Add(insert, "@t", viewedAt.ToString("o"));
                await insert.ExecuteNonQueryAsync(ct);
            }

            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM skill_usage WHERE skill_name=@n;";
            Add(count, "@n", name);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            return Result<int>.Success(total);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<int>.Failure(new Error("StorageError", ex.Message));
        }
    }

    private const string SelectColumns = """
        SELECT name, description, body, version, provenance_session, created_at, updated_at
        FROM learned_skills
        """;

    private static void FillDefinition(SqliteCommand command, SkillDefinition skill)
    {
        Add(command, "@n", skill.Name);
        Add(command, "@d", skill.Description);
        Add(command, "@b", skill.Body);
        Add(command, "@v", skill.Version);
        Add(command, "@p", (object?)skill.ProvenanceSessionId ?? DBNull.Value);
        Add(command, "@c", skill.CreatedAt.ToString("o"));
        Add(command, "@u", skill.UpdatedAt.ToString("o"));
    }

    private static void FillHistory(SqliteCommand command, SkillDefinition skill)
    {
        Add(command, "@n", skill.Name);
        Add(command, "@v", skill.Version);
        Add(command, "@d", skill.Description);
        Add(command, "@b", skill.Body);
        // History rows carry the version's authoring time (UpdatedAt), which for a
        // fresh create equals CreatedAt; current-row created_at semantics are separate.
        Add(command, "@c", skill.UpdatedAt.ToString("o"));
    }

    private static SkillDefinition MapRow(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            SkillSource.Learned,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            DateTimeOffset.Parse(reader.GetString(6)));

    private static void Add(SqliteCommand command, string name, object value)
        => command.Parameters.AddWithValue(name, value);
}
