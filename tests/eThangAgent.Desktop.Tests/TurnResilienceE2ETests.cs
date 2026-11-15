using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.OpenRouter.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The turn-resilience behaviours observed through the COMPOSED app: a transient
///     provider fault retries the same request, a refusing model is dropped for the session
///     and the turn is resent, and the user's stored choice survives both.
///     <para>WHY THROUGH COMPOSITION: the retry loop lives in the agent domain and the drop
///     loop lives in the app, but what the user sees is the product of the two wired
///     together. A hand-built view-model would prove none of the wiring.</para>
///     <para>THE PROVIDER'S OWN RETRY IS PINNED TO ONE ATTEMPT in every test here, so the
///     attempts counted are the TURN's. At the default four a 503 is absorbed inside a single
///     provider call, and the two layers' attempts are indistinguishable.</para></summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class TurnResilienceE2ETests
{
  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  /// <summary>A transient fault retries the same turn and the answer still arrives, with one
  ///     retry line naming the attempt and the wait.</summary>
  [Fact]
  public async Task ATransientFault_RetriesTheTurn_AndTheAnswerArrives()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ProviderRetry = new RetryPolicy(maxAttempts: 1);
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync().ConfigureAwait(true);

    // The first request to this model is refused with a retryable 503; the second answers.
    _ = host.Mock.ReturnsStatusForModel(E2E.SessionModel, 503, 200);
    _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletion("answered after the wait"));

    await host.Vm.RunTurnAsync("say it back").WaitAsync(TestCeiling.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The answer is rendered, and the retry line names the attempt and the wait.
    Assert.Contains(host.Vm.Transcript.Entries.OfType<AssistantTextEntry>(),
        entry => entry.Text.Contains("answered after the wait", StringComparison.Ordinal));
    Assert.Contains(host.Vm.Transcript.Notices,
        notice => notice.Contains("retry 2 of 6", StringComparison.Ordinal));

    // The turn persisted ONE user message: a retry resends the request, not the input.
    Result<IReadOnlyList<Message>> read = await host.Services
        .GetRequiredService<IAgentStore>()
        .GetTranscriptAsync(host.RootId, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.True(read.IsSuccess);
    _ = Assert.Single(read.Value, m => m.Role is Role.User);
  }

  /// <summary>A refusing model is dropped for the session, the turn is resent against the
  ///     surviving model, and the user's stored choice is left alone.</summary>
  [Fact]
  public async Task ARefusingModel_IsDropped_AndTheTurnIsResent()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ProviderRetry = new RetryPolicy(maxAttempts: 1);
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync().ConfigureAwait(true);

    // Two models: the first is refused on its merits (401), the second answers. The
    // backup's answer is scripted on the REQUEST CONTENT rather than on the model queue,
    // because a model queue is drawn in arrival order and the refusal consumes it.
    host.Services.GetRequiredService<SessionModelPreferences>().ModelIds =
        [E2E.SessionModel, "mock/backup-model"];
    _ = host.Mock.ReturnsStatusForModel(E2E.SessionModel, 401);
    _ = host.Mock.ReturnsForRequestContaining("say it back", RawCompletion("answered by the backup"));

    await host.Vm.RunTurnAsync("say it back").WaitAsync(TestCeiling.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The backup served the turn, and one drop line names the model that was removed.
    Assert.Contains(host.Vm.Transcript.Entries.OfType<AssistantTextEntry>(),
        entry => entry.Text.Contains("answered by the backup", StringComparison.Ordinal));
    Assert.Contains(host.Vm.Transcript.Notices,
        notice => notice.Contains("removed for this session", StringComparison.Ordinal)
            && notice.Contains("1 model left", StringComparison.Ordinal));

    // The user's stored preference still names BOTH models: a drop is per session.
    SessionModelPreferences stored = host.Services.GetService<SessionModelPreferences>()
        ?? throw new InvalidOperationException("the session's preferences must be registered.");
    Assert.NotNull(stored.ModelIds);
    Assert.Equal(2, stored.ModelIds.Count);
  }
}
