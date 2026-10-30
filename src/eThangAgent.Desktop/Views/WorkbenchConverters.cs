using Avalonia.Data.Converters;
using Avalonia.Media;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Views;

/// <summary>Converters for the workbench strip (issue #8) and its git view (issue #9).
///     Small and local to the view: they carry presentation decisions (which icon looks
///     active, how unavailable reads, what colour a diff line is) that no view-model
///     should own.</summary>
internal static class WorkbenchConverters
{
  /// <summary>Active icon background: a subtle highlight, transparent otherwise.</summary>
  public static readonly IValueConverter ActiveIconBackground =
      new FuncValueConverter<bool, IBrush>(active =>
          active ? new SolidColorBrush(Color.Parse("#334C8CFF")) : Brushes.Transparent);

  /// <summary>Unavailable icons render dimmed (0.35) so 'not built yet' is visible
  ///     before the click rather than after it.</summary>
  public static readonly IValueConverter AvailabilityOpacity =
      new FuncValueConverter<bool, double>(available => available ? 1.0 : 0.35);

  /// <summary>Non-zero count shows its element; zero hides it. Used for section
  ///     headers that should not appear over an empty list.</summary>
  public static readonly IValueConverter CountToVisibility =
      new FuncValueConverter<int, bool>(count => count > 0);

  /// <summary>The chain status colour (issue #312 D2): PARKED amber, because it is the one
  ///     status that needs a human; RUNNING blue; COMPLETED and ABANDONED muted; CORRUPT red.
  ///     <para>COLOUR IS THE SECOND CUE, never the only one: the status WORD is always rendered
  ///     beside it, so a reader who cannot distinguish the hues still gets the fact.</para></summary>
  public static readonly IValueConverter ChainStatusBrush =
      new FuncValueConverter<ChainStatusKind, IBrush>(kind => kind switch
      {
        ChainStatusKind.Parked => new SolidColorBrush(Color.Parse("#FFB454")),
        ChainStatusKind.Running => new SolidColorBrush(Color.Parse("#6F8FC9")),
        ChainStatusKind.Completed => new SolidColorBrush(Color.Parse("#5FD18B")),
        ChainStatusKind.Abandoned => new SolidColorBrush(Color.Parse("#B9C2D4")),
        ChainStatusKind.Corrupt => new SolidColorBrush(Color.Parse("#FF6B6B")),
        _ => Brushes.Transparent,
      });

  /// <summary>A chain STEP marker's colour by status, for the same reason: a failed step and a
  ///     parked one are different facts and must not read alike.</summary>
  public static readonly IValueConverter ChainStepBrush =
      new FuncValueConverter<ChainStepStatus, IBrush>(status => status switch
      {
        ChainStepStatus.Done => new SolidColorBrush(Color.Parse("#5FD18B")),
        ChainStepStatus.Running => new SolidColorBrush(Color.Parse("#6F8FC9")),
        ChainStepStatus.Parked => new SolidColorBrush(Color.Parse("#FFB454")),
        ChainStepStatus.Failed => new SolidColorBrush(Color.Parse("#FF6B6B")),
        ChainStepStatus.Pending => new SolidColorBrush(Color.Parse("#B9C2D4")),
        ChainStepStatus.Skipped => new SolidColorBrush(Color.Parse("#B9C2D4")),
        _ => Brushes.Transparent,
      });

  /// <summary>The ledger line colour (issue #318 D3): a REFUTED verdict is amber-red, because it
  ///     is the highest-value record in a ledger - it is what stops the next session spending
  ///     budget on something already disproved - and it must be scannable rather than buried in
  ///     a chronological list.
  ///     <para>COLOUR IS THE SECOND CUE, never the only one: a refuted verdict also carries its
  ///     own marker glyph and its own card class, so a reader who cannot distinguish the hue
  ///     still gets the fact.</para></summary>
  public static readonly IValueConverter LedgerLineBrush =
      new FuncValueConverter<LedgerLineViewModel, IBrush>(line =>
          line is null ? Brushes.Transparent : LedgerLineColor(line));

  /// <summary>The ledger line's brush. A separate method rather than an inline conditional: a
  ///     refuted verdict outranks its kind, and folding that precedence into the null check
  ///     would hide the one rule the colour exists to express.</summary>
  /// <param name="line">The line being coloured.</param>
  private static IBrush LedgerLineColor(LedgerLineViewModel line) =>
      line.IsRefuted ? new SolidColorBrush(Color.Parse("#FF8A65")) : LedgerKindColor(line.Kind);

  /// <summary>The colour a record kind renders as, once the refuted verdict's precedence is
  ///     settled. Its own method so neither the precedence nor the kind set has to carry the
  ///     other's complexity.</summary>
  /// <param name="kind">The record kind.</param>
  private static IBrush LedgerKindColor(ExperimentRecordKind kind) => kind switch
  {
    ExperimentRecordKind.Hypothesis => new SolidColorBrush(Color.Parse("#B9C2D4")),
    ExperimentRecordKind.Prediction => new SolidColorBrush(Color.Parse("#6F8FC9")),
    ExperimentRecordKind.Verdict => new SolidColorBrush(Color.Parse("#5FD18B")),
    ExperimentRecordKind.Observation => new SolidColorBrush(Color.Parse("#B9C2D4")),
    ExperimentRecordKind.Conclusion => new SolidColorBrush(Color.Parse("#FFB454")),
    ExperimentRecordKind.Skip => new SolidColorBrush(Color.Parse("#B9C2D4")),
    ExperimentRecordKind.Unrecognized => new SolidColorBrush(Color.Parse("#8A8A8A")),
    _ => Brushes.Transparent,
  };

  /// <summary>The expand/collapse button's label, derived from the state it toggles rather than
  ///     held beside it - a label and a flag that can disagree is one of them lying.</summary>
  public static readonly IValueConverter ExpandLabel =
      new FuncValueConverter<bool, string>(expanded => expanded ? "Hide steps" : "Show steps");

  /// <summary>A diff line's colour by kind: added green, removed red, hunk header the
  ///     muted blue the mockup uses, context the ordinary foreground.
  ///     <para>Colour is the ONLY cue a unified diff has, so the mapping lives in one
  ///     place rather than being re-derived per template.</para></summary>
  public static readonly IValueConverter DiffLineBrush =
      new FuncValueConverter<DiffLineKind, IBrush>(kind => kind switch
      {
        DiffLineKind.Added => new SolidColorBrush(Color.Parse("#5FD18B")),
        DiffLineKind.Removed => new SolidColorBrush(Color.Parse("#FF6B6B")),
        DiffLineKind.Hunk => new SolidColorBrush(Color.Parse("#6F8FC9")),
        DiffLineKind.Context => new SolidColorBrush(Color.Parse("#B9C2D4")),
        _ => Brushes.Transparent,
      });
}
