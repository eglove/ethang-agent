using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The archive service contract over an in-memory fake: byte-stable handles,
///     excerpt rendering, and paged read-back. A domain test never knows SQL exists —
///     the fake stands in for the Storage ACL implementation.</summary>
public class ToolOutputArchiveServiceTests
{
  private readonly FakeArchive _archive = new();

  [Fact]
  public void HandleOf_IsByteStable_AndPrefixed()
  {
    string content = "some large tool output";

    string first = ToolOutputArchiveFormat.HandleOf(content);
    string second = ToolOutputArchiveFormat.HandleOf(content);

    Assert.Equal(first, second);
    Assert.StartsWith("arch:", first, StringComparison.Ordinal);
    Assert.Equal(5 + 16, first.Length);
    Assert.True(ToolOutputArchiveFormat.IsWellFormedHandle(first));
  }

  [Fact]
  public void HandleOf_DistinctContent_DistinctHandles()
      => Assert.NotEqual(ToolOutputArchiveFormat.HandleOf("a"), ToolOutputArchiveFormat.HandleOf("b"));

  [Fact]
  public void Excerpt_LargeContent_MarkerHeadSeparatorTail()
  {
    string content = new string('h', 5000) + new string('m', 8000) + new string('t', 2000);

    string excerpt = _archive.Excerpt(content, 4000, 1000);

    // The excerpt is the marker line, then head + separator + tail as one body.
    string[] lines = excerpt.Split('\n');
    Assert.Equal(2, lines.Length);
    Assert.Matches("^\\[tool-output archived: arch:[0-9a-f]{16} \\| 10000 chars omitted \\| read back with the tool_output_read tool\\]$", lines[0]);
    Assert.Equal(content[..4000], lines[1][..4000]);
    Assert.Equal(ToolOutputArchiveFormat.Separator, lines[1][4000..4001]);
    Assert.Equal(content[^1000..], lines[1][4001..]);
  }

  [Fact]
  public void Excerpt_ErrorForm_ZeroTail_KeepsHeadOnly()
  {
    string content = "Error [WebFetchFailed]: long diagnostic " + new string('x', 9000);

    string excerpt = _archive.Excerpt(content, 4000, 0);

    string[] lines = excerpt.Split('\n');
    Assert.Equal(2, lines.Length);
    Assert.StartsWith("Error [WebFetchFailed]", lines[1], StringComparison.Ordinal);
    Assert.Equal(content[..4000], lines[1]);
  }

  [Fact]
  public void Excerpt_FittingContent_Unchanged()
      => Assert.Equal("ok", _archive.Excerpt("ok", 4000, 1000));

  [Fact]
  public async Task ReadBack_Pages_AreContiguous()
  {
    string content = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line-{i:000}"));
    Result<string> archived = await _archive.ArchiveAsync(content, TestContext.Current.CancellationToken);

    Result<ArchivePage> first = await _archive.ReadBackAsync(archived.Value!, 0, 1000, TestContext.Current.CancellationToken);
    Result<ArchivePage> second = await _archive.ReadBackAsync(archived.Value!, first.Value!.Offset + first.Value.Text.Length, 1000, TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess, second.Error?.Message);
    Assert.Equal(content.Substring(1000, 1000), second.Value.Text);
    // Line accounting is contiguous across pages: the 1000-char boundary splits a
    // line, so the second page starts on the SAME line the first page ended on.
    Assert.Equal(first.Value.EndLine, second.Value.StartLine);
    Assert.True(second.Value.HasMore);
  }

  [Fact]
  public async Task ReadBack_UnknownHandle_FailsArchiveNotFound()
  {
    Result<ArchivePage> r = await _archive.ReadBackAsync("arch:0123456789abcdef", 0, 100, TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("ArchiveNotFound", r.Error.Code);
  }

  [Fact]
  public async Task Archive_EmptyContent_FailsTyped()
  {
    Result<string> r = await _archive.ArchiveAsync("", TestContext.Current.CancellationToken);

    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidParameterValue", r.Error.Code);
  }

  /// <summary>In-memory archive standing in for the Storage ACL implementation:
  ///     content-addressed dictionary, excerpt through the shared format contract,
  ///     paged read-back with global line accounting.</summary>
  private sealed class FakeArchive : IToolOutputArchive
  {
    private readonly Dictionary<string, string> _stored = [];

    public Task<Result<string>> ArchiveAsync(string content, CancellationToken ct = default)
    {
      if (string.IsNullOrEmpty(content))
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("InvalidParameterValue",
            "Archived content must be non-empty.")));
      }

      string handle = ToolOutputArchiveFormat.HandleOf(content);
      _ = _stored.TryAdd(handle, content);
      return Task.FromResult(Result.Success(handle));
    }

    public string Excerpt(string content, int headChars, int tailChars)
    {
      ArgumentNullException.ThrowIfNull(content);
      if (ToolOutputArchiveFormat.FitsWithin(content, headChars, tailChars))
      {
        return content;
      }

      int omitted = content.Length - headChars - tailChars;
      return ToolOutputArchiveFormat.MarkerLine(ToolOutputArchiveFormat.HandleOf(content), omitted)
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
            "'offset' must be non-negative and 'maxChars' at least 1.")));
      }

      if (offset >= content.Length)
      {
        return Task.FromResult(Result.Failure<ArchivePage>(new DomainError("InvalidParameterValue",
            $"'offset' {offset} is at or beyond the archived length ({content.Length} chars).")));
      }

      int take = Math.Min(maxChars, content.Length - offset);
      string text = content.Substring(offset, take);
      int startLine = CountLines(content[..offset]);
      int endLine = CountLines(content[..(offset + take)]);
      return Task.FromResult(Result.Success(new ArchivePage(handle, content.Length, offset, text,
          Math.Max(1, startLine), Math.Max(1, endLine), offset + take < content.Length)));
    }

    private static int CountLines(string text)
    {
      if (text.Length == 0)
      {
        return 1;
      }

      int lines = 1;
      for (int i = 0; i < text.Length; i++)
      {
        char c = text[i];
        if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
        {
          lines++;
        }
      }

      return lines;
    }
  }
}
