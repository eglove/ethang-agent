using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>Task 19 E2E: with computer use enabled, the loop's computer.observe result carries
///     the screenshot image part into the conversation, and the NEXT provider request wire
///     contains the image part (OpenRouter row: parts on the tool message). The computer access
///     is a FAKE (the real broker path is the ACL integration suite's job); the composition and
///     the loop are REAL.</summary>
[Trait("Category", "Integration")]
public sealed class ComputerUseWireE2ETests
{
  private sealed class FakeComputerAccess : IComputerAccess
  {
    public Task<ComputerOutcome> ExecuteAsync(ComputerCommand command, CancellationToken ct = default)
    {
      return Task.FromResult<ComputerOutcome>(new ComputerOutcome.Observation(
          "state_id s-1\nwindow: \"fake\" id=1 bounds=[0,0,100,100]",
          "s-1",
          [],
          new ComputerFrameRef("frame-1", 4, 4),
          null,
          new ToolResultImage("image/png", "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII=")));
    }
  }

  /// <summary>True when one provider request body carries the screenshot as an
  ///     input_image part on the function_call_output item for <paramref name="callId"/>.</summary>
  private static bool BodyCarriesScreenshot(string body, string callId)
  {
    using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("input", out System.Text.Json.JsonElement input)
        || input.ValueKind != System.Text.Json.JsonValueKind.Array)
    {
      return false;
    }

    foreach (System.Text.Json.JsonElement item in input.EnumerateArray())
    {
      if (IsScreenshotOutputFor(item, callId, out System.Text.Json.JsonElement output)
          && OutputHasImagePart(output))
      {
        return true;
      }
    }

    return false;
  }

  private static bool IsScreenshotOutputFor(System.Text.Json.JsonElement item, string callId,
      out System.Text.Json.JsonElement output)
  {
    output = default;
    return item.ValueKind == System.Text.Json.JsonValueKind.Object
        && item.TryGetProperty("type", out System.Text.Json.JsonElement type)
        && type.GetString() == "function_call_output"
        && item.TryGetProperty("call_id", out System.Text.Json.JsonElement callIdElement)
        && callIdElement.GetString() == callId
        && item.TryGetProperty("output", out output)
        && output.ValueKind == System.Text.Json.JsonValueKind.Array;
  }

  private static bool OutputHasImagePart(System.Text.Json.JsonElement output)
      => output.EnumerateArray().Any(part =>
          part.TryGetProperty("type", out System.Text.Json.JsonElement partType)
          && partType.GetString() == "input_image"
          && part.TryGetProperty("image_url", out System.Text.Json.JsonElement url)
          && url.GetString() is { } imageUrl
          && imageUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal));

  private sealed class FakeComputerAccessProvider : IComputerAccessProvider
  {
    public IComputerAccess? ForWorkspace(string workspaceRoot) => new FakeComputerAccess();
  }

  [Fact]
  public async Task Observe_Screenshot_ReachesNextProviderRequest()
  {
    using E2E.HostHarness harness = new()
    {
      ComputerAccessProviderFactory = () => new FakeComputerAccessProvider(),
    };
    _ = await harness.StartAsync(computerUse: true).ConfigureAwait(true);

    string observeArgs = System.Text.Json.JsonSerializer.Serialize(new
    {
      timeoutSeconds = 60,
      action = "observe",
      app_ref = new { name = "fake" },
    });
    _ = harness.Mock.Returns(E2E.ToolCall("c1", "computer", observeArgs));
    _ = harness.Mock.Returns(E2E.RawCompletion("seen the screenshot"));

    await E2E.RunTurnAsync(harness.Vm, "look at the screen").ConfigureAwait(true);
    await E2E.RunTurnAsync(harness.Vm, "and again").ConfigureAwait(true);

    // The screenshot rides as an input_image part inside the function_call_output item
    // whose call_id matches the computer tool call that produced it.
    bool imageOnWire = harness.Mock.RequestBodies.Any(body => BodyCarriesScreenshot(body, "c1"));
    Assert.True(imageOnWire, "the provider request must carry the screenshot image part");
  }

  // I12: with the PostTurnUser projection the image part of the tool result rides a
  // synthetic user message. The persisted conversation is provider-neutral; assert it
  // carries the image part (which a provider wire maps onto the synthetic user message
  // - pinned in PartsProjectionWireTests).
  [Fact]
  public async Task Observe_Screenshot_ConversationCarriesImagePart_AcrossTurnPersistence()
  {
    using E2E.HostHarness harness = new() { ComputerAccessProviderFactory = () => new FakeComputerAccessProvider() };
    _ = await harness.StartAsync(computerUse: true).ConfigureAwait(true);

    string observeArgs = System.Text.Json.JsonSerializer.Serialize(new
    {
      timeoutSeconds = 60,
      action = "observe",
      app_ref = new { name = "fake" },
    });
    _ = harness.Mock.Returns(E2E.ToolCall("c1", "computer", observeArgs));
    _ = harness.Mock.Returns(E2E.RawCompletion("seen"));
    await E2E.RunTurnAsync(harness.Vm, "look").ConfigureAwait(true);

    ConversationDomain.Conversation conversation =
        harness.Services.GetRequiredService<ConversationDomain.Conversation>();
    bool hasImagePart = conversation.Messages.Any(
        m => m.Parts is { } parts && parts.OfType<ConversationDomain.MessagePart.ImagePart>().Any());
    Assert.True(hasImagePart, "the conversation must carry the image part across turn persistence");
  }
}
