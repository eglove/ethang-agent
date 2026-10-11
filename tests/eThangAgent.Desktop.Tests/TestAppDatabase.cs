using eThangAgent.Storage.ACL;

namespace eThangAgent.Desktop.Tests;

/// <summary>Explicit throwaway storage: composed tests must never fall back to the
/// user's database, even when another test clears ETHANG_AGENT_DB.</summary>
internal sealed class TestAppDatabase : IDisposable
{
  private readonly string _path;

  private TestAppDatabase(string path)
  {
    _path = path;
    Database = new AppDatabase(path);
  }

  public AppDatabase Database { get; }

  public static TestAppDatabase Create() =>
      new(Path.Combine(Path.GetTempPath(), $"ethang-desktop-{Guid.NewGuid():N}.db"));

  public void Dispose()
  {
    GC.SuppressFinalize(this);
#pragma warning disable CA1031, S108 // Best-effort cleanup of disposable test storage.
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
