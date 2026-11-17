using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>One tool that answers every call with a scripted result and records the arguments
///     it was handed. The reference injector resolves through the tool surface, so a test
///     asserts on what each tool was ASKED as well as what came back.</summary>
internal sealed class StubReadTool(string name, Func<RawToolInput, ToolResult> answer) : ITool
{
  public List<RawToolInput> Calls { get; } = [];

  public ToolDefinition Definition { get; } = new(name, name + " stub", [], []);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    Calls.Add(input);
    return Task.FromResult(answer(input));
  }

  /// <summary>The JSON arguments of the single call this tool received.</summary>
  public JsonElement LastArguments => JsonElement.Parse(Assert.Single(Calls).JsonArguments);

  /// <summary>The path the single call this tool received named.</summary>
  public string LastPath
  {
    get
    {
      using JsonDocument args = JsonDocument.Parse(Assert.Single(Calls).JsonArguments);
      return args.RootElement.GetProperty("path").GetString()!;
    }
  }
}

/// <summary>A tool surface over the given tools, so a test can wire exactly the read tools a
///     reference needs and nothing else.</summary>
internal static class ReferenceToolSurface
{
  public static AgentToolsProvider Over(params ITool[] tools)
      => new("agent", [.. tools.Select(tool => new AgentToolBinding(tool, tool.Definition.Name))]);

  /// <summary>A read tool that answers exactly as the real one does over this file system: the
  ///     read tool's own output format for one page, or the reader's own refusal.</summary>
  public static StubReadTool Text(IFileSystemAccess files) => new("read", input =>
  {
    string path = PathOf(input);
    Result<FileRead> read = files.ReadLinesAsync(path, 1, 1000).GetAwaiter().GetResult();
    return read.IsSuccess
        ? new ToolResult(ReadOutputRenderer.Render(path, 1, 1000, read.Value, new FileCoverage([]), forced: false), false)
        : new ToolResult($"Error [{read.Error.Code}]: {read.Error.Message}", true);
  });

  /// <summary>A read_image tool that answers with an annotation and one attached PNG.</summary>
  public static StubReadTool Image(string mediaType = "image/png") => new("read_image", input =>
      new ToolResult($"[read_image {PathOf(input)} {mediaType}, 3 bytes]", false,
          Images: [new ToolResultImage(mediaType, Convert.ToBase64String([1, 2, 3]))]));

  /// <summary>A pdf tool that answers a read with one page of text.</summary>
  public static StubReadTool Pdf() => new("pdf", input =>
      new ToolResult($"[pdf {PathOf(input)} v1:1 pages 1-1 of 1]\n--- page 1 (612x792pt) ---\nquarterly figures", false));

  /// <summary>A docx tool that answers a read with one paragraph block.</summary>
  public static StubReadTool Docx() => new("docx", input =>
      new ToolResult($"[docx {PathOf(input)} v1:1 blocks 1-1 of 1]\np0 | Normal | contract terms", false));

  /// <summary>A tool that refuses, the way a real one does when the bytes are not its format.
  ///     </summary>
  public static StubReadTool Refusing(string name, string error) => new(name, _ => new ToolResult(error, true));

  private static string PathOf(RawToolInput input)
  {
    using JsonDocument args = JsonDocument.Parse(input.JsonArguments);
    return args.RootElement.GetProperty("path").GetString()!;
  }
}
