using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>An image-generation server tool returns its artifact INSIDE the provider's
///     response, where nothing on this side could reach it (issue #230): the user paid
///     for an image and got a markdown reference to a path that did not exist here.
///     The loop must persist the bytes into the workspace and NAME the artifact in the
///     surfaced line, so the transcript shows what was produced.</summary>
public class ServerToolImageSurfacingTests
{
  private const string PngBase64 = "iVBORw0KGgoAAQI=";

  private static ModelConfig DefaultConfig =>
      ModelConfig.Create("test-model", null, 100, 0.5f, 8192).Value!;

  private static ModelConfig VisionConfig =>
      ModelConfig.Create("vision-model", null, 100, 0.5f, 8192, acceptsImageInput: true).Value!;

  private static Agent BuildAgent(IServerToolImageStore store, ModelConfig config) =>
      new(new ScriptedProvider(new ModelResponse("done", [],
              ServerToolCalls: [new ServerToolCall("image_generation", null, null,
                  new ServerToolCallImage("image/png", PngBase64))])),
          new Conversation(), config, new ToolRegistry([]),
          new AgentOptions { ServerToolImages = store });

  [Fact]
  public async Task ImageGenerationCall_SavesArtifactAndNamesItInTheSurfacedLine()
  {
    FakeImageStore store = new(Result.Success(Saved));
    Agent agent = BuildAgent(store, DefaultConfig);
    List<string> surfaced = [];

    Result<string> result = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsSuccess);
    // Verbatim contract: the surfaced line NAMES the artifact (the pre-fix line was a
    // bare "[image_generation]" that told the user nothing).
    Assert.StartsWith("[image_generation] .ethang/images/a.png", Assert.Single(surfaced),
        StringComparison.Ordinal);
    Assert.Contains(agent.Conversation.Messages, m => m.Role is Role.System
        && m.Content.StartsWith("[image_generation] .ethang/images/a.png", StringComparison.Ordinal));
    // The bytes actually reached the store, with the media type the wire carried.
    Assert.Equal("image/png", store.SavedMediaType);
    Assert.Equal(PngBase64, store.SavedBase64);
  }

  [Fact]
  public async Task ImageGenerationCall_DeliversTheImageOnTheToolResultCallback()
  {
    // The Desktop renders images through the tool-result surface (the computer
    // screenshot path). The artifact rides that same callback so the user SEES the
    // picture instead of a path string.
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
    // A failed write is a TYPED error the model sees, never a silent skip: the user
    // must learn the artifact could not be saved.
    FakeImageStore store = new(Result.Failure<SavedServerToolImage>(
        new DomainError("PathOutsideWorkspace", "refused")));
    Agent agent = BuildAgent(store, DefaultConfig);
    List<string> surfaced = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    string line = Assert.Single(surfaced);
    Assert.StartsWith("[image_generation] Error [PathOutsideWorkspace]", line, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ImageGenerationCall_NoStoreWired_NamesTheToolOnly()
  {
    // Legacy wiring (no store) keeps the pre-existing shape: the call is surfaced,
    // the image is not persisted, and nothing throws.
    Agent agent = new(new ScriptedProvider(new ModelResponse("done", [],
            ServerToolCalls: [new ServerToolCall("image_generation", null, null,
                new ServerToolCallImage("image/png", PngBase64))])),
        new Conversation(), DefaultConfig, new ToolRegistry([]));
    List<string> surfaced = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    Assert.Equal("[image_generation]", Assert.Single(surfaced));
  }

  [Fact]
  public async Task ImageGenerationCall_VisionModel_LineNamesThePathAndHowToSeeIt()
  {
    // RECORDED DESIGN DECISION (issue #230 leaves the carrier open; the wire settles it).
    // The artifact reaches the MODEL as a PATH, not as an inlined image part:
    //   - an assistant message cannot carry input_image (the Responses API's Message
    //     input shape accepts input_image only on user/system/developer roles), so
    //     relaxing Conversation.NormalizeParts to allow assistant parts would emit a
    //     shape the provider rejects;
    //   - a synthetic tool result is unattributable (function_call_output needs a
    //     call_id from a function_call item, and a server-tool item is not one);
    //   - inlining the bytes costs 1.3-2.7 M base64 characters for a 1024x1024 PNG,
    //     re-sent on EVERY later request.
    // The path costs a dozen tokens and read_image pulls the bytes on demand - the
    // issue's own third option. So the line names the path and says how to see it.
    FakeImageStore store = new(Result.Success(Saved));
    Agent agent = BuildAgent(store, VisionConfig);
    List<string> surfaced = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    string line = Assert.Single(surfaced);
    Assert.Contains(".ethang/images/a.png", line, StringComparison.Ordinal);
    Assert.Contains("read_image", line, StringComparison.Ordinal);
    // Nothing was inlined into the history: the path is the carrier.
    Assert.DoesNotContain(agent.Conversation.Messages, m => m.Parts is { Count: > 0 });
  }

  [Fact]
  public async Task ImageGenerationCall_TextOnlyModel_GetsThePathWithoutTheReadImageHint()
  {
    // A model with no image input cannot read_image the artifact, so the line names the
    // path for the USER but must not invite a call that would fail.
    FakeImageStore store = new(Result.Success(Saved));
    Agent agent = BuildAgent(store, DefaultConfig);
    List<string> surfaced = [];

    _ = await agent.SendMessage("draw a cube",
        new TurnCallbacks(OnSystemMessage: surfaced.Add), ct: TestContext.Current.CancellationToken);

    string line = Assert.Single(surfaced);
    Assert.Contains(".ethang/images/a.png", line, StringComparison.Ordinal);
    Assert.DoesNotContain("read_image", line, StringComparison.Ordinal);
  }

  private static readonly SavedServerToolImage Saved =
      new(".ethang/images/a.png", "C:/ws/.ethang/images/a.png");

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
