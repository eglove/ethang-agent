using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>User-message image parts (issue #20): SendMessage's optional imageParts
///     ride the user message as MessagePart.ImagePart entries; null keeps the legacy
///     shape; the parts reach the provider request, not only the conversation.</summary>
public class AgentUserImagePartsTests
{
  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  [Fact]
  public async Task SendMessage_WithImageParts_UserMessageCarriesParts()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    Result<string> result = await agent.SendMessage("what is this",
        imageParts: [new MessagePart.ImagePart("image/png", "aGVsbG8=")],
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    Message user = agent.Conversation.Messages.Single(m => m.Role is Role.User);
    Assert.Equal("what is this", user.Content);
    MessagePart.ImagePart image = Assert.IsType<MessagePart.ImagePart>(Assert.Single(user.Parts!));
    Assert.Equal("image/png", image.MediaType);
  }

  [Fact]
  public async Task SendMessage_WithoutImageParts_UserMessageStaysLegacyShape()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    _ = await agent.SendMessage("plain", ct: TestContext.Current.CancellationToken);

    Message user = agent.Conversation.Messages.Single(m => m.Role is Role.User);
    Assert.Null(user.Parts);
  }

  [Fact]
  public async Task SendMessage_EmptyImageParts_UserMessageStaysLegacyShape()
  {
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    _ = await agent.SendMessage("plain", imageParts: [], ct: TestContext.Current.CancellationToken);

    Message user = agent.Conversation.Messages.Single(m => m.Role is Role.User);
    Assert.Null(user.Parts);
  }

  [Fact]
  public async Task SendMessage_WithImageParts_RequestCarriesUserParts()
  {
    // The parts must reach the provider request, not only the conversation.
    FakeProvider provider = new(Result.Success(new ModelResponse("done", [])));
    Agent agent = new(provider, new Conversation(), DefaultConfig, new ToolRegistry([]));

    _ = await agent.SendMessage("look",
        imageParts: [new MessagePart.ImagePart("image/jpeg", "aGVsbG8=")],
        ct: TestContext.Current.CancellationToken);

    ModelRequest request = Assert.Single(provider.RequestsSeen);
    Message sent = Assert.Single(request.Messages, m => m.Role is Role.User);
    Assert.NotNull(sent.Parts);
    _ = Assert.IsType<MessagePart.ImagePart>(Assert.Single(sent.Parts));
  }
}
