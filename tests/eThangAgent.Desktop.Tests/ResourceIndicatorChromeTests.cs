using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The resource indicator's place in the window chrome (spec D14): the bar
///     sits ABOVE the tab strip, it shows the usage, the level, and a reachable
///     details toggle, and the details carry the consumers and the governor's
///     decisions. The indicator is READ-ONLY: the chrome offers nothing that would
///     let the user act on the governor (the user declined a manual reclaim).</summary>
public sealed class ResourceIndicatorChromeTests
{
  [AvaloniaFact]
  public void ResourceBar_SitsAboveTheTabStrip()
  {
    MainWindow window = new(CreateShell());
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Border bar = Control<Border>(window, "ResourceBar");
    TabControl tabs = Control<TabControl>(window, "AgentTabs");

    // The bar is in the row ABOVE the tabs: the budget is app-wide, so it is not
    // per-tab and must not sit inside one.
    Assert.Equal(0, Grid.GetRow(bar));
    Assert.Equal(1, Grid.GetRow(tabs));
  }

  [AvaloniaFact]
  public void ResourceBar_ShowsTheUsageAndTheLevel()
  {
    MainViewModel vm = CreateShell();
    MainWindow window = new(vm);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    vm.Resource.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram,
            6_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, [])],
        []);
    Dispatcher.UIThread.RunJobs();

    TextBlock usage = Control<TextBlock>(window, "ResourceUsageText");
    TextBlock level = Control<TextBlock>(window, "ResourceLevelText");

    Assert.Contains("6.0 GB", usage.Text ?? "", StringComparison.Ordinal);
    // The CHROME reads in plain language, not in the domain's level names.
    Assert.Equal("Acting", level.Text);
  }

  [AvaloniaFact]
  public void DetailsToggle_RevealsTheConsumersAndTheDecisions()
  {
    MainViewModel vm = CreateShell();
    MainWindow window = new(vm);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    vm.Resource.Update(
        new ResourceGovernorTick(ResourceLevel.Notice, "c1", ResourceAction.Steer,
            5_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 5_000_000_000L, [])],
        [new WatchdogEvent(Guid.NewGuid(), null, WatchdogEventKind.ResourceActed,
            "conversation c1 at 5000 MB of its 8000 MB share -> Steer", 0, 5000,
            new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero))]);
    Dispatcher.UIThread.RunJobs();

    Button toggle = Control<Button>(window, "ResourceDetailsButton");
    ItemsControl consumers = Control<ItemsControl>(window, "ResourceConsumersList");
    ItemsControl decisions = Control<ItemsControl>(window, "ResourceDecisionsList");

    // The details toggle is REACHABLE and its data is shown: the consumer row and
    // the decision line are both bound.
    // The consumers list carries the CONSUMER rows and the decisions list carries
    // the DECISION lines. Checking the item TYPES, not just the counts: two lists
    // that both hold one item would pass a count-only assertion even when one of
    // them is bound to the wrong collection.
    ResourceConsumerRow consumer = Assert.IsType<ResourceConsumerRow>(Assert.Single(consumers.Items));
    string decision = Assert.IsType<string>(Assert.Single(decisions.Items));
    Assert.Equal("c1", consumer.ConversationId);
    // The decision line is PLAIN LANGUAGE: the raw rung name is internal
    // vocabulary and must not reach the chrome.
    Assert.Contains("steer", decision, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("Steer", decision, StringComparison.Ordinal);

    toggle.Command?.Execute(toggle);
    Dispatcher.UIThread.RunJobs();
    Assert.True(vm.IsResourceDetailsOpen);
  }

  /// <summary>The chrome must render the consumer's TEXT, not the record. Binding
  ///     the record makes Avalonia fall back to Record.ToString(), and the screen then
  ///     shows a C# type name, a structural dump and a raw id - a broken screen that
  ///     an item-TYPE assertion cannot see, which is exactly how the broken binding
  ///     passed the chrome tests before.</summary>
  [AvaloniaFact]
  public void DetailsToggle_RendersTheConsumerText_NotTheRecordTypeName()
  {
    MainViewModel vm = CreateShell();
    MainWindow window = new(vm);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    vm.Resource.Update(
        new ResourceGovernorTick(ResourceLevel.Notice, "c1", ResourceAction.Steer,
            5_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 5_000_000_000L, [])],
        []);
    Dispatcher.UIThread.RunJobs();

    ItemsControl consumers = Control<ItemsControl>(window, "ResourceConsumersList");

    // The details must be OPEN for the rows to be realized at all: a collapsed
    // panel renders nothing, and a test that reads an unrealized list proves
    // nothing about what the user sees.
    Button toggle = Control<Button>(window, "ResourceDetailsButton");
    toggle.Command?.Execute(toggle);
    Dispatcher.UIThread.RunJobs();

    // The RENDERED text: what the user actually sees in the consumer list.
    string[] rendered = [.. window.GetVisualDescendants().OfType<TextBlock>()
        .Select(block => block.Text ?? string.Empty)
        .Where(text => text.Length > 0)];

    Assert.Contains(rendered, text => text.Contains("5.0 GB", StringComparison.Ordinal));
    Assert.DoesNotContain(rendered, text => text.Contains("ResourceConsumerRow", StringComparison.Ordinal));
    Assert.DoesNotContain(rendered, text => text.Contains("ConversationId =", StringComparison.Ordinal));
    Assert.NotEmpty(consumers.Items);
  }

  [AvaloniaFact]
  public void ResourceBar_OffersNoActionOnTheGovernor()
  {
    MainWindow window = new(CreateShell());
    window.Show();
    Dispatcher.UIThread.RunJobs();

    // The only button in the bar is the DETAILS TOGGLE: a view control. There is no
    // reclaim, no restart, no 'free memory now' - the governor owns the ladder and
    // the user declined a manual action.
    Button[] buttons = [.. window.GetVisualDescendants().OfType<Button>()
        .Where(b => b.Name?.StartsWith("Resource", StringComparison.Ordinal) == true)];

    Button only = Assert.Single(buttons);
    Assert.Equal("ResourceDetailsButton", only.Name);
  }


  /// <summary>The named control, or a hard failure: a missing control is a broken
  ///     chrome, not a null to assert on.</summary>
  private static T Control<T>(Window window, string name) where T : Control
      => window.FindControl<T>(name)
          ?? throw new InvalidOperationException($"the window must carry a '{name}' control.");
  private static MainViewModel CreateShell()
  {
    static Task<Result<AgentSession>> create(string root, string provider)
        => Task.FromResult(Result.Success(FakeSession(root)));
    return new MainViewModel(create);
  }

  private static AgentSession FakeSession(string root)
      => new(
          new ServiceCollection().BuildServiceProvider(),
          AgentId.NewId(),
          new Conversation(),
          Handler: null!,
          Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
          Model: ModelConfig.Create("test/model", 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root,
          ProviderName: "openrouter",
          Inbox: new BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime());
}
