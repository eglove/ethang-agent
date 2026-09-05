using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp-file IO and best-effort cleanup are deliberate;
// HttpClient ownership transfers to the code under test.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>Integration coverage for the P0 path-validation false positives observed in
/// real use: tools sharing one WorkspacePathResolver rejected paths INSIDE the workspace
/// when the root carried a trailing separator (folder-picker roots do). These tests drive
/// the real resolver with the real file ACL — no fakes — over a temp workspace.</summary>
public sealed class WorkspacePathIntegrationTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-wsint").FullName;

  private (WorkspacePathResolver Resolver, DirectFileSystemAccess Files) Make() =>
    // The production shape: the desktop folder picker hands the host a root that may
    // end in a directory separator; the resolver must normalize it away.
    (new(_root + Path.DirectorySeparatorChar), new());

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch { /* best effort */ }
  }

  [Fact]
  public async Task ReadThroughResolvedInsidePath_ReturnsContent()
  {
    (WorkspacePathResolver? resolver, DirectFileSystemAccess? files) = Make();
    _ = Directory.CreateDirectory(Path.Combine(_root, "src"));
    await File.WriteAllTextAsync(Path.Combine(_root, "src", "code.cs"), "class Marker { }", TestContext.Current.CancellationToken);

    Result<string> resolved = resolver.Resolve(".");
    Assert.True(resolved.IsSuccess, $"root '.' rejected: {resolved.Error?.Message}");

    Result<string> codeResolved = resolver.Resolve(Path.Combine("src", "code.cs"));
    Assert.True(codeResolved.IsSuccess, $"src/code.cs rejected: {codeResolved.Error?.Message}");

    Result<FileRead> read = await files.ReadLinesAsync(codeResolved.Value, 1, 10, TestContext.Current.CancellationToken);
    Assert.True(read.IsSuccess, $"read failed: {read.Error?.Message}");
    Assert.Contains(read.Value.Lines, l => l.Contains("Marker", StringComparison.Ordinal));
  }

  [Fact]
  public async Task WriteEditRoundtripUnderDocs_Succeeds()
  {
    (WorkspacePathResolver? resolver, DirectFileSystemAccess? files) = Make();
    _ = Directory.CreateDirectory(Path.Combine(_root, "docs"));

    Result<string> docPath = resolver.Resolve(Path.Combine("docs", "note.md"));
    Assert.True(docPath.IsSuccess, $"docs/note.md rejected: {docPath.Error?.Message}");

    Result<FileWriteOutcome> written = await files.WriteFileAsync(docPath.Value, "hello", overwrite: false, ct: TestContext.Current.CancellationToken);
    Assert.True(written.IsSuccess, $"write failed: {written.Error?.Message}");

    Result<ReplaceOutcome> edited = await files.ReplaceInFileAsync(docPath.Value, "hello", "hello again",
        occurrences: 1, ct: TestContext.Current.CancellationToken);
    Assert.True(edited.IsSuccess, $"edit failed: {edited.Error?.Message}");

    Assert.Contains("hello again", await File.ReadAllTextAsync(docPath.Value, TestContext.Current.CancellationToken), StringComparison.Ordinal);
  }

  [Fact]
  public void GenuinelyExternalPath_StillRejected()
  {
    (WorkspacePathResolver? resolver, DirectFileSystemAccess _) = Make();
    string external = Path.Combine(Path.GetDirectoryName(_root.TrimEnd(Path.DirectorySeparatorChar))!,
        "definitely-outside.txt");
    Result<string> r = resolver.Resolve(external);
    Assert.False(r.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", r.Error.Code);
  }
}
