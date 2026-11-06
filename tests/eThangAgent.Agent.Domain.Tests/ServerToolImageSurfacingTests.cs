using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>An image-generation server tool returns its artifact INSIDE the provider's
///     response, where nothing on this side could reach it: the user paid for an image and got
///     a markdown reference to a path that did not exist here.
///     <para>The artifact is PERSISTED into the session's scratch store and NAMED by its
///     ABSOLUTE path, so the model can read_image it on a later turn and a model that wants it
///     kept copies it into the workspace. The bytes also ride the tool-result callback so the
///     transcript renders the picture. Nothing is written into the user's workspace.</para>
///     </summary>
public class ServerToolImageSurfacingTests
{
  private const string PngBase64 = "iVBORw0KGgoAAQI=";

  private const string SavedPath = "C:/Temp/ethang-agent/images/session-a/generated-1.png";

  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", 100, 0.5f, 8192).Value!;

  private static Agent BuildAgent(IServerToolImageStore store, ModelConfig config) =>
      new(new ScriptedProvider(new ModelResponse("done", [],
              ServerToolCalls: [new ServerToolCall("image_generation", null, null,
                  new ServerToolCallImage("image/png", PngBase64))])),
          new Conversation(), config, new ToolRegistry([]),
          new AgentOptions { ServerToolImages = store });

  [Fact]
  public async Task ImageGenerationCall_SavesTheArtifactAndNamesItsAbsolutePath()
  {
    FakeImageStore store = new(Result.Success(Saved));
    Agent agent = BuildAgent(store, DefaultConfig);
    List<string> surfaced = [];

    Result<string> result = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // The line NAMES the path: that is what the model hands to read_image on a later turn.
    Assert.Equal($"[image_generation] {SavedPath}", Assert.Single(surfaced));
    Assert.Contains(agent.Conversation.Messages, m => m.Role is Role.System
        && m.Content == $"[image_generation] {SavedPath}");
    // The bytes actually reached the store, with the media type the wire carried.
    Assert.Equal("image/png", store.SavedMediaType);
    Assert.Equal(PngBase64, store.SavedBase64);
  }

  [Fact]
  public async Task ImageGenerationCall_DeliversTheImageOnTheToolResultCallback()
  {
    // The Desktop renders images through the tool-result surface (the computer screenshot
    // path), so the artifact rides that same callback and the user SEES the picture.
    FakeImageStore store = new(Result.Success(Saved));
    Agent agent = BuildAgent(store, DefaultConfig);
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
  public async Task ImageGenerationCall_StoreFailure_SurfacesATypedError()
  {
    // A failed write is a TYPED error the model sees, never a silent skip: the user must
    // learn the artifact could not be saved.
    FakeImageStore store = new(Result.Failure<SavedServerToolImage>(
        new DomainError("ImageUnwritable", "refused")));
    Agent agent = BuildAgent(store, DefaultConfig);
    List<string> surfaced = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("[image_generation] Error [ImageUnwritable]", Assert.Single(surfaced),
        StringComparison.Ordinal);
  }

  [Fact]
  public async Task ImageGenerationCall_NoStoreWired_NamesTheToolOnly()
  {
    // Legacy wiring (no store) keeps the pre-existing shape: the call is surfaced, the image
    // is not persisted, and nothing throws.
    Agent agent = new(new ScriptedProvider(new ModelResponse("done", [],
            ServerToolCalls: [new ServerToolCall("image_generation", null, null,
                new ServerToolCallImage("image/png", PngBase64))])),
        new Conversation(), DefaultConfig, new ToolRegistry([]));
    List<string> surfaced = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.Equal("[image_generation]", Assert.Single(surfaced));
  }

  private static readonly SavedServerToolImage Saved = new(SavedPath, "generated-1.png");

  private sealed class FakeImageStore(Result<SavedServerToolImage> outcome) : IServerToolImageStore
  {
    public string? SavedMediaType { get; private set; }

    public string? SavedBase64 { get; private set; }

    public Task<Result<SavedServerToolImage>> SaveAsync(string mediaType, string base64Data,
        CancellationToken ct = default)
    {
      SavedMediaType = mediaType;
      SavedBase64 = base64Data;
      return Task.FromResult(outcome);
    }
  }

  private sealed class ScriptedProvider(ModelResponse response) : IModelProvider
  {
    public Task<Result<ModelResponse>> SendAsync(ModelConfig config, ModelRequest request, CancellationToken ct = default)
        => Task.FromResult(Result.Success(response));
  }
}
