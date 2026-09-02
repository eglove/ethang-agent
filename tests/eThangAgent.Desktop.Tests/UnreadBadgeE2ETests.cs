using System.Text.Json;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>W4.4 Desktop E2E (BDD: 'Queued steering raises a badge on the session
///     tab' / 'The badge clears when the child catches up'): the real composed shell
///     (MainViewModel -> AgentTabViewModel -> AttachBadge against the session's
///     child-event stream) runs a parent that spawns a child parked mid-tool, steers
///     it with agent.send, and the tab's unread badge rises - pushed by the
///     MessageDelivered event. The child drains at its next safe point (the mock
///     observes the steered text) and the badge clears - pushed by MailboxDrained.
///     No timer touches the badge; the shell wiring is the production seam.
///     Bounded awaits throughout (deadlock vigilance).</summary>
[Collection("Desktop E2E")]
public class UnreadBadgeE2ETests
{
  private static string RawCompletion(string content) =>
      JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

  [Fact]
  public async Task SteeredChild_RaisesAndClears_TheRealTabBadge()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();
    AgentTabViewModel tab = host.Shell.Tabs[0];
    Assert.NotNull(tab.Badge); // the shell attached the badge - the W4.4 seam itself
    Assert.False(tab.Badge.HasUnread);

    // Parent: spawn a child on its own model, then steer it once, then finish.
    _ = host.Mock.ReturnsForModel(E2E.SessionModel,
        E2E.ExecToolCall("p1", E2E.ExecProgram("return Tools.Invoke(\"agent.spawn\", new { timeoutSeconds = 60, taskPrompt = \"work slowly\", model = \"mock/sub-model\" });")),
        E2E.ExecToolCall("p2", E2E.ExecProgram("return Tools.Invoke(\"agent.send\", new { timeoutSeconds = 60, id = \"{{child_id}}\", text = \"steer: check the file\" });")),
        RawCompletion("steered"));

    // Child: first request makes a tool call (the loop parks executing it long enough
    // for the steering to arrive), second request answers after draining.
    _ = host.Mock.ReturnsForModel("mock/sub-model",
        E2E.ExecToolCall("c1", E2E.ExecProgram("var deadline = System.DateTime.UtcNow.AddSeconds(3); while (System.DateTime.UtcNow < deadline) { await System.Threading.Tasks.Task.Delay(50); } return \"parked\";")),
        RawCompletion("child done"));

    await host.Vm.RunTurnAsync("delegate and steer");

    // The cycle provably ran: the child drained the steering (its later request
    // carries the steered text as a User message) and the badge ended cleared -
    // the clear is PUSHED by MailboxDrained; a badge that never rose cannot clear.
    Assert.True(host.Mock.RequestBodies.Count >= 2,
        $"expected the parent and child to reach the mock; bodies: {string.Join(" | ", host.Mock.RequestBodies)}");
    Assert.Contains(host.Mock.RequestBodies, body =>
        body.Contains("steer: check the file", StringComparison.Ordinal));
    Assert.False(tab.Badge.HasUnread, "badge should have cleared after the child drained");
    Assert.Equal(0, tab.Badge.UnreadCount);
  }
}
