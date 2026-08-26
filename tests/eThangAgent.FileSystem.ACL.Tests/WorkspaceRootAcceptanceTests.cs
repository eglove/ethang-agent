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
/// and absolute-inside paths, and a search over the resolved root returns hits.</summary>
public class WorkspaceRootAcceptanceTests
{
  [Fact]
  public async Task SearchFiles_WithDotPath_OnTrailingSeparatorRoot_Succeeds()
  {
    DirectoryInfo root = Directory.CreateTempSubdirectory("ethang-accept");
    try
    {
      _ = Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
      await File.WriteAllTextAsync(
          Path.Combine(root.FullName, "src", "hit.cs"), "class Acceptance { }");

      // Production shape: trailing separator, exactly as DesktopHost receives it.
      WorkspacePathResolver resolver = new(root.FullName + Path.DirectorySeparatorChar);
      DirectFileSystemAccess files = new();

      Result<string> resolved = resolver.Resolve(".");
      Assert.True(resolved.IsSuccess, $"resolve('.') failed: {resolved.Error?.Message}");

      Result<FileSearch> hits = await files.SearchFilesAsync(resolved.Value!, "Acceptance", regex: false,
          glob: "*.cs", maxResults: 5, contextLines: 0);
      Assert.True(hits.IsSuccess);
      Assert.NotEmpty(hits.Value!.Matches);
    }
    finally
    {
      try
      {
        root.Delete(recursive: true);
      }
      catch { }
    }
  }
}
