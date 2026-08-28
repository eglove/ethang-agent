using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>SQLite persistence for learned skills: current rows, version history,
/// and usage analytics. Global scope — rows are not keyed by workspace.</summary>
public sealed class SqliteLearnedSkillStore(AppDatabase database) : ILearnedSkillStore
{
  private const string StorageError = "StorageError";

  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

  public async Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(skill);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      using (SqliteCommand exists = connection.CreateCommand())
      {
        exists.Transaction = transaction;
        exists.CommandText = "SELECT name FROM learned_skills WHERE name=@n;";
        Add(exists, "@n", skill.Name);
        using SqliteDataReader reader = await exists.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
          return Result.Failure<SkillDefinition>(
              new DomainError("SkillExists", $"A learned skill named '{skill.Name}' already exists."));
        }
      }

      using (SqliteCommand insertCurrent = connection.CreateCommand())
      {
        insertCurrent.Transaction = transaction;
        insertCurrent.CommandText = """
                    INSERT INTO learned_skills (name, description, body, version, provenance_session, created_at, updated_at)
                    VALUES (@n, @d, @b, @v, @p, @c, @u);
                    """;
        FillDefinition(insertCurrent, skill);
        _ = await insertCurrent.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      using (SqliteCommand insertHistory = connection.CreateCommand())
      {
        insertHistory.Transaction = transaction;
        insertHistory.CommandText = """
                    INSERT INTO skill_versions (name, version, description, body, created_at)
                    VALUES (@n, @v, @d, @b, @c);
                    """;
        FillHistory(insertHistory, skill);
        _ = await insertHistory.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(skill);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<SkillDefinition>(new DomainError(StorageError, ex.Message));
    }
  }

  public async Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = $"""{SelectColumns} WHERE name=@n;""";
      Add(command, "@n", name);
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      return Result.Success(
          await reader.ReadAsync(ct).ConfigureAwait(false) ? MapRow(reader) : null);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<SkillDefinition?>(new DomainError(StorageError, ex.Message));
    }
  }

  public async Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = $"""{SelectColumns} ORDER BY name;""";
      List<SkillDefinition> skills = [];
      using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
      while (await reader.ReadAsync(ct).ConfigureAwait(false))
      {
        skills.Add(MapRow(reader));
      }

      return Result.Success<IReadOnlyList<SkillDefinition>>(skills);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<IReadOnlyList<SkillDefinition>>(new DomainError(StorageError, ex.Message));
    }
  }

  public async Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(updated);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      int changed;
      using (SqliteCommand update = connection.CreateCommand())
      {
        update.Transaction = transaction;
        update.CommandText = """
                    UPDATE learned_skills
                    SET description=@d, body=@b, version=@v, provenance_session=@p, created_at=@c, updated_at=@u
                    WHERE name=@n;
                    """;
        ArgumentNullException.ThrowIfNull(updated);
        FillDefinition(update, updated);
        changed = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      if (changed == 0)
      {
        return Result.Failure<SkillDefinition>(
            new DomainError("SkillNotFound", $"No learned skill named '{updated.Name}' exists."));
      }

      using (SqliteCommand insertHistory = connection.CreateCommand())
      {
        insertHistory.Transaction = transaction;
        insertHistory.CommandText = """
                    INSERT INTO skill_versions (name, version, description, body, created_at)
                    VALUES (@n, @v, @d, @b, @c);
                    """;
        FillHistory(insertHistory, updated);
        _ = await insertHistory.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(updated);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<SkillDefinition>(new DomainError(StorageError, ex.Message));
    }
  }

  public async Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
#pragma warning restore CA2007

      int deleted;
      using (SqliteCommand delete = connection.CreateCommand())
      {
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM learned_skills WHERE name=@n;";
        Add(delete, "@n", name);
        deleted = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      if (deleted == 0)
      {
        return Result.Failure<bool>(
            new DomainError("SkillNotFound", $"No learned skill named '{name}' exists."));
      }

      using (SqliteCommand deleteHistory = connection.CreateCommand())
      {
        deleteHistory.Transaction = transaction;
        deleteHistory.CommandText = "DELETE FROM skill_versions WHERE name=@n;";
        Add(deleteHistory, "@n", name);
        _ = await deleteHistory.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      await transaction.CommitAsync(ct).ConfigureAwait(false);
      return Result.Success(true);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<bool>(new DomainError(StorageError, ex.Message));
    }
  }

  public async Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default)
  {
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using SqliteConnection connection = _database.Open();
#pragma warning restore CA2007
      using (SqliteCommand insert = connection.CreateCommand())
      {
        insert.CommandText = "INSERT INTO skill_usage (skill_name, viewed_at) VALUES (@n, @t);";
        Add(insert, "@n", name);
        Add(insert, "@t", viewedAt.ToString("o"));
        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
      }

      using SqliteCommand count = connection.CreateCommand();
      count.CommandText = "SELECT COUNT(*) FROM skill_usage WHERE skill_name=@n;";
      Add(count, "@n", name);
      int total = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
      return Result.Success(total);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      return Result.Failure<int>(new DomainError(StorageError, ex.Message));
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
          DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
          DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));

  private static void Add(SqliteCommand command, string name, object value)
      => command.Parameters.AddWithValue(name, value);
}
