using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Secondary parts consumer: the context_edit List render names an image a
///     trimmable result carries, so a model choosing what to trim never discards a
///     picture it was not told about. Pinned through the tool surface with the real
///     Conversation Domain service - the same adapter stack the composition wires.</summary>
public class ContextEditImageMarkerTests
{
  private static ContextEditTool Tool(Conversation conversation) =>
      new(new ConversationContextServiceAdapter(conversation));

  private static RawToolInput ListInput() => new("context_edit", /*lang=json,strict*/ """
      {"timeoutSeconds":5, "action": "List" }
      """);

  [Fact]
  public async Task List_ToolResultWithImagePart_NamesTheImage()
  {
    Conversation conversation = new();
    conversation.AddAssistantMessage("", [new ToolCall("c1", "screenshot", "{}")]);
    conversation.AddToolResult("c1", "screenshot captured",
        [new MessagePart.ImagePart("image/png", "aGVsbG8=")]); // 'hello' decoded: 5 bytes
    ContextEditTool tool = Tool(conversation);

    ToolResult result = await tool.ExecuteAsync(ListInput(), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("[image]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_TextOnlyToolResult_CarriesNoImageMarker()
  {
    Conversation conversation = new();
    conversation.AddAssistantMessage("", [new ToolCall("c1", "read", "{}")]);
    conversation.AddToolResult("c1", "plain text");
    ContextEditTool tool = Tool(conversation);

    ToolResult result = await tool.ExecuteAsync(ListInput(), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.DoesNotContain("[image]", result.Content, StringComparison.Ordinal);
    Assert.Contains("r1", result.Content, StringComparison.Ordinal);
    Assert.Contains("read", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_UserMessageWithImagePart_IsNotATrimCandidate()
  {
    // Only tool results can be trimmed, so a user message - image or not - never
    // appears in the listing. Offering it would invite a call that must fail.
    Conversation conversation = new();
    conversation.AddUserMessage("what is this",
        [new MessagePart.ImagePart("image/jpeg", "aGVsbG8=")]);
    ContextEditTool tool = Tool(conversation);

    ToolResult result = await tool.ExecuteAsync(ListInput(), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[context: no trimmable tool results]", result.Content);
  }
}

/// <summary>The composition's adapter over the Conversation Domain service: the
///     Tool Domain's port is implemented by bridging to ConversationContextService.
///     Lives here because the composition project is not a test dependency.</summary>
internal sealed class ConversationContextServiceAdapter(Conversation conversation) : IConversationContextService
{
  private readonly ConversationContextService _service = new(conversation);

  public string List() => _service.List();

  public string ListTrimmable() => _service.ListTrimmable();

  public Result<string> Remove(string selection) => _service.Remove(selection);

  public Result<string> Shorten(string selection, string text) => _service.Shorten(selection, text);

  public Result<string> TrimToolResults(string selection) => _service.TrimToolResults(selection);
}
