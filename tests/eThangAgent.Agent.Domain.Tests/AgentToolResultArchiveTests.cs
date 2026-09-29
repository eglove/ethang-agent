using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The loop's store-and-read-back context policy: oversized tool results are
///     archived in full and enter history as an excerpt whose marker line names the
///     byte-stable handle; images pass through untouched; small error results pass
///     through; large error results keep the Error head (tail dropped); read-back
///     pages; and an archive-store failure degrades to the legacy full-content entry.</summary>
public class AgentToolResultArchiveTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  /// <summary>Repeats a character. A helper, not inline <c>new(c, n)</c>: the
  ///     target-typed form trips IDE0090 inside typed local declarations.</summary>
  private static string Repeat(char c, int count) => new(c, count);

  /// <summary>A tool returning a fixed ToolResult.</summary>
  private sealed class FixedTool(string name, ToolResult result) : ITool
  {
    public ToolDefinition Definition { get; } = new(name, "desc", []);

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => Task.FromResult(result);
  }

  private static AgentOptions ArchiveOptions(FakeToolOutputArchive archive) => new()
  {
    ToolOutputArchive = archive,
    ToolResultArchiveThreshold = 6000,
    ToolResultExcerptHeadChars = 4000,
    ToolResultExcerptTailChars = 1000,
  };

  [Fact]
  public async Task OversizedResult_HistoryCarriesExcerpt_WithHandleMarker()
  {
    FakeToolOutputArchive archive = new();
    string content = Repeat('a', 4000) + Repeat('m', 8000) + Repeat('t', 1000);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "big", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("big", new ToolResult(content, false))]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.True(toolMessage.Content.Length < content.Length);
    Assert.StartsWith("[tool-output archived: arch:", toolMessage.Content, StringComparison.Ordinal);
    Assert.Contains("8000 chars omitted | read back with the tool_output_read tool]", toolMessage.Content, StringComparison.Ordinal);
    Assert.Equal(content[..4000], toolMessage.Content.Split('\n')[1][..4000]);
    Assert.EndsWith(content[^1000..], toolMessage.Content, StringComparison.Ordinal);
    // Full content is in the archive; the handle resolves it.
    List<string> handles = [.. archive.Archived.Keys];
    Assert.Contains(ExtractHandle(toolMessage.Content), handles);
  }

  [Fact]
  public async Task SmallResult_PassesThroughUnchanged()
  {
    FakeToolOutputArchive archive = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "small", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("small", new ToolResult("tiny result", false))]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.Equal("tiny result", toolMessage.Content);
    Assert.Empty(archive.Archived);
  }

  [Fact]
  public async Task SmallErrorResult_PassesThroughUnchanged()
  {
    FakeToolOutputArchive archive = new();
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "err", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("err", new ToolResult("Error [Boom]: bad", true))]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.Equal("Error [Boom]: bad", toolMessage.Content);
    Assert.Empty(archive.Archived);
  }

  [Fact]
  public async Task LargeErrorResult_Archived_KeepsErrorHead_DropsTail()
  {
    FakeToolOutputArchive archive = new();
    string content = "Error [Boom]: the fault line\n" + Repeat('x', 9000);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "err", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("err", new ToolResult(content, true))]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.StartsWith("[tool-output archived: arch:", toolMessage.Content, StringComparison.Ordinal);
    Assert.Contains("5029 chars omitted", toolMessage.Content, StringComparison.Ordinal);
    // Head kept (the Error line carries the fault), tail dropped: the excerpt body
    // is exactly the 4000-char head.
    string body = toolMessage.Content[(toolMessage.Content.IndexOf('\n', StringComparison.Ordinal) + 1)..];
    Assert.Equal(content[..4000], body);
    // Tail dropped: the excerpt body ends at the 4000-char head, never reaching the
    // 9000-char fault detail.
    Assert.Equal(4000, body.Length);
    _ = Assert.Single(archive.Archived);
  }

  [Fact]
  public async Task Images_PassThroughUntouched_WhenTextArchived()
  {
    FakeToolOutputArchive archive = new();
    ToolResultImage image = new("image/png", "aGVsbG8=");
    string content = Repeat('a', 20_000);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "shot", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("shot", new ToolResult(content, false, Images: [image]))]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.StartsWith("[tool-output archived: arch:", toolMessage.Content, StringComparison.Ordinal);
    Assert.NotNull(toolMessage.Parts);
    MessagePart.ImagePart part = Assert.IsType<MessagePart.ImagePart>(toolMessage.Parts[0]);
    Assert.Equal("image/png", part.MediaType);
    Assert.Equal("aGVsbG8=", part.Base64Data);
  }

  [Fact]
  public async Task Handles_AreByteStable_AcrossIdenticalResults()
  {
    FakeToolOutputArchive archive = new();
    string content = Repeat('a', 20_000);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "big", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])),
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_2", "big", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done again", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("big", new ToolResult(content, false))]), ArchiveOptions(archive));

    Result<string> first = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);
    Result<string> second = await agent.SendMessage("again", ct: TestContext.Current.CancellationToken);

    Assert.True(first.IsSuccess);
    Assert.True(second.IsSuccess);
    string[] toolContents = [.. agent.Conversation.Messages.Where(m => m.Role is Role.Tool).Select(m => m.Content)];
    Assert.Equal(2, toolContents.Length);
    string handle1 = ExtractHandle(toolContents[0]);
    string handle2 = ExtractHandle(toolContents[1]);
    Assert.Equal(handle1, handle2);
    // Dedup: one stored entry for identical content.
    _ = Assert.Single(archive.Archived);
  }

  [Fact]
  public async Task ArchiveStoreFailure_DegradesToFullContentEntry()
  {
    FakeToolOutputArchive archive = new() { FailArchives = true };
    string content = Repeat('a', 20_000);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "big", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("big", new ToolResult(content, false))]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.Equal(content, toolMessage.Content);
  }

  [Fact]
  public async Task NoArchiveWired_LegacyFullContentEntry()
  {
    string content = Repeat('a', 20_000);
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "big", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("big", new ToolResult(content, false))]));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.Equal(content, toolMessage.Content);
  }

  [Fact]
  public async Task ReadBackToolResult_NeverReArchived_HistoryCarriesPageVerbatim()
  {
    FakeToolOutputArchive archive = new();
    string content = string.Join("\n", Enumerable.Range(1, 900).Select(i => $"row-{i:000}"));
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "big", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_2", "tool_output_read", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    string bigHandle = ToolOutputArchiveFormat.HandleOf(content);
    _ = archive.Archived.TryAdd(bigHandle, content);
    // A read-back page over the threshold: the annotation line plus gutter lines.
    string page = "[tool-output-read " + bigHandle + " chars 1-6440 of 9000 | lines 1-720]\n" +
        string.Join("\n", Enumerable.Range(1, 720).Select(i => $"{i,3}→ row-{i:000}"));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry(
        [
            new FixedTool("big", new ToolResult(content, false)),
            new FixedTool("tool_output_read", new ToolResult(page, false) { BypassesArchivePolicy = true }),
        ]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    List<Message> toolMessages = [.. agent.Conversation.Messages.Where(m => m.Role is Role.Tool)];
    Assert.Equal(2, toolMessages.Count);
    // The first (oversized) result is archived; the read-back page is NOT.
    Assert.StartsWith("[tool-output archived: arch:", toolMessages[0].Content, StringComparison.Ordinal);
    Assert.Equal(page, toolMessages[1].Content);
    Assert.DoesNotContain("[tool-output archived:", toolMessages[1].Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ReadBackThroughArchive_ReturnsPagedContent()
  {
    FakeToolOutputArchive archive = new();
    string content = string.Join("\n", Enumerable.Range(1, 900).Select(i => $"row-{i:000}"));
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "big", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("big", new ToolResult(content, false))]), ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string handle = ExtractHandle(agent.Conversation.Messages.Single(m => m.Role is Role.Tool).Content);
    Result<ArchivePage> page = await archive.ReadBackAsync(handle, 0, 1000, TestContext.Current.CancellationToken);
    Assert.True(page.IsSuccess, page.Error?.Message);
    Assert.Equal(content[..1000], page.Value.Text);
    Assert.True(page.Value.HasMore);
  }

  [Fact]
  public async Task OversizedSkillViewResult_EntersHistoryVerbatim_NeverArchived()
  {
    FakeToolOutputArchive archive = new();
    // A skill body large enough to trip the 6,000-char threshold on its own.
    string body = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line-{i:000} " + new string('x', 40)));
    string content = "[skill big-skill | builtin | v1]\n" + body;
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "skill_view", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("skill_view", new ToolResult(content, false) { BypassesArchivePolicy = true })]),
        ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    // The full skill body enters history byte-for-byte: no excerpt, no marker, no archive row.
    Assert.Equal(content, toolMessage.Content);
    Assert.DoesNotContain("[tool-output archived:", toolMessage.Content, StringComparison.Ordinal);
    Assert.Empty(archive.Archived);
  }

  [Fact]
  public async Task OversizedSkillViewResult_WithoutBypass_StillArchived()
  {
    FakeToolOutputArchive archive = new();
    string body = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line-{i:000} " + new string('x', 40)));
    string content = "[skill big-skill | builtin | v1]\n" + body;
    FakeProvider provider = new(
        Result.Success(new ModelResponse(null, [new ToolCallRequest("call_1", "skill_view", "{}")], FinishReason.ToolCalls)),
        Result.Success(new ModelResponse("done", [])));
    // No bypass flag: the policy still archives a large result from a tool that
    // does not claim the exemption.
    Agent agent = new(provider, new Conversation(), DefaultConfig,
        new ToolRegistry([new FixedTool("skill_view", new ToolResult(content, false))]),
        ArchiveOptions(archive));

    Result<string> result = await agent.SendMessage("go", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message toolMessage = agent.Conversation.Messages.Single(m => m.Role is Role.Tool);
    Assert.StartsWith("[tool-output archived: arch:", toolMessage.Content, StringComparison.Ordinal);
    Assert.NotEmpty(archive.Archived);
  }

  private static string ExtractHandle(string excerpt)

  {
    string marker = excerpt.Split('\n')[0];
    int start = marker.IndexOf("arch:", StringComparison.Ordinal);
    return marker[start..marker.IndexOf(' ', start)];
  }

  /// <summary>In-memory archive fake (a domain test never knows SQL exists).</summary>
  internal sealed class FakeToolOutputArchive : IToolOutputArchive
  {
    public Dictionary<string, string> Archived { get; } = [];

    /// <summary>When set, ArchiveAsync resolves to a StorageUnavailable failure.</summary>
    public bool FailArchives { get; init; }

    public Task<Result<string>> ArchiveAsync(string content, CancellationToken ct = default)
    {
      if (FailArchives)
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("StorageUnavailable", "db closed")));
      }

      if (string.IsNullOrEmpty(content))
      {
        return Task.FromResult(Result.Failure<string>(new DomainError("InvalidParameterValue", "empty")));
      }

      string handle = ToolOutputArchiveFormat.HandleOf(content);
      _ = Archived.TryAdd(handle, content);
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
      if (!Archived.TryGetValue(handle, out string? content))
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
          1, 1, offset + take < content.Length)));
    }
  }
}
