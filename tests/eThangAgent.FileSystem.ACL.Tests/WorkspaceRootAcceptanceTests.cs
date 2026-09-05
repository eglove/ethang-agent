using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Acceptance gate from the work order: the exact real-world failure shapes
/// must be impossible on the fixed resolver — a workspace root carrying the trailing
/// separator (as desktop folder pickers deliver it) accepts '.', relative subpaths,
/// and absolute-inside paths, and a read through a resolved inside-path returns content.</summary>
public class WorkspaceRootAcceptanceTests
{
  [Fact]
  public async Task ReadFile_WithDotPath_OnTrailingSeparatorRoot_Succeeds()
  {
    DirectoryInfo root = Directory.CreateTempSubdirectory("ethang-accept");
    try
    {
      _ = Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
      await File.WriteAllTextAsync(
          Path.Combine(root.FullName, "src", "hit.cs"), "class Acceptance { }", TestContext.Current.CancellationToken);

      // Production shape: trailing separator, exactly as DesktopHost receives it.
      WorkspacePathResolver resolver = new(root.FullName + Path.DirectorySeparatorChar);
      DirectFileSystemAccess files = new();

      Result<string> resolved = resolver.Resolve(".");
      Assert.True(resolved.IsSuccess, $"resolve('.') failed: {resolved.Error?.Message}");

      Result<string> hitResolved = resolver.Resolve(Path.Combine("src", "hit.cs"));
      Assert.True(hitResolved.IsSuccess, $"resolve('src/hit.cs') failed: {hitResolved.Error?.Message}");

      Result<FileRead> read = await files.ReadLinesAsync(hitResolved.Value, 1, 10, TestContext.Current.CancellationToken);
      Assert.True(read.IsSuccess, $"read failed: {read.Error?.Message}");
      Assert.Contains(read.Value.Lines, l => l.Contains("Acceptance", StringComparison.Ordinal));
    }
    finally
    {
      try
      {
        root.Delete(recursive: true);
      }
#pragma warning disable S108 // Best-effort cleanup — nothing to do on failure.
      catch { }
#pragma warning restore S108
    }
  }
}
