using eThangAgent.ToolDomain;

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
          new ToolResultImage("image/png", Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }))));
    }
  }

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
    _ = harness.Mock.Returns(/*lang=json,strict*/ "{\"choices\":[{\"message\":{\"content\":\"seen the screenshot\"}}]}");

    await E2E.RunTurnAsync(harness.Vm, "look at the screen").ConfigureAwait(true);
    await E2E.RunTurnAsync(harness.Vm, "and again").ConfigureAwait(true);

    IReadOnlyList<string> bodies = harness.Mock.RequestBodies;
    bool imageOnWire = false;
    foreach (string body in bodies)
    {
      using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
      if (doc.RootElement.GetRawText().Contains("data:image/png;base64,", StringComparison.Ordinal))
      {
        imageOnWire = true;
      }
    }
    Assert.True(imageOnWire, "the provider request must carry the screenshot image part");
  }
}
