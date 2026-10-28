using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Restores the pre-V24 <c>plan_steps</c> shape on a database a migration test is
///     about to rewind.
///     <para>WHY THIS EXISTS: the migration tests seed a fully-migrated database and then
///     stamp an older <c>user_version</c> so the constructor re-runs the migrations above
///     it. That is only faithful if the tables below the stamp really are the OLD shape.
///     V24 (issue #310) drops <c>todo_id</c>, so a V24-shaped table no longer resembles what
///     a V14 or V19 database carried - V20's rebuild copies that column and would fail on
///     the fixture rather than on any real database. Every rewind below V24 therefore
///     restores the column first, with NULL values (nothing in this build can write one),
///     which is exactly what a database that never had a todo link holds.</para>
///     <para>The CHECK constraints are deliberately left to the migration under test: V20
///     rebuilds the table with its own, so restoring them here would hide a regression in
///     that rebuild.</para></summary>
internal static class LegacyPlanStepsShape
{
  /// <summary>Re-adds <c>todo_id</c> to <c>plan_steps</c>, preserving every row, id and
  ///     position. Call it before stamping a <c>user_version</c> below 24.</summary>
  public static void Restore(SqliteConnection connection)
  {
    ArgumentNullException.ThrowIfNull(connection);
    // Named decision (CA2100): the SQL is a literal fixture, never user input.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE plan_steps_pre_v24 (
            plan_id INTEGER NOT NULL REFERENCES plans(id),
            position INTEGER NOT NULL,
            title TEXT NOT NULL,
            detail TEXT,
            todo_id INTEGER,
            status TEXT NOT NULL,
            PRIMARY KEY (plan_id, position)
        );
        INSERT INTO plan_steps_pre_v24 (plan_id, position, title, detail, todo_id, status)
            SELECT plan_id, position, title, detail, NULL, status FROM plan_steps;
        DROP TABLE plan_steps;
        ALTER TABLE plan_steps_pre_v24 RENAME TO plan_steps;
        """;
    _ = command.ExecuteNonQuery();
#pragma warning restore CA2100
  }
}
