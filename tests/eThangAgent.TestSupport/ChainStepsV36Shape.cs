using Microsoft.Data.Sqlite;

namespace eThangAgent.TestSupport;

/// <summary>Puts a migrated database's <c>chain_steps</c> back into its V36 shape, with the two
///     model columns <c>role_model</c> and <c>served_model</c> present.
///     <para>A FIXTURE THAT STAMPS A VERSION BELOW V37 MUST START FROM A DATABASE THAT ACTUALLY
///     SHIPPED AT THAT VERSION. A <see cref="TemplateDatabase.Copy"/> is already at the latest
///     version, so a pre-V37 fixture that rebuilds the table from its own SELECT list would
///     reference a column the copy no longer has. Restoring the shape first makes the fixture
///     reproduce history rather than the present.</para></summary>
public static class ChainStepsV36Shape
{
  /// <summary>Adds the two model columns back and stamps user_version 36. Idempotent: a
  ///     database that already carries the columns is left alone.</summary>
  /// <param name="databasePath">The database file to restore.</param>
  public static void Restore(string databasePath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
    using SqliteConnection connection = new($"Data Source={databasePath}");
    connection.Open();
    using SqliteCommand command = connection.CreateCommand();
    // Named decision (CA2100): the SQL is a literal fixture, not user input.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    command.CommandText = """
        PRAGMA foreign_keys = OFF;
        CREATE TABLE chain_steps_v36 (
            chain_id INTEGER NOT NULL REFERENCES chains(id),
            position INTEGER NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('Pending', 'Running', 'Done', 'Failed', 'Skipped', 'Parked')),
            session_id TEXT,
            attempts INTEGER NOT NULL,
            outcome TEXT,
            actor TEXT NOT NULL,
            claimed_at TEXT,
            completed_at TEXT,
            role TEXT NOT NULL DEFAULT 'Implement' CHECK (role IN ('Implement', 'Review')),
            role_model TEXT,
            served_model TEXT,
            review_attempts INTEGER NOT NULL DEFAULT 0 CHECK (review_attempts >= 0),
            invalidated_by TEXT,
            group_id INTEGER,
            worktree_path TEXT,
            worktree_branch TEXT,
            PRIMARY KEY (chain_id, position)
        );
        INSERT INTO chain_steps_v36 (chain_id, position, status, session_id, attempts, outcome,
            actor, claimed_at, completed_at, role, role_model, served_model, review_attempts,
            invalidated_by, group_id, worktree_path, worktree_branch)
            SELECT chain_id, position, status, session_id, attempts, outcome, actor, claimed_at,
                completed_at, role, NULL, NULL, review_attempts, invalidated_by, group_id,
                worktree_path, worktree_branch FROM chain_steps;
        DROP TABLE chain_steps;
        ALTER TABLE chain_steps_v36 RENAME TO chain_steps;
        PRAGMA user_version = 36;
        PRAGMA foreign_keys = ON;
        """;
#pragma warning restore CA2100
    _ = command.ExecuteNonQuery();
  }
}
