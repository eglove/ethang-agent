using System.Text.Json;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Composition.Tests;

/// <summary>The image an image-generation SERVER tool produces must reach a file the user and
///     the model can both reach, in a real composed session.
///     <para>WHY THIS IS A COMPOSITION TEST AND NOT A DOMAIN TEST. The agent loop has persisted
///     the artifact correctly since issue #230 was closed, and its own test proves it against a
///     fake store. What a session actually gets is decided one layer up: whether the container
///     hands the loop a store at all. A missing hand-off is invisible to the loop's own test and
///     fatal in production - which is exactly how this shipped dark. The surfaced line was a bare
///     '[image_generation]' and no file existed anywhere on the machine.</para>
///     <para>SO THE ASSERTION IS THE FILE, read back off disk, plus the line the transcript
///     shows. Both are absent when the wiring is absent.</para></summary>
[Trait("Category", "Process")]
public class ServerToolImageCompositionTests
{
  // A real 1x1 PNG. The store SNIFFS the bytes and rejects a claim they contradict, so a
  // signature-only fixture would fail the admission rule for the wrong reason.
  private const string PngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

  /// <summary>The output item the live wire delivers: OpenRouter's own item type, carrying the
  ///     artifact under 'result' as a data URL (verified live 2026-10).</summary>
  private static string ImageGenerationResponse() => JsonSerializer.Serialize(new
  {
    model = (string?)null,
    status = "completed",
    output = new object[]
    {
      new
      {
        type = "openrouter:image_generation",
        id = "ig_test",
        status = "completed",
        result = $"data:image/png;base64,{PngBase64}",
        prompt = "a single red apple on a white table",
      },
      new
      {
        type = "message",
        role = "assistant",
        content = new object[] { new { type = "output_text", text = "here is your image" } },
      },
    },
    usage = new { input_tokens = 11, output_tokens = 5 },
  });

  [Fact]
  public async Task ImageGenerationServerTool_PersistsTheArtifactAndNamesItsPath()
  {
    using ComposedHostHarness session = await ComposedHostHarness.StartAsync([
        "alpha/model"]);
    session.Provider.Responder = _ => MockAnswer.Ok(ImageGenerationResponse());
    List<string> surfaced = [];

    Result<string> turn = await session.Handler.Handle(
        new SendMessageCommand("draw me something"),
        new TurnCallbacks(OnSystemMessage: surfaced.Add),
        ct: TestContext.Current.CancellationToken)
        .WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.True(turn.IsSuccess, turn.Error?.Message);

    // THE LINE NAMES THE FILE, and the file is there. The pre-fix line was the bare
    // '[image_generation]': a user who paid for an image, and a model told to read_image a
    // path it was never given.
    string line = Assert.Single(surfaced);
    const string Prefix = "[image_generation] ";
    Assert.StartsWith(Prefix, line, StringComparison.Ordinal);
    string path = line[Prefix.Length..];
    Assert.True(Path.IsPathRooted(path), "the surfaced path must be absolute, got '" + path + "'");
    Assert.True(File.Exists(path), "the surfaced line named '" + path + "', which does not exist");

    // THE BYTES ARE THE ONES THE PROVIDER SENT, not a placeholder the store invented.
    Assert.Equal(Convert.FromBase64String(PngBase64), await File.ReadAllBytesAsync(path,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    // The transcript records the same line, so a resumed session still names its artifact.
    Assert.Contains(session.Conversation.Messages, m => m.Role is Role.System
        && m.Content == line);
  }
}
