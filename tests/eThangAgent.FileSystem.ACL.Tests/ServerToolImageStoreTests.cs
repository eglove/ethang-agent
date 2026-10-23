using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

// Test helpers: sync temp IO and best-effort cleanup are deliberate.
#pragma warning disable CA1849 // Call async methods when in an async method
#pragma warning disable CA2000 // Call IDisposable.Dispose on object created by
#pragma warning disable CA1031 // Do not catch general exception types

/// <summary>The generated-image artifact store (issue #230): the bytes a server-side
///     image tool produced must land in the workspace so the user can open the file and
///     the model can read_image it later. The store creates its own directory (the file
///     write seam never does), validates the format by SNIFFING the decoded bytes rather
///     than trusting the wire's claim, bounds the size, and refuses paths outside the
///     workspace.</summary>
public sealed class ServerToolImageStoreTests : IDisposable
{
  // A minimal PNG: the 8-byte signature plus three payload bytes.
  private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02];

  private readonly string _root = Directory.CreateTempSubdirectory("ethang-img").FullName;
  private readonly DirectFileSystemAccess _access = new(LineEndingStub.For(LineEnding.Lf));

  private ServerToolImageStore Store => new(new WorkspacePathResolver(_root), _access);

  public void Dispose()
  {
    _access.Dispose();
    try
    {
      Directory.Delete(_root, recursive: true);
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
  public async Task Save_WritesTheBytesInsideTheWorkspaceAndReportsBothPaths()
  {
    Result<SavedServerToolImage> result = await Store.SaveAsync("image/png",
        Convert.ToBase64String(Png), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // Workspace-relative, forward-slashed: the transcript names this and it must read
    // the same on every platform.
    Assert.StartsWith(".ethang/images/", result.Value.RelativePath, StringComparison.Ordinal);
    Assert.EndsWith(".png", result.Value.RelativePath, StringComparison.Ordinal);
    Assert.True(File.Exists(result.Value.FullPath));
    Assert.Equal(Png, await File.ReadAllBytesAsync(result.Value.FullPath, TestContext.Current.CancellationToken));
    // The reported full path is inside the workspace root.
    Assert.StartsWith(Path.GetFullPath(_root), result.Value.FullPath, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task Save_CreatesTheArtifactDirectoryWhenAbsent()
  {
    // The file write seam never creates directories; the store must.
    Assert.False(Directory.Exists(Path.Combine(_root, ".ethang")));

    Result<SavedServerToolImage> result = await Store.SaveAsync("image/png",
        Convert.ToBase64String(Png), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.True(Directory.Exists(Path.Combine(_root, ".ethang", "images")));
  }

  [Fact]
  public async Task Save_TwoImages_LandAtDistinctPaths()
  {
    Result<SavedServerToolImage> first = await Store.SaveAsync("image/png",
        Convert.ToBase64String(Png), TestContext.Current.CancellationToken);
    Result<SavedServerToolImage> second = await Store.SaveAsync("image/png",
        Convert.ToBase64String(Png), TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess);
    Assert.NotEqual(first.Value.RelativePath, second.Value.RelativePath);
  }

  [Fact]
  public async Task Save_MediaTypeClaimingPngButCarryingJpegBytes_SniffsTheRealFormat()
  {
    // The wire's claim is never trusted: the bytes decide. A JPEG payload under an
    // image/png claim is written as .jpg, not mislabelled .png.
    byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22];

    Result<SavedServerToolImage> result = await Store.SaveAsync("image/png",
        Convert.ToBase64String(jpeg), TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Assert.EndsWith(".jpg", result.Value.RelativePath, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Save_UnsupportedFormat_IsATypedFailure()
  {
    byte[] webp = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00];

    Result<SavedServerToolImage> result = await Store.SaveAsync("image/webp",
        Convert.ToBase64String(webp), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("UnsupportedImageFormat", result.Error.Code);
  }

  [Fact]
  public async Task Save_InvalidBase64_IsATypedFailure()
  {
    Result<SavedServerToolImage> result = await Store.SaveAsync("image/png", "not base64!!",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidImagePayload", result.Error.Code);
  }

  [Fact]
  public async Task Save_EmptyPayload_IsATypedFailure()
  {
    Result<SavedServerToolImage> result = await Store.SaveAsync("image/png", "",
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidImagePayload", result.Error.Code);
  }

  [Fact]
  public async Task Save_OversizedImage_IsATypedFailure()
  {
    // ImageLimits.MaxBytes is the shared bound every image entry point enforces.
    byte[] huge = new byte[ImageLimits.MaxBytes + 1];
    Png.CopyTo(huge, 0);

    Result<SavedServerToolImage> result = await Store.SaveAsync("image/png",
        Convert.ToBase64String(huge), TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("ImageTooLarge", result.Error.Code);
  }
}
