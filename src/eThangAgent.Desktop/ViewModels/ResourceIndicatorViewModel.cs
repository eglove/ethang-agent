using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One conversation's row in the consumer list: which conversation, and how
///     much memory it is using right now, in words a user can read.</summary>
internal sealed record ResourceConsumerRow(string ConversationId, string Display);

/// <summary>The app-wide memory indicator (spec D14): usage against the derived
///     limit, the largest consumers, and the decisions the governor has taken. It is
///     the audit trail's FIRST reader - before this, breaches were recorded and never
///     seen.
///     <para>READ-ONLY by design: the panel reports what IS, never what should be, and
///     offers no manual reclaim action (the user declined one - the governor owns the
///     ladder alone). It therefore exposes no command at all.</para>
///     <para>Two levels of vocabulary on purpose: <see cref="Level"/> carries the
///     domain's raw level name (the domain-facing contract), while
///     <see cref="LevelDisplay"/> carries the plain-language word the chrome shows -
///     the user is told what is happening, not which component decided it.</para></summary>
internal sealed partial class ResourceIndicatorViewModel : ObservableObject
{
  /// <summary>What the machine is doing with memory, in words: the usage beside the
  ///     limit it is measured against.</summary>
  [ObservableProperty]
  public partial string UsageDisplay { get; set; } = "measuring...";

  /// <summary>The usage as a fraction of the limit, for the bar. Always between zero
  ///     and one: a bar that overflowed would render as more than full.</summary>
  [ObservableProperty]
  public partial double UsageFraction { get; set; }

  /// <summary>The domain's level name - the raw vocabulary the domain speaks.</summary>
  [ObservableProperty]
  public partial string Level { get; set; } = "Normal";

  /// <summary>The level in plain language, for the chrome: the user is told what is
  ///     happening, not which component decided it.</summary>
  [ObservableProperty]
  public partial string LevelDisplay { get; set; } = "Normal";

  /// <summary>The largest consumers, biggest first.</summary>
  [ObservableProperty]
  public partial IReadOnlyList<ResourceConsumerRow> Consumers { get; set; } = [];

  /// <summary>The governor's recent decisions, newest first - the audit trail, in the
  ///     words the audit wrote.</summary>
  [ObservableProperty]
  public partial IReadOnlyList<string> RecentDecisions { get; set; } = [];

  /// <summary>Applies one live sample. The caller hands in the tick's facts, the
  ///     observations it was derived from, and the recent audit rows - every number
  ///     shown here comes from those three inputs, never from an earlier update.
  ///     <para>The limit is the APP-WIDE budget when the tick carries one (the
  ///     governor derives both: the app-wide total and the per-conversation share),
  ///     and the tick's own budget otherwise - so a caller that knows only the
  ///     per-conversation share still renders against a real limit.</para></summary>
  public void Update(ResourceGovernorTick tick, IReadOnlyList<ResourceObservation> observations,
      IReadOnlyList<WatchdogEvent> recent)
  {
    ArgumentNullException.ThrowIfNull(tick);
    ArgumentNullException.ThrowIfNull(observations);
    ArgumentNullException.ThrowIfNull(recent);

    long limit = tick.TotalBytes ?? tick.BudgetBytes;
    long used = observations.Sum(observation => observation.UsageBytes);
    UsageFraction = limit > 0 ? Math.Min(1.0, (double)used / limit) : 0;
    UsageDisplay = Bytes(used) + " used of " + Bytes(limit) + " limit";
    Level = tick.Level.ToString();
    LevelDisplay = LevelText(tick.Level);
    Consumers = [.. observations
        .OrderByDescending(observation => observation.UsageBytes)
        .Select(observation => new ResourceConsumerRow(observation.ConversationId,
            Bytes(observation.UsageBytes)))];
    RecentDecisions = [.. recent.Select(DecisionText)];
  }

  /// <summary>How the memory level reads on screen. The domain's level names are
  ///     internal vocabulary, so each one is translated into words a user can act on.
  ///     An unknown level reads as the calm one - never a raw enum name.</summary>
  private static string LevelText(ResourceLevel level) => level switch
  {
    ResourceLevel.Normal => "Normal",
    ResourceLevel.Notice => "Watch",
    ResourceLevel.Act => "Acting",
    ResourceLevel.Emergency => "Critical",
    _ => "Normal",
  };

  /// <summary>One audit row as a user-facing line: the time, then the detail the
  ///     governor wrote. The stored detail is already plain language ("conversation
  ///     c1 at 6000 MB of its 8000 MB share -> KillProgram"), so it is shown as
  ///     written - the trail must not be paraphrased into something the audit does not
  ///     actually say.</summary>
  private static string DecisionText(WatchdogEvent decision)
      => decision.CreatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
      + "  " + decision.Detail;

  /// <summary>A byte count in words a user can read: bytes below a megabyte, then
  ///     megabytes, then gigabytes. One decimal place, always.</summary>
  private static string Bytes(long bytes)
  {
    // DECIMAL units, deliberately: a user reading "6.0 GB" means six billion bytes,
    // and the OS's own memory readouts are decimal. Binary units would render the
    // same number as "5.6 GB" and read as a different amount than the user sees
    // everywhere else.
    const double Kilobyte = 1000.0;
    const double Megabyte = 1000.0 * 1000.0;
    const double Gigabyte = 1000.0 * 1000.0 * 1000.0;
    double absolute = Math.Abs((double)bytes);
    double divisor;
    string unit;
    if (absolute < Megabyte)
    {
      divisor = Kilobyte;
      unit = "KB";
    }
    else if (absolute < Gigabyte)
    {
      divisor = Megabyte;
      unit = "MB";
    }
    else
    {
      divisor = Gigabyte;
      unit = "GB";
    }

    return (bytes / divisor).ToString("F1", CultureInfo.InvariantCulture) + " " + unit;
  }
}
