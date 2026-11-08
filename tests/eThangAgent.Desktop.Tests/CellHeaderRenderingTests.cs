using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #331, the RENDER half: a cell's header shows its label, its phase chip beside
///     it, and its final report once it settles.
///     <para>THE VISUAL TREE IS THE ASSERTION, not the view-model. The defect this issue closes
///     is a missing SURFACE: a chip property that nothing binds would leave every unit test
///     here green while the header rendered exactly as before. Each test therefore hosts the
///     real <see cref="WorkspaceTabView" /> in a headless window and reads the rendered
///     blocks.</para>
///     <para>THE REPORT IS DRIVEN THROUGH THE STORE, never by assigning the cell's state: the
///     card's own settle path is what fills it, so these tests prove the read AND the binding
///     rather than only the template.</para></summary>
[Trait("Category", "Process")]
public class CellHeaderRenderingTests
{
  /// <summary>The header renders the cell's label AND a chip reading Starting on a fresh cell:
  ///     the two facts the parent's decision 9 requires side by side.</summary>
  [AvaloniaFact]
  public void TheHeader_ShowsTheLabel_AndAStartingChip()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel cell = tab.Cells[0];
    Window window = Show(tab);

    Assert.Contains(Blocks(window, cell), block => block.Text == cell.Title);
    Assert.Equal(CellPhaseChip.Starting, Chip(window, cell).Text);
    window.Close();
  }

  /// <summary>The chip in the header follows the cell's phase, so a running agent's header
  ///     really moves through the vocabulary.
  ///     <para>THIS IS THE BINDING, ASSERTED: the property can be right while the template
  ///     points at nothing.</para></summary>
  [AvaloniaFact]
  public void TheHeaderChip_FollowsTheCellsPhase()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel cell = tab.Cells[0];
    Window window = Show(tab);

    cell.ApplyPhaseBeat("tool:exec");
    Dispatcher.UIThread.RunJobs();

    Assert.Equal("Running exec", Chip(window, cell).Text);

    cell.ApplyPhaseBeat("iteration");
    Dispatcher.UIThread.RunJobs();

    Assert.Equal(CellPhaseChip.Thinking, Chip(window, cell).Text);
    window.Close();
  }

  /// <summary>A settled cell's header reads Finished and the card renders the agent's report
  ///     (parent D5): what the agent concluded, not only that it ended.
  ///     <para>THE PANEL IS ABSENT UNTIL THERE IS A REPORT: an always-present empty panel would
  ///     read as 'this agent concluded nothing' on every running card.</para></summary>
  [AvaloniaFact]
  public void ASettledCard_ShowsFinished_AndRendersTheReport()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    ReportStore store = new() { Report = "the diff is correct; nothing to change." };
    AgentCellViewModel cell = AddHostCell(tab, store);
    Window window = Show(tab);
    Assert.False(ShowsReport(window, cell), "a running card renders no report panel");

    cell.MarkFinished();
    PumpUntil(() => cell.PhaseChip == CellPhaseChip.Finished);

    Assert.Equal(CellPhaseChip.Finished, Chip(window, cell).Text);
    SelectableTextBlock report = ReportBlock(window, cell);
    Assert.Equal("the diff is correct; nothing to change.", report.Text);
    Assert.True(report.IsEffectivelyVisible, "a card with a report must render it");
    window.Close();
  }

  /// <summary>A failed report read renders the NOTICE rather than an empty panel, so 'could not
  ///     be read' and 'concluded nothing' never look alike.</summary>
  [AvaloniaFact]
  public void AFailedReportRead_RendersTheNotice_NotAnEmptyPanel()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    ReportStore store = new() { Unreadable = true };
    AgentCellViewModel cell = AddHostCell(tab, store);
    Window window = Show(tab);

    cell.MarkFinished();
    PumpUntil(() => cell.PhaseChip == CellPhaseChip.Finished);

    Assert.Equal(CellPhaseChip.Finished, Chip(window, cell).Text);
    Assert.False(ShowsReport(window, cell), "a failed read must not render an empty panel");
    Assert.Contains(Blocks(window, cell),
        block => block.Text == AgentCellViewModel.ReportUnavailableNotice && block.IsEffectivelyVisible);
    window.Close();
  }

  /// <summary>An alerting cell's frame RENDERS the alert outline (parent D12): the outline is
  ///     the whole of the at-a-glance half of an alert in a parentless model.
  ///     <para>THE RENDERED BRUSH AND THICKNESS ARE THE ASSERTION, never the class list. A
  ///     style class lands on the element whether or not any style setter wins, so asserting
  ///     <c>Classes</c> would pass on a card whose frame is byte-identical to a non-alerting
  ///     one - which is exactly what a local value on the element used to cause.</para></summary>
  [AvaloniaFact]
  public void AnAlertingCard_RendersTheAlertOutline()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel cell = tab.Cells[0];
    Window window = Show(tab);
    Border frame = Frame(window, cell);
    Assert.DoesNotContain("alerting", frame.Classes);
    Assert.NotEqual(Color.Parse(AlertBrush), BrushColor(frame));

    cell.ApplyAlert("this agent has been idle for 12m.");
    Dispatcher.UIThread.RunJobs();

    Assert.Contains("alerting", frame.Classes);
    Assert.Equal(Color.Parse(AlertBrush), BrushColor(frame));
    Assert.Equal(new Thickness(AlertThickness), frame.BorderThickness);
    window.Close();
  }

  /// <summary>A cell that is not streaming RENDERS the idle outline (issue #307 D5): a stalled or
  ///     waiting agent is visible without reading its transcript.
  ///     <para>THE IDLE STYLE HAS THE SAME SHAPE AS THE ALERT ONE, so it needs the same kind of
  ///     assertion: a baseline brush set on the element rather than in a style would suppress
  ///     this outline too, and the class would still be there to fool a weaker test.</para></summary>
  [AvaloniaFact]
  public void AnIdleCard_RendersTheIdleOutline()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel cell = tab.Cells[0];
    Window window = Show(tab);
    Border frame = Frame(window, cell);

    Assert.Contains("idle", frame.Classes);
    Assert.Equal(Color.Parse(IdleBrush), BrushColor(frame));
    Assert.Equal(new Thickness(IdleThickness), frame.BorderThickness);
    window.Close();
  }

  /// <summary>The rendered brush of a cell's frame, as a colour: the frame's own brush is what a
  ///     reader sees, so it is what a style-class assertion has to read.</summary>
  /// <param name="frame">The cell's frame.</param>
  private static Color BrushColor(Border frame)
  {
    IBrush? brush = frame.BorderBrush;
    Assert.NotNull(brush);
    return ((ISolidColorBrush)brush).Color;
  }

  /// <summary>The alert outline's brush, matching the <c>.alerting</c> style setter.</summary>
  private const string AlertBrush = "#CCFF6B6B";

  /// <summary>The alert outline's thickness, matching the <c>.alerting</c> style setter.</summary>
  private const double AlertThickness = 2;

  /// <summary>The idle outline's brush, matching the <c>.idle</c> style setter.</summary>
  private const string IdleBrush = "#66FFB454";

  /// <summary>The idle outline's thickness, matching the <c>.idle</c> style setter.</summary>
  private const double IdleThickness = 1;

  /// <summary>Appends a host cell whose agent runs in its own host, so the settle path really
  ///     reads the report from a store rather than from a container this app owns.</summary>
  /// <param name="tab">The tab to append to.</param>
  /// <param name="store">The store the cell reads.</param>
  private static AgentCellViewModel AddHostCell(AgentTabViewModel tab, IAgentStore store)
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();
    return tab.AddAgentCell(AgentId.NewId(), "reviewer", placement: null, store, vm);
  }

  /// <summary>Pumps the dispatcher until the settle path's async read has landed. The read runs
  ///     off the caller's thread, so a single <c>RunJobs</c> would race it.</summary>
  /// <param name="condition">The outcome to wait for.</param>
  private static void PumpUntil(Func<bool> condition)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (!condition() && DateTimeOffset.UtcNow < deadline)
    {
      Dispatcher.UIThread.RunJobs();
      Thread.Sleep(5);
    }

    Dispatcher.UIThread.RunJobs();
    Assert.True(condition(), "the settle path never completed.");
  }

  /// <summary>Shows the tab in a headless window so the cell template is really built.</summary>
  /// <param name="tab">The tab to host.</param>
  private static Window Show(AgentTabViewModel tab)
  {
    Window window = new() { Width = 900, Height = 600, Content = new WorkspaceTabView { DataContext = tab } };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    return window;
  }

  /// <summary>Every block inside ONE cell's frame.
  ///     <para>SCOPED TO THE CELL, NEVER TO THE WINDOW: a tab can hold several cells, so a
  ///     window-wide search for the chip by name finds the FIRST cell's and would report the
  ///     wrong card's phase.</para></summary>
  /// <param name="window">The hosting window.</param>
  /// <param name="cell">The cell whose frame to search.</param>
  private static IEnumerable<TextBlock> Blocks(Window window, AgentCellViewModel cell)
      => Frame(window, cell).GetVisualDescendants().OfType<TextBlock>();

  /// <summary>The cell's phase chip, found by its own name so a transcript block cannot be
  ///     mistaken for it.</summary>
  /// <param name="window">The hosting window.</param>
  /// <param name="cell">The cell whose chip to read.</param>
  private static TextBlock Chip(Window window, AgentCellViewModel cell)
      => Blocks(window, cell).First(block => block.Name == "PhaseChipText");

  /// <summary>The cell's report block. It exists in the visual tree even while hidden, so
  ///     <see cref="ShowsReport"/> is what asks whether the card is really rendering it.</summary>
  /// <param name="window">The hosting window.</param>
  /// <param name="cell">The cell whose report to read.</param>
  private static SelectableTextBlock ReportBlock(Window window, AgentCellViewModel cell)
      => Frame(window, cell).GetVisualDescendants().OfType<SelectableTextBlock>()
          .First(block => block.Name == "ReportText");

  /// <summary>Whether the card is really rendering its report panel.</summary>
  /// <param name="window">The hosting window.</param>
  /// <param name="cell">The cell to inspect.</param>
  private static bool ShowsReport(Window window, AgentCellViewModel cell)
      => ReportBlock(window, cell).IsEffectivelyVisible;

  /// <summary>The rendered frame of ONE cell, matched on its view-model because that is the
  ///     cell's identity (the grid never keys a cell by position).</summary>
  /// <param name="window">The hosting window.</param>
  /// <param name="cell">The cell whose frame to find.</param>
  private static Border Frame(Window window, AgentCellViewModel cell)
      => window.GetVisualDescendants().OfType<Border>()
          .First(border => border.Name == "CellFrame" && ReferenceEquals(border.DataContext, cell));

  /// <summary>A store holding only the agent's terminal record, which is where a settling run
  ///     leaves its report.
  ///     <para><see cref="Unreadable"/> drives the failure contract: a store that cannot answer
  ///     is what the card must state rather than render blank.</para></summary>
  private sealed class ReportStore : IAgentStore
  {
    /// <summary>The report the agent's record carries.</summary>
    public string? Report { get; init; }

    /// <summary>When true every record read fails.</summary>
    public bool Unreadable { get; init; }

    public Task<Result<string>> SaveAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("saved"));

    public Task<Result<string>> UpdateAsync(AgentRecord record, CancellationToken ct = default)
        => Task.FromResult(Result.Success("updated"));

    public Task<Result<AgentRecord>> GetAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Unreadable ? Failure() : Success(id));

    private static Result<AgentRecord> Failure()
        => Result.Failure<AgentRecord>(new DomainError("StoreFault", "unreadable"));

    private Result<AgentRecord> Success(AgentId id)
    {
      AgentRecord record = AgentRecord.Started(
          id, "test/model", "reviewer", "p", DateTimeOffset.UtcNow);
      return Result.Success(record with
      {
        Status = AgentStatus.Completed,
        CompletedAt = DateTimeOffset.UtcNow,
        FinalReport = Report,
      });
    }

    public Task<Result<string>> AppendMessageAsync(AgentId id, Message message, CancellationToken ct = default)
        => Task.FromResult(Result.Success("appended"));

    public Task<Result<string>> ReplaceTranscriptAsync(AgentId id, IReadOnlyList<Message> messages,
        CancellationToken ct = default) => Task.FromResult(Result.Success(id.ToString()));

    public Task<Result<IReadOnlyList<Message>>> GetTranscriptAsync(AgentId id, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<Message>>([]));

    public Task<Result<IReadOnlyList<AgentRecord>>> ListAllAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<AgentRecord>>([]));
  }
}
