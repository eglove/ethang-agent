using eThangAgent.ConversationDomain;

namespace eThangAgent.Conversation.Domain.Tests;

public class ToolResultMarkerTests
{
  private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

  [Fact]
  public void For_NamesTheToolCallAndTokenEstimate()
  {
    Message call = new(Role.Assistant, "", T,
        [new ToolCall("c1", "read", "{" + '"' + "path" + '"' + ":" + '"' + "src/big.cs" + '"' + "}")]);
    Message result = new(Role.Tool, new string('x', 40_000), T, ToolCallId: "c1");

    string marker = ToolResultMarker.For([call, result], 1);

    Assert.Equal(
        "[trimmed: read({" + '"' + "path" + '"' + ":" + '"' + "src/big.cs" + '"' + "}) — original result ~10000 tokens evicted; re-run the tool if needed]",
        marker);
  }

  [Fact]
  public void For_LongArgumentsAreTruncated()
  {
    Message call = new(Role.Assistant, "", T, [new ToolCall("c1", "exec", new string('a', 200))]);
    Message result = new(Role.Tool, "r", T, ToolCallId: "c1");

    string marker = ToolResultMarker.For([call, result], 1);

    Assert.StartsWith("[trimmed: exec(", marker, StringComparison.Ordinal);
    Assert.Contains("…", marker, StringComparison.Ordinal);
  }

  [Fact]
  public void For_UnpairedResult_StillMarks()
  {
    Message result = new(Role.Tool, new string('x', 800), T, ToolCallId: "ghost");

    string marker = ToolResultMarker.For([result], 0);

    Assert.Equal("[trimmed: tool result ~200 tokens evicted; re-run the tool if needed]", marker);
  }
}
