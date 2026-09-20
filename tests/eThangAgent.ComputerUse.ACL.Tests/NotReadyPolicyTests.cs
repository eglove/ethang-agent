namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Cold-start schedule (spec 1.3): backoff 250/500/750/1000/1500 ms, max 6
///     attempts, then TIMEOUT (retryable). Runs instantly over the fake clock.</summary>
public class NotReadyPolicyTests
{
  [Fact]
  public async Task Schedule_YieldsExactBackoffSteps_ThenExhausts()
  {
    FakeDelayer delayer = new();
    NotReadyPolicy policy = new(delayer);
    TimeSpan[] expected =
    [
      TimeSpan.FromMilliseconds(250),
      TimeSpan.FromMilliseconds(500),
      TimeSpan.FromMilliseconds(750),
      TimeSpan.FromMilliseconds(1000),
      TimeSpan.FromMilliseconds(1500),
    ];

    List<bool> outcomes = [];
    for (int attempt = 0; attempt < 6; attempt++)
    {
      outcomes.Add(await policy.WaitBeforeRetryAsync(attempt + 1, TestContext.Current.CancellationToken));
    }

    Assert.Equal(expected, delayer.Delays);
    Assert.Equal([true, true, true, true, true, false], outcomes);
  }

  [Fact]
  public void GiveUpCode_IsTimeout()
    => Assert.Equal("TIMEOUT", NotReadyPolicy.GiveUpCode);

  [Fact]
  public void MaxAttempts_IsSix()
    => Assert.Equal(6, NotReadyPolicy.MaxAttempts);
}
