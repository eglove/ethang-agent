using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL.Tests;

/// <summary>Restores the pre-V30 messaging tables on a database a migration test is about to
///     rewind.
///     <para>WHY THIS EXISTS: the migration tests seed a fully-migrated database and then
///     stamp an older <c>user_version</c> so the constructor re-runs the migrations above it.
///     That is only faithful if the tables below the stamp really are the OLD shape. V30
///     (issue #316) DROPS <c>mailbox_messages</c> and <c>agent_links</c>, so a current-shaped
///     database no longer resembles what a V14/V15/V16/V17/V19 database carried - V19's
///     <c>ALTER TABLE mailbox_messages ADD COLUMN message_id</c> would fail on the fixture
///     rather than on any real database (measured: 'no such table: mailbox_messages'). Every
///     rewind below V30 therefore restores both tables first, empty, which is exactly what a
///     database that predates the retirement holds after the sweep.</para>
///     <para>THE SHAPES ARE THE V11/V12/V19 ONES VERBATIM, including V19's nullable
///     <c>message_id</c>: V19 only adds the column when it is missing, so a fixture without
///     it would exercise a path no real database takes.</para></summary>
internal static class LegacyMessagingTablesShape
{
  /// <summary>Re-creates <c>mailbox_messages</c> and <c>agent_links</c> in their pre-V30
  ///     shape. Call it before stamping a <c>user_version</c> below 30.</summary>
  public static void Restore(SqliteConnection connection)
  {
    ArgumentNullException.ThrowIfNull(connection);
    // Named decision (CA2100): the SQL is a literal fixture, never user input.
#pragma warning disable CA2100 // Review SQL query for security vulnerabilities
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS mailbox_messages (
            agent_id   TEXT NOT NULL,
            seq        INTEGER NOT NULL,
            sender     TEXT NOT NULL,
            urgency    INTEGER NOT NULL,
            text       TEXT NOT NULL,
            created_at TEXT NOT NULL,
            message_id TEXT NULL,
            PRIMARY KEY (agent_id, seq)
        );
        CREATE TABLE IF NOT EXISTS agent_links (
            workspace_id  TEXT NOT NULL,
            name          TEXT NOT NULL,
            container     TEXT NOT NULL,
            agent_address TEXT NOT NULL,
            created_at    TEXT NOT NULL,
            PRIMARY KEY (workspace_id, name)
        );
        """;
    _ = command.ExecuteNonQuery();
#pragma warning restore CA2100
  }
}
