using System.Net;
using System.Text;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

#pragma warning disable CA2000 // HttpClient owns the handler; tool lifetime bounds it
namespace eThangAgent.Zai.ACL.Tests;

public class ZaiWorkspaceToolsTests
{
  private static readonly Uri BaseUrl = new("https://zai.test");
  private static ZaiConfiguration Config => new("test-key", BaseUrl);

  private static RawToolInput Args(string json) => new("test", json);

  private static HttpResponseMessage Json(string body) =>
      new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

  private sealed class StubResolver : IPathResolver
  {
    public Result<string> Resolve(string path) => Result.Success(Path.GetFullPath(path));
  }

  private sealed class StubFiles : IFileSystemAccess, IFileWriteAccess
  {
    public byte[]? BytesToServe { get; set; }
    public List<(string Path, byte[] Bytes)> Written { get; } = [];

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<FileRead>(new DomainError("Unused", "byte reads only")));

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => BytesToServe is null
            ? Task.FromResult(Result.Failure<byte[]>(new DomainError("FileNotFound", $"File not found: {path}")))
            : Task.FromResult(Result.Success(BytesToServe));

    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
        => Task.FromResult(Result.Failure<FileWriteOutcome>(new DomainError("Unused", "byte writes only")));

    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
        string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
    {
      Written.Add((path, bytes));
      return Task.FromResult(Result.Success(new FileWriteOutcome(Created: true, BytesWritten: bytes.Length)));
    }
  }

  // ---- generate_image ----

  [Fact]
  public async Task GenerateImage_Generates_Downloads_AndWritesWorkspacePng()
  {
    byte[] png = [1, 2, 3, 4];
    string? requestedUrl = null;
    FakeHttpMessageHandler handler = new(req =>
    {
      if (req.RequestUri!.AbsolutePath.EndsWith("/images/generations", StringComparison.Ordinal))
      {
        return Task.FromResult(Json(/*lang=json,strict*/ """{"data":[{"url":"https://cdn.zai.test/img.png"}]}"""));
      }
      requestedUrl = req.RequestUri.ToString();
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
    });
    StubFiles files = new();
    ZaiImageTool tool = new(new HttpClient(handler), Config, new StubResolver(), files);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":60,"prompt":"a cat","filename":"out/cat.png"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("https://cdn.zai.test/img.png", requestedUrl);
    (string path, byte[] bytes) = Assert.Single(files.Written);
    Assert.EndsWith("cat.png", path, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(png, bytes);
    Assert.Contains("[image written: out/cat.png (1280x1280, 4 bytes)]", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"prompt":"x"}""", "MissingParameter")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"prompt":"x","filename":"out/cat.jpg"}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"prompt":"x","filename":"a.png","size":"100x100"}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"prompt":"x","filename":"a.png","size":"1030x1280"}""", "InvalidParameterValue")]
  public async Task GenerateImage_RejectsInvalidInput(string json, string code)
  {
    ZaiImageTool tool = new(new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(Json("{}")))),
        Config, new StubResolver(), new StubFiles());

    ToolResult result = await tool.ExecuteAsync(Args(json), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith($"Error [{code}]:", result.Content, StringComparison.Ordinal);
  }

  // ---- ocr_document ----

  [Fact]
  public async Task Ocr_SendsBase64File_ReturnsMarkdownWithPageCount()
  {
    string? capturedBody = null;
    FakeHttpMessageHandler handler = new(async req =>
    {
      Assert.NotNull(req.Content);
      capturedBody = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
      return Json(/*lang=json,strict*/ """{"md_results":"# Hello","data_info":{"num_pages":2}}""");
    });
    StubFiles files = new() { BytesToServe = [9, 9, 9] };
    ZaiOcrTool tool = new(new HttpClient(handler), Config, new StubResolver(), files);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":30,"path":"doc.pdf"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("\"model\":\"glm-ocr\"", capturedBody, StringComparison.Ordinal);
    Assert.Contains(Convert.ToBase64String([9, 9, 9]), capturedBody, StringComparison.Ordinal);
    Assert.Contains("[ocr doc.pdf: 2 page(s)]", result.Content, StringComparison.Ordinal);
    Assert.Contains("# Hello", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5}""", "MissingParameter")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"path":"doc.docx"}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"path":"doc.pdf","startPage":0}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"path":"doc.pdf","startPage":3,"endPage":2}""", "InvalidParameterValue")]
  public async Task Ocr_RejectsInvalidInput(string json, string code)
  {
    ZaiOcrTool tool = new(new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(Json("{}")))),
        Config, new StubResolver(), new StubFiles());

    ToolResult result = await tool.ExecuteAsync(Args(json), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith($"Error [{code}]:", result.Content, StringComparison.Ordinal);
  }

  // ---- transcribe_audio ----

  [Fact]
  public async Task Transcribe_PostsMultipart_AndReturnsText()
  {
    string? capturedContentType = null;
    List<(string? Name, string? FileName, string Text)> parts = [];
    FakeHttpMessageHandler handler = new(req =>
    {
      capturedContentType = req.Content?.Headers.ContentType?.MediaType;
      if (req.Content is MultipartFormDataContent multipart)
      {
        // Inspect the part headers without serializing the streaming content —
        // multipart cannot be read twice, and HttpClient may buffer it on send.
        parts.AddRange(multipart.Select(p =>
            (p.Headers.ContentDisposition?.Name?.Trim('"'),
             p.Headers.ContentDisposition?.FileName?.Trim('"'),
             p is StringContent s ? s.ReadAsStringAsync().GetAwaiter().GetResult() : "<binary>")));
      }
      return Task.FromResult(Json(/*lang=json,strict*/ """{"text":"hello there"}"""));
    });
    StubFiles files = new() { BytesToServe = [1, 2] };
    ZaiTranscriptionTool tool = new(new HttpClient(handler), Config, new StubResolver(), files);

    ToolResult result = await tool.ExecuteAsync(Args(
                             /*lang=json,strict*/
                             """{"timeoutSeconds":30,"path":"clip.wav","context":"general meeting"}"""), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError, result.Content);
    Assert.Equal("multipart/form-data", capturedContentType);
    Assert.Contains(("model", null, "glm-asr-2512"), parts);
    Assert.Contains(("prompt", null, "general meeting"), parts);
    Assert.Contains(parts, p => p.Name == "file" && p.FileName == "clip.wav");
    Assert.Contains("[transcribed clip.wav]", result.Content, StringComparison.Ordinal);
    Assert.Contains("hello there", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5}""", "MissingParameter")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds":5,"path":"clip.flac"}""", "InvalidParameterValue")]
  public async Task Transcribe_RejectsInvalidInput(string json, string code)
  {
    ZaiTranscriptionTool tool = new(new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(Json("{}")))),
        Config, new StubResolver(), new StubFiles());

    ToolResult result = await tool.ExecuteAsync(Args(json), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith($"Error [{code}]:", result.Content, StringComparison.Ordinal);
  }
}
