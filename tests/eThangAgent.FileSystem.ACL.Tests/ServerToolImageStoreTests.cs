using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>A generated image is SCRATCH, not durable state: it lands under the operating
///     system's temp directory in a per-session folder, never in the user's workspace and
///     never in the app's database. The returned path is ABSOLUTE because that is what
///     read_image accepts on a later turn.</summary>
public sealed class ServerToolImageStoreTests : IDisposable
{
  // A real 1x1 PNG: the store SNIFFS the bytes, so a signature-only fixture would not do.
  private const string PngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

  private readonly string _session = "test-" + Guid.NewGuid().ToString("N");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      string folder = Path.Combine(Path.GetTempPath(), ServerToolImageStore.ArtifactFolderName, _session);
      if (Directory.Exists(folder))
      {
        Directory.Delete(folder, recursive: true);
      }
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private ServerToolImageStore Store => new(_session);

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  [Fact]
  public async Task Save_WritesUnderTheTempDirectory_NotTheWorkspace()
  {
    SavedServerToolImage saved = Must(await Store.SaveAsync("image/png", PngBase64,
        TestContext.Current.CancellationToken));

    Assert.True(File.Exists(saved.FullPath));
    Assert.True(Path.IsPathRooted(saved.FullPath), "read_image needs an absolute path");
    Assert.StartsWith(Path.GetTempPath(), saved.FullPath, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(_session, saved.FullPath, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Save_ReportsTheFileNameBesideThePath()
  {
    SavedServerToolImage saved = Must(await Store.SaveAsync("image/png", PngBase64,
        TestContext.Current.CancellationToken));

    Assert.EndsWith(".png", saved.FileName, StringComparison.Ordinal);
    Assert.Equal(saved.FileName, Path.GetFileName(saved.FullPath));
  }

  [Fact]
  public async Task Save_TwiceInOneMillisecond_NeverOverwrites()
  {
    SavedServerToolImage first = Must(await Store.SaveAsync("image/png", PngBase64,
        TestContext.Current.CancellationToken));
    SavedServerToolImage second = Must(await Store.SaveAsync("image/png", PngBase64,
        TestContext.Current.CancellationToken));

    Assert.NotEqual(first.FullPath, second.FullPath);
    Assert.True(File.Exists(first.FullPath));
    Assert.True(File.Exists(second.FullPath));
  }

  [Fact]
  public async Task Save_OfAPayloadThatIsNotAnImage_IsRefused()
  {
    Result<SavedServerToolImage> refused = await Store.SaveAsync("image/png",
        Convert.ToBase64String("not an image"u8.ToArray()), TestContext.Current.CancellationToken);

    Assert.False(refused.IsSuccess);
    Assert.Equal("UnsupportedImageFormat", refused.Error.Code);
  }


  /// <summary>The round trip that makes a generated artifact useful: the store writes it to
  ///     the temp directory and hands back an ABSOLUTE path, and read_image - wired the way a
  ///     session wires it, over a workspace-rooted resolver - opens that path and attaches the
  ///     bytes. Without this the model could never reach an image it produced.</summary>
  [Fact]
  public async Task GeneratedArtifact_IsReadableFromItsAbsolutePath_OutsideTheWorkspace()
  {
    string workspace = Path.Combine(Path.GetTempPath(), "ethang-ws-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(workspace);
    try
    {
      SavedServerToolImage saved = Must(await Store.SaveAsync("image/png", PngBase64,
          TestContext.Current.CancellationToken));

      // The session's real wiring: a workspace-rooted resolver decorated with the harness's own
      // artifact folder. The artifact lives in temp, outside the workspace, so this proves the
      // route the model actually takes to open an image it generated.
      using DirectFileSystemAccess files = new(new EditorConfigLineEndingPolicy(workspace));
      TrustedDirectoryPathResolver resolver = new(
          new WorkspacePathResolver(workspace),
          [new TrustedDirectory(ServerToolImageStore.ArtifactRoot)],
          enforceWritePolicy: false);
      ReadImageTool tool = new(resolver, files, new Vision(true));
      // Serialized, not pasted: a Windows path carries backslashes that break hand-built JSON.
      string arguments = JsonSerializer.Serialize(new { timeoutSeconds = 60, path = saved.FullPath });
      ToolResult result = await tool.ExecuteAsync(new RawToolInput("read_image", arguments),
          ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.False(result.IsError, result.Content);
      ToolResultImage image = Assert.Single(result.Images!);
      Assert.Equal("image/png", image.MediaType);
      Assert.Equal(PngBase64, image.Base64Data);
    }
    finally
    {
      // Named decision (CA1031): temp cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
      try
      {
        Directory.Delete(workspace, recursive: true);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }

  private sealed class Vision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }
  [Fact]
  public async Task Save_OfAnEmptyPayload_IsRefused()
  {
    Result<SavedServerToolImage> refused = await Store.SaveAsync("image/png", "",
        TestContext.Current.CancellationToken);

    Assert.False(refused.IsSuccess);
    Assert.Equal("InvalidImagePayload", refused.Error.Code);
  }
}
