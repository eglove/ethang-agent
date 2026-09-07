namespace eThangAgent.Desktop.ViewModels;

/// <summary>Formats a tool card's elapsed seconds: one decimal below a minute,
///     m:ss at or above, with the error marker appended on a failed result. The
///     single formatter for the live call-card count-up and the frozen result
///     total, so the two cards can never disagree on the format. FormatBudget
///     renders the exec timeout budget as a duration (900 -> 15m) in the same
///     family, so the header never shows a raw 900s blob.</summary>
internal static class ToolElapsed
{
  internal static string Format(double seconds, bool isError = false)
    => $"{FormatSeconds(seconds)}{(isError ? " ✗" : "")}";

  internal static string FormatBudget(int seconds) => seconds switch
  {
    < 60 => $"{seconds}s",
    _ when seconds % 60 == 0 => $"{seconds / 60}m",
    _ => $"{seconds / 60}m {seconds % 60}s",
  };

  private static string FormatSeconds(double seconds)
    => seconds < 60 ? $"{seconds:0.0}s" : $"{(int)(seconds / 60)}:{(int)seconds % 60:00}";
}
