using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Read-only inspection seam over the agent's own app database — the store
///     behind sessions, transcripts, state, memories, skills, and preferences.
///     Implementations must run every statement on a read-only connection so no input
///     can mutate the database. SQL passed to <see cref="QueryAsync"/> must already
///     have passed <see cref="ReadOnlySqlValidator.Validate"/>; the read-only
///     connection remains the enforcement backstop.</summary>
public interface ISelfDatabaseAccess
{
  /// <summary>Reports the database structure: schema version, every table and view
  ///     with its columns and indexes. Internal sqlite_* tables and FTS5 shadow
  ///     tables are hidden from the report; row counts appear only when
  ///     <paramref name="includeCounts"/> is true.</summary>
  Task<Result<SelfDatabaseSchema>> DescribeAsync(bool includeCounts, CancellationToken ct = default);

  /// <summary>Runs one read-only query and returns up to <paramref name="maxRows"/>
  ///     rows, reporting whether more existed beyond the cap. Any SQLite failure
  ///     (syntax, unknown object, readonly violation) surfaces as a
  ///     <c>QueryFailed</c> error the caller can show verbatim.</summary>
  Task<Result<SelfQueryResult>> QueryAsync(string sql, int maxRows, CancellationToken ct = default);
}
