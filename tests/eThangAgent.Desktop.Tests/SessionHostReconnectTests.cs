using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>A host that dies mid-turn is not the turn's fault. The app reconnects through the
///     port and sends the SAME turn again, at most <c>MaxHostReconnects</c> times, and each
///     attempt renders one notice; when the reconnects are spent the transport's own failure is
///     the turn's outcome and it names how many were attempted.
///     <para>ONLY HOST LOSS IS RECONNECTED. A payload the seam refused (<c>RootTurnPayloadTooLarge</c>),
///     and a stopped turn (<c>TurnCancelled</c>) are decisions the turn itself answers, and
///     reconnecting on them would spend the session's reconnects on a request that can never
///     be sent.</para></summary>
public sealed class SessionHostReconnectTests
{
  /// <summary>The ordinary case: the host dies, one reconnect brings it back, the resent turn
  ///     answers, and the transcript holds the answer with exactly one reconnect notice.</summary>
  [Fact]
  public async Task HostDiesOnce_OneReconnectResendsAndTheTurnSucceeds()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.FailsBeforeSettling.Enqueue(HostLost());

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal(1, session.Host.Reconnects);
    Assert.Equal(2, session.Host.TurnsSent);
    Assert.Contains("answer", session.ViewModel.Transcript.AssistantText, StringComparison.Ordinal);
    _ = Assert.Single(session.ViewModel.Transcript.Notices,
        n => n.Contains("reconnect 1 of 2", StringComparison.Ordinal));
    // The turn that succeeded is the turn that was sent: nothing reached the in-process runner.
    Assert.Equal(0, session.Runner.Invocations);
  }

  /// <summary>The resent turn carries the same request the failed attempt carried: the input is
  ///     the turn's, not the attempt's, so a resend is a resend rather than a new message.</summary>
  [Fact]
  public async Task TheResentTurn_CarriesTheSameInput()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.FailsBeforeSettling.Enqueue(HostLost());

    await session.ViewModel.SubmitAsync("the same question").ConfigureAwait(true);

    Assert.Equal("the same question", session.Host.LastRequest!.Input, StringComparer.Ordinal);
    Assert.Equal(2, session.Host.TurnsSent);
  }

  /// <summary>Two host losses, two reconnects, then the turn succeeds - and each reconnect is one
  ///     notice, so the user reads the count against the bound.</summary>
  [Fact]
  public async Task HostDiesTwice_TwoNoticesThenTheTurnSucceeds()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);
    session.Host.FailsBeforeSettling.Enqueue(HostLost());
    session.Host.FailsBeforeSettling.Enqueue(HostLost());

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal(2, session.Host.Reconnects);
    Assert.Equal(3, session.Host.TurnsSent);
    Assert.Contains("answer", session.ViewModel.Transcript.AssistantText, StringComparison.Ordinal);
    Assert.Contains(session.ViewModel.Transcript.Notices, n => n.Contains("reconnect 2 of 2", StringComparison.Ordinal));
  }

  /// <summary>The bound: a host that stays dead ends the turn after two reconnects, and the line
  ///     the user reads names both the transport's own code and the reconnect count.</summary>
  [Fact]
  public async Task HostStaysDead_EndsTheTurnNamingTwoReconnects()
  {
    FakeHostSession session = await FakeHostSession.StartAsync([]).ConfigureAwait(true);
    for (int i = 0; i < 3; i++)
    {
      session.Host.FailsBeforeSettling.Enqueue(HostLost());
    }

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal(2, session.Host.Reconnects);
    Assert.Equal(3, session.Host.TurnsSent);
    Assert.Contains(session.ViewModel.Transcript.Notices, n =>
        n.Contains("Error [HostUnavailable]", StringComparison.Ordinal)
        && n.Contains("2 reconnect(s) attempted", StringComparison.Ordinal));
  }

  /// <summary>A host that cannot be reached again is not retried forever: the reconnect's own
  ///     failure ends the turn, and it is the line the user reads.</summary>
  [Fact]
  public async Task AFailedReconnect_EndsTheTurnInsteadOfResending()
  {
    FakeHostSession session = await FakeHostSession.StartAsync([]).ConfigureAwait(true);
    session.Host.FailsBeforeSettling.Enqueue(HostLost());
    session.Host.ReconnectRefusal = new DomainError("HostUnavailable", "the host refused to start.");

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal(1, session.Host.Reconnects);
    Assert.Equal(1, session.Host.TurnsSent);
    Assert.Contains(session.ViewModel.Transcript.Notices, n =>
        n.Contains("reconnect 1 of 2 failed", StringComparison.Ordinal));
  }

  /// <summary>A stopped turn and an unsendable request are NOT host losses: no reconnect is
  ///     attempted, because the connection is not what failed.</summary>
  [Theory]
  [InlineData("TurnCancelled", "the turn was stopped.")]
  [InlineData("RootTurnPayloadTooLarge", "frame of 99999999 bytes exceeds the limit.")]
  public async Task OutcomesThatAreNotHostLoss_AreNeverReconnected(string code, string message)
  {
    FakeHostSession session = await FakeHostSession.StartAsync([]).ConfigureAwait(true);
    session.Host.FailWith = new DomainError(code, message);

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal(0, session.Host.Reconnects);
    Assert.Equal(1, session.Host.TurnsSent);
    Assert.Contains(session.ViewModel.Transcript.Notices, n =>
        n.Contains("Error [" + code + "]", StringComparison.Ordinal));
  }

  /// <summary>A turn that never lost its host reconnects nothing: the ordinary path is
  ///     unchanged.</summary>
  [Fact]
  public async Task ASuccessfulTurn_ReconnectsNothing()
  {
    FakeHostSession session = await FakeHostSession.StartAsync(["answer"]).ConfigureAwait(true);

    await session.ViewModel.SubmitAsync("hi").ConfigureAwait(true);

    Assert.Equal(0, session.Host.Reconnects);
    Assert.Equal(1, session.Host.TurnsSent);
  }

  private static DomainError HostLost()
      => new("HostUnavailable", "the conversation host connection was lost.");
}
