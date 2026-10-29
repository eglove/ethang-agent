using System.Reflection;
using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>Issue #303 D5: no auto-resume on restart. A restart leaves every running chain
///     exactly as it was, and the USER's resume (the Sessions menu) is the trusted mechanism.
///     <para>WHY A STRUCTURAL TEST: the absence of a startup behaviour is not observable by
///     running one. What can be pinned is that the chain surface exposes NO method that would
///     enact a resume on its own - so adding one is a deliberate, visible act rather than a
///     quiet drift back to auto-resume.</para></summary>
public class ChainRestartPolicyTests
{
  [Fact]
  public void ChainService_ExposesNoStartupResume_OnlyTheUsersExplicitResume()
  {
    // ResumeAsync is the USER's act (the Sessions menu / the rail). Anything named for a
    // startup sweep - AutoResume, ResumeAll, RecoverRunning, OnStartup - would be D5's
    // violation, and its absence is what D5 requires.
    string[] forbidden = ["AutoResume", "ResumeAll", "RecoverRunning", "ResumeRunning", "OnStartup"];
    MethodInfo[] methods = typeof(ChainService).GetMethods(BindingFlags.Public | BindingFlags.Instance);
    foreach (string name in forbidden)
    {
      Assert.DoesNotContain(methods, m => m.Name.Contains(name, StringComparison.Ordinal));
    }
  }

  [Fact]
  public void Resume_IsTheUsersAct_AndRequiresAnActor()
  {
    // D5's positive half: the ONLY way a parked chain moves back to Running is a call that
    // names who did it. The user's resume is the trusted mechanism, so the record can always
    // say a human un-parked the chain.
    Chain parked = Chain.New("C:\\ws", 1, 1, "t", "root", [1], DateTimeOffset.UnixEpoch)
        .Park("waiting on a human", 1, ChainActor.Harness, DateTimeOffset.UnixEpoch);

    Chain resumed = parked.Resume(ChainActor.User, DateTimeOffset.UnixEpoch);

    Assert.Equal(ChainStatus.Running, resumed.Status);
    Assert.Null(resumed.ParkReason);
    Assert.Null(resumed.ParkedAtPosition);
  }

  [Fact]
  public void ARunningChain_IsNeverTouchedByTheRecordItself()
  {
    // A restart is a process event, not a record event: nothing on the aggregate moves a
    // Running chain on its own. A Running chain with a claimed step stays exactly that.
    DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
    Chain chain = Chain.New("C:\\ws", 1, 1, "t", "root", [1], t0).ClaimStep(1, "session-a", t0);

    Assert.Equal(ChainStatus.Running, chain.Status);
    Assert.Equal(ChainStepStatus.Running, chain.Steps[0].Status);
    Assert.Equal("session-a", chain.Steps[0].SessionId);
  }
}
