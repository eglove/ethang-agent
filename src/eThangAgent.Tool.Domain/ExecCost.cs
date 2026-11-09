using System.Globalization;

namespace eThangAgent.ToolDomain;

/// <summary>What one exec run cost against the budget its caller stated. Carried on
///     <see cref="ToolResult.Cost"/> so the agent loop can judge the run without an
///     ambient observer: the exec tool is a process-wide singleton shared by every
///     session in a host, so per-session state cannot live on it.</summary>
public sealed record ExecCost(TimeSpan Elapsed, TimeSpan Budget)
{
  /// <summary>The fraction of its budget a run must use to be judged over-provisioned.
  ///     A tenth is unambiguous: no caller sizes a budget ten times the work on purpose.</summary>
  public const double OverProvisionedFraction = 0.10;

  /// <summary>True when the run finished inside a tenth of the budget it was given.</summary>
  public bool IsOverProvisioned =>
      Budget > TimeSpan.Zero && Elapsed < Budget * OverProvisionedFraction;

  /// <summary>The annotation line appended to the run's result, e.g.
  ///     <c>[exec: 1.2s of 600s budget]</c>. Both numbers appear so an over-sized
  ///     budget is visible without the harness commenting on it.</summary>
  public string Render() =>
      "[exec: " + FormatElapsed(Elapsed) + " of " + FormatBudget(Budget) + " budget]";

  /// <summary>One decimal below ten seconds (a sub-second run reads as 0.3s), whole
  ///     seconds at or above it, so the line stays short on every result.</summary>
  private static string FormatElapsed(TimeSpan elapsed) =>
      elapsed.TotalSeconds < 10
          ? elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s"
          : Math.Round(elapsed.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + "s";

  private static string FormatBudget(TimeSpan budget) =>
      Math.Round(budget.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + "s";
}
