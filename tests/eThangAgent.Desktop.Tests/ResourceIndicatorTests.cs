using System.Reflection;
using System.Windows.Input;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The app-wide memory indicator (spec D14): the audit trail's first reader.
///     Usage against the derived limit, the largest consumers, and the decisions the
///     governor has taken - plain language in the chrome, read-only by design.</summary>
public sealed class ResourceIndicatorTests
{
  [Fact]
  public void Update_TickAndObservations_RendersUsageAgainstTheLimit()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram,
            6_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, [])],
        []);

    Assert.Contains("6.0 GB", vm.UsageDisplay, StringComparison.Ordinal);
    Assert.Contains("8.0 GB", vm.UsageDisplay, StringComparison.Ordinal);
    Assert.Equal(0.75, vm.UsageFraction, 2);
    Assert.Equal("Act", vm.Level);
  }

  [Fact]
  public void Update_TwoConsumers_ListsTheLargestFirst()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Notice, "big", ResourceAction.Steer, 5_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("small", 100_000_000L, []),
         new ResourceObservation("big", 5_000_000_000L, [])],
        []);

    Assert.Equal("big", vm.Consumers[0].ConversationId);
  }

  [Fact]
  public void Update_RecentDecisions_RendersTheAuditTrailReadably()
  {
    ResourceIndicatorViewModel vm = new();
    WatchdogEvent evt = new(Guid.NewGuid(), null, WatchdogEventKind.ResourceActed,
        "conversation c1 at 6000 MB of its 8000 MB share -> KillProgram", 0, 6000,
        new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram, 6_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, [])],
        [evt]);

    Assert.Contains(vm.RecentDecisions, d => d.Contains("KillProgram", StringComparison.Ordinal));
  }

  /// <summary>The chrome reads in plain language, not in the domain's level names: the
  ///     user is told what is happening, not which component decided it.</summary>
  [Theory]
  [InlineData(ResourceLevel.Normal, "Normal")]
  [InlineData(ResourceLevel.Notice, "Watch")]
  [InlineData(ResourceLevel.Act, "Acting")]
  [InlineData(ResourceLevel.Emergency, "Critical")]
  public void Update_LevelDisplay_RendersPlainLanguage(ResourceLevel level, string expected)
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(level, "c1", ResourceAction.None, 1_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 1_000_000_000L, [])],
        []);

    Assert.Equal(expected, vm.LevelDisplay);
  }

  /// <summary>The raw domain level stays available beside the display one: the
  ///     domain-facing contract carries the enum's name, the chrome carries the words.</summary>
  [Fact]
  public void Update_Level_KeepsTheRawDomainName()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Emergency, "c1", ResourceAction.RestartApp, 9_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 9_000_000_000L, [])],
        []);

    Assert.Equal("Emergency", vm.Level);
    Assert.Equal("Critical", vm.LevelDisplay);
  }

  /// <summary>Nothing on the indicator lets the user act on the governor: the user
  ///     declined a manual reclaim, and the governor owns the ladder alone. The check
  ///     is by shape - the indicator exposes no command at all.</summary>
  [Fact]
  public void Indicator_ExposesNoCommandTheUserCouldActWith()
  {
    PropertyInfo[] commands = [.. typeof(ResourceIndicatorViewModel)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => typeof(ICommand).IsAssignableFrom(p.PropertyType))];

    Assert.Empty(commands);

    Assert.Empty(commands);
  }

  /// <summary>Usage above the budget reads as 100% of the bar, never as an overflow
  ///     number the progress bar cannot render.</summary>
  [Fact]
  public void Update_UsageAboveTheBudget_ClampsTheFractionToOne()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Emergency, "c1", ResourceAction.RestartApp, 12_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 12_000_000_000L, [])],
        []);

    Assert.Equal(1.0, vm.UsageFraction, 3);
  }

  /// <summary>A zero budget (a failed machine probe) must not divide by zero: the
  ///     fraction reads as zero and the usage line still names the limit.</summary>
  [Fact]
  public void Update_ZeroBudget_DoesNotDivideByZero()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Normal, null, ResourceAction.None, 0, 0),
        [],
        []);

    Assert.Equal(0, vm.UsageFraction);
    Assert.Contains("limit", vm.UsageDisplay, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Every decision line carries its time, so the trail reads as a sequence
  ///     of events rather than an undated pile.</summary>
  [Fact]
  public void Update_RecentDecisions_PrefixesTheTime()
  {
    ResourceIndicatorViewModel vm = new();
    WatchdogEvent evt = new(Guid.NewGuid(), null, WatchdogEventKind.ResourceNotice,
        "conversation c1 at 6000 MB of its 8000 MB share (Notice)", 0, 6000,
        new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Notice, "c1", ResourceAction.None, 6_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, [])],
        [evt]);

    // The prefix is LOCAL time (the trail is read by a person on this machine), so
    // the expectation is derived the same way instead of hard-coding a zone.
    string expected = evt.CreatedAt.ToLocalTime()
        .ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    Assert.StartsWith(expected, vm.RecentDecisions[0], StringComparison.Ordinal);
  }

  /// <summary>The consumers carry their own usage, so the list answers 'who is using
  ///     the memory' without the caller cross-referencing another list.</summary>
  [Fact]
  public void Update_Consumers_CarryTheirUsage()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Notice, "big", ResourceAction.Steer, 5_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("big", 5_000_000_000L, [])],
        []);

    Assert.Contains("5.0 GB", vm.Consumers[0].Display, StringComparison.Ordinal);
  }

  /// <summary>The app-wide limit is the app-wide budget, not one conversation's share:
  ///     the indicator is app-wide, so it must render against the app-wide budget the
  ///     governor derived. A tick that carries the total makes the two agree.</summary>
  [Fact]
  public void Update_TickCarryingTheAppWideTotal_RendersAgainstIt()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram,
            6_000_000_000L, 4_000_000_000L, TotalBytes: 8_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, [])],
        []);

    Assert.Contains("8.0 GB", vm.UsageDisplay, StringComparison.Ordinal);
    Assert.Equal(0.75, vm.UsageFraction, 2);
  }

  /// <summary>Null inputs are a programmer error at the seam, not a silent no-op.</summary>
  [Fact]
  public void Update_NullTick_Throws()
  {
    ResourceIndicatorViewModel vm = new();

    _ = Assert.Throws<ArgumentNullException>(() => vm.Update(null!, [], []));
  }
}
