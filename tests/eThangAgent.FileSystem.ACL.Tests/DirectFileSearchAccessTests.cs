
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Integration: the real DirectFileSearchAccess over real files — glob matching,
///     exclusion pruning, and the result cap.</summary>
public sealed class DirectFileSearchAccessTests : IDisposable
{
  private readonly string _root = Path.Combine(Path.GetTempPath(), "ethang-dfsa-" + Guid.NewGuid().ToString("N"));

  public DirectFileSearchAccessTests() => _ = Directory.CreateDirectory(_root);

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, true);
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
  public async Task Recursive_Glob_FindsMatches_RelativeToNothing_AbsolutePaths()
  {
    _ = Directory.CreateDirectory(Path.Combine(_root, "src", "nested"));
    await File.WriteAllTextAsync(Path.Combine(_root, "src", "App.cs"), "x", TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(Path.Combine(_root, "src", "nested", "App.Tests.cs"), "x", TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(Path.Combine(_root, "readme.md"), "x", TestContext.Current.CancellationToken);

    DirectFileSearchAccess search = new();
    Result<IReadOnlyList<string>> result = await search.EnumerateFilesAsync(_root, "App*", recurse: true, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(2, result.Value.Count);
  }

  [Fact]
  public async Task ExcludedDirectories_NeverSearched()
  {
    _ = Directory.CreateDirectory(Path.Combine(_root, "src"));
    _ = Directory.CreateDirectory(Path.Combine(_root, "bin", "debug"));
    _ = Directory.CreateDirectory(Path.Combine(_root, ".git", "hooks"));
    await File.WriteAllTextAsync(Path.Combine(_root, "src", "Keep.cs"), "x", TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(Path.Combine(_root, "bin", "debug", "Skip.cs"), "x", TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(Path.Combine(_root, ".git", "hooks", "Skip2.cs"), "x", TestContext.Current.CancellationToken);

    DirectFileSearchAccess search = new();
    Result<IReadOnlyList<string>> result = await search.EnumerateFilesAsync(_root, "*.cs", recurse: true, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    _ = Assert.Single(result.Value);
    Assert.Contains("Keep.cs", result.Value[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task ResultCap_BoundsTheMatches()
  {
    _ = Directory.CreateDirectory(_root);
    for (int i = 0; i < 10; i++)
    {
      await File.WriteAllTextAsync(Path.Combine(_root, $"f{i}.txt"), "x", TestContext.Current.CancellationToken);
    }

    DirectFileSearchAccess search = new(resultCap: 4);
    Result<IReadOnlyList<string>> result = await search.EnumerateFilesAsync(_root, "*.txt", recurse: true, TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.Equal(4, result.Value.Count);
  }

  [Fact]
  public async Task MissingRoot_Fails()
  {
    DirectFileSearchAccess search = new();
    Result<IReadOnlyList<string>> result = await search.EnumerateFilesAsync(
        Path.Combine(_root, "does-not-exist"), "*", recurse: true, TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("DirectoryNotFound", result.Error.Code);
  }
}
