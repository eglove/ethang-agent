using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The tool_output_read tool's contract: gutter-formatted pages with the
///     read tool's annotation shape, typed errors, strict input validation, and an
///     advertisement that documents the format contract verbatim.</summary>
public class ToolOutputReadToolTests
{
  private readonly FakeArchive _archive = new();

  private ToolOutputReadTool NewTool() => new(_archive);

  private static RawToolInput Args(string json) => new("tool_output_read", json);

  [Fact]
  public async Task Read_RendersGutterAnnotation_AndNumberedLines()
  {
    string content = "alpha\nbeta\ngamma";
    string handle = (await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken)).Value!;

    ToolResult result = await NewTool().ExecuteAsync(Args(
        /*lang=json,strict*/ $"{{\"timeoutSeconds\":30, \"handle\":\"{handle}\"}}"), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    string[] lines = result.Content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    Assert.Equal($"[tool-output-read {handle} chars 1-{content.Length} of {content.Length} | lines 1-3]", lines[0]);
    Assert.Equal("1→ alpha", lines[1]);
    Assert.Equal("2→ beta", lines[2]);
    Assert.Equal("3→ gamma", lines[3]);
  }

  [Fact]
  public async Task Read_CappedPage_AppendsMoreNotice_WithNextOffset()
  {
    string content = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"row-{i:000}"));
    string handle = (await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken)).Value!;

    ToolResult result = await NewTool().ExecuteAsync(Args(
        /*lang=json,strict*/ $"{{\"timeoutSeconds\":30, \"handle\":\"{handle}\", \"maxChars\":500}}"), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.EndsWith("[more content follows — pass offset 500 to continue]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_UnknownHandle_TypedError()
  {
    ToolResult result = await NewTool().ExecuteAsync(Args(
        /*lang=json,strict*/ "{\"timeoutSeconds\":30, \"handle\":\"arch:0123456789abcdef\"}"), TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [ArchiveNotFound]: ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_MissingHandle_TypedError()
  {
    ToolResult result = await NewTool().ExecuteAsync(Args(
        /*lang=json,strict*/ "{\"timeoutSeconds\":30}"), TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [MissingParameter]: ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_MalformedHandle_TypedError()
  {
    ToolResult result = await NewTool().ExecuteAsync(Args(
        /*lang=json,strict*/ "{\"timeoutSeconds\":30, \"handle\":\"bogus\"}"), TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]: ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_UnknownParameter_TypedError()
  {
    string handle = (await _archive.ArchiveAsync("abc", TestContext.Current.CancellationToken)).Value!;

    ToolResult result = await NewTool().ExecuteAsync(Args(
        /*lang=json,strict*/ $"{{\"timeoutSeconds\":30, \"handle\":\"{handle}\", \"tail\":5}}"), TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [UnknownParameter]: ", result.Content, StringComparison.Ordinal);
    Assert.Contains("tail", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Read_OffsetBeyondLength_TypedError()
  {
    string handle = (await _archive.ArchiveAsync("abc", TestContext.Current.CancellationToken)).Value!;

    ToolResult result = await NewTool().ExecuteAsync(Args(
        /*lang=json,strict*/ $"{{\"timeoutSeconds\":30, \"handle\":\"{handle}\", \"offset\":99}}"), TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidParameterValue]: ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void Advertisement_StatesHandleMarker_AndGutterContract()
  {
    string d = NewTool().Definition.Description;
    Assert.Contains("[tool-output archived: arch:", d, StringComparison.Ordinal);
    Assert.Contains("tool_output_read", d, StringComparison.Ordinal);
    Assert.Contains("→", d, StringComparison.Ordinal);
    Assert.Contains("Error [ArchiveNotFound]", d, StringComparison.Ordinal);
    Assert.Contains("offset", d, StringComparison.Ordinal);
    Assert.Contains("maxChars", d, StringComparison.Ordinal);
  }

  [Fact]
  public void Advertisement_OnlyHandle_IsRequired()
  {
    ToolOutputReadTool tool = NewTool();
    Assert.Equal(["timeoutSeconds", "handle"], tool.Definition.RequiredParameters);
    Assert.Equal(4, tool.Definition.Parameters.Count);
  }

  /// <summary>In-memory archive fake (the domain test never knows SQL exists).</summary>
  private sealed class FakeArchive : IToolOutputArchive
  {
    private readonly Dictionary<string, string> _stored = [];

    public Task<Result<string>> ArchiveAsync(string content, CancellationToken ct = default)
    {
      if (string.IsNullOrEmpty(content))
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("InvalidParameterValue", "empty")));
      }

      string handle = ToolOutputArchiveFormat.HandleOf(content);
      _ = _stored.TryAdd(handle, content);
      return Task.FromResult(Result.Success(handle));
    }

    public string Excerpt(string content, int headChars, int tailChars)
    {
      ArgumentNullException.ThrowIfNull(content);
      return ToolOutputArchiveFormat.FitsWithin(content, headChars, tailChars)
          ? content
          : ToolOutputArchiveFormat.MarkerLine(ToolOutputArchiveFormat.HandleOf(content),
                content.Length - headChars - tailChars)
          + "\n" + ToolOutputArchiveFormat.ExcerptBody(content, headChars, tailChars);
    }

    public Task<Result<ArchivePage>> ReadBackAsync(string handle, int offset, int maxChars,
        CancellationToken ct = default)
    {
      if (!_stored.TryGetValue(handle, out string? content))
      {
        return Task.FromResult(Result.Failure<ArchivePage>(new DomainError("ArchiveNotFound",
            $"No archived tool output with handle '{handle}'.")));
      }

      if (offset < 0 || maxChars < 1)
      {
        return Task.FromResult(Result.Failure<ArchivePage>(new DomainError("InvalidParameterValue",
            "bad paging arguments.")));
      }

      if (offset >= content.Length)
      {
        return Task.FromResult(Result.Failure<ArchivePage>(new DomainError("InvalidParameterValue",
            $"'offset' {offset} is at or beyond the archived length ({content.Length} chars).")));
      }

      int take = Math.Min(maxChars, content.Length - offset);
      string text = content.Substring(offset, take);
      return Task.FromResult(Result.Success(new ArchivePage(handle, content.Length, offset, text,
          LineAt(content, offset), LineAt(content, offset + take - 1),
          offset + take < content.Length)));
    }

    private static int LineAt(string content, int charIndex)
    {
      int line = 1;
      for (int i = 0; i < charIndex && i < content.Length; i++)
      {
        if (content[i] == '\n')
        {
          line++;
        }
      }

      return line;
    }
  }
}
