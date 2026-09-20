using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Secondary parts consumer: the context_edit List render shows an
///     '[image]' marker for messages carrying image parts, beside unchanged text
///     content. Pinned through the tool surface with the real Conversation Domain
///     service - the same adapter stack the composition wires.</summary>
public class ContextEditImageMarkerTests
{
  private static ContextEditTool Tool(Conversation conversation) =>
      new(new ConversationContextServiceAdapter(conversation));

  private static RawToolInput ListInput() => new("context_edit", /*lang=json,strict*/ """
      {"timeoutSeconds":5, "action": "List" }
      """);

  [Fact]
  public async Task List_MessageWithImagePart_ShowsImageMarker_BesideText()
  {
    Conversation conversation = new();
    conversation.AddToolResult("c1", "screenshot captured",
        [new MessagePart.ImagePart("image/png", "aGVsbG8=")]); // 'hello' decoded: 5 bytes
    ContextEditTool tool = Tool(conversation);

    ToolResult result = await tool.ExecuteAsync(ListInput(), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("screenshot captured [image: image/png, 5 bytes]", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_TextOnlyMessages_StayUnmarked()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("plain text");
    conversation.AddAssistantMessage("the answer");
    ContextEditTool tool = Tool(conversation);

    ToolResult result = await tool.ExecuteAsync(ListInput(), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.DoesNotContain("[image]", result.Content, StringComparison.Ordinal);
    Assert.Contains("plain text", result.Content, StringComparison.Ordinal);
    Assert.Contains("the answer", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task List_UserMessageWithImagePart_IsMarked()
  {
    Conversation conversation = new();
    conversation.AddUserMessage("what is this",
        [new MessagePart.ImagePart("image/jpeg", "aGVsbG8=")]);
    ContextEditTool tool = Tool(conversation);

    ToolResult result = await tool.ExecuteAsync(ListInput(), TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("what is this [image: image/jpeg, 5 bytes]", result.Content, StringComparison.Ordinal);
  }
}

/// <summary>The composition's adapter over the Conversation Domain service: the
///     Tool Domain's port is implemented by bridging to ConversationContextService.
///     Lives here because the composition project is not a test dependency.</summary>
internal sealed class ConversationContextServiceAdapter(Conversation conversation) : IConversationContextService
{
  private readonly ConversationContextService _service = new(conversation);

  public string List() => _service.List();

  public Result<string> Remove(string selection) => _service.Remove(selection);

  public Result<string> Shorten(string selection, string text) => _service.Shorten(selection, text);
}
