using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns no handler here; the tool never reaches the network under test
namespace eThangAgent.Zai.ACL.Tests;

public class ZaiPathToolRootingTests
{
  private static readonly string Anchor = Path.Combine(Path.GetTempPath(), "zai-rooting-anchor");

  [Fact]
  public async Task ImageTool_RootedAt_RefusesOutsideAnchor_BeforeAnyIo()
  {
    string outside = Path.Combine(Path.GetTempPath(), $"zai-escape-{Guid.NewGuid():N}.png");
    ZaiImageTool tool = new(new HttpClient(), new ZaiConfiguration("key", new Uri("https://zai.test")),
        new UnrootedPathResolver(), new ThrowingWrites());

    ToolResult result = await tool.RootedAt(Anchor).ExecuteAsync(new RawToolInput("generate_image",
        JsonSerializer.Serialize(new { timeoutSeconds = 60, prompt = "x", filename = outside })),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]:", result.Content, StringComparison.Ordinal);
    Assert.Equal("generate_image", tool.RootedAt(Anchor).Definition.Name);
  }

  [Fact]
  public async Task OcrTool_RootedAt_RefusesOutsideAnchor_BeforeAnyIo()
  {
    string outside = Path.Combine(Path.GetTempPath(), $"zai-escape-{Guid.NewGuid():N}.pdf");
    ZaiOcrTool tool = new(new HttpClient(), new ZaiConfiguration("key", new Uri("https://zai.test")),
        new UnrootedPathResolver(), new ThrowingReads());

    ToolResult result = await tool.RootedAt(Anchor).ExecuteAsync(new RawToolInput("ocr_document",
        JsonSerializer.Serialize(new { timeoutSeconds = 60, path = outside })),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]:", result.Content, StringComparison.Ordinal);
    Assert.Equal("ocr_document", tool.RootedAt(Anchor).Definition.Name);
  }

  [Fact]
  public async Task TranscriptionTool_RootedAt_RefusesOutsideAnchor_BeforeAnyIo()
  {
    string outside = Path.Combine(Path.GetTempPath(), $"zai-escape-{Guid.NewGuid():N}.wav");
    ZaiTranscriptionTool tool = new(new HttpClient(), new ZaiConfiguration("key", new Uri("https://zai.test")),
        new UnrootedPathResolver(), new ThrowingReads());

    ToolResult result = await tool.RootedAt(Anchor).ExecuteAsync(new RawToolInput("transcribe_audio",
        JsonSerializer.Serialize(new { timeoutSeconds = 60, path = outside })),
        TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [PathOutsideWorkspace]:", result.Content, StringComparison.Ordinal);
    Assert.Equal("transcribe_audio", tool.RootedAt(Anchor).Definition.Name);
  }

  private sealed class ThrowingWrites : IFileWriteAccess
  {
    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
        string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
        => throw new NotImplementedException();
  }

  private sealed class ThrowingReads : IFileSystemAccess
  {
    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine,
        CancellationToken ct = default) => throw new NotImplementedException();
  }
}
