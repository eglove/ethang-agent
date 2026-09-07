using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Read-only inspection seam over ARBITRARY SQLite files inside the
///     workspace — the general sibling of <see cref="ISelfDatabaseAccess"/>, which
///     covers only the agent's own app database. Implementations must run every
///     statement on a read-only connection so no input can mutate any file. SQL passed
///     to <see cref="QueryAsync"/> must already have passed
///     <see cref="ReadOnlySqlValidator.Validate"/>; the read-only connection remains
///     the enforcement backstop. A missing or non-SQLite file surfaces as a
///     <c>QueryFailed</c> error the caller can show verbatim — never an exception.</summary>
public interface ISqliteFileAccess
{
  /// <summary>Runs one read-only query against the SQLite file at
  ///     <paramref name="path"/> (an absolute, resolver-resolved path) and returns up to
  ///     <paramref name="maxRows"/> rows, reporting whether more existed beyond the
  ///     cap. The connection must be opened read-only so a missing file is never
  ///     created and no write can land.</summary>
  Task<Result<SelfQueryResult>> QueryAsync(string path, string sql, int maxRows, CancellationToken ct = default);
}
