using System.Text.Json;

namespace eThangAgent.Desktop.Tests;

/// <summary>Full-vertical E2E: a scripted usage frame pushes the real accountant past
///     the 80% threshold, the real compactor rewrites the conversation (summarizer falls
///     back to the serving mock model), and the summarizer call must appear between turns.</summary>
[Collection("Desktop E2E")]
public class CompactionE2ETests
{
  private static string RawCompletionWithUsage(string content, int promptTokens, int completionTokens) =>
      JsonSerializer.Serialize(new
      {
        choices = new[] { new { message = new { content } } },
        usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens },
      });

  [Fact]
  public async Task ThresholdCrossing_TurnCompacts_AndSummarizerCallFires()
  {
    using E2E.HostHarness host = await new E2E.HostHarness().StartAsync();

    // Turn 1: 30K chars of assistant bulk makes eviction possible; usage crosses 90%.
    _ = host.Mock.ReturnsForModel(E2E.SessionModel,
        RawCompletionWithUsage(new string('h', 200_000), 110_000, 64));
    await host.Vm.RunTurnAsync("start some work").WaitAsync(
        TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

    // Turns 2-3 build groups; each turn's usage re-crosses the threshold so the
    // trigger keeps firing every iteration boundary.
    _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletionWithUsage("t2", 110_000, 32));
    await host.Vm.RunTurnAsync("continue the work").WaitAsync(
        TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

    _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletionWithUsage("t3", 110_000, 32));
    await host.Vm.RunTurnAsync("keep going").WaitAsync(
        TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

    _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletionWithUsage("t4", 110_000, 32));
    await host.Vm.RunTurnAsync("and again").WaitAsync(
        TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken);

    IReadOnlyList<string> bodies = host.Mock.RequestBodies;
    bool failNotice = bodies.Any(b => b.Contains("Context compaction failed", StringComparison.Ordinal));
    Assert.True(failNotice || bodies.Count >= 6,
        $"no failure notice and only {bodies.Count} calls — trigger never fired");

    // The compaction handoff: a summarizer request renders the evicted conversation
    // (verbatim marker from DefaultContextCompactor.Render).
    bool summarizerCall = bodies.Any(b => b.Contains("## Evicted messages", StringComparison.Ordinal));
    Assert.True(summarizerCall, $"render absent; count={bodies.Count}; markers: " + string.Join(";", bodies.Select((b, i) => $"[{i}] len={b.Length} evicted={b.Contains("## Evicted", StringComparison.Ordinal)} t1={b.Contains("turn one", StringComparison.Ordinal)}")));

    // And some request after compaction carries the summary marker.
    bool summaryRide = bodies.Any(b => b.Contains("compacted", StringComparison.OrdinalIgnoreCase));
    Assert.True(summaryRide, "expected the summary message to ride a later request");
  }
}
