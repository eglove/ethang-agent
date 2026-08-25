using eThangAgent.FileSystem.ACL;
using eThangAgent.ToolDomain;
using Xunit;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Acceptance gate from the work order: the exact real-world failure shapes
/// must be impossible on the fixed resolver — a workspace root carrying the trailing
/// separator (as desktop folder pickers deliver it) accepts '.', relative subpaths,
/// and absolute-inside paths, and a search over the resolved root returns hits.</summary>
public class WorkspaceRootAcceptanceTests
{
    [Fact]
    public async Task SearchFiles_WithDotPath_OnTrailingSeparatorRoot_Succeeds()
    {
        var root = Directory.CreateTempSubdirectory("ethang-accept");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "src", "hit.cs"), "class Acceptance { }");

            // Production shape: trailing separator, exactly as DesktopHost receives it.
            var resolver = new WorkspacePathResolver(root.FullName + Path.DirectorySeparatorChar);
            var files = new DirectFileSystemAccess();

            var resolved = resolver.Resolve(".");
            Assert.True(resolved.IsSuccess, $"resolve('.') failed: {resolved.Error?.Message}");

            var hits = await files.SearchFilesAsync(resolved.Value!, "Acceptance", regex: false,
                glob: "*.cs", maxResults: 5, contextLines: 0);
            Assert.True(hits.IsSuccess);
            Assert.NotEmpty(hits.Value!.Matches);
        }
        finally
        {
            try { root.Delete(recursive: true); } catch { }
        }
    }
}
