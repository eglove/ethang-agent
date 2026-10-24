using System.Globalization;
using Microsoft.Data.Sqlite;

namespace eThangAgent.Storage.ACL;

/// <summary>Reads SQLite's <c>PRAGMA data_version</c> over the app database (issue #8).
///     <para>THE CONNECTION MUST PERSIST. data_version is a PER-CONNECTION counter seeded
///     when the connection opens: a fresh connection always reports 1, so opening one per
///     read (the obvious implementation) would never detect a change at all - it would
///     silently report "nothing happened" forever. This class therefore holds one
///     connection for its lifetime and disposes it with itself.</para>
///     <para>Because the connection is the signal's OWN, it moves on any commit by any
///     OTHER connection - the ChildHost's writes (what it exists for) and this process's
///     own store writes alike. That is deliberate: the in-process event sources make
///     those refreshes earlier, and a redundant re-read of a small table is cheap, while
///     trying to distinguish "my process" from "the host" would need a second signal
///     that can only ever under-report.</para>
///     <para>A storage fault reads as minus one, never an exception: a view that cannot
///     probe must keep showing the last state it loaded rather than blank itself.</para>
///     <para>Dispose this with the view that owns it - an undisposed signal pins a
///     database connection for the process lifetime.</para></summary>
public sealed class SqliteChangeSignal(AppDatabase database) : IChangeSignal, IDisposable
{
  private readonly AppDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
  private readonly Lock _gate = new();
  private SqliteConnection? _connection;
  private bool _disposed;

  public long Read()
  {
    lock (_gate)
    {
      if (_disposed)
      {
        return -1;
      }

      // Named decision (CA1031): a probe failure is reported as no-change, never a
      // crash - the caller's job is to render state, not to report storage health.
#pragma warning disable CA1031 // Do not catch general exception types
      try
      {
        _connection ??= _database.Open();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "PRAGMA data_version;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
      }
      catch (SqliteException)
      {
        // A broken connection is dropped so the next read reopens; otherwise one fault
        // would poison every later read with the same failure.
        _connection?.Dispose();
        _connection = null;
        return -1;
      }
#pragma warning restore CA1031
    }
  }

  public void Dispose()
  {
    lock (_gate)
    {
      if (_disposed)
      {
        return;
      }

      _disposed = true;
      _connection?.Dispose();
      _connection = null;
    }
  }
}
