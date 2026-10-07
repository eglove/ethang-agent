using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The read_image tool (issue #20): sniffs PNG/JPEG magic bytes, enforces the
///     20 MB bound, attaches a ToolResultImage when the model can see images, and
///     withholds (with a notice line) when it cannot - the computer tool's screenshot
///     contract, pinned through the same shape of tests.</summary>
public class ReadImageToolTests
{
  // A real 1x1 PNG - decodable by ToolResultImage's base64 validation.
  private const string TinyPngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";
  private static readonly byte[] TinyPng = Convert.FromBase64String(TinyPngBase64);

  // JPEG bytes need not be a valid image: the tool sniffs magic bytes only, so a
  // 4-byte JPEG header suffices for the format path.
  private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0];
  private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  private sealed class FakeVision(bool accepts) : IImageInputCapability
  {
    public bool AcceptsImages => accepts;
  }

  private sealed class FakeFiles(byte[] bytes) : IFileSystemAccess
  {
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Result.Success(bytes));
  }

  private sealed class FailingFiles(DomainError error) : IFileSystemAccess
  {
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => throw new NotImplementedException();
    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<byte[]>(error));
  }

  private static ReadImageTool MakeTool(byte[] bytes, bool vision = true, IPathResolver? resolver = null)
      => new(resolver ?? new UnrootedPathResolver(), new FakeFiles(bytes), new FakeVision(vision));

  private static ToolResult Run(ReadImageTool tool, string path = "img.png") =>
      tool.ExecuteAsync(new RawToolInput("read_image",
          "{\"timeoutSeconds\":60,\"path\":\"" + path + "\"}"),
          ct: TestContext.Current.CancellationToken).GetAwaiter().GetResult();

  [Fact]
  public void Definition_HasCorrectNameAndParams()
  {
    ReadImageTool tool = MakeTool(TinyPng);
    Assert.Equal("read_image", tool.Definition.Name);
    Assert.Equal(2, tool.Definition.Parameters.Count);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == ToolTimeout.ParameterName && p.Minimum == 1);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "path" && p.Type == ToolParameterType.Text);
    Assert.Equal([ToolTimeout.ParameterName, "path"], tool.Definition.RequiredParameters);
  }

  [Fact]
  public void PngFile_WithVision_AnnotatesAndAttachesImage()
  {
    ReadImageTool tool = MakeTool(TinyPng);
    ToolResult result = Run(tool);

    Assert.False(result.IsError);
    Assert.StartsWith($"[read_image {Path.GetFullPath("img.png")} image/png, {TinyPng.Length} bytes]",
        result.Content, StringComparison.Ordinal);
    ToolResultImage image = Assert.Single(result.Images!);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(Convert.ToBase64String(TinyPng), image.Base64Data);
  }

  [Fact]
  public void JpegFile_WithVision_AttachesImageJpeg()
  {
    ReadImageTool tool = MakeTool(JpegBytes);
    ToolResult result = Run(tool, "photo.jpg");

    Assert.False(result.IsError);
    Assert.Contains("image/jpeg", result.Content, StringComparison.Ordinal);
    Assert.Equal("image/jpeg", Assert.Single(result.Images!).MediaType);
  }

  [Fact]
  public void WithoutVision_WithholdsImage_AndAppendsNotice()
  {
    ReadImageTool tool = MakeTool(TinyPng, vision: false);
    ToolResult result = Run(tool);

    Assert.False(result.IsError);
    Assert.Null(result.Images);
    Assert.Contains("[read_image] image withheld: model has no image input",
        result.Content, StringComparison.Ordinal);
    // The annotation still reports the format and size - the model learns what it missed.
    Assert.Contains("image/png", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void NonImageBytes_FailWithUnsupportedImageFormat()
  {
    ReadImageTool tool = MakeTool([0x47, 0x49, 0x46, 0x38, 0x39, 0x61]); // "GIF89a"
    ToolResult result = Run(tool, "anim.gif");

    Assert.True(result.IsError);
    Assert.Contains("Error [UnsupportedImageFormat]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("image/png or image/jpeg", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void OversizedImage_FailsWithImageTooLarge()
  {
    // A valid PNG header on an oversized payload: the bound fires after the sniff.
    byte[] big = new byte[ImageLimits.MaxBytes + 1];
    PngMagic.CopyTo(big, 0);
    ReadImageTool tool = MakeTool(big);
    ToolResult result = Run(tool);

    Assert.True(result.IsError);
    Assert.Contains("Error [ImageTooLarge]:", result.Content, StringComparison.Ordinal);
    Assert.Contains(ImageLimits.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void FileNotFound_SurfacesAclError()
  {
    ReadImageTool tool = new(new UnrootedPathResolver(),
        new FailingFiles(new DomainError("FileNotFound", "File not found: nope.png")),
        new FakeVision(true));
    ToolResult result = Run(tool, "nope.png");

    Assert.True(result.IsError);
    Assert.Contains("Error [FileNotFound]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void PathOutsideWorkspace_SurfacesResolverError()
  {
    ReadImageTool tool = MakeTool(TinyPng, resolver: new ThrowingResolver());
    ToolResult result = Run(tool, "../escape.png");

    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void RelativePath_ResolvedThroughPathResolver()
  {
    StubResolver resolver = new();
    ReadImageTool tool = MakeTool(TinyPng, resolver: resolver);
    _ = Run(tool);

    Assert.Equal("img.png", resolver.Requested);
  }

  private sealed class StubResolver : IPathResolver
  {
    public string? Requested { get; private set; }
    public Result<string> Resolve(string path)
    {
      Requested = path;
      return Result.Success(Path.GetFullPath(path));
    }
  }

  private sealed class ThrowingResolver : IPathResolver
  {
    public Result<string> Resolve(string path) => Result.Failure<string>(
        new DomainError("PathOutsideWorkspace", "'..\\escape.png' resolves outside the workspace."));
  }
}
