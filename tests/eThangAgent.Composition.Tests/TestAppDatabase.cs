using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>A throwaway app database per test (a temp file, deleted on dispose):
///     composition tests must never open - and never migrate - the user's real
///     database. The V16 migration exposed the habit: a user database stamped past
///     an old schema version made every container build fail. Tests pass Database
///     to AddEThangAgentCore's database parameter.</summary>
internal sealed class TestAppDatabase : IDisposable
{
  private readonly string _path;

  private TestAppDatabase(string path, AppDatabase database)
  {
    _path = path;
    Database = database;
  }

  public AppDatabase Database { get; }

  public static TestAppDatabase Create()
  {
    string path = Path.Combine(Path.GetTempPath(), $"ethang-comp-{Guid.NewGuid():N}.db");
    return new TestAppDatabase(path, new AppDatabase(path));
  }

  public void Dispose()
  {
    GC.SuppressFinalize(this);
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_path);
    }
    catch
    {
    }
#pragma warning restore CA1031, S108
  }
}
