namespace eThangAgent.AgentDomain;

/// <summary>The HOST cap (issue #305 D2): how many agent host processes this machine may run
///     at once. The unit changes from 'children inside one host' to 'hosts', because D1/D2 make
///     every agent its own process — so the scarce resource is a per-host job object, pipe pair,
///     and working set rather than a slot in one process.
///     <para>DERIVED FROM A MEASUREMENT, NEVER A SETTING (D2, inheriting #254's argument). The
///     caller supplies the machine's total memory from <c>IMachineResources</c> — the same seam
///     the resource governor derives its budget from — and this type turns it into a cap. A
///     constant here would be wrong on every machine it was not measured on, and a configurable
///     one is a cap somebody eventually sets to 'unlimited'.</para>
///     <para>THE NUMBERS ARE MEASURED, AND THEY ARE NAMED SO THEY CAN BE RE-MEASURED. The
///     per-host working set was measured on this workspace's live hosts (2026-10-28): 318 MB,
///     278 MB, 233 MB across three running hosts, so 320 MB is the rounded upper bound rather
///     than an average. The reserve keeps the app process, the exec engine, the model
///     runtimes, and the operating system itself out of the arithmetic — sizing hosts to 100%
///     of RAM would guarantee thrash at the cap, which is worse than a cap that refuses.</para>
///     <para>THE CAP MUST ADMIT THE MEASURED PEAK. #305 measured 62 concurrent live children
///     across the whole database, and 62 in the single widest session — that is a REQUIREMENT,
///     not an average to size against. A 32 GB machine yields 66 with these numbers, so the
///     measured peak is admitted; a 16 GB machine yields 33, which is why the cap is derived
///     per machine instead of shipped as one number.</para></summary>
public static class HostCapacity
{
  /// <summary>The working set one host process is budgeted, in bytes (320 MB). Measured, not
  ///     assumed: the three live hosts sampled 318/278/233 MB, and the cap must survive the
  ///     largest of them, not the mean.</summary>
  public const long PerHostBytes = 320L * 1024 * 1024;

  /// <summary>The share of machine memory NOT available to agent hosts (35%). It covers the
  ///     app process, the exec engine, the provider clients, and the OS page cache. Sizing
  ///     hosts to all of RAM would make the cap a thrash threshold rather than a safety bound.</summary>
  public const double ReserveFraction = 0.35;

  /// <summary>The floor: at least one host must always be admissible, whatever the arithmetic
  ///     says. A machine too small to be measured usefully still runs one agent — refusing
  ///     every host would make the harness unusable rather than careful. A cap of zero is
  ///     never a safety property; it is a broken installation.</summary>
  public const int MinimumHosts = 1;

  /// <summary>The cap for a machine reporting <paramref name="totalBytes"/> of usable memory.
  ///     Pure: the same input always yields the same cap, so a test can pin the arithmetic
  ///     without a live machine. A non-positive total is a failed probe and yields the floor,
  ///     never a division by a fabricated number.</summary>
  public static int ForMachine(long totalBytes)
  {
    if (totalBytes <= 0)
    {
      return MinimumHosts;
    }

    double usable = totalBytes * (1.0 - ReserveFraction);
    int hosts = (int)(usable / PerHostBytes);
    return Math.Max(MinimumHosts, hosts);
  }

  /// <summary>The cap for a machine memory PROBE that may have failed: an unreadable machine
  ///     yields the floor rather than an exception. The caller reports the probe failure
  ///     separately; the cap is the safety bound either way.</summary>
  public static int ForProbe(bool success, long totalBytes)
      => success ? ForMachine(totalBytes) : MinimumHosts;
}
