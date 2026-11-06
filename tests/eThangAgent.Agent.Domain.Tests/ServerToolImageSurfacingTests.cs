using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>An image-generation server tool returns its artifact INSIDE the provider's
///     response, where nothing on this side could reach it (issue #230): the user paid for an
///     image and got a markdown reference to a path that did not exist here.
///     <para>The artifact is DELIVERED, never written: the bytes ride the tool-result callback,
///     so the transcript renders the picture. Nothing lands in the user's directory, and the
///     conversation records the line without carrying the bytes.</para></summary>
public class ServerToolImageSurfacingTests
{
  private const string PngBase64 = "iVBORw0KGgoAAQI=";

  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  private static Agent BuildAgent(ModelConfig config) =>
      new(new ScriptedProvider(new ModelResponse("done", [],
              ServerToolCalls: [new ServerToolCall("image_generation", null, null,
                  new ServerToolCallImage("image/png", PngBase64))])),
          new Conversation(), config, new ToolRegistry([]));

  [Fact]
  public async Task ImageGenerationCall_SurfacesTheLineAndRecordsItInTheConversation()
  {
    Agent agent = BuildAgent(DefaultConfig);
    List<string> surfaced = [];

    Result<string> result = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    string line = Assert.Single(surfaced);
    Assert.Equal("[image_generation] image generated (image/png)", line);

    // The conversation records WHAT was produced, and carries no bytes: a generated image
    // re-sent on every later request would cost hundreds of thousands of tokens.
    Message carrier = Assert.Single(agent.Conversation.Messages,
        m => m.Role is Role.System && m.Content == line);
    Assert.Null(carrier.Parts);
  }

  [Fact]
  public async Task ImageGenerationCall_DeliversTheImageOnTheToolResultCallback()
  {
    // The Desktop renders images through the tool-result surface (the computer screenshot
    // path), so the artifact rides that same callback.
    Agent agent = BuildAgent(DefaultConfig);
    List<(string Name, ToolResult? Rich)> results = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnToolResult: (name, summary, full, isError, rich) =>
            results.Add((name, rich))),
        ct: TestContext.Current.CancellationToken);

    (string name, ToolResult? rich) = Assert.Single(results);
    Assert.Equal("image_generation", name);
    Assert.NotNull(rich);
    ToolResultImage image = Assert.Single(rich.Images!);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(PngBase64, image.Base64Data);
  }

  [Fact]
  public async Task ImageGenerationCall_WritesNothingToDisk()
  {
    // The harness adds no artifacts to the user's machine outside its database, so the
    // surfaced line names no saved path: the artifact is delivered, never stored.
    Agent agent = BuildAgent(DefaultConfig);
    List<string> surfaced = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    string line = Assert.Single(surfaced);
    Assert.DoesNotContain(".png", line, StringComparison.Ordinal);
    Assert.DoesNotContain("saved to", line, StringComparison.Ordinal);
  }

  private sealed class ScriptedProvider(ModelResponse response) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(response));
  }
}
