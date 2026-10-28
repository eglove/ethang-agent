using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>The host cap (issue #305 D2): derived from a MEASUREMENT, never a setting, and it
///     must admit the measured peak. #305 measured 62 concurrent live children across the whole
///     database and 62 in the single widest session, so 62 is a requirement rather than an
///     average to size against.</summary>
public class HostCapacityTests
{
  private const long Gigabyte = 1024L * 1024 * 1024;

  /// <summary>The measured peak the cap must survive (issue #305's table).</summary>
  private const int MeasuredPeakConcurrentHosts = 62;

  [Fact]
  public void ForMachine_AdmitsTheMeasuredPeak_OnA32GbMachine()
  {
    // The machine this workspace runs on measured 31.76 GB of usable memory.
    int cap = HostCapacity.ForMachine(34_101_985_280L);

    Assert.True(cap >= MeasuredPeakConcurrentHosts,
        $"a 32 GB machine must admit the measured peak of {MeasuredPeakConcurrentHosts} hosts; it admitted {cap}.");
  }

  [Fact]
  public void ForMachine_ScalesWithTheMachine_NeverAConstant()
  {
    // The point of deriving from a measurement: two machines get two caps. A shipped constant
    // would be wrong on whichever machine it was not measured on.
    int small = HostCapacity.ForMachine(8 * Gigabyte);
    int large = HostCapacity.ForMachine(64 * Gigabyte);

    Assert.True(large > small, $"a bigger machine must admit more hosts: 64 GB gave {large}, 8 GB gave {small}.");
  }

  [Fact]
  public void ForMachine_IsPure_SameInputSameCap()
      => Assert.Equal(HostCapacity.ForMachine(16 * Gigabyte), HostCapacity.ForMachine(16 * Gigabyte));

  [Fact]
  public void ForMachine_NonPositiveTotal_YieldsTheFloor_NotAThrownDivision()
  {
    Assert.Equal(HostCapacity.MinimumHosts, HostCapacity.ForMachine(0));
    Assert.Equal(HostCapacity.MinimumHosts, HostCapacity.ForMachine(-1));
  }

  [Fact]
  public void ForProbe_FailedProbe_YieldsTheFloor()
  {
    // A failed machine probe is a named condition the caller reports; the cap stays the safe
    // floor rather than an exception or a fabricated budget.
    Assert.Equal(HostCapacity.MinimumHosts, HostCapacity.ForProbe(success: false, totalBytes: 0));
    Assert.Equal(HostCapacity.ForMachine(32 * Gigabyte), HostCapacity.ForProbe(success: true, totalBytes: 32 * Gigabyte));
  }

  // A cap of zero is a broken installation, not a safety property: it would park every
  // start forever and the harness would run no agent at all.
  [Fact]
  public void ForMachine_NeverReturnsZero()
      => Assert.True(HostCapacity.ForMachine(1) >= HostCapacity.MinimumHosts);

  // Pinned as a MEASUREMENT, not a preference: the three live hosts sampled 318/278/233 MB,
  // so the budget must be at least the largest of them.
  [Fact]
  public void PerHostBytes_MatchesTheMeasuredWorkingSet()
      => Assert.True(HostCapacity.PerHostBytes >= 318L * 1024 * 1024,
          "the per-host budget must cover the largest measured host working set (318 MB).");
}
