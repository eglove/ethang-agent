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
  ///     domain-facing contract carries the enum's name, the chrome carries the words.
  ///     <para>RISK, RECORDED: a raw-name property on a user-facing view-model is a
  ///     standing leak vector - the chrome must bind LevelDisplay and never Level.
  ///     The raw name stays because the task brief's own test asserts it, and the
  ///     brief is the contract; the leak is closed by the chrome test, which asserts
  ///     the RENDERED text and not the item type.</para></summary>
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

  /// <summary>A failed machine probe is NOT a normal, empty machine. The tick
  ///     carries no budget at all, so the indicator must say the memory could not be
  ///     read - claiming "Normal" and "0.0 KB of 0.0 KB" reports a healthy machine
  ///     the app never measured.</summary>
  [Fact]
  public void Update_AFailedMachineProbe_SaysTheMemoryCouldNotBeRead()
  {
    ResourceIndicatorViewModel vm = new();

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Normal, null, ResourceAction.None, 0, 0),
        [],
        []);

    Assert.Equal("Unknown", vm.LevelDisplay);
    Assert.Contains("could not be read", vm.UsageDisplay, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("0.0 KB", vm.UsageDisplay, StringComparison.Ordinal);
    Assert.Equal(0, vm.UsageFraction);
  }

  /// <summary>The bar and the level must not be READ as the same quantity. The
  ///     governor decides on the WORST CONVERSATION against ITS OWN SHARE, while the
  ///     bar is app-wide; putting the two side by side without naming both produced a
  ///     bar at 6% beside the word "Critical", which is not honest. The level's own
  ///     basis is therefore stated, next to the chip, in words.</summary>
  [Fact]
  public void Update_LevelBasis_NamesTheQuantityTheLevelWasComputedAgainst()
  {
    ResourceIndicatorViewModel vm = new();

    // One conversation at 75% of its own share, and an app far larger than the
    // app-wide budget: the chip says "Acting" about the CONVERSATION, while the
    // bar is app-wide - so the chip's own quantity must be visible.
    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram,
            6_000_000_000L, 8_000_000_000L, TotalBytes: 16_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, []),
         new ResourceObservation("app", 10_000_000_000L, [])],
        []);

    // The bar is app-wide: the conversation plus the app, against the app-wide limit.
    Assert.Equal(1.0, vm.UsageFraction, 2);
    Assert.Contains("16.0 GB", vm.UsageDisplay, StringComparison.Ordinal);

    // The level's basis is the conversation's own usage against its own share - and
    // it is STATED, so the chip is never read against the app-wide bar.
    Assert.Contains("6.0 GB", vm.LevelBasisDisplay, StringComparison.Ordinal);
    Assert.Contains("8.0 GB", vm.LevelBasisDisplay, StringComparison.Ordinal);
  }

  /// <summary>The audit trail is read by a person, so the decision line speaks their
  ///     language: no raw level names, no raw action names, no "harness". The original
  ///     implementer claimed the leak was audit fidelity - the plain-language
  ///     constraint wins, and the raw vocabulary stays in the audit table itself.</summary>
  [Fact]
  public void Update_RecentDecisions_RendersPlainLanguage()
  {
    ResourceIndicatorViewModel vm = new();
    WatchdogEvent evt = new(Guid.NewGuid(), null, WatchdogEventKind.ResourceActed,
        "conversation c1 at 5722 MB of its 7629 MB share (Notice; harness budget 7629 MB) -> Steer",
        0, 5722, new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Notice, "c1", ResourceAction.Steer,
            5_722_000_000L, 7_629_000_000L),
        [new ResourceObservation("c1", 5_722_000_000L, [])],
        [evt]);

    string line = Assert.Single(vm.RecentDecisions);
    Assert.DoesNotContain("Notice", line, StringComparison.Ordinal);
    Assert.DoesNotContain("Steer", line, StringComparison.Ordinal);
    Assert.DoesNotContain("harness", line, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("watch", line, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>A REFUSED action is read by a person too: the raw rung name and the raw
  ///     error code are internal vocabulary and must not reach the chrome.</summary>
  [Fact]
  public void Update_AFailedAction_RendersPlainLanguage()
  {
    ResourceIndicatorViewModel vm = new();
    WatchdogEvent evt = new(Guid.NewGuid(), null, WatchdogEventKind.WatchdogErrored,
        "conversation c1 -> KillProgram failed: RungRefused: the app refused to enact KillProgram: no host.",
        0, 5722, new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram,
            5_722_000_000L, 7_629_000_000L),
        [new ResourceObservation("c1", 5_722_000_000L, [])],
        [evt]);

    string line = Assert.Single(vm.RecentDecisions);
    Assert.DoesNotContain("KillProgram", line, StringComparison.Ordinal);
    Assert.DoesNotContain("RungRefused", line, StringComparison.Ordinal);
  }
}
