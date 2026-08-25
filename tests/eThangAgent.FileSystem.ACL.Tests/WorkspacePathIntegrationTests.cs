using eThangAgent.FileSystem.ACL;
using eThangAgent.ToolDomain;
using Xunit;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Integration coverage for the P0 path-validation false positives observed in
/// real use: tools sharing one WorkspacePathResolver rejected paths INSIDE the workspace
/// when the root carried a trailing separator (folder-picker roots do). These tests drive
/// the real resolver with the real file ACL — no fakes — over a temp workspace.</summary>
public sealed class WorkspacePathIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-wsint").FullName;

    private (WorkspacePathResolver Resolver, DirectFileSystemAccess Files) Make()
    {
        // The production shape: the desktop folder picker hands the host a root that may
        // end in a directory separator; the resolver must normalize it away.
        return (new(_root + Path.DirectorySeparatorChar), new());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task SearchOverWorkspaceRoot_ReturnsHits()
    {
        var (resolver, files) = Make();
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "code.cs"), "class Marker { }");

        var resolved = resolver.Resolve(".");
        Assert.True(resolved.IsSuccess, $"root '.' rejected: {resolved.Error?.Message}");

        var hits = await files.SearchFilesAsync(resolved.Value!, "Marker", regex: false,
            glob: "*.cs", maxResults: 10, contextLines: 0);
        Assert.True(hits.IsSuccess, $"search failed: {hits.Error?.Message}");
        Assert.Contains(hits.Value!.Matches, m => m.Path.Contains("code.cs"));
    }

    [Fact]
    public async Task WriteEditRoundtripUnderDocs_Succeeds()
    {
        var (resolver, files) = Make();
        Directory.CreateDirectory(Path.Combine(_root, "docs"));

        var docPath = resolver.Resolve(Path.Combine("docs", "note.md"));
        Assert.True(docPath.IsSuccess, $"docs/note.md rejected: {docPath.Error?.Message}");

        var written = await files.WriteFileAsync(docPath.Value!, "hello", overwrite: false);
        Assert.True(written.IsSuccess, $"write failed: {written.Error?.Message}");

        var edited = await files.ReplaceInFileAsync(docPath.Value!, "hello", "hello again",
            occurrences: 1);
        Assert.True(edited.IsSuccess, $"edit failed: {edited.Error?.Message}");

        Assert.Contains("hello again", await File.ReadAllTextAsync(docPath.Value!));
    }

    [Fact]
    public void GenuinelyExternalPath_StillRejected()
    {
        var (resolver, _) = Make();
        var external = Path.Combine(Path.GetDirectoryName(_root.TrimEnd(Path.DirectorySeparatorChar))!,
            "definitely-outside.txt");
        var r = resolver.Resolve(external);
        Assert.False(r.IsSuccess);
        Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
    }
}
