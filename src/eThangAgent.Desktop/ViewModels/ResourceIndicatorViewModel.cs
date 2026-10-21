using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>One conversation's row in the consumer list: which conversation, and how
///     much memory it is using right now, in words a user can read.
///     <para>The chrome binds <see cref="Text"/> EXPLICITLY. Binding the record
///     itself makes Avalonia fall back to Record.ToString(), and the screen then
///     shows a C# type name, a structural dump and a raw id - a broken screen that
///     no item-type assertion can see.</para></summary>
internal sealed record ResourceConsumerRow(string ConversationId, string Display)
{
  /// <summary>The conversation's name, in the words the user knows it by.</summary>
  public string Label { get; init; } = ConversationId;

  /// <summary>What the chrome renders: the name and the usage, and nothing else.</summary>
  public string Text => Label + ": " + Display;
}

/// <summary>The app-wide memory indicator (spec D14): usage against the derived
///     limit, the largest consumers, and the decisions the governor has taken. It is
///     the audit trail's FIRST reader - before this, breaches were recorded and never
///     seen.
///     <para>READ-ONLY by design: the panel reports what IS, never what should be, and
///     offers no manual reclaim action (the user declined one - the governor owns the
///     ladder alone). It therefore exposes no command at all.</para>
///     <para>TWO QUANTITIES, EACH LABELLED, NEVER MIXED: the bar is APP-WIDE (the
///     budget is app-wide), and the level chip is about the busiest CONVERSATION
///     against its own share - the pair the governor actually decided on. Putting
///     the two side by side without naming both produced a bar at 6% beside the word
///     "Critical", which is not honest; <see cref="LevelBasisDisplay"/> now states the
///     quantity the level was computed against, right next to the chip.</para>
///     <para>Two levels of vocabulary on purpose: <see cref="Level"/> carries the
///     domain's raw level name (the domain-facing contract), while
///     <see cref="LevelDisplay"/> carries the plain-language word the chrome shows -
///     the user is told what is happening, not which component decided it.</para></summary>
internal sealed partial class ResourceIndicatorViewModel : ObservableObject
{
  /// <summary>What the machine is doing with memory, in words: the app-wide usage
  ///     beside the app-wide limit it is measured against.</summary>
  [ObservableProperty]
  public partial string UsageDisplay { get; set; } = "measuring...";

  /// <summary>The app-wide usage as a fraction of the app-wide limit, for the bar.
  ///     Always between zero and one: a bar that overflowed would render as more
  ///     than full.</summary>
  [ObservableProperty]
  public partial double UsageFraction { get; set; }

  /// <summary>The domain's level name - the raw vocabulary the domain speaks.
  ///     <para>RISK, RECORDED: a raw-name property on a user-facing view-model is a
  ///     standing leak vector. The chrome binds <see cref="LevelDisplay"/> and never
  ///     this; the raw name stays because the task brief's own test asserts it, and
  ///     the brief is the contract. The chrome test asserts the RENDERED text, so
  ///     the leak cannot come back unnoticed.</para></summary>
  [ObservableProperty]
  public partial string Level { get; set; } = "Normal";

  /// <summary>The level in plain language, for the chrome: the user is told what is
  ///     happening, not which component decided it. "Unknown" when the machine's
  ///     memory could not be read at all - claiming "Normal" there would report a
  ///     healthy machine the app never measured.</summary>
  [ObservableProperty]
  public partial string LevelDisplay { get; set; } = "Normal";

  /// <summary>The quantity the level was computed against: the busiest
  ///     conversation's usage against its own share. It sits BESIDE the level chip,
  ///     so the chip is never read against the app-wide bar.</summary>
  [ObservableProperty]
  public partial string LevelBasisDisplay { get; set; } = "measuring...";

  /// <summary>The app process's own usage, on its own line: it is real usage, but it
  ///     is not a conversation and the governor never acts on it.</summary>
  [ObservableProperty]
  public partial string AppUsageDisplay { get; set; } = "the app itself: measuring...";

  /// <summary>The largest consumers, biggest first. Conversations only: the app
  ///     process is shown on its own line, never as a row the governor could pick.</summary>
  [ObservableProperty]
  public partial IReadOnlyList<ResourceConsumerRow> Consumers { get; set; } = [];

  /// <summary>The governor's recent decisions, newest first - the audit trail, in
  ///     plain language.</summary>
  [ObservableProperty]
  public partial IReadOnlyList<string> RecentDecisions { get; set; } = [];

  /// <summary>Resolves a conversation id to the name the user knows it by. Set by
  ///     the composition root; null means "no name known".</summary>
  public Func<string, string?>? ConversationNames { get; set; }

  /// <summary>Applies one live sample. The caller hands in the tick's facts, the
  ///     observations it was derived from, and the recent audit rows - every number
  ///     shown here comes from those three inputs, never from an earlier update.
  ///     <para>The BAR is app-wide: every consumer's usage (the conversations plus
  ///     the app process itself) against the app-wide budget the governor derived.
  ///     The LEVEL is the governor's own, and <see cref="LevelBasisDisplay"/> names
  ///     the quantity behind it.</para></summary>
  public void Update(ResourceGovernorTick tick, IReadOnlyList<ResourceObservation> observations,
      IReadOnlyList<WatchdogEvent> recent)
  {
    ArgumentNullException.ThrowIfNull(tick);
    ArgumentNullException.ThrowIfNull(observations);
    ArgumentNullException.ThrowIfNull(recent);

    long limit = tick.TotalBytes ?? tick.BudgetBytes;
    List<ResourceObservation> conversations = [];
    long appUsage = 0;
    foreach (ResourceObservation observation in observations)
    {
      if (string.Equals(observation.ConversationId, ResourceHostObservation.AppConsumerId,
              StringComparison.Ordinal))
      {
        appUsage = observation.UsageBytes;
        continue;
      }

      conversations.Add(observation);
    }

    long used = conversations.Sum(conversation => conversation.UsageBytes) + appUsage;
    bool measured = limit > 0;

    Level = tick.Level.ToString();
    LevelDisplay = measured ? LevelText(tick.Level) : "Unknown";
    UsageFraction = measured ? Math.Min(1.0, (double)used / limit) : 0;
    UsageDisplay = measured
        ? "app-wide: " + Bytes(used) + " used of " + Bytes(limit) + " limit"
        : "app-wide: the machine's memory could not be read";

    // The level's own basis: the busiest conversation against its own share -
    // exactly the pair the governor decided on. Stated here so the chip is never
    // read against the app-wide bar.
    LevelBasisDisplay = tick.BudgetBytes > 0
        ? "busiest conversation: " + Bytes(tick.UsageBytes) + " of its "
          + Bytes(tick.BudgetBytes) + " limit"
        : "no conversation is under pressure";

    AppUsageDisplay = "the app itself: " + Bytes(appUsage);

    Consumers = [.. conversations
        .OrderByDescending(conversation => conversation.UsageBytes)
        .Select(conversation => new ResourceConsumerRow(conversation.ConversationId,
            Bytes(conversation.UsageBytes)) { Label = LabelFor(conversation.ConversationId) })];
    RecentDecisions = [.. recent.Select(DecisionText)];
  }

  /// <summary>The name the user knows a conversation by, falling back to a short
  ///     form of its id. A raw id is not a name, and a full id is not a name
  ///     either - the screen must show something a person can read.</summary>
  private string LabelFor(string conversationId)
  {
    string? name = ConversationNames?.Invoke(conversationId);
    if (!string.IsNullOrWhiteSpace(name))
    {
      return name;
    }

    string fallback = conversationId.Length > 8 ? "conversation " + conversationId[..8] : conversationId;
    return fallback;
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

  /// <summary>One audit row as a user-facing line: the time, then what happened, in
  ///     the reader's language.
  ///     <para>The audit TABLE keeps the raw vocabulary - that is the record, and it
  ///     is what an operator greps. The CHROME is for a person: the level name, the
  ///     rung name, the error code and the word "harness" are internal vocabulary and
  ///     are translated here. Audit fidelity is preserved in the table; the screen
  ///     speaks plainly.</para></summary>
  private static string DecisionText(WatchdogEvent decision)
      => decision.CreatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
      + "  " + Plain(decision.Detail);

  /// <summary>The raw rung names, in the words the reader can act on. "KillProgram"
  ///     is a MISNOMER in production - the app stops the whole conversation host
  ///     process - so the phrase says what actually happens.
  ///     <para>ONE table, TWO readers: the enforcement seam names its refusals with
  ///     the same phrases, so a refusal recorded in the audit trail reads the way
  ///     the decision that caused it does.</para></summary>
  private static string RungPhrase(string rung) => ResourceRungPhrases.Of(rung);

  /// <summary>The failure shape, translated: "conversation c1 -&gt; Steer failed:
  ///     RungRefused: ..." becomes "could not steer the conversation for conversation
  ///     c1: ...".</summary>
  private static string Plain(string detail)
  {
    const string Failed = " failed: ";
    int failed = detail.IndexOf(Failed, StringComparison.Ordinal);
    if (failed > 0)
    {
      string head = detail[..failed];
      string tail = detail[(failed + Failed.Length)..];
      const string Arrow = "-> ";
      int arrow = head.IndexOf(Arrow, StringComparison.Ordinal);
      if (arrow > 0)
      {
        string subject = head[..arrow].Trim();
        string rung = head[(arrow + Arrow.Length)..].Trim();
        return "could not " + RungPhrase(rung) + " for " + subject + ": " + Substitute(tail);
      }
    }

    return Substitute(detail);
  }

  /// <summary>Every internal word the audit writes, replaced with the word a reader
  ///     can act on. The audit table keeps the originals; only the screen changes.</summary>
  private static string Substitute(string text)
  {
    string result = text;
    result = result.Replace("(Notice; ", "(watch; ", StringComparison.Ordinal);
    result = result.Replace("(Act; ", "(acting; ", StringComparison.Ordinal);
    result = result.Replace("(Emergency; ", "(critical; ", StringComparison.Ordinal);
    result = result.Replace("(Normal; ", "(normal; ", StringComparison.Ordinal);
    // The level names arrive in BOTH forms: the governor writes the bare name
    // ((Emergency; ...)), while a row written by ANOTHER component (the agent
    // watchdog) can carry the type-qualified form (ResourceLevel.Emergency).
    // Matching only the bare form leaked the domain vocabulary into the chrome.
    result = result.Replace("ResourceLevel.Notice", "watch", StringComparison.Ordinal);
    result = result.Replace("ResourceLevel.Act", "acting", StringComparison.Ordinal);
    result = result.Replace("ResourceLevel.Emergency", "critical", StringComparison.Ordinal);
    result = result.Replace("ResourceLevel.Normal", "normal", StringComparison.Ordinal);
    result = result.Replace("harness budget", "app budget", StringComparison.Ordinal);
    result = result.Replace("harness", "app", StringComparison.Ordinal);
    result = result.Replace("RungRefused", "refused", StringComparison.Ordinal);
    result = result.Replace("ConversationNotOpen", "conversation not open", StringComparison.Ordinal);
    result = result.Replace("ReclaimFailed", "reclaim failed", StringComparison.Ordinal);
    result = result.Replace("MachineMemoryUnavailable", "the machine's memory could not be read",
        StringComparison.Ordinal);

    foreach (string rung in new[] { "Steer", "KillProgram", "ReclaimMemory", "RestartConversation",
        "RestartApp", "None" })
    {
      result = result.Replace(rung, RungPhrase(rung), StringComparison.Ordinal);
    }

    return result;
  }

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
