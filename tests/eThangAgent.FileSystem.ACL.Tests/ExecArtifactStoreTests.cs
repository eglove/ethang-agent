namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

public sealed class ExecArtifactStoreTests : IDisposable
{
  private readonly string _dir = Path.Combine(
      Path.GetTempPath(), $"ethang-artifacts-{Guid.NewGuid():N}");

  public void Dispose()
  {
    try
    {
      Directory.Delete(_dir, recursive: true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task WriteAsync_CreatesDirectory_WritesContent_ReturnsPath()
  {
    ExecArtifactStore store = new(_dir);

    string path = await store.WriteAsync("full output text");

    Assert.StartsWith(_dir, path, StringComparison.Ordinal);
    Assert.True(File.Exists(path));
    Assert.Equal("full output text", await File.ReadAllTextAsync(path));
  }

  [Fact]
  public async Task WriteAsync_Twice_ReturnsDistinctPaths()
  {
    ExecArtifactStore store = new(_dir);

    string first = await store.WriteAsync("one");
    string second = await store.WriteAsync("two");

    Assert.NotEqual(first, second);
  }
}
