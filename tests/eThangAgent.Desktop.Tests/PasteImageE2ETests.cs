using eThangAgent.ConversationDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #20 E2E: a staged paste image rides the user message into the
///     conversation and onto the next provider request wire as an input_image part.
///     The composition and the loop are REAL; the image enters through the same
///     staging surface the view's Ctrl+V handler calls.</summary>
[Trait("Category", "Integration")]
[Collection("Desktop E2E")]
public sealed class PasteImageE2ETests
{
  private const string TinyPngBase64 =
      "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=";

  [Fact]
  public async Task StagedImage_RidesUserMessageOntoProviderWire()
  {
    using E2E.HostHarness harness = new();
    _ = await harness.StartAsync().ConfigureAwait(true);

    byte[] png = Convert.FromBase64String(TinyPngBase64);
    Assert.True(harness.Vm.AttachImageBytes(png, "clipboard.png"));

    _ = harness.Mock.Returns(E2E.RawCompletion("seen the image"));
    await E2E.RunTurnAsync(harness.Vm, "what is this").ConfigureAwait(true);

    // The conversation user message carries the image part...
    Conversation conversation = harness.Services.GetRequiredService<Conversation>();
    Message user = Assert.Single(conversation.Messages, m => m.Role is Role.User);
    MessagePart.ImagePart part = Assert.IsType<MessagePart.ImagePart>(Assert.Single(user.Parts!));
    Assert.Equal("image/png", part.MediaType);

    // ...and the NEXT provider request serializes it as an input_image part.
    bool onWire = harness.Mock.RequestBodies.Any(body =>
    {
      using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
      return doc.RootElement.TryGetProperty("input", out System.Text.Json.JsonElement input)
          && input.EnumerateArray().Any(item =>
          item.ValueKind == System.Text.Json.JsonValueKind.Object
          && item.TryGetProperty("role", out System.Text.Json.JsonElement role) && role.GetString() == "user"
          && item.TryGetProperty("content", out System.Text.Json.JsonElement content)
          && content.EnumerateArray().Any(p =>
              p.TryGetProperty("type", out System.Text.Json.JsonElement pt) && pt.GetString() == "input_image"
              && p.TryGetProperty("image_url", out System.Text.Json.JsonElement url)
              && (url.GetString() ?? "").StartsWith("data:image/png;base64,", StringComparison.Ordinal)));
    });
    Assert.True(onWire, "the provider request must carry the pasted image part");
  }
}
